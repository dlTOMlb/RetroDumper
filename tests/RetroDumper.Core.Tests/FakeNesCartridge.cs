using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Tests;

/// <summary>
/// ファミコンのカートリッジ（NROM）のシミュレータ。
///
/// 実機と同じく、**ROM に載っていないバンク番号は折り返す**。
/// 上位アドレス線が繋がっていないぶん番号が丸められ、
/// 先頭のバンクと同じ内容が見える。
/// ファミコンのカセットは容量を申告しないので、
/// 容量判定はこの折り返しを利用するしかない。
///
/// NROM は書き込みを一切必要としないので、
/// 内容を書き換えるライトが起きないことも確かめられる。
/// </summary>
public sealed class FakeNesCartridge : IRfcaLink
{
    private readonly byte[] _prg;
    private readonly byte[] _chr;

    public FakeNesCartridge(byte[] prg, byte[] chr)
    {
        _prg = prg;
        _chr = chr;
    }

    public bool AllowWrites { get; set; }

    /// <summary>
    /// UxROM 相当のバンク切り替えを行うか。
    ///
    /// $8000-$BFFF はラッチで選んだバンク、$C000-$FFFF は最終バンク固定。
    /// ラッチに載らない上位ビットは折り返す（実機と同じ）。
    /// </summary>
    public bool UxRomBanking { get; init; }

    private int _latch;

    /// <summary>検証用: 内容を書き換えるライト。NROM では 1 件も起きてはいけない。</summary>
    public List<(uint Opcode, uint Address, byte[] Data)> Writes { get; } = [];

    /// <summary>検証用: マッパーのラッチへの書き込み。</summary>
    public List<(uint Address, byte Value)> BankRegisterWrites { get; } = [];

    public RfcaStatus GetStatus() =>
        new([0, 0, 0, 0, 4, 0, 0, 0, (byte)CartridgeKind.Famicom, 0, 0, 0]);

    public RfcaStatus GetStatusWithRetry(int attempts = 3) => GetStatus();

    public CartridgeKind DetectCartridge() => CartridgeKind.Famicom;

    public byte[] SendControl(uint opcode, uint address = 0, uint size = 0,
                              uint parameter = 0, uint headerField = 0x08) => new byte[8];

    public void WriteBankRegister(CartridgeKind kind, uint opcode, uint address, byte value,
                                  uint headerField = 0x08)
    {
        if (MapperRegister.IsSaveMemory(kind, address))
            throw new RfcaWriteBlockedException($"0x{address:X4} はセーブ領域です");

        if (!MapperRegister.IsBankRegister(kind, address))
            throw new RfcaWriteBlockedException($"0x{address:X4} はバンクレジスタではありません");

        BankRegisterWrites.Add((address, value));

        if (UxRomBanking && address >= 0x8000) _latch = value;
    }

    public byte[] Read(uint opcode, uint address, int size, uint headerField = 0x08)
    {
        var buffer = new byte[size];
        Read(opcode, address, buffer.AsSpan(), headerField);
        return buffer;
    }

    public void Read(uint opcode, uint address, Span<byte> destination, uint headerField = 0x08)
    {
        if (opcode == RfcaOpcode.NesPpuRead)
        {
            Fill(destination, _chr, address, 0x2000);
            return;
        }

        if (opcode != RfcaOpcode.NesCpuRead)
            throw new RfcaNakException($"未対応 opcode 0x{opcode:X2}", new byte[8]);

        if (address < 0x8000)
        {
            // WRAM 領域。搭載していないので開放バス。
            destination.Fill(0xFF);
            return;
        }

        if (UxRomBanking && _prg.Length > 0)
        {
            int banks = _prg.Length / 0x4000;

            for (int i = 0; i < destination.Length; i++)
            {
                uint at = address + (uint)i;
                int bank = at < 0xC000 ? _latch % banks : banks - 1;

                destination[i] = _prg[bank * 0x4000 + (int)(at & 0x3FFF)];
            }

            return;
        }

        // CPU $8000-$FFFF の 32KB 窓。ROM が小さければ折り返す。
        Fill(destination, _prg, address - 0x8000, 0x8000);
    }

    /// <summary>窓の中のオフセットを ROM 長で折り返して読む。</summary>
    private static void Fill(Span<byte> destination, byte[] rom, uint offset, int windowSize)
    {
        if (rom.Length == 0) { destination.Fill(0xFF); return; }

        for (int i = 0; i < destination.Length; i++)
        {
            long at = (offset + i) % windowSize;
            destination[i] = rom[at % rom.Length];
        }
    }

    public void Write(uint opcode, uint address, ReadOnlySpan<byte> data)
    {
        if (!AllowWrites) throw new RfcaWriteBlockedException("書き込み保護");
        Writes.Add((opcode, address, data.ToArray()));
    }

    public void WriteByte(uint opcode, uint address, byte value)
        => Write(opcode, address, stackalloc byte[] { value });

    public void CompletePendingWrite(int size, byte filler = 0xFF)
        => throw new RfcaWriteBlockedException("使用しません");
}
