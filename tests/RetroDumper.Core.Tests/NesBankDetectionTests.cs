using RetroDumper.Core.Dumping;
using RetroDumper.Core.Nes;
using Xunit;

namespace RetroDumper.Core.Tests;

/// <summary>
/// バンクの比較は**バンク全体**で行うこと。
///
/// 容量判定もマッパーの絞り込みも「2 つのバンクが同じ内容か」で決まる。
/// 以前は 16KB 読んでおきながら先頭 256 バイトしか比べていなかった。
/// ファミコンのバンクは先頭が 0xFF などの埋め草で始まるものが多く、
/// 中身の違うバンク同士が先頭だけ一致する。そのせいで
/// 容量を半分と誤り、正しいマッパーを「切り替わらない」と捨てていた。
///
/// 読み出し自体は元から全バンク分行っているので、
/// 比較を広げても通信量は増えない。
/// </summary>
public sealed class NesBankDetectionTests
{
    private const int Bank = 0x4000;

    /// <summary>先頭が同じ埋め草で始まり、中身は違う 2 バンクの PRG。</summary>
    private static byte[] PrgWithSharedPrefix()
    {
        var prg = new byte[2 * Bank];

        for (int bank = 0; bank < 2; bank++)
            for (int i = 0; i < Bank; i++)
                prg[bank * Bank + i] = i < 256
                    ? (byte)0xFF                                  // 共通の埋め草
                    : (byte)(i * 7 + bank * 0x5B + bank);         // バンクごとに違う

        return prg;
    }

    [Fact]
    public void 先頭が同じ埋め草でも32KBと判定される()
    {
        var cart = new FakeNesCartridge(PrgWithSharedPrefix(), Varied(0x2000));

        var options = new DumpOptions { NesMapperOverride = 0 };
        var dumper = new NesDumper();
        var info = dumper.Identify(cart, options);

        var result = dumper.Dump(cart, info, options, null, CancellationToken.None);

        // iNES ヘッダ 16 + PRG 32KB + CHR 8KB
        Assert.Equal(16 + 2 * Bank + 0x2000, result.Rom.Length);
        Assert.Equal(2, result.Rom[4]);        // PRG は 16KB 単位で 2
    }

    /// <summary>折り返しは今までどおり検出できること（畳みすぎない）。</summary>
    [Fact]
    public void 本当に折り返しているなら16KBのまま()
    {
        var cart = new FakeNesCartridge(Varied(Bank), Varied(0x2000));

        var options = new DumpOptions { NesMapperOverride = 0 };
        var dumper = new NesDumper();
        var info = dumper.Identify(cart, options);

        var result = dumper.Dump(cart, info, options, null, CancellationToken.None);

        Assert.Equal(16 + Bank + 0x2000, result.Rom.Length);
        Assert.Equal(1, result.Rom[4]);
    }

    /// <summary>
    /// UxROM の容量判定は、実際にバンクを切り替えて行うこと。
    ///
    /// UxROM は「最終バンクは $C000 に固定なので切り替えない」と実装されている。
    /// 探索時に総バンク数として bank + 1 を渡すと、**どのバンクを要求しても
    /// 最終バンク扱い**になり、常に同じ $C000 の内容が返る。
    /// それを折り返しと読み違えて、容量を下限に張り付かせていた。
    /// </summary>
    [Fact]
    public void UxROMの容量判定はバンクを切り替えて行う()
    {
        const int banks = 8;                                   // 128KB
        var prg = new byte[banks * Bank];

        for (int bank = 0; bank < banks; bank++)
            for (int i = 0; i < Bank; i++)
                prg[bank * Bank + i] = (byte)(i + bank * 0x11);

        var cart = new FakeNesCartridge(prg, []) { UxRomBanking = true, AllowWrites = true };

        var options = new DumpOptions { NesMapperOverride = 2 };
        var dumper = new NesDumper();
        var info = dumper.Identify(cart, options);

        var result = dumper.Dump(cart, info, options, null, CancellationToken.None);

        Assert.Equal(16 + banks * Bank, result.Rom.Length);
        Assert.Equal(banks, result.Rom[4]);
        Assert.Equal(0, result.Rom[5]);        // UxROM は CHR-RAM
        Assert.Equal(prg, result.Rom[16..]);
    }

    private static byte[] Varied(int size)
    {
        var data = new byte[size];

        for (int i = 0; i < size; i++)
            data[i] = (byte)(i * 31 + (i >> 8) * 7 + 1);

        return data;
    }
}
