using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Niimbot.Net.Diagnostics;
using Niimbot.Net.Transport.MacOS;

namespace Niimbot.Net.Transport;

/// <summary>
/// <see cref="INiimbotTransport"/> over Bluetooth Low Energy on macOS, via CoreBluetooth driven
/// directly through Objective-C runtime interop (pure managed code — no Catalyst, no native shim;
/// see <see cref="MacOS.ObjC"/>). Newer NIIMBOT models (B1 Pro and friends) expose their wireless
/// data channel only over BLE: the same <c>55 55 … AA AA</c> packets, carried as GATT
/// notifications (printer → host) and write-without-response (host → printer).
///
/// <para>A dumb byte duplex like every transport (spec §5.1): notification payloads may split
/// packets mid-frame, and <see cref="Framing.PacketAccumulator"/> above already reassembles them.
/// Connection targets a peripheral by advertised <b>name</b> — on macOS CoreBluetooth exposes no
/// stable MAC address, so name is the durable handle (community-wiki guidance).</para>
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacBleTransport : INiimbotTransport, CoreBluetoothDelegate.ICallbacks
{
    /// <summary>GATT service most NIIMBOT models expose for the data channel.</summary>
    public const string DefaultServiceUuid = "E7810A71-73AE-499D-8C15-FAA9AEF0C3F2";

    /// <summary>Data characteristic (NOTIFY + WRITE_NO_RESPONSE) under <see cref="DefaultServiceUuid"/>.</summary>
    public const string DefaultCharacteristicUuid = "BEF8D6C9-9C21-4C9E-B632-BD58C1009F9F";

    // CoreBluetooth constants (CBManagerState / CBCharacteristicProperties / CBCharacteristicWriteType).
    private const long StatePoweredOn = 5;
    private const long StateUnauthorized = 3;
    private const long StateUnsupported = 2;
    private const nuint PropertyWriteWithoutResponse = 0x04;
    private const nuint PropertyNotify = 0x10;
    private const long WriteWithoutResponse = 1;

    private readonly string _deviceName;
    private readonly int _connectTimeoutMs;
    private readonly int _readTimeoutMs;

    private readonly object _stateLock = new();
    private readonly List<string> _seenNames = [];
    private AsyncByteQueue? _queue;
    private SemaphoreSlim? _writeReady;
    private TaskCompletionSource? _connectTcs;
    private IntPtr _delegate;
    private IntPtr _dispatchQueue;
    private IntPtr _central;
    private IntPtr _peripheral;
    private IntPtr _characteristic;
    private int _pendingServiceDiscoveries;
    private bool _characteristicFound;
    private volatile bool _connected;

    /// <summary>Whether this transport can run on the current OS.</summary>
    public static bool IsSupported => OperatingSystem.IsMacOS();

    /// <param name="deviceName">
    /// Advertised peripheral name to connect to, matched case-insensitively as a substring (e.g.
    /// <c>"B1 Pro"</c> matches <c>"B1 Pro-A1B2C3"</c>). Use <see cref="ScanAsync"/> to discover names.
    /// </param>
    /// <param name="connectTimeoutMs">Overall budget for power-on, scan, connect, and GATT setup.</param>
    /// <param name="readTimeoutMs">Idle-line timeout per <see cref="ReadAsync"/>, mirroring serial.</param>
    public MacBleTransport(string deviceName, int connectTimeoutMs = 20_000, int readTimeoutMs = 500)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("MacBleTransport requires macOS (CoreBluetooth).");
        _deviceName = !string.IsNullOrWhiteSpace(deviceName)
            ? deviceName
            : throw new ArgumentException("A target device name is required.", nameof(deviceName));
        _connectTimeoutMs = connectTimeoutMs;
        _readTimeoutMs = readTimeoutMs;
    }

    public bool IsConnected => _connected;

    public event EventHandler<TransportState>? StateChanged;

    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        if (_connected)
            return;

        StateChanged?.Invoke(this, TransportState.Connecting);
        NiimbotTrace.Log("ble", $"connect '{_deviceName}' (timeout {_connectTimeoutMs}ms)");

        ObjC.EnsureCoreBluetoothLoaded();
        _queue = new AsyncByteQueue();
        _writeReady = new SemaphoreSlim(0, 1);
        _connectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _delegate = CoreBluetoothDelegate.Create(this);
        _dispatchQueue = ObjC.dispatch_queue_create("net.niimbot.ble", IntPtr.Zero);

        // The delegate's centralManagerDidUpdateState: fires once the manager is ready and drives
        // the rest of the pipeline (scan → connect → discover → notify).
        var central = ObjC.MsgSend(ObjC.objc_getClass("CBCentralManager"), ObjC.Sel.Alloc);
        _central = ObjC.MsgSend(central, ObjC.Sel.InitWithDelegateQueue, _delegate, _dispatchQueue);

        try
        {
            await _connectTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(_connectTimeoutMs), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var seen = _seenNames.Count > 0 ? $" Seen peripherals: {string.Join(", ", _seenNames)}." : string.Empty;
            NiimbotTrace.Log("ble", $"connect FAILED '{_deviceName}': {ex.GetType().Name}: {ex.Message}.{seen}");
            await CleanupAsync().ConfigureAwait(false);
            StateChanged?.Invoke(this, TransportState.Faulted);
            if (ex is TimeoutException)
                throw new TimeoutException($"No BLE peripheral matching '{_deviceName}' completed setup " +
                    $"within {_connectTimeoutMs}ms.{seen}");
            throw;
        }

        _connected = true;
        NiimbotTrace.Log("ble", $"connect OK '{_deviceName}' (max write {MaxWriteLength()} bytes)");
        StateChanged?.Invoke(this, TransportState.Connected);
    }

    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        var wasConnected = _connected;
        await CleanupAsync().ConfigureAwait(false);
        if (wasConnected)
            StateChanged?.Invoke(this, TransportState.Disconnected);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var peripheral = _peripheral;
        var characteristic = _characteristic;
        if (!_connected || peripheral == IntPtr.Zero || characteristic == IntPtr.Zero)
            throw new InvalidOperationException("BLE transport is not connected.");

        if (NiimbotTrace.IsEnabled)
            NiimbotTrace.Bytes("ble", "→ write", data.Span);

        var chunkSize = MaxWriteLength();
        for (var offset = 0; offset < data.Length; offset += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = data.Slice(offset, Math.Min(chunkSize, data.Length - offset));
            await WaitUntilReadyToSendAsync(peripheral, ct).ConfigureAwait(false);

            unsafe
            {
                fixed (byte* bytes = chunk.Span)
                {
                    // alloc + initWithBytes:length: yields an owned reference — managed threads have
                    // no autorelease pool, so the autoreleasing dataWithBytes:length: would leak
                    // one NSData per chunk across a print job.
                    var nsData = ObjC.MsgSend(ObjC.MsgSend(ObjC.objc_getClass("NSData"), ObjC.Sel.Alloc),
                        ObjC.Sel.InitWithBytesLength, (IntPtr)bytes, (nuint)chunk.Length);
                    try
                    {
                        ObjC.MsgSendVoid(peripheral, ObjC.Sel.WriteValue, nsData, characteristic, WriteWithoutResponse);
                    }
                    finally
                    {
                        ObjC.objc_release(nsData); // writeValue retains what it needs
                    }
                }
            }
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var queue = _queue;
        if (queue is null)
            return 0;

        var read = await queue.ReadAsync(buffer, _readTimeoutMs, ct).ConfigureAwait(false);
        if (read > 0 && NiimbotTrace.IsEnabled)
            NiimbotTrace.Bytes("ble", "← read", buffer.Span[..read]);
        return read;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    /// <summary>
    /// Scan for advertising BLE peripherals and return their names (deduplicated, discovery
    /// order). NIIMBOT printers advertise as e.g. <c>B1 Pro-XXXXXX</c>. macOS only.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ScanAsync(TimeSpan duration, CancellationToken ct = default)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("BLE scan requires macOS (CoreBluetooth).");

        ObjC.EnsureCoreBluetoothLoaded();
        var session = new ScanSession();
        var delegateInstance = CoreBluetoothDelegate.Create(session);
        var queue = ObjC.dispatch_queue_create("net.niimbot.ble.scan", IntPtr.Zero);
        var central = ObjC.MsgSend(ObjC.MsgSend(ObjC.objc_getClass("CBCentralManager"), ObjC.Sel.Alloc),
            ObjC.Sel.InitWithDelegateQueue, delegateInstance, queue);
        try
        {
            await Task.Delay(duration, ct).ConfigureAwait(false);
            return session.Names;
        }
        finally
        {
            ObjC.MsgSendVoid(central, ObjC.Sel.StopScan);
            // Detach before release — same use-after-free hazard as in CleanupAsync.
            ObjC.MsgSendVoid(central, ObjC.Sel.SetDelegate, IntPtr.Zero);
            ObjC.objc_release(central);
            CoreBluetoothDelegate.Destroy(delegateInstance);
        }
    }

    private int MaxWriteLength()
    {
        // maximumWriteValueLengthForType: is per-connection; clamp to sane NIIMBOT bounds. 20 is
        // the BLE 4.0 floor (ATT MTU 23 − 3), and niimblue caps around 512.
        var reported = (int)ObjC.MsgSendNUInt(_peripheral, ObjC.Sel.MaxWriteLength, WriteWithoutResponse);
        return Math.Clamp(reported, 20, 512);
    }

    private async ValueTask WaitUntilReadyToSendAsync(IntPtr peripheral, CancellationToken ct)
    {
        // Modern macOS exposes back-pressure for write-without-response; honor it when present.
        var canSendSel = ObjC.Sel.CanSendWriteWithoutResponse;
        if (ObjC.MsgSendBool(peripheral, ObjC.Sel.RespondsToSelector, canSendSel) == 0)
            return;
        var ready = _writeReady;
        while (ObjC.MsgSendBool(peripheral, canSendSel) == 0)
        {
            if (ready is null)
                return;
            // peripheralIsReadyToSendWriteWithoutResponse: releases the gate; 250 ms is a safety
            // re-poll in case the callback is missed around a disconnect.
            await ready.WaitAsync(250, ct).ConfigureAwait(false);
            if (!_connected)
                throw new InvalidOperationException("BLE transport disconnected mid-write.");
        }
    }

    private ValueTask CleanupAsync()
    {
        lock (_stateLock)
        {
            _connected = false;

            if (_characteristic != IntPtr.Zero && _peripheral != IntPtr.Zero)
                ObjC.MsgSendVoid(_peripheral, ObjC.Sel.SetNotifyValue, 0, _characteristic); // best effort

            if (_central != IntPtr.Zero)
            {
                ObjC.MsgSendVoid(_central, ObjC.Sel.StopScan); // harmless when not scanning
                if (_peripheral != IntPtr.Zero)
                    ObjC.MsgSendVoid(_central, ObjC.Sel.CancelPeripheralConnection, _peripheral);
            }

            // Detach the delegate from both objects BEFORE releasing it: CoreBluetooth holds its
            // delegate weakly and delivers callbacks async on the GCD queue, so releasing our sole
            // strong reference while still attached is a use-after-free waiting for the next
            // queued callback.
            if (_peripheral != IntPtr.Zero)
                ObjC.MsgSendVoid(_peripheral, ObjC.Sel.SetDelegate, IntPtr.Zero);
            if (_central != IntPtr.Zero)
                ObjC.MsgSendVoid(_central, ObjC.Sel.SetDelegate, IntPtr.Zero);

            ReleaseHandle(ref _characteristic);
            ReleaseHandle(ref _peripheral);
            ReleaseHandle(ref _central);
            if (_delegate != IntPtr.Zero)
            {
                CoreBluetoothDelegate.Destroy(_delegate);
                _delegate = IntPtr.Zero;
            }

            _queue?.Dispose();
            _queue = null;
            _writeReady?.Dispose();
            _writeReady = null;
            _connectTcs = null;
        }

        return ValueTask.CompletedTask;
    }

    private static void ReleaseHandle(ref IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return;
        ObjC.objc_release(handle);
        handle = IntPtr.Zero;
    }

    // CoreBluetooth callbacks — all arrive on the dispatch queue's thread.

    void CoreBluetoothDelegate.ICallbacks.OnManagerStateChanged(IntPtr central, long state)
    {
        NiimbotTrace.Log("ble", $"manager state {state}");
        if (state == StatePoweredOn)
        {
            // Scan unfiltered: NIIMBOT printers don't reliably advertise the data service UUID.
            ObjC.MsgSendVoid(central, ObjC.Sel.ScanForPeripherals, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        var reason = state switch
        {
            StateUnauthorized => "Bluetooth permission denied — grant it in System Settings → Privacy & Security → Bluetooth.",
            StateUnsupported => "This Mac reports no BLE support.",
            _ => $"Bluetooth is unavailable (CBManagerState {state}).",
        };
        Fail(new InvalidOperationException(reason));
    }

    void CoreBluetoothDelegate.ICallbacks.OnPeripheralDiscovered(IntPtr peripheral)
    {
        var name = ObjC.FromNSString(ObjC.MsgSend(peripheral, ObjC.Sel.Name));
        if (name.Length == 0)
            return;

        lock (_stateLock)
        {
            if (!_seenNames.Contains(name))
                _seenNames.Add(name);
            if (_peripheral != IntPtr.Zero || !name.Contains(_deviceName, StringComparison.OrdinalIgnoreCase))
                return;
            _peripheral = ObjC.objc_retain(peripheral);
        }

        NiimbotTrace.Log("ble", $"matched '{name}', connecting");
        ObjC.MsgSendVoid(_central, ObjC.Sel.StopScan);
        ObjC.MsgSendVoid(peripheral, ObjC.Sel.SetDelegate, _delegate);
        ObjC.MsgSendVoid(_central, ObjC.Sel.ConnectPeripheral, peripheral, IntPtr.Zero);
    }

    void CoreBluetoothDelegate.ICallbacks.OnPeripheralConnected(IntPtr peripheral)
    {
        NiimbotTrace.Log("ble", "connected, discovering services");
        ObjC.MsgSendVoid(peripheral, ObjC.Sel.DiscoverServices, IntPtr.Zero);
    }

    void CoreBluetoothDelegate.ICallbacks.OnPeripheralConnectFailed(IntPtr peripheral, string error) =>
        Fail(new InvalidOperationException($"BLE connect failed: {error}"));

    void CoreBluetoothDelegate.ICallbacks.OnPeripheralDisconnected(IntPtr peripheral, string error)
    {
        NiimbotTrace.Log("ble", $"disconnected{(error.Length > 0 ? $": {error}" : string.Empty)}");
        if (!_connected)
        {
            Fail(new InvalidOperationException($"BLE peripheral disconnected during setup: {error}"));
            return;
        }

        // Surprise power-off / out-of-range: fault like a serial surprise-unplug.
        _connected = false;
        _queue?.Dispose();
        StateChanged?.Invoke(this, TransportState.Faulted);
    }

    void CoreBluetoothDelegate.ICallbacks.OnServicesDiscovered(IntPtr peripheral, string error)
    {
        if (error.Length > 0)
        {
            Fail(new InvalidOperationException($"BLE service discovery failed: {error}"));
            return;
        }

        var services = ObjC.MsgSend(peripheral, ObjC.Sel.Services);
        var count = services == IntPtr.Zero ? 0 : (int)ObjC.MsgSendNUInt(services, ObjC.Sel.Count);

        // Prefer the known NIIMBOT service; fall back to the community-wiki rule (any service with
        // a long/128-bit UUID) so unseen models still resolve.
        var candidates = new List<IntPtr>();
        for (var i = 0; i < count; i++)
        {
            var service = ObjC.MsgSendAtIndex(services, ObjC.Sel.ObjectAtIndex, (nuint)i);
            var uuid = ObjC.FromNSString(ObjC.MsgSend(ObjC.MsgSend(service, ObjC.Sel.Uuid), ObjC.Sel.UuidString));
            if (string.Equals(uuid, DefaultServiceUuid, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Insert(0, service);
                NiimbotTrace.Log("ble", $"service {uuid} (known NIIMBOT data service)");
            }
            else if (uuid.Length > 4)
            {
                candidates.Add(service);
                NiimbotTrace.Log("ble", $"service {uuid} (candidate)");
            }
        }

        if (candidates.Count == 0)
        {
            Fail(new InvalidOperationException("No candidate GATT service found (no long-UUID services)."));
            return;
        }

        _pendingServiceDiscoveries = candidates.Count;
        foreach (var service in candidates)
            ObjC.MsgSendVoid(peripheral, ObjC.Sel.DiscoverCharacteristics, IntPtr.Zero, service);
    }

    void CoreBluetoothDelegate.ICallbacks.OnCharacteristicsDiscovered(IntPtr peripheral, IntPtr service, string error)
    {
        lock (_stateLock)
        {
            _pendingServiceDiscoveries--;
            if (_characteristicFound)
                return;

            if (error.Length == 0)
            {
                var characteristics = ObjC.MsgSend(service, ObjC.Sel.Characteristics);
                var count = characteristics == IntPtr.Zero ? 0 : (int)ObjC.MsgSendNUInt(characteristics, ObjC.Sel.Count);
                for (var i = 0; i < count; i++)
                {
                    var characteristic = ObjC.MsgSendAtIndex(characteristics, ObjC.Sel.ObjectAtIndex, (nuint)i);
                    var properties = ObjC.MsgSendNUInt(characteristic, ObjC.Sel.Properties);
                    var uuid = ObjC.FromNSString(
                        ObjC.MsgSend(ObjC.MsgSend(characteristic, ObjC.Sel.Uuid), ObjC.Sel.UuidString));
                    var usable = (properties & PropertyNotify) != 0 && (properties & PropertyWriteWithoutResponse) != 0;
                    if (!usable)
                        continue;
                    // First usable characteristic wins; the known UUID is logged for the record.
                    NiimbotTrace.Log("ble", $"characteristic {uuid} props 0x{(ulong)properties:X}" +
                        (string.Equals(uuid, DefaultCharacteristicUuid, StringComparison.OrdinalIgnoreCase)
                            ? " (known NIIMBOT data characteristic)" : string.Empty));
                    _characteristic = ObjC.objc_retain(characteristic);
                    _characteristicFound = true;
                    ObjC.MsgSendVoid(peripheral, ObjC.Sel.SetNotifyValue, 1, characteristic);
                    return;
                }
            }

            if (_pendingServiceDiscoveries <= 0)
                Fail(new InvalidOperationException(
                    "No GATT characteristic with NOTIFY + WRITE_NO_RESPONSE found — not a NIIMBOT data channel?"));
        }
    }

    void CoreBluetoothDelegate.ICallbacks.OnNotificationStateChanged(IntPtr characteristic, string error)
    {
        if (error.Length > 0)
        {
            Fail(new InvalidOperationException($"BLE notification subscribe failed: {error}"));
            return;
        }

        NiimbotTrace.Log("ble", "notifications on — link ready");
        _connectTcs?.TrySetResult();
    }

    void CoreBluetoothDelegate.ICallbacks.OnCharacteristicValueUpdated(IntPtr characteristic, string error)
    {
        if (error.Length > 0)
        {
            NiimbotTrace.Log("ble", $"notification error: {error}");
            return;
        }

        var payload = ObjC.FromNSData(ObjC.MsgSend(characteristic, ObjC.Sel.Value));
        if (payload.Length > 0)
            _queue?.Append(payload);
    }

    void CoreBluetoothDelegate.ICallbacks.OnReadyToSendWriteWithoutResponse()
    {
        var ready = _writeReady;
        if (ready is { CurrentCount: 0 })
        {
            try
            {
                ready.Release();
            }
            catch (SemaphoreFullException)
            {
                // Benign race with another release.
            }
            catch (ObjectDisposedException)
            {
                // Raced with cleanup.
            }
        }
    }

    private void Fail(Exception ex) => _connectTcs?.TrySetException(ex);

    /// <summary>Scan-only callback sink for <see cref="ScanAsync"/>.</summary>
    private sealed class ScanSession : CoreBluetoothDelegate.ICallbacks
    {
        private readonly object _lock = new();
        private readonly List<string> _names = [];

        public IReadOnlyList<string> Names
        {
            get
            {
                lock (_lock)
                    return [.. _names];
            }
        }

        public void OnManagerStateChanged(IntPtr central, long state)
        {
            // Start the unfiltered scan as soon as the radio is ready; a non-poweredOn state simply
            // yields an empty result list at timeout (scan is a best-effort discovery aid).
            if (state == 5 /* poweredOn */)
                ObjC.MsgSendVoid(central, ObjC.Sel.ScanForPeripherals, IntPtr.Zero, IntPtr.Zero);
        }

        public void OnPeripheralDiscovered(IntPtr peripheral)
        {
            var name = ObjC.FromNSString(ObjC.MsgSend(peripheral, ObjC.Sel.Name));
            if (name.Length == 0)
                return;
            lock (_lock)
            {
                if (!_names.Contains(name))
                    _names.Add(name);
            }
        }

        public void OnPeripheralConnected(IntPtr peripheral) { }
        public void OnPeripheralConnectFailed(IntPtr peripheral, string error) { }
        public void OnPeripheralDisconnected(IntPtr peripheral, string error) { }
        public void OnServicesDiscovered(IntPtr peripheral, string error) { }
        public void OnCharacteristicsDiscovered(IntPtr peripheral, IntPtr service, string error) { }
        public void OnCharacteristicValueUpdated(IntPtr characteristic, string error) { }
        public void OnNotificationStateChanged(IntPtr characteristic, string error) { }
        public void OnReadyToSendWriteWithoutResponse() { }
    }
}
