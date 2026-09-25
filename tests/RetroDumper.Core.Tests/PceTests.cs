using RetroDumper.Core.Dumping;
using RetroDumper.Core.Pce;
using RetroDumper.Core.Transport;
using Xunit;

namespace RetroDumper.Core.Tests;

/// <summary>
/// Hu カードのバンク配置。
///
/// Hu カードはヘッダを持たず、容量も配置も申告しない。
/// 先頭 8KB が別の番地でも同じ内容に見えるかで見分けるしかない。
/// </summary>
public sealed class PceMapperTests
{
    [Fact]
    public void リニアは素直に並ぶ()
    {
        Assert.Equal(0u, PceMapper.BankAddress(PceMapping.Linear, 0));
        Assert.Equal(0x8000u, PceMapper.BankAddress(PceMapping.Linear, 1));
        Assert.Equal(0x40000u, PceMapper.BankAddress(PceMapping.Linear, 8));
    }

    /// <summary>
    /// インターバルは 256KB を越えたところで 256KB ぶん飛ばす。
    /// 飛ばさずに読むと、空きを ROM の一部として取り込んでしまう。
    /// </summary>
    [Theory]
    [InlineData(0, 0x00000u)]
    [InlineData(7, 0x38000u)]      // 256KB の直前
    [InlineData(8, 0x80000u)]      // 256KB ぶん飛ぶ
    [InlineData(9, 0x88000u)]
    public void インターバルは256KBの後ろを飛ばす(int bank, uint expected)
        => Assert.Equal(expected, PceMapper.BankAddress(PceMapping.Interval, bank));

    /// <summary>SF2 は 32 バンク目以降を 16 バンクの窓へ畳む。</summary>
    [Theory]
    [InlineData(0, 0x00000u)]
    [InlineData(31, 0xF8000u)]
    [InlineData(32, 0x80000u)]     // 窓の先頭へ戻る
    [InlineData(33, 0x88000u)]
    public void SF2は窓へ畳んで読む(int bank, uint expected)
        => Assert.Equal(expected, PceMapper.BankAddress(PceMapping.Sf2Dash, bank));

    /// <summary>SF2 だけがレジスタの切り替えを伴う。</summary>
    [Fact]
    public void SF2以外はレジスタを触らない()
    {
        Assert.Null(PceMapper.Sf2Register(PceMapping.Linear, 5));
        Assert.Null(PceMapper.Sf2Register(PceMapping.Interval, 5));
        Assert.Equal(0x1FF0u, PceMapper.Sf2Register(PceMapping.Sf2Dash, 5));
        Assert.Equal(0x1FF1u, PceMapper.Sf2Register(PceMapping.Sf2Dash, 32));
    }

    /// <summary>
    /// SF2 のレジスタだけを、バンク切り替えとして書ける範囲にすること。
    /// ROM 領域へ書けてしまうと、ほかの Hu カードで事故になる。
    /// </summary>
    [Theory]
    [InlineData(0x1FF0u, true)]
    [InlineData(0x1FF7u, true)]
    [InlineData(0x1FEFu, false)]
    [InlineData(0x1FF8u, false)]
    [InlineData(0x0000u, false)]
    public void SF2のレジスタだけ書ける(uint address, bool expected)
        => Assert.Equal(expected,
            MapperRegister.IsBankRegister(CartridgeKind.PcEngineHuCard, address));
}

/// <summary>
/// Hu カードの吸い出し。配置の見分けと、折り返しによる容量の実測。
/// </summary>
public sealed class PceDumperTests
{
    /// <summary>先頭 8KB が $40000 と違えばリニア。</summary>
    [Fact]
    public void 先頭と256KBが違えばリニア()
    {
        var cart = new FakePceCard(Pattern(512 * 1024));

        Assert.Equal(PceMapping.Linear, PceDumper.DetectMapping(cart));
    }

    /// <summary>
    /// 256KB のカードは $40000 も $80000 も先頭と同じに見える。
    /// 折り返しているだけなのでリニア。
    /// </summary>
    [Fact]
    public void 折り返していればリニア()
    {
        var cart = new FakePceCard(Pattern(256 * 1024));

        Assert.Equal(PceMapping.Linear, PceDumper.DetectMapping(cart));
    }

    [Fact]
    public void 吸い出した容量が実際の容量と一致する()
    {
        var rom = Pattern(256 * 1024);
        var cart = new FakePceCard(rom);
        var dumper = new PceDumper();

        var info = dumper.Identify(cart, new DumpOptions());
        var result = dumper.Dump(cart, info, new DumpOptions(), null, CancellationToken.None);

        Assert.Equal(rom, result.Rom);
    }

    /// <summary>
    /// 128KB ぶんがすべて同じ内容なら、32KB のカードが折り返して見えているだけ。
    /// 4 倍の大きさで保存してしまわないこと。
    /// </summary>
    [Fact]
    public void 小さいカードは折り返しぶんを含めずに保存する()
    {
        var rom = Pattern(32 * 1024);
        var cart = new FakePceCard(rom);
        var dumper = new PceDumper();

        var info = dumper.Identify(cart, new DumpOptions());
        var result = dumper.Dump(cart, info, new DumpOptions(), null, CancellationToken.None);

        Assert.Equal(rom, result.Rom);
    }

    /// <summary>Hu カードはタイトルを持たないので、空のまま返すこと。</summary>
    [Fact]
    public void タイトルは持たない()
    {
        var cart = new FakePceCard(Pattern(256 * 1024));
        var info = new PceDumper().Identify(cart, new DumpOptions());

        Assert.Equal("", info.Title);
        Assert.Equal(".pce", info.RomExtension);
    }

    private static byte[] Pattern(int size)
    {
        var data = new byte[size];

        for (int i = 0; i < size; i++)
            data[i] = (byte)(i * 31 + (i >> 8) * 7 + (i >> 16) * 13 + 1);

        return data;
    }
}
