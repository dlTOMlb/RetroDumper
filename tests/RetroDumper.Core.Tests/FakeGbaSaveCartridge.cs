using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Tests;

/// <summary>
/// GBA のセーブ装置のシミュレータ。
///
/// セーブ専用の opcode だけを受け付け、それ以外の書き込みは
/// 実機と同じく届かないものとして扱う。
/// ROM 側へ書けてしまわないことを確かめるために使う。
/// </summary>
public sealed class FakeGbaSaveCartridge(int saveSize) : IRfcaLink
{
    private readonly byte[] _save = new byte[saveSize];

    public bool AllowWrites { get; set; }

    public bool AllowSaveWrites { get; set; }

    /// <summary>検証用: 読み戻しで 1 バイトだけ化けさせる。照合の確認に使う。</summary>
    public int CorruptAt { get; set; } = -1;

    /// <summary>
    /// 検証用: 読むたびに値が変わる位置。実機で起きた読み出しの揺れを模す。
    /// </summary>
    public int UnstableAt { get; set; } = -1;

    private byte _jitter;

    public byte[] Snapshot() => _save.ToArray();

    public void Preset(byte[] data) => data.CopyTo(_save, 0);

    public RfcaStatus GetStatus() =>
        new([0, 0, 0, 0, 4, 0, 0, 0, (byte)CartridgeKind.GameBoyAdvance, 0, 0, 0]);

    public RfcaStatus GetStatusWithRetry(int attempts = 3) => GetStatus();

    public CartridgeKind DetectCartridge() => CartridgeKind.GameBoyAdvance;

    public byte[] SendControl(uint opcode, uint address = 0, uint size = 0,
                              uint parameter = 0, uint headerField = 0x08) => new byte[8];

    public int ReadGbaFlashId() => 0x1B32;      // 対応表にある 512K の石

    public void WriteSaveMemory(CartridgeKind kind, uint opcode, uint address, ReadOnlySpan<byte> data)
    {
        if (!AllowSaveWrites)
            throw new RfcaWriteBlockedException("セーブデータの書き込みが許可されていません");

        if (!SaveMemory.IsSaveWrite(kind, opcode, address))
            throw new RfcaWriteBlockedException(
                $"opcode 0x{opcode:X2} はセーブデータの領域ではありません");

        data.CopyTo(_save.AsSpan((int)address));
    }

    public void WriteBankRegister(CartridgeKind kind, uint opcode, uint address, byte value,
                                  uint headerField = 0x08)
        => throw new RfcaWriteBlockedException("GBA にバンクレジスタはありません");

    public byte[] Read(uint opcode, uint address, int size, uint headerField = 0x08)
    {
        var buffer = new byte[size];
        Read(opcode, address, buffer.AsSpan(), headerField);
        return buffer;
    }

    public void Read(uint opcode, uint address, Span<byte> destination, uint headerField = 0x08)
    {
        if (!SaveMemory.IsDedicatedSaveOpcode(CartridgeKind.GameBoyAdvance, opcode))
            throw new RfcaNakException($"未対応 opcode 0x{opcode:X2}", new byte[8]);

        _save.AsSpan((int)address, destination.Length).CopyTo(destination);

        if (CorruptAt >= address && CorruptAt < address + destination.Length)
            destination[(int)(CorruptAt - address)] ^= 0xFF;

        if (UnstableAt >= address && UnstableAt < address + destination.Length)
            destination[(int)(UnstableAt - address)] ^= _jitter++ % 2 == 0 ? (byte)0x00 : (byte)0xFF;
    }

    public void Write(uint opcode, uint address, ReadOnlySpan<byte> data)
        => throw new RfcaWriteBlockedException("この経路では書き込まない");

    public void WriteByte(uint opcode, uint address, byte value)
        => Write(opcode, address, stackalloc byte[] { value });

    public void CompletePendingWrite(int size, byte filler = 0xFF) { }
}
