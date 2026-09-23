using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Tests;

/// <summary>
/// ゲームボーイのカートリッジ（MBC 付き）のシミュレータ。
///
/// 実機と同じく、バンク切り替えレジスタへ書かないと
/// $4000-$7FFF から 2 本目以降のバンクが読めない。
/// バンクを選ばずに読むと、常にバンク 1 が見える。
///
/// **レジスタへの書き込みはカセットの内容を変えない。**
/// 電源を切れば消える揮発性の値であり、
/// セーブ領域 ($A000-$BFFF) への書き込みとは性質が違う。
/// この区別が付いていなかったため、書き込み保護を有効にしたまま
/// GB を吸い出せない不具合が出ていた。
/// </summary>
public sealed class FakeGbCartridge : IRfcaLink
{
    public const int BankSize = 0x4000;

    private readonly byte[] _rom;
    private int _romBank = 1;

    public FakeGbCartridge(byte[] rom) => _rom = rom;

    public bool AllowWrites { get; set; }

    public bool AllowSaveWrites { get; set; }

    /// <summary>検証用: セーブ領域への書き込み。</summary>
    public List<(uint Opcode, uint Address, byte[] Data)> SaveWrites { get; } = [];

    public void WriteSaveMemory(CartridgeKind kind, uint opcode, uint address, ReadOnlySpan<byte> data)
    {
        if (!AllowSaveWrites)
            throw new RfcaWriteBlockedException("セーブデータの書き込みが許可されていません");

        if (!SaveMemory.IsSaveWrite(kind, opcode, address))
            throw new RfcaWriteBlockedException(
                $"opcode 0x{opcode:X2} アドレス 0x{address:X6} はセーブデータの領域ではありません");

        SaveWrites.Add((opcode, address, data.ToArray()));
    }

    /// <summary>検証用: バンク切り替えレジスタへの書き込み。</summary>
    public List<(uint Address, byte Value)> BankRegisterWrites { get; } = [];

    /// <summary>検証用: 内容を書き換える通常のライト。GB でも 1 件も起きてはいけない。</summary>
    public List<(uint Opcode, uint Address, byte[] Data)> Writes { get; } = [];

    public RfcaStatus GetStatus() =>
        new([0, 0, 0, 0, 4, 0, 0, 0, (byte)CartridgeKind.GameBoy, 0, 0, 0]);

    public RfcaStatus GetStatusWithRetry(int attempts = 3) => GetStatus();

    public CartridgeKind DetectCartridge() => CartridgeKind.GameBoy;

    public byte[] SendControl(uint opcode, uint address = 0, uint size = 0,
                              uint parameter = 0, uint headerField = 0x08) => new byte[8];

    public void WriteBankRegister(CartridgeKind kind, uint opcode, uint address, byte value,
                                  uint headerField = 0x08)
    {
        if (kind != CartridgeKind.GameBoy)
            throw new RfcaWriteBlockedException($"{kind} はこのカセットではありません");

        if (MapperRegister.IsSaveMemory(kind, address))
            throw new RfcaWriteBlockedException($"0x{address:X4} はセーブ領域です");

        if (!MapperRegister.IsBankRegister(kind, address))
            throw new RfcaWriteBlockedException($"0x{address:X4} はバンクレジスタではありません");

        BankRegisterWrites.Add((address, value));

        // MBC5 の ROM バンク下位 8bit。テストで使うのはこれだけ。
        if (address is >= 0x2000 and <= 0x2FFF) _romBank = value == 0 ? 1 : value;
        else if (address is >= 0x3000 and <= 0x3FFF) _romBank = (_romBank & 0xFF) | (value << 8);
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
            uint at = address + (uint)i;

            // $0000-$3FFF は常にバンク 0。$4000-$7FFF が切り替え対象。
            long offset = at < BankSize
                ? at
                : (long)_romBank * BankSize + (at - BankSize);

            destination[i] = offset < _rom.Length ? _rom[offset] : (byte)0xFF;
        }
    }

    public void Write(uint opcode, uint address, ReadOnlySpan<byte> data)
    {
        if (!AllowWrites)
            throw new RfcaWriteBlockedException("書き込み保護");

        Writes.Add((opcode, address, data.ToArray()));
    }

    public void WriteByte(uint opcode, uint address, byte value)
        => Write(opcode, address, stackalloc byte[] { value });

    public void CompletePendingWrite(int size, byte filler = 0xFF)
        => throw new RfcaWriteBlockedException("使用しません");
}
