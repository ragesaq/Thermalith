using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Niimbot.Net.Transport.MacOS;

/// <summary>
/// Minimal Objective-C runtime interop for <see cref="MacBleTransport"/> — just enough of
/// <c>libobjc</c> + <c>libdispatch</c> to drive CoreBluetooth from plain managed code (no
/// Catalyst, no bindings package, no native shim to ship). Every <c>objc_msgSend</c> overload
/// below is declared with the exact signature of the message it sends, which is the supported
/// calling pattern on both arm64 and x64 macOS.
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class ObjC
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.dylib";
    private const string CoreBluetoothFramework =
        "/System/Library/Frameworks/CoreBluetooth.framework/CoreBluetooth";

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr objc_getClass(string name);

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr sel_registerName(string name);

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);

    [LibraryImport(LibObjC)]
    internal static partial void objc_registerClassPair(IntPtr cls);

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

    [LibraryImport(LibObjC)]
    internal static partial IntPtr objc_retain(IntPtr obj);

    [LibraryImport(LibObjC)]
    internal static partial void objc_release(IntPtr obj);

    // objc_msgSend overloads — one per message shape we send.

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr MsgSend(IntPtr receiver, IntPtr sel);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr MsgSend(IntPtr receiver, IntPtr sel, IntPtr arg1);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr MsgSend(IntPtr receiver, IntPtr sel, IntPtr arg1, IntPtr arg2);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr MsgSend(IntPtr receiver, IntPtr sel, IntPtr bytes, nuint length);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr MsgSendUtf8(IntPtr receiver, IntPtr sel, string utf8);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr MsgSendAtIndex(IntPtr receiver, IntPtr sel, nuint index);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void MsgSendVoid(IntPtr receiver, IntPtr sel);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void MsgSendVoid(IntPtr receiver, IntPtr sel, IntPtr arg1);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void MsgSendVoid(IntPtr receiver, IntPtr sel, IntPtr arg1, IntPtr arg2);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void MsgSendVoid(IntPtr receiver, IntPtr sel, sbyte arg1, IntPtr arg2);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void MsgSendVoid(IntPtr receiver, IntPtr sel, IntPtr arg1, IntPtr arg2, long arg3);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial long MsgSendLong(IntPtr receiver, IntPtr sel);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial nuint MsgSendNUInt(IntPtr receiver, IntPtr sel);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial nuint MsgSendNUInt(IntPtr receiver, IntPtr sel, long arg1);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial sbyte MsgSendBool(IntPtr receiver, IntPtr sel);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial sbyte MsgSendBool(IntPtr receiver, IntPtr sel, IntPtr arg1);

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr dispatch_queue_create(string label, IntPtr attr);

    /// <summary>Force-load the CoreBluetooth framework so its classes are registered.</summary>
    [LibraryImport(LibSystem, EntryPoint = "dlopen", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr DlOpen(string path, int mode);

    private static bool _coreBluetoothLoaded;

    internal static void EnsureCoreBluetoothLoaded()
    {
        if (_coreBluetoothLoaded)
            return;
        if (DlOpen(CoreBluetoothFramework, 2 /* RTLD_NOW */) == IntPtr.Zero)
            throw new PlatformNotSupportedException("CoreBluetooth.framework could not be loaded.");
        _coreBluetoothLoaded = true;
    }

    /// <summary>Create an autoreleased <c>NSString</c> from managed text.</summary>
    internal static IntPtr NSString(string text) =>
        MsgSendUtf8(objc_getClass("NSString"), Sel.StringWithUtf8String, text);

    /// <summary>Read an <c>NSString</c> into managed text (empty for nil).</summary>
    internal static string FromNSString(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero)
            return string.Empty;
        var utf8 = MsgSend(nsString, Sel.Utf8String);
        return utf8 == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(utf8) ?? string.Empty;
    }

    /// <summary>Copy an <c>NSData</c>'s bytes out (empty for nil).</summary>
    internal static byte[] FromNSData(IntPtr nsData)
    {
        if (nsData == IntPtr.Zero)
            return [];
        var length = (int)MsgSendNUInt(nsData, Sel.Length);
        if (length == 0)
            return [];
        var bytes = MsgSend(nsData, Sel.Bytes);
        var result = new byte[length];
        Marshal.Copy(bytes, result, 0, length);
        return result;
    }

    /// <summary>Describe an <c>NSError</c> (empty for nil).</summary>
    internal static string DescribeError(IntPtr nsError) =>
        nsError == IntPtr.Zero ? string.Empty : FromNSString(MsgSend(nsError, Sel.LocalizedDescription));

    /// <summary>Pre-registered selectors used across the transport.</summary>
    internal static class Sel
    {
        internal static readonly IntPtr Alloc = sel_registerName("alloc");
        internal static readonly IntPtr Init = sel_registerName("init");
        internal static readonly IntPtr StringWithUtf8String = sel_registerName("stringWithUTF8String:");
        internal static readonly IntPtr Utf8String = sel_registerName("UTF8String");
        internal static readonly IntPtr Length = sel_registerName("length");
        internal static readonly IntPtr Bytes = sel_registerName("bytes");
        internal static readonly IntPtr Count = sel_registerName("count");
        internal static readonly IntPtr ObjectAtIndex = sel_registerName("objectAtIndex:");
        internal static readonly IntPtr LocalizedDescription = sel_registerName("localizedDescription");
        internal static readonly IntPtr DataWithBytesLength = sel_registerName("dataWithBytes:length:");
        internal static readonly IntPtr InitWithBytesLength = sel_registerName("initWithBytes:length:");
        internal static readonly IntPtr UuidWithString = sel_registerName("UUIDWithString:");
        internal static readonly IntPtr UuidString = sel_registerName("UUIDString");
        internal static readonly IntPtr Identifier = sel_registerName("identifier");
        internal static readonly IntPtr Name = sel_registerName("name");
        internal static readonly IntPtr State = sel_registerName("state");
        internal static readonly IntPtr InitWithDelegateQueue = sel_registerName("initWithDelegate:queue:");
        internal static readonly IntPtr ScanForPeripherals = sel_registerName("scanForPeripheralsWithServices:options:");
        internal static readonly IntPtr StopScan = sel_registerName("stopScan");
        internal static readonly IntPtr ConnectPeripheral = sel_registerName("connectPeripheral:options:");
        internal static readonly IntPtr CancelPeripheralConnection = sel_registerName("cancelPeripheralConnection:");
        internal static readonly IntPtr SetDelegate = sel_registerName("setDelegate:");
        internal static readonly IntPtr DiscoverServices = sel_registerName("discoverServices:");
        internal static readonly IntPtr Services = sel_registerName("services");
        internal static readonly IntPtr DiscoverCharacteristics = sel_registerName("discoverCharacteristics:forService:");
        internal static readonly IntPtr Characteristics = sel_registerName("characteristics");
        internal static readonly IntPtr Uuid = sel_registerName("UUID");
        internal static readonly IntPtr Properties = sel_registerName("properties");
        internal static readonly IntPtr SetNotifyValue = sel_registerName("setNotifyValue:forCharacteristic:");
        internal static readonly IntPtr WriteValue = sel_registerName("writeValue:forCharacteristic:type:");
        internal static readonly IntPtr MaxWriteLength = sel_registerName("maximumWriteValueLengthForType:");
        internal static readonly IntPtr CanSendWriteWithoutResponse = sel_registerName("canSendWriteWithoutResponse");
        internal static readonly IntPtr RespondsToSelector = sel_registerName("respondsToSelector:");
        internal static readonly IntPtr Value = sel_registerName("value");
    }
}
