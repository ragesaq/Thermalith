# HyperReview patch-review — feat/ble-transport-macos (dirty worktree)

Target: /srv/codex/repo/thermalith, branch `feat/ble-transport-macos` off `upstream/main`
(uncommitted at review time). Scope: macOS BLE transport for Niimbot.Net (upstream issue #13)
— `MacBleTransport`, `MacOS/ObjC` interop, `MacOS/CoreBluetoothDelegate`, `AsyncByteQueue`,
`NiimbotClient.FromBleDevice`, print-harness `scan`/`--ble` wiring, `AsyncByteQueueTests`.

## Tier

Full. Trigger 5 (blast radius): ~1,150 hand-authored source+test lines across 7 files (no
generated/lockfile churn to exclude), over the ~500-line threshold. Also trigger 1: two
high-severity findings accepted below.

## Verdict: WARN

Contract shape (INiimbotTransport §5.1: dumb byte duplex, state events, 0-on-idle reads) is
correctly preserved and the OS-independent read path is well tested. Three accepted findings,
two high, all concretely fixable in-loop; no broken contract, no unsafe trust crossing left
unmitigated.

## Accepted findings

F1 (high, resource-correctness / build-contract lens) — autoreleased ObjC objects created on
managed threads leak. `WriteAsync` builds one `NSData` per BLE chunk via
`dataWithBytes:length:` (autoreleasing convenience constructor) on a .NET thread pool thread,
which has no autorelease pool:

    var nsData = ObjC.MsgSend(ObjC.objc_getClass("NSData"), ObjC.Sel.DataWithBytesLength,
        (IntPtr)bytes, (nuint)chunk.Length);
    ObjC.MsgSendVoid(peripheral, ObjC.Sel.WriteValue, nsData, characteristic, WriteWithoutResponse);

A print job pushes a bitmap in 20–512-byte chunks → hundreds of leaked NSData per label, on
the golden print path. Falsifier: none found — Apple documents convenience constructors as
autorelease; no pool exists on managed threads. Remedy: `alloc` + `initWithBytes:length:` and
explicit `objc_release` after the write (owned reference, no pool needed); same pattern for
any other convenience-constructor use on managed threads.

F2 (high, lifecycle/stability lens) — delegate can be deallocated while CoreBluetooth still
messages it. `CleanupAsync` releases the delegate (sole strong reference) without first
clearing it from the central/peripheral:

    ReleaseHandle(ref _central);
    if (_delegate != IntPtr.Zero)
    {
        CoreBluetoothDelegate.Destroy(_delegate);   // objc_release → dealloc

CBCentralManager/CBPeripheral hold their delegate weakly and dispatch callbacks async on the
GCD queue; a queued callback delivered after dealloc is a use-after-free crash (managed-side
routing is safe — Owners map — but the ObjC receiver itself is gone). Same shape in
`ScanAsync`'s finally block. Invariant: never release a delegate an active manager can still
message. Remedy: `setDelegate:nil` on peripheral and central (and cancel/stop first), then
release; in ScanAsync, stopScan + setDelegate:nil before Destroy.

F3 (medium, operator-safety lens) — a managed exception escaping an `[UnmanagedCallersOnly]`
callback terminates the process. All ten delegate IMPs call into owner logic that allocates,
locks, and logs (e.g. `DidDiscover` → list ops + `objc_retain` + trace); any throw unwinds
into a native GCD frame → runtime fail-fast. The transport can fail a connect gracefully via
`Fail(...)`; it must not be able to crash the host app from a callback. Remedy: wrap each
routed callback body in try/catch (route to `Fail`/trace, never rethrow across the native
boundary).

## Rejected candidates (ledger)

- `_central` field read from discovery callback before assignment completes — benign: scan
  starts only from the state callback (post-init), discovery arrives ms later; the state
  callback itself uses the passed `central` pointer.
- `setNotifyValue:` overload ambiguity (int literal → sbyte vs nint) — ABI-identical either
  way at this arity (arg in same register, low byte read as BOOL); compile-time resolution
  verified by green build.
- Duplicate ObjC class-name registration if two copies of Niimbot.Net load in one process —
  real but exotic (plugin/ALC scenarios); would fail loudly at first connect, not silently.
- `AsyncByteQueue` spurious 0-return when a reader's rebalance races a writer's release —
  within the transport read contract (0 = idle; the client pump re-polls), covered by the
  cross-thread stream test.

## Test evidence

- 52/52 Niimbot.Net tests green, including 8 new `AsyncByteQueueTests` covering ordering,
  fragmentation reassembly through the real `PacketAccumulator` (community-wiki example
  packet), idle-timeout, cancellation, dispose-wakes-reader, and a 16 KiB cross-thread
  stream with unaligned chunk boundaries.
- Test gap (accepted, reason recorded): `MacBleTransport` connect state machine and the ObjC
  interop layer are macOS-hardware-bound and cannot execute on this Linux host; verification
  is the planned on-hardware pass with ragesaq's B1 Pro via `print-harness scan` / `--ble`
  (work map ragesaq-004), which doubles as the catalogue verification report.
- 26 pre-existing `Thermalith.Core.Tests` failures are `libSkiaSharp.so` missing on this
  container (native dep), untouched surface, present on clean upstream/main.

## Conditional passes

- boundary-threat: ACTIVATED (new wireless input path). Notification payloads are copied
  with exact `length` bounds (`FromNSData`) and parsed only by the existing hardened
  `PacketAccumulator` (resync-on-noise). Peripheral name matching is substring-based and
  user-directed; worst case is connecting to a non-printer, which fails at characteristic
  discovery with a clear error. No credential, network, or persistence surface. No findings
  beyond F3.
- operational-impact: ACTIVATED (packaging). The bundled macOS app will need
  `NSBluetoothAlwaysUsageDescription` in Info.plist (Pack-MacApp.sh) before app-level BLE
  ships; the CLI harness relies on the terminal's TCC Bluetooth prompt. Follow-up noted for
  the app-integration PR — not a defect in this library-scoped diff.
- authority-provenance, migration-safety, code-map-evidence: skipped — no identity/authority
  surface, no schema/data migration, no code-map artifacts in this repo.

## Next action

Fix F1–F3 in-loop (implementer, post-review), re-run tests, then proceed to on-hardware
verification before the upstream PR (ragesaq-005). App-level BLE UI + Info.plist wiring is a
separate follow-up PR.

cleanResult: false

## Post-review disposition (implementer, same session)

F1, F2, F3 all fixed immediately after the review: owned NSData (alloc/initWithBytes:length: +
objc_release) in the write path; setDelegate:nil on peripheral/central (and stopScan) before
releasing the delegate in both CleanupAsync and ScanAsync; Guarded() try/catch wrapper around
all ten UnmanagedCallersOnly IMPs. Rebuild clean, 52/52 Niimbot.Net tests green. Remaining
gate before upstream PR: on-hardware verification (B1 Pro).
