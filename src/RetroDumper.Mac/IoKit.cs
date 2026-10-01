using System.Runtime.InteropServices;

namespace RetroDumper.Mac;

/// <summary>
/// IOKit の USB ユーザ空間 API。
///
/// IOKit の interface は COM 風で、構造体の先頭から関数ポインタが並ぶ。
/// 呼び出しは (*p)->Fn(p, ...) の形なので、p は「ポインタのポインタ」であり、
/// 第 1 引数には p 自身を渡す。
///
/// vtable の添字は SDK のヘッダ
/// (IOKit.framework/Headers/usb/IOUSBLib.h) の宣言順から求めた。
/// </summary>
internal static unsafe class IoKit
{
    private const string IOKitLib = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public const int Success = 0;
    public const uint TransactionTimeout = 0xE0004051;
    public const ushort FindInterfaceDontCare = 0xFFFF;
    public const byte DirectionOut = 0, DirectionIn = 1;
    public const byte TransferTypeBulk = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct CFUUIDBytes
    {
        public byte b0, b1, b2, b3, b4, b5, b6, b7, b8, b9, b10, b11, b12, b13, b14, b15;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FindInterfaceRequest
    {
        public ushort Class, SubClass, Protocol, AlternateSetting;
    }

    /// <summary>IOUSBDevRequestTO。制御要求 1 件ぶん。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceRequestTO
    {
        public byte bmRequestType;
        public byte bRequest;
        public ushort wValue;
        public ushort wIndex;
        public ushort wLength;
        public IntPtr pData;
        public uint wLenDone;
        public uint noDataTimeout;
        public uint completionTimeout;
    }

    [DllImport(IOKitLib)]
    public static extern IntPtr IOServiceMatching([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(IOKitLib)]
    public static extern int IOServiceGetMatchingServices(IntPtr mainPort, IntPtr matching, out uint iterator);

    [DllImport(IOKitLib)]
    public static extern uint IOIteratorNext(uint iterator);

    [DllImport(IOKitLib)]
    public static extern int IOObjectRelease(uint obj);

    [DllImport(IOKitLib)]
    public static extern IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);

    [DllImport(IOKitLib)]
    public static extern int IOCreatePlugInInterfaceForService(
        uint service, IntPtr pluginType, IntPtr interfaceType, out IntPtr theInterface, out int theScore);

    [DllImport(IOKitLib)]
    public static extern int IODestroyPlugInInterface(IntPtr theInterface);

    [DllImport(CoreFoundationLib)]
    public static extern IntPtr CFUUIDGetConstantUUIDWithBytes(IntPtr alloc,
        byte b0, byte b1, byte b2, byte b3, byte b4, byte b5, byte b6, byte b7,
        byte b8, byte b9, byte b10, byte b11, byte b12, byte b13, byte b14, byte b15);

    [DllImport(CoreFoundationLib)]
    public static extern CFUUIDBytes CFUUIDGetUUIDBytes(IntPtr uuid);

    [DllImport(CoreFoundationLib)]
    public static extern IntPtr CFStringCreateWithCString(IntPtr alloc,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string cStr, uint encoding);

    [DllImport(CoreFoundationLib)]
    public static extern void CFRelease(IntPtr cf);

    [DllImport(CoreFoundationLib)]
    public static extern bool CFNumberGetValue(IntPtr number, long theType, out int valuePtr);

    // ---- ヘッダから求めた UUID ----
    public static IntPtr DeviceUserClientTypeId => CFUUIDGetConstantUUIDWithBytes(IntPtr.Zero,
        0x9D, 0xC7, 0xB7, 0x80, 0x9E, 0xC0, 0x11, 0xD4, 0xA5, 0x4F, 0x00, 0x0A, 0x27, 0x05, 0x28, 0x61);

    public static IntPtr InterfaceUserClientTypeId => CFUUIDGetConstantUUIDWithBytes(IntPtr.Zero,
        0x2D, 0x97, 0x86, 0xC6, 0x9E, 0xF3, 0x11, 0xD4, 0xAD, 0x51, 0x00, 0x0A, 0x27, 0x05, 0x28, 0x61);

    public static IntPtr DeviceInterfaceId182 => CFUUIDGetConstantUUIDWithBytes(IntPtr.Zero,
        0x15, 0x2F, 0xC4, 0x96, 0x48, 0x91, 0x11, 0xD5, 0x9D, 0x52, 0x00, 0x0A, 0x27, 0x80, 0x1E, 0x86);

    public static IntPtr InterfaceInterfaceId182 => CFUUIDGetConstantUUIDWithBytes(IntPtr.Zero,
        0x49, 0x23, 0xAC, 0x4C, 0x48, 0x96, 0x11, 0xD5, 0x92, 0x08, 0x00, 0x0A, 0x27, 0x80, 0x1E, 0x86);

    public static IntPtr PlugInInterfaceId => CFUUIDGetConstantUUIDWithBytes(IntPtr.Zero,
        0xC2, 0x44, 0xE8, 0x58, 0x10, 0x9C, 0x11, 0xD4, 0x91, 0xD4, 0x00, 0x50, 0xE4, 0xC6, 0x42, 0x6F);

    /// <summary>(*p)->vtable[index] を関数ポインタとして取り出す。</summary>
    private static IntPtr Fn(IntPtr p, int index) => ((IntPtr*)*(IntPtr**)p)[index];

    public static int QueryInterface(IntPtr p, CFUUIDBytes iid, out IntPtr result)
    {
        var fn = (delegate* unmanaged[Cdecl]<IntPtr, CFUUIDBytes, IntPtr*, int>)Fn(p, 1);
        IntPtr outp;
        int hr = fn(p, iid, &outp);
        result = outp;
        return hr;
    }

    public static int Release(IntPtr p)
        => ((delegate* unmanaged[Cdecl]<IntPtr, int>)Fn(p, 3))(p);

    // ---- デバイス側 (IOUSBDeviceInterface182) ----
    public static int DeviceOpen(IntPtr p)
        => ((delegate* unmanaged[Cdecl]<IntPtr, int>)Fn(p, 8))(p);

    public static int DeviceClose(IntPtr p)
        => ((delegate* unmanaged[Cdecl]<IntPtr, int>)Fn(p, 9))(p);

    public static int CreateInterfaceIterator(IntPtr p, ref FindInterfaceRequest req, out uint iterator)
    {
        var fn = (delegate* unmanaged[Cdecl]<IntPtr, FindInterfaceRequest*, uint*, int>)Fn(p, 28);
        uint it;
        int kr;
        fixed (FindInterfaceRequest* rp = &req) kr = fn(p, rp, &it);
        iterator = it;
        return kr;
    }

    public static int DeviceRequest(IntPtr p, ref DeviceRequestTO req)
    {
        var fn = (delegate* unmanaged[Cdecl]<IntPtr, DeviceRequestTO*, int>)Fn(p, 30);
        fixed (DeviceRequestTO* rp = &req) return fn(p, rp);
    }

    // ---- インターフェース側 (IOUSBInterfaceInterface182) ----
    public static int InterfaceOpen(IntPtr p)
        => ((delegate* unmanaged[Cdecl]<IntPtr, int>)Fn(p, 8))(p);

    public static int InterfaceClose(IntPtr p)
        => ((delegate* unmanaged[Cdecl]<IntPtr, int>)Fn(p, 9))(p);

    public static int GetInterfaceClass(IntPtr p, out byte cls)
    {
        var fn = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)Fn(p, 10);
        byte c; int kr = fn(p, &c); cls = c; return kr;
    }

    public static int GetNumEndpoints(IntPtr p, out byte n)
    {
        var fn = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)Fn(p, 19);
        byte v; int kr = fn(p, &v); n = v; return kr;
    }

    public static int GetPipeProperties(IntPtr p, byte pipe,
        out byte direction, out byte number, out byte transferType, out ushort maxPacket, out byte interval)
    {
        var fn = (delegate* unmanaged[Cdecl]<IntPtr, byte, byte*, byte*, byte*, ushort*, byte*, int>)Fn(p, 26);
        byte d, num, tt, iv; ushort mps;
        int kr = fn(p, pipe, &d, &num, &tt, &mps, &iv);
        direction = d; number = num; transferType = tt; maxPacket = mps; interval = iv;
        return kr;
    }

    public static int AbortPipe(IntPtr p, byte pipe)
        => ((delegate* unmanaged[Cdecl]<IntPtr, byte, int>)Fn(p, 28))(p, pipe);

    public static int ClearPipeStall(IntPtr p, byte pipe)
        => ((delegate* unmanaged[Cdecl]<IntPtr, byte, int>)Fn(p, 30))(p, pipe);

    public static int ReadPipeTO(IntPtr p, byte pipe, byte* buf, ref uint size,
                                 uint noDataTimeout, uint completionTimeout)
    {
        var fn = (delegate* unmanaged[Cdecl]<IntPtr, byte, byte*, uint*, uint, uint, int>)Fn(p, 39);
        uint s = size;
        int kr = fn(p, pipe, buf, &s, noDataTimeout, completionTimeout);
        size = s;
        return kr;
    }

    public static int WritePipeTO(IntPtr p, byte pipe, byte* buf, uint size,
                                  uint noDataTimeout, uint completionTimeout)
    {
        var fn = (delegate* unmanaged[Cdecl]<IntPtr, byte, byte*, uint, uint, uint, int>)Fn(p, 40);
        return fn(p, pipe, buf, size, noDataTimeout, completionTimeout);
    }

    /// <summary>レジストリの整数プロパティを読む。無ければ -1。</summary>
    public static int RegistryInt(uint entry, string key)
    {
        IntPtr k = CFStringCreateWithCString(IntPtr.Zero, key, 0x08000100 /* UTF8 */);
        IntPtr v = IORegistryEntryCreateCFProperty(entry, k, IntPtr.Zero, 0);
        CFRelease(k);
        if (v == IntPtr.Zero) return -1;
        bool ok = CFNumberGetValue(v, 9 /* kCFNumberIntType */, out int result);
        CFRelease(v);
        return ok ? result : -1;
    }
}
