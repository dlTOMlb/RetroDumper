using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Tests;

/// <summary>
/// PC エンジン Hu カードのシミュレータ。
///
/// 実機と同じく、載っていない範囲は折り返す。上位アドレス線が
/// 繋がっていないぶん番地が丸められ、先頭と同じ内容が見える。
/// Hu カードは容量を申告しないので、容量判定はこの折り返しに頼るしかない。
/// </summary>
public sealed class FakePceCard(byte[] rom) : IRfcaLink
{
    public bool AllowWrites { get; set; }

    public bool AllowSaveWrites { get; set; }

    /// <summary>検証用: バンクレジスタへの書き込み。</summary>
    public List<(uint Address, byte Value)> BankRegisterWrites { get; } = [];

    public RfcaStatus GetStatus() =>
        new([0, 0, 0, 0, 4, 0, 0, 0, (byte)CartridgeKind.PcEngineHuCard, 0, 0, 0]);

    public RfcaStatus GetStatusWithRetry(int attempts = 3) => GetStatus();

    public CartridgeKind DetectCartridge() => CartridgeKind.PcEngineHuCard;

    public byte[] SendControl(uint opcode, uint address = 0, uint size = 0,
                              uint parameter = 0, uint headerField = 0x08) => new byte[8];

    public void WriteBankRegister(CartridgeKind kind, uint opcode, uint address, byte value,
                                  uint headerField = 0x08)
    {
        if (!MapperRegister.IsBankRegister(kind, address))
            throw new RfcaWriteBlockedException($"0x{address:X4} はバンクレジスタではありません");

        // ROM しか持たない普通の Hu カードなので、書いても内容は変わらない。
        BankRegisterWrites.Add((address, value));
    }

    public void WriteSaveMemory(CartridgeKind kind, uint opcode, uint address, ReadOnlySpan<byte> data)
        => throw new RfcaWriteBlockedException("Hu カードにセーブ領域はありません");

    public byte[] Read(uint opcode, uint address, int size, uint headerField = 0x08)
    {
        var buffer = new byte[size];
        Read(opcode, address, buffer.AsSpan(), headerField);
        return buffer;
    }

    public void Read(uint opcode, uint address, Span<byte> destination, uint headerField = 0x08)
    {
        if (opcode != RfcaOpcode.PcEngineRead)
            throw new RfcaNakException($"未対応 opcode 0x{opcode:X2}", new byte[8]);

        for (int i = 0; i < destination.Length; i++)
            destination[i] = rom[(address + i) % rom.Length];
    }

    public void Write(uint opcode, uint address, ReadOnlySpan<byte> data)
        => throw new RfcaWriteBlockedException("この経路では書き込まない");

    public void WriteByte(uint opcode, uint address, byte value)
        => Write(opcode, address, stackalloc byte[] { value });

    public void CompletePendingWrite(int size, byte filler = 0xFF) { }
}
