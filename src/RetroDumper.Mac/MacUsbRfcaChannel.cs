using System.Runtime.Versioning;
using RetroDumper.Core.Transport;

namespace RetroDumper.Mac;

/// <summary>
/// macOS で RFCA と話すためのバイト列の通り道。
///
/// **macOS にはこのアダプタのシリアルポートが生えない。**
/// CDC-ACM のクラスコードは名乗っているが、CDC functional descriptor
/// (Header / Call Management / ACM / Union) が 1 つも入っていないため、
/// macOS の ACM ドライバは制御用とデータ用の対応を決められず結合を諦める。
/// 実測: AppleUSBCDCCompositeDevice が !matched のままで、
/// /dev/tty.usbmodem* は作られない。Windows の usbser.sys は対応を
/// 推測して繋ぐので、同じ機器で COM ポートとして見える。
///
/// そこで USB のバルク転送を直に使う。探索は VID/PID なので、
/// **USB ハブ経由でも直結でも同じように見つかる**。
///
/// 実測で確かめた仕様:
///   interface 0 = CDC 制御 (class 2/2/1)、EP 0x83 Interrupt IN 8B
///   interface 1 = CDC データ (class 10)、EP 0x81 Bulk IN 64B / 0x02 Bulk OUT 64B
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacUsbRfcaChannel : IRfcaByteChannel
{
    /// <summary>アダプタの USB ID。実測値。</summary>
    public const int VendorId = 0xF00D;
    public const int ProductId = 0x1337;

    /// <summary>既存のシリアル実装と同じ 115200 8N1。</summary>
    public const uint BaudRate = 115200;

    /// <summary>DTR/RTS を立ててから話し始めるまでの待ち時間。シリアル版と同じ。</summary>
    public const int SettleMilliseconds = 300;

    private IntPtr _device;
    private IntPtr _interface;
    private byte _pipeIn, _pipeOut;
    private bool _disposed;

    public Action<string>? Trace { get; set; }

    public static bool IsPresent() => FindDevice() != 0;

    private static uint FindDevice()
    {
        IntPtr matching = IoKit.IOServiceMatching("IOUSBHostDevice");
        if (matching == IntPtr.Zero) return 0;
        if (IoKit.IOServiceGetMatchingServices(IntPtr.Zero, matching, out uint it) != IoKit.Success)
            return 0;

        uint found = 0;
        for (uint svc; (svc = IoKit.IOIteratorNext(it)) != 0;)
        {
            if (IoKit.RegistryInt(svc, "idVendor") == VendorId &&
                IoKit.RegistryInt(svc, "idProduct") == ProductId)
            {
                found = svc;
                break;
            }
            IoKit.IOObjectRelease(svc);
        }
        IoKit.IOObjectRelease(it);
        return found;
    }

    private static InvalidOperationException Fail(string what, int kr)
        => new($"{what} に失敗しました (kr=0x{(uint)kr:X8}" +
               (((uint)kr) == IoKit.TransactionTimeout ? " : 転送タイムアウト" : "") + ")");

    public void Connect()
    {
        uint svc = FindDevice();
        if (svc == 0)
            throw new InvalidOperationException(
                $"アダプタ (VID=0x{VendorId:X4} PID=0x{ProductId:X4}) が USB に見つかりません。");

        try
        {
            int kr = IoKit.IOCreatePlugInInterfaceForService(
                svc, IoKit.DeviceUserClientTypeId, IoKit.PlugInInterfaceId, out IntPtr plug, out _);
            if (kr != IoKit.Success || plug == IntPtr.Zero) throw Fail("デバイスの PlugIn 生成", kr);

            kr = IoKit.QueryInterface(plug, IoKit.CFUUIDGetUUIDBytes(IoKit.DeviceInterfaceId182), out _device);
            IoKit.IODestroyPlugInInterface(plug);
            if (kr != 0 || _device == IntPtr.Zero) throw Fail("デバイス interface の取得", kr);

            kr = IoKit.DeviceOpen(_device);
            if (kr != IoKit.Success) throw Fail("USBDeviceOpen", kr);
            Trace?.Invoke($"アダプタを開きました (VID=0x{VendorId:X4} PID=0x{ProductId:X4})");

            ConfigureCdc();
            Thread.Sleep(SettleMilliseconds);
            OpenDataInterface();
            DrainInput();
        }
        finally
        {
            IoKit.IOObjectRelease(svc);
        }
    }

    /// <summary>
    /// CDC の回線設定と DTR/RTS。
    ///
    /// **これを送らないとバルク転送に応答が返らない。**
    /// SerialPort を開くとドライバが同じことをしているので、
    /// シリアル版には現れなかった手順。
    /// 実測: これを省くと WritePipeTO の直後の ReadPipeTO が
    /// 転送タイムアウト (0xE0004051) になる。
    /// </summary>
    private void ConfigureCdc()
    {
        // SET_LINE_CODING (0x20): [u32 baud][u8 stopBits][u8 parity][u8 dataBits]
        byte[] coding = new byte[7];
        BitConverter.TryWriteBytes(coding.AsSpan(0, 4), BaudRate);
        coding[4] = 0; // 1 ストップビット
        coding[5] = 0; // パリティなし
        coding[6] = 8; // 8 データビット

        fixed (byte* p = coding)
        {
            var req = new IoKit.DeviceRequestTO
            {
                bmRequestType = 0x21, // ホスト→デバイス / クラス / インターフェース
                bRequest = 0x20,
                wValue = 0,
                wIndex = 0,           // interface 0 = CDC 制御
                wLength = (ushort)coding.Length,
                pData = (IntPtr)p,
                noDataTimeout = 500,
                completionTimeout = 1000,
            };
            int kr = IoKit.DeviceRequest(_device, ref req);
            if (kr != IoKit.Success) throw Fail("SET_LINE_CODING", kr);
        }
        Trace?.Invoke($"SET_LINE_CODING {BaudRate} 8N1");

        // SET_CONTROL_LINE_STATE (0x22): DTR|RTS
        var line = new IoKit.DeviceRequestTO
        {
            bmRequestType = 0x21,
            bRequest = 0x22,
            wValue = 0x0003,
            wIndex = 0,
            wLength = 0,
            pData = IntPtr.Zero,
            noDataTimeout = 500,
            completionTimeout = 1000,
        };
        int kr2 = IoKit.DeviceRequest(_device, ref line);
        if (kr2 != IoKit.Success) throw Fail("SET_CONTROL_LINE_STATE", kr2);
        Trace?.Invoke("SET_CONTROL_LINE_STATE DTR|RTS");
    }

    /// <summary>CDC データ用インターフェース (class 10) を開き、バルクのパイプを見つける。</summary>
    private void OpenDataInterface()
    {
        var req = new IoKit.FindInterfaceRequest
        {
            Class = IoKit.FindInterfaceDontCare,
            SubClass = IoKit.FindInterfaceDontCare,
            Protocol = IoKit.FindInterfaceDontCare,
            AlternateSetting = IoKit.FindInterfaceDontCare,
        };
        if (IoKit.CreateInterfaceIterator(_device, ref req, out uint iter) != IoKit.Success)
            throw new InvalidOperationException("インターフェースの列挙に失敗しました。");

        try
        {
            for (uint intf; (intf = IoKit.IOIteratorNext(iter)) != 0;)
            {
                try
                {
                    if (IoKit.IOCreatePlugInInterfaceForService(
                            intf, IoKit.InterfaceUserClientTypeId, IoKit.PlugInInterfaceId,
                            out IntPtr ip, out _) != IoKit.Success || ip == IntPtr.Zero)
                        continue;

                    int hr = IoKit.QueryInterface(ip, IoKit.CFUUIDGetUUIDBytes(IoKit.InterfaceInterfaceId182),
                                                  out IntPtr cand);
                    IoKit.IODestroyPlugInInterface(ip);
                    if (hr != 0 || cand == IntPtr.Zero) continue;

                    IoKit.GetInterfaceClass(cand, out byte cls);
                    if (cls == 10) { _interface = cand; break; }
                    IoKit.Release(cand);
                }
                finally { IoKit.IOObjectRelease(intf); }
            }
        }
        finally { IoKit.IOObjectRelease(iter); }

        if (_interface == IntPtr.Zero)
            throw new InvalidOperationException("CDC データ用インターフェース (class 10) が見つかりません。");

        int kr = IoKit.InterfaceOpen(_interface);
        if (kr != IoKit.Success) throw Fail("USBInterfaceOpen", kr);

        IoKit.GetNumEndpoints(_interface, out byte n);
        for (byte p = 1; p <= n; p++)
        {
            if (IoKit.GetPipeProperties(_interface, p, out byte dir, out byte num,
                                        out byte tt, out ushort mps, out _) != IoKit.Success)
                continue;
            if (tt != IoKit.TransferTypeBulk) continue;
            if (dir == IoKit.DirectionIn && _pipeIn == 0) { _pipeIn = p; Trace?.Invoke($"Bulk IN  pipe={p} ep={num} max={mps}"); }
            if (dir == IoKit.DirectionOut && _pipeOut == 0) { _pipeOut = p; Trace?.Invoke($"Bulk OUT pipe={p} ep={num} max={mps}"); }
        }
        if (_pipeIn == 0 || _pipeOut == 0)
            throw new InvalidOperationException("バルク転送のパイプが揃いません。");
    }

    /// <summary>残っている受信データを捨てる。シリアル版の DrainInput と同じ役目。</summary>
    public void DrainInput()
    {
        var junk = new byte[64];
        fixed (byte* p = junk)
        {
            for (int i = 0; i < 32; i++)
            {
                uint size = (uint)junk.Length;
                if (IoKit.ReadPipeTO(_interface, _pipeIn, p, ref size, 50, 100) != IoKit.Success) break;
                if (size == 0) break;
                Trace?.Invoke($"残留 {size} バイトを破棄");
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
        {
            int kr = IoKit.WritePipeTO(_interface, _pipeOut, p, (uint)data.Length, 500, 1000);
            if (kr != IoKit.Success)
            {
                Recover(_pipeOut);
                throw Fail("バルク書き込み", kr);
            }
        }
    }

    /// <summary>
    /// 要求した長さが揃うまで読み足し、実際に読めた長さを返す。
    ///
    /// **1 回の ReadPipeTO では揃わない。**
    /// 実測では 12 バイトの状態応答が 8 バイトと 4 バイトに分かれて届いた。
    /// シリアル版の TryReadExact と同じ考えで、揃うまで繰り返す。
    /// </summary>
    public int ReadExact(Span<byte> destination, TimeSpan timeout)
    {
        int total = 0;
        var deadline = DateTime.UtcNow + timeout;

        fixed (byte* basePtr = destination)
        {
            while (total < destination.Length && DateTime.UtcNow < deadline)
            {
                uint size = (uint)(destination.Length - total);
                int kr = IoKit.ReadPipeTO(_interface, _pipeIn, basePtr + total, ref size, 500, 1000);
                if (kr != IoKit.Success)
                {
                    if ((uint)kr == IoKit.TransactionTimeout) break;
                    Recover(_pipeIn);
                    throw Fail("バルク読み出し", kr);
                }
                if (size == 0) break;
                total += (int)size;
            }
        }
        return total;
    }

    /// <summary>タイムアウト後のパイプは使えなくなるので復旧させる。</summary>
    private void Recover(byte pipe)
    {
        IoKit.AbortPipe(_interface, pipe);
        IoKit.ClearPipeStall(_interface, pipe);
        Thread.Sleep(150);
    }

    // ------------------------------------------------------------------
    // IRfcaByteChannel
    // ------------------------------------------------------------------

    /// <summary>画面の一覧に出す名前。シリアルポートではないと分かる形にしてある。</summary>
    public string Name => MacUsbAdapter.EntryName;

    public bool IsOpen => _interface != IntPtr.Zero;

    /// <summary>VID/PID で探すので、ハブ経由でも直結でも同じように見つかる。</summary>
    public bool IsStillPresent => IsPresent();

    public void Open()
    {
        if (!IsOpen) Connect();
    }

    public void Close() => Dispose();

    public void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    /// <summary>
    /// 1 回のバルク読み出し。読めた分だけ返す。
    ///
    /// 待ち時間を過ぎたときは <see cref="TimeoutException"/> を投げる。
    /// SerialPort.Read と同じ約束にしておくことで、
    /// <c>RfcaLink</c> 側の読み足しループがそのまま使える。
    /// </summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        uint size = (uint)count;
        int kr;
        fixed (byte* p = buffer)
            kr = IoKit.ReadPipeTO(_interface, _pipeIn, p + offset, ref size,
                                  (uint)Math.Max(1, ReadTimeout), (uint)Math.Max(1, ReadTimeout));

        if (kr != IoKit.Success)
        {
            if ((uint)kr == IoKit.TransactionTimeout)
            {
                // タイムアウトしたパイプはそのままでは次が通らない。
                Recover(_pipeIn);
                throw new TimeoutException("バルク読み出しが待ち時間を過ぎました。");
            }
            Recover(_pipeIn);
            throw Fail("バルク読み出し", kr);
        }
        return (int)size;
    }

    public int ReadTimeout { get; set; } = 500;

    public int WriteTimeout { get; set; } = 1000;

    /// <summary>
    /// バルク転送に「溜まっている量」という概念が無いので 0 を返す。
    ///
    /// 呼び出し側はこれが 0 なら捨てる処理を省くので、
    /// 捨てるのは <see cref="DiscardInBuffer"/> の側で必ず行う。
    /// </summary>
    public int BytesToRead => 0;

    /// <summary>書き込みは同期的に完了するので、送信待ちは常に無い。</summary>
    public int BytesToWrite => 0;

    public void DiscardInBuffer() => DrainInput();

    /// <summary>送信側に溜める場所が無いので何もしない。</summary>
    public void DiscardOutBuffer() { }

    public string Describe() =>
        $"USB バルク転送 (IN pipe={_pipeIn} / OUT pipe={_pipeOut}, 最大 64 バイト) / " +
        $"読み {ReadTimeout}ms / 書き {WriteTimeout}ms";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_interface != IntPtr.Zero) { IoKit.InterfaceClose(_interface); IoKit.Release(_interface); _interface = IntPtr.Zero; }
        if (_device != IntPtr.Zero) { IoKit.DeviceClose(_device); IoKit.Release(_device); _device = IntPtr.Zero; }
    }
}
