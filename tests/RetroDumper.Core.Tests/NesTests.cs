using RetroDumper.Core.Nes;
using RetroDumper.Core.Transport;
using Xunit;

namespace RetroDumper.Core.Tests;

/// <summary>
/// iNES ヘッダの組み立て。
/// エミュレータはこのヘッダだけを見てマッパーと容量を判断するので、
/// ここが違うと吸い出しが正しくてもソフトが動かない。
/// </summary>
public sealed class INesHeaderTests
{
    [Fact]
    public void 先頭はNESマジックで始まる()
    {
        var file = NesDumper.BuildINesFile(0, new byte[0x8000], new byte[0x2000]);

        Assert.Equal((byte)'N', file[0]);
        Assert.Equal((byte)'E', file[1]);
        Assert.Equal((byte)'S', file[2]);
        Assert.Equal(0x1A, file[3]);
    }

    [Fact]
    public void PRGは16KB単位CHRは8KB単位で記録される()
    {
        var file = NesDumper.BuildINesFile(0, new byte[0x8000], new byte[0x2000]);

        Assert.Equal(2, file[4]);   // 32KB / 16KB
        Assert.Equal(1, file[5]);   //  8KB /  8KB
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(66)]
    public void マッパー番号が上下ニブルに分かれて入る(int mapper)
    {
        var file = NesDumper.BuildINesFile(mapper, new byte[0x4000], []);

        int decoded = (file[6] >> 4) | (file[7] & 0xF0);
        Assert.Equal(mapper, decoded);
    }

    [Fact]
    public void ヘッダのあとにPRGCHRの順で並ぶ()
    {
        var prg = new byte[0x4000];
        var chr = new byte[0x2000];
        prg[0] = 0xAA; prg[^1] = 0xBB;
        chr[0] = 0xCC; chr[^1] = 0xDD;

        var file = NesDumper.BuildINesFile(0, prg, chr);

        Assert.Equal(16 + prg.Length + chr.Length, file.Length);
        Assert.Equal(0xAA, file[16]);
        Assert.Equal(0xBB, file[16 + prg.Length - 1]);
        Assert.Equal(0xCC, file[16 + prg.Length]);
        Assert.Equal(0xDD, file[^1]);
    }

    [Fact]
    public void CHRRAMのカセットはCHR0として記録される()
    {
        var file = NesDumper.BuildINesFile(2, new byte[0x20000], []);

        Assert.Equal(0, file[5]);
        Assert.Equal(16 + 0x20000, file.Length);
    }
}

/// <summary>
/// ファミコンのバンクレジスタ範囲。
///
/// $8000-$FFFF は PRG-ROM が見えている領域だが、ROM は読み出し専用なので
/// 書き込みはマッパーのラッチに入るだけで内容は変わらない。
/// 一方 $6000-$7FFF はバッテリーバックアップ WRAM で、
/// ここへ書くとセーブデータが壊れる。
/// </summary>
public sealed class NesMapperRegisterTests
{
    [Theory]
    [InlineData(0x8000)]
    [InlineData(0x8001)]
    [InlineData(0xA000)]
    [InlineData(0xC000)]
    [InlineData(0xE000)]
    [InlineData(0xFFFF)]
    public void マッパーのラッチ範囲は許可される(int address)
        => Assert.True(MapperRegister.IsBankRegister(CartridgeKind.Famicom, (uint)address));

    [Theory]
    [InlineData(0x6000)]
    [InlineData(0x7000)]
    [InlineData(0x7FFF)]
    public void セーブ用WRAMは拒否される(int address)
    {
        Assert.False(MapperRegister.IsBankRegister(CartridgeKind.Famicom, (uint)address));
        Assert.True(MapperRegister.IsSaveMemory(CartridgeKind.Famicom, (uint)address));
    }

    [Fact]
    public void 範囲より下は拒否される()
        => Assert.False(MapperRegister.IsBankRegister(CartridgeKind.Famicom, 0x5FFF));
}

/// <summary>対応マッパーの登録内容。</summary>
public sealed class NesMapperRegistryTests
{
    [Theory]
    [InlineData(0, "NROM")]
    [InlineData(1, "MMC1")]
    [InlineData(2, "UxROM")]
    [InlineData(3, "CNROM")]
    [InlineData(4, "MMC3")]
    public void 番号から引ける(int number, string name)
        => Assert.Equal(name, NesMapper.ForNumber(number)?.Name);

    [Fact]
    public void 未対応の番号はnullを返す()
        => Assert.Null(NesMapper.ForNumber(999));

    /// <summary>UxROM は CHR-RAM なので CHR-ROM を吸い出さない。</summary>
    [Fact]
    public void UxROMはCHRROMを持たない()
        => Assert.Equal(0, NesMapper.ForNumber(2)!.ChrBankSize);

    /// <summary>MMC3 は PRG 8KB / CHR 1KB 単位。</summary>
    [Fact]
    public void MMC3のバンク単位()
    {
        var mmc3 = NesMapper.ForNumber(4)!;
        Assert.Equal(0x2000, mmc3.PrgBankSize);
        Assert.Equal(0x0400, mmc3.ChrBankSize);
    }
}
