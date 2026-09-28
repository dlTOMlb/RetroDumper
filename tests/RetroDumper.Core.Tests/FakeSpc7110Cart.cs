using RetroDumper.Core.Snes;
using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Tests;

/// <summary>
/// SPC7110 を積んだカセットのシミュレータ。
///
/// 先頭 4MB はバンク $C0 以降にそのまま並ぶ。
/// それを超える分は $4831 で 1MB の窓を選び、バンク $D0 に現れる。
///
/// **窓の外を読もうとしたら例外にする。**
/// 黙って別の場所を返すと、貼り替えを忘れていても
/// テストが通ってしまう。
/// </summary>
public sealed class FakeSpc7110Cart(byte[] rom) : IRfcaLink
{
    private const uint DirectBank = 0xC0;
    private const uint WindowBank = 0xD0;
    private const long Block = SnesAddressMap.MmcBlockSize;   // 1MB

    public bool AllowWrites { get; set; } = true;

    public bool AllowSaveWrites { get; set; }

    /// <summary>$4831 の値。$D0-$DF にブロック (値 + 1) を貼る。</summary>
    public byte Page => _page1;

    private byte _page1;
    private byte _page2 = 1;
    private byte _page3 = 2;

    /// <summary>検証用: $4831 へ書かれた回数。</summary>
    public int PageWrites { get; private set; }

    /// <summary>検証用: 初期化の並びが送られたか。</summary>
    public bool Initialized { get; private set; }

    private readonly List<(uint Address, byte Value)> _writes = [];

    public RfcaStatus GetStatus() =>
        new([0, 0, 0, 0, 4, 0, 0, 0, (byte)CartridgeKind.SuperFamicom, 0, 0, 0]);

    public RfcaStatus GetStatusWithRetry(int attempts = 3) => GetStatus();

    public CartridgeKind DetectCartridge() => CartridgeKind.SuperFamicom;

    public byte[] SendControl(uint opcode, uint address = 0, uint size = 0,
                              uint parameter = 0, uint headerField = 0x08) => new byte[8];

    public void WriteByte(uint opcode, uint address, byte value)
        => Write(opcode, address, stackalloc byte[] { value });

    public void Write(uint opcode, uint address, ReadOnlySpan<byte> data)
    {
        if (!AllowWrites)
            throw new RfcaWriteBlockedException("書き込み保護が有効です");

        foreach (byte value in data)
            Apply(address, value);
    }

    private void Apply(uint address, byte value)
    {
        _writes.Add((address, value));

        if (address == 0x004831) { _page1 = value; PageWrites++; }
        if (address == 0x004832) _page2 = value;
        if (address == 0x004833) _page3 = value;

        // 初期化の並びの最後。$4834 から始まり $4833 で終わる。
        if (address == 0x004833 && _writes.Any(w => w.Address == 0x004834))
            Initialized = true;
    }

    public void WriteSaveMemory(CartridgeKind kind, uint opcode, uint address, ReadOnlySpan<byte> data)
        => throw new RfcaWriteBlockedException("このテストではセーブを扱わない");

    /// <summary>
    /// 窓の貼り替えはこちらを通る。**書き込み保護の対象外**。
    /// 通してよい番地かは MapperRegister が決めるので、ここでも同じ判定をする。
    /// </summary>
    public void WriteBankRegister(CartridgeKind kind, uint opcode, uint address, byte value,
                                  uint headerField = 0x08)
    {
        if (!MapperRegister.IsBankRegister(kind, address))
            throw new RfcaWriteBlockedException(
                $"0x{address:X6} はバンクレジスタではありません");

        Apply(address, value);
    }

    public byte[] Read(uint opcode, uint address, int size, uint headerField = 0x08)
    {
        var buffer = new byte[size];
        Read(opcode, address, buffer.AsSpan(), headerField);
        return buffer;
    }

    public void Read(uint opcode, uint address, Span<byte> destination, uint headerField = 0x08)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            long at = RomOffset(address + (uint)i);

            destination[i] = at >= 0 && at < rom.Length ? rom[at] : (byte)0xFF;
        }
    }

    /// <summary>
    /// バスアドレス → ROM の位置。
    ///
    /// **$D0 以降は常に窓**。$4831 / $4832 / $4833 がそれぞれ
    /// $D0-$DF / $E0-$EF / $F0-$FF に 1MB を貼る。値 v は
    /// ブロック v+1 を指す。
    ///
    /// 電源投入時の既定値 0 / 1 / 2 だと、ブロック 1 / 2 / 3 が並び、
    /// 固定の $C0-$CF（ブロック 0）と合わせて先頭 4MB が連続して見える。
    /// **だから 4MB までは 1 バイトも書かずに読める。**
    /// </summary>
    private long RomOffset(uint busAddress)
    {
        uint bank = busAddress >> 16;
        uint offset = busAddress & 0xFFFF;

        if (bank < 0xC0)
            throw new RfcaException($"読める範囲の外です: ${bank:X2}:{offset:X4}");

        long block = (bank >> 4) switch
        {
            0xC => 0,                  // 固定
            0xD => _page1 + 1,
            0xE => _page2 + 1,
            0xF => _page3 + 1,
            _ => throw new RfcaException($"読める範囲の外です: ${bank:X2}:{offset:X4}"),
        };

        return block * Block + (bank & 0x0F) * SnesAddressMap.BankSize + offset;
    }

    public void CompletePendingWrite(int size, byte filler = 0xFF) { }
}
