using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Niimbot.Net.Diagnostics;

namespace Niimbot.Net.Transport.MacOS;

/// <summary>
/// A single Objective-C class, registered at runtime, that acts as both
/// <c>CBCentralManagerDelegate</c> and <c>CBPeripheralDelegate</c>. Each managed
/// <see cref="MacBleTransport"/> owns one instance; callbacks are routed back to the owner through
/// a static map keyed on the delegate instance pointer (callbacks arrive on the CoreBluetooth
/// dispatch queue's thread).
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class CoreBluetoothDelegate
{
    /// <summary>What a transport must implement to receive the routed callbacks.</summary>
    internal interface ICallbacks
    {
        void OnManagerStateChanged(IntPtr central, long state);
        void OnPeripheralDiscovered(IntPtr peripheral);
        void OnPeripheralConnected(IntPtr peripheral);
        void OnPeripheralConnectFailed(IntPtr peripheral, string error);
        void OnPeripheralDisconnected(IntPtr peripheral, string error);
        void OnServicesDiscovered(IntPtr peripheral, string error);
        void OnCharacteristicsDiscovered(IntPtr peripheral, IntPtr service, string error);
        void OnCharacteristicValueUpdated(IntPtr characteristic, string error);
        void OnNotificationStateChanged(IntPtr characteristic, string error);
        void OnReadyToSendWriteWithoutResponse();
    }

    private static readonly ConcurrentDictionary<IntPtr, ICallbacks> Owners = new();
    private static IntPtr _delegateClass;
    private static readonly object RegisterLock = new();

    /// <summary>Create a delegate instance wired to <paramref name="owner"/>. Release via <see cref="Destroy"/>.</summary>
    internal static IntPtr Create(ICallbacks owner)
    {
        EnsureClassRegistered();
        var instance = ObjC.MsgSend(ObjC.MsgSend(_delegateClass, ObjC.Sel.Alloc), ObjC.Sel.Init);
        Owners[instance] = owner;
        return instance;
    }

    internal static void Destroy(IntPtr instance)
    {
        if (instance == IntPtr.Zero)
            return;
        Owners.TryRemove(instance, out _);
        ObjC.objc_release(instance);
    }

    private static void EnsureClassRegistered()
    {
        if (_delegateClass != IntPtr.Zero)
            return;

        lock (RegisterLock)
        {
            if (_delegateClass != IntPtr.Zero)
                return;

            var cls = ObjC.objc_allocateClassPair(ObjC.objc_getClass("NSObject"), "NiimbotCoreBluetoothDelegate", 0);
            if (cls == IntPtr.Zero)
                throw new InvalidOperationException("Could not allocate the CoreBluetooth delegate class.");

            Add(cls, "centralManagerDidUpdateState:", (delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&DidUpdateState, "v@:@");
            Add(cls, "centralManager:didDiscoverPeripheral:advertisementData:RSSI:",
                (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidDiscover, "v@:@@@@");
            Add(cls, "centralManager:didConnectPeripheral:",
                (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidConnect, "v@:@@");
            Add(cls, "centralManager:didFailToConnectPeripheral:error:",
                (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidFailToConnect, "v@:@@@");
            Add(cls, "centralManager:didDisconnectPeripheral:error:",
                (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidDisconnect, "v@:@@@");
            Add(cls, "peripheral:didDiscoverServices:",
                (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidDiscoverServices, "v@:@@");
            Add(cls, "peripheral:didDiscoverCharacteristicsForService:error:",
                (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidDiscoverCharacteristics, "v@:@@@");
            Add(cls, "peripheral:didUpdateValueForCharacteristic:error:",
                (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidUpdateValue, "v@:@@@");
            Add(cls, "peripheral:didUpdateNotificationStateForCharacteristic:error:",
                (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidUpdateNotificationState, "v@:@@@");
            Add(cls, "peripheralIsReadyToSendWriteWithoutResponse:",
                (delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&IsReadyToSend, "v@:@");

            ObjC.objc_registerClassPair(cls);
            _delegateClass = cls;
        }
    }

    private static void Add(IntPtr cls, string selector, void* imp, string types)
    {
        if (!ObjC.class_addMethod(cls, ObjC.sel_registerName(selector), (IntPtr)imp, types))
            throw new InvalidOperationException($"class_addMethod failed for {selector}.");
    }

    private static ICallbacks? Owner(IntPtr self) => Owners.GetValueOrDefault(self);

    /// <summary>
    /// Run one routed callback crash-safely. A managed exception escaping an
    /// <c>[UnmanagedCallersOnly]</c> frame fail-fasts the whole process; a broken callback must
    /// fail one transport operation (via trace + the transport's own fault paths), never the app.
    /// </summary>
    private static void Guarded(Action callback)
    {
        try
        {
            callback();
        }
        catch (Exception ex)
        {
            NiimbotTrace.Log("ble", $"delegate callback error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [UnmanagedCallersOnly]
    private static void DidUpdateState(IntPtr self, IntPtr sel, IntPtr central) =>
        Guarded(() => Owner(self)?.OnManagerStateChanged(central, ObjC.MsgSendLong(central, ObjC.Sel.State)));

    [UnmanagedCallersOnly]
    private static void DidDiscover(IntPtr self, IntPtr sel, IntPtr central, IntPtr peripheral, IntPtr advertisementData, IntPtr rssi) =>
        Guarded(() => Owner(self)?.OnPeripheralDiscovered(peripheral));

    [UnmanagedCallersOnly]
    private static void DidConnect(IntPtr self, IntPtr sel, IntPtr central, IntPtr peripheral) =>
        Guarded(() => Owner(self)?.OnPeripheralConnected(peripheral));

    [UnmanagedCallersOnly]
    private static void DidFailToConnect(IntPtr self, IntPtr sel, IntPtr central, IntPtr peripheral, IntPtr error) =>
        Guarded(() => Owner(self)?.OnPeripheralConnectFailed(peripheral, ObjC.DescribeError(error)));

    [UnmanagedCallersOnly]
    private static void DidDisconnect(IntPtr self, IntPtr sel, IntPtr central, IntPtr peripheral, IntPtr error) =>
        Guarded(() => Owner(self)?.OnPeripheralDisconnected(peripheral, ObjC.DescribeError(error)));

    [UnmanagedCallersOnly]
    private static void DidDiscoverServices(IntPtr self, IntPtr sel, IntPtr peripheral, IntPtr error) =>
        Guarded(() => Owner(self)?.OnServicesDiscovered(peripheral, ObjC.DescribeError(error)));

    [UnmanagedCallersOnly]
    private static void DidDiscoverCharacteristics(IntPtr self, IntPtr sel, IntPtr peripheral, IntPtr service, IntPtr error) =>
        Guarded(() => Owner(self)?.OnCharacteristicsDiscovered(peripheral, service, ObjC.DescribeError(error)));

    [UnmanagedCallersOnly]
    private static void DidUpdateValue(IntPtr self, IntPtr sel, IntPtr peripheral, IntPtr characteristic, IntPtr error) =>
        Guarded(() => Owner(self)?.OnCharacteristicValueUpdated(characteristic, ObjC.DescribeError(error)));

    [UnmanagedCallersOnly]
    private static void DidUpdateNotificationState(IntPtr self, IntPtr sel, IntPtr peripheral, IntPtr characteristic, IntPtr error) =>
        Guarded(() => Owner(self)?.OnNotificationStateChanged(characteristic, ObjC.DescribeError(error)));

    [UnmanagedCallersOnly]
    private static void IsReadyToSend(IntPtr self, IntPtr sel, IntPtr peripheral) =>
        Guarded(() => Owner(self)?.OnReadyToSendWriteWithoutResponse());
}
