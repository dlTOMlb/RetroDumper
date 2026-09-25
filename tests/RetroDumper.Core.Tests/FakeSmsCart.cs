using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Tests;

/// <summary>
/// マークIII / ゲームギアのカートリッジ RAM のシミュレータ。
///
/// $FFFC で有効化とバンク選択を行い、$8000-$BFFF に 16KB の窓が開く。
/// 載っている容量より小さい窓しか無い場合は折り返す（実機と同じ）。
/// </summary>
public sealed class FakeSmsCart(byte[] save) : IRfcaLink
{
    private const uint ControlRegister = 0xFFFC;
    private const uint WindowBase = 0x8000;
    private const int BankSize = 0x4000;

    public bool AllowWrites { get; set; }

    public bool AllowSaveWrites { get; set; }

    /// <summary>検証用: 最後に $FFFC へ書かれた値。</summary>
    public byte Control { get; private set; }

    public byte[] Snapshot() => save.ToArray();

    public RfcaStatus GetStatus() =>
        new([0, 0, 0, 0, 4, 0, 0, 0, (byte)CartridgeKind.MarkIIIOrGameGear, 0, 0, 0]);

    public RfcaStatus GetStatusWithRetry(int attempts = 3) => GetStatus();

    public CartridgeKind DetectCartridge() => CartridgeKind.MarkIIIOrGameGear;

    public byte[] SendControl(uint opcode, uint address = 0, uint size = 0,
                              uint parameter = 0, uint headerField = 0x08) => new byte[8];

    public void WriteBankRegister(CartridgeKind kind, uint opcode, uint address, byte value,
                                  uint headerField = 0x08)
    {
        if (!MapperRegister.IsBankRegister(kind, address))
            throw new RfcaWriteBlockedException($"0x{address:X4} はバンクレジスタではありません");

        if (address == ControlRegister) Control = value;
    }

    public void WriteSaveMemory(CartridgeKind kind, uint opcode, uint address, ReadOnlySpan<byte> data)
    {
        if (!AllowSaveWrites)
            throw new RfcaWriteBlockedException("セーブデータの書き込みが許可されていません");

        if (!SaveMemory.IsSaveWrite(kind, opcode, address))
            throw new RfcaWriteBlockedException($"0x{address:X4} はセーブデータの領域ではありません");

        if ((Control & 0x08) == 0)
            throw new RfcaException("RAM が有効になっていません");

        for (int i = 0; i < data.Length; i++)
            save[Offset(address + (uint)i)] = data[i];
    }

    public byte[] Read(uint opcode, uint address, int size, uint headerField = 0x08)
    {
        var buffer = new byte[size];
        Read(opcode, address, buffer.AsSpan(), headerField);
        return buffer;
    }

    public void Read(uint opcode, uint address, Span<byte> destination, uint headerField = 0x08)
    {
        if (opcode != RfcaOpcode.SmsRead)
            throw new RfcaNakException($"未対応 opcode 0x{opcode:X2}", new byte[8]);

        if ((Control & 0x08) == 0)
        {
            // 無効のときはバスが浮く。
            destination.Fill(0xFF);
            return;
        }

        for (int i = 0; i < destination.Length; i++)
            destination[i] = save[Offset(address + (uint)i)];
    }

    /// <summary>窓の中の位置と選んだバンクから、セーブ全体での位置を出す。</summary>
    private int Offset(uint address)
    {
        int bank = (Control & 0x04) != 0 ? 1 : 0;
        int at = bank * BankSize + (int)(address - WindowBase);

        return at % save.Length;
    }

    public void Write(uint opcode, uint address, ReadOnlySpan<byte> data)
        => throw new RfcaWriteBlockedException("この経路では書き込まない");

    public void WriteByte(uint opcode, uint address, byte value)
        => Write(opcode, address, stackalloc byte[] { value });

    public void CompletePendingWrite(int size, byte filler = 0xFF) { }
}
