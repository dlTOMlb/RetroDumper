using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Tests;

/// <summary>
/// ゲームボーイの外部 RAM（セーブ）のシミュレータ。
///
/// 実機と同じく、$0000-$1FFF に 0x0A を書くまで外部 RAM は読めない。
/// 有効化とバンク切り替えの手順を守っているかを、これで確かめる。
/// </summary>
public sealed class FakeGbSaveCartridge(int saveSize, byte cartType) : IRfcaLink
{
    private readonly byte[] _save = new byte[saveSize];

    private bool _ramEnabled;
    private int _bank;

    public bool AllowWrites { get; set; }

    public bool AllowSaveWrites { get; set; }

    /// <summary>検証用: 最後に外部 RAM が有効のままになっていないか。</summary>
    public bool RamLeftEnabled => _ramEnabled;

    /// <summary>検証用: MBC1 のモード切り替えに書かれた値の並び。</summary>
    public List<byte> ModeWrites { get; } = [];

    public byte[] Snapshot() => _save.ToArray();

    public void Preset(byte[] data) => data.CopyTo(_save, 0);

    public RfcaStatus GetStatus() =>
        new([0, 0, 0, 0, 4, 0, 0, 0, (byte)CartridgeKind.GameBoy, 0, 0, 0]);

    public RfcaStatus GetStatusWithRetry(int attempts = 3) => GetStatus();

    public CartridgeKind DetectCartridge() => CartridgeKind.GameBoy;

    public byte[] SendControl(uint opcode, uint address = 0, uint size = 0,
                              uint parameter = 0, uint headerField = 0x08) => new byte[8];

    public void WriteBankRegister(CartridgeKind kind, uint opcode, uint address, byte value,
                                  uint headerField = 0x08)
    {
        if (!MapperRegister.IsBankRegister(kind, address))
            throw new RfcaWriteBlockedException($"0x{address:X4} はバンクレジスタではありません");

        if (address <= 0x1FFF) _ramEnabled = value == 0x0A;
        else if (address is >= 0x4000 and <= 0x5FFF) _bank = value;
        else if (address is >= 0x6000 and <= 0x7FFF) ModeWrites.Add(value);
    }

    public void WriteSaveMemory(CartridgeKind kind, uint opcode, uint address, ReadOnlySpan<byte> data)
    {
        if (!AllowSaveWrites)
            throw new RfcaWriteBlockedException("セーブデータの書き込みが許可されていません");

        if (!SaveMemory.IsSaveWrite(kind, opcode, address))
            throw new RfcaWriteBlockedException(
                $"opcode 0x{opcode:X2} アドレス 0x{address:X4} はセーブデータの領域ではありません");

        if (!_ramEnabled)
            throw new RfcaException("外部 RAM が有効になっていません");

        data.CopyTo(_save.AsSpan(Offset(address)));
    }

    public byte[] Read(uint opcode, uint address, int size, uint headerField = 0x08)
    {
        var buffer = new byte[size];
        Read(opcode, address, buffer.AsSpan(), headerField);
        return buffer;
    }

    public void Read(uint opcode, uint address, Span<byte> destination, uint headerField = 0x08)
    {
        if (address < 0xA000)
            throw new RfcaNakException("セーブ領域以外は用意していません", new byte[8]);

        if (!_ramEnabled)
        {
            // 実機では無効時は 0xFF しか返らない。
            destination.Fill(0xFF);
            return;
        }

        _save.AsSpan(Offset(address), destination.Length).CopyTo(destination);
    }

    /// <summary>バンクと窓の中の位置から、セーブ全体での位置を出す。</summary>
    private int Offset(uint address)
    {
        int bankSize = GbSaveBankSize();
        return _bank * bankSize + (int)(address - 0xA000);
    }

    private int GbSaveBankSize() => cartType is 0x05 or 0x06 ? 512 : 0x2000;

    public void Write(uint opcode, uint address, ReadOnlySpan<byte> data)
        => throw new RfcaWriteBlockedException("この経路では書き込まない");

    public void WriteByte(uint opcode, uint address, byte value)
        => Write(opcode, address, stackalloc byte[] { value });

    public void CompletePendingWrite(int size, byte filler = 0xFF) { }
}
