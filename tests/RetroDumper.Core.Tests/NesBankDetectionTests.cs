using RetroDumper.Core.Transport;
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

/// <summary>
/// マッパー 206（Namcot 108）の登録と手順。
///
/// ワルキューレの冒険が使っている石。レジスタの構えは MMC3 と同じだが、
/// PRG は R6/R7 の 2 本だけ、CHR は 2KB×2 + 1KB×4 に分かれている。
/// 連続した 8KB を読むには 6 本すべてを並びが繋がるように設定する。
/// </summary>
public sealed class Namcot108Tests
{
    [Fact]
    public void 総当たりの対象に入っている()
        => Assert.Contains(NesMapper.All, m => m.Number == 206);

    [Fact]
    public void PRGはR6で切り替える()
    {
        var cart = new FakeNesCartridge(new byte[0x8000], []) { AllowWrites = true };
        var mapper = NesMapper.ForNumber(206)!;

        mapper.ReadPrgBank(new NesBus(cart), bank: 3, size: 0x2000, totalBanks: 8);

        Assert.Equal([(0x8000u, (byte)0x06), (0x8001u, (byte)0x03)], cart.BankRegisterWrites);
    }

    /// <summary>CHR は R2 の 1KB 窓を動かして読む。</summary>
    [Fact]
    public void CHRはR2の1KB窓で読む()
    {
        var cart = new FakeNesCartridge(new byte[0x8000], new byte[0x2000]) { AllowWrites = true };
        var mapper = NesMapper.ForNumber(206)!;

        mapper.ReadChrBank(new NesBus(cart), bank: 5, size: 0x400);

        Assert.Equal([(0x8000u, (byte)0x02), (0x8001u, (byte)5)], cart.BankRegisterWrites);

        // 最後のリードは PPU $1000 からの 1KB。
        Assert.Equal(
            (RfcaOpcode.NesPpuRead, 0x1000u, 0x400),
            cart.Reads[^1]);
    }

    /// <summary>
    /// PRG 32KB の基板は切り替わらないので、レジスタを触らずに窓から読むこと。
    ///
    /// CPU の A13/A14 が ROM に直結しているため、R6 に何を書いても
    /// 内容は変わらない。それを知らずに R6 で読むと、どのバンクも
    /// 同じ内容になり、吸い出しが壊れる。
    /// </summary>
    [Fact]
    public void PRG32Kの基板はレジスタを触らない()
    {
        var cart = new FakeNesCartridge(new byte[0x8000], []) { AllowWrites = true };
        var mapper = NesMapper.ForNumber(206)!;
        var bus = new NesBus(cart);

        mapper.ReadPrgBank(bus, bank: 0, size: 0x2000, totalBanks: 4);
        mapper.ReadPrgBank(bus, bank: 3, size: 0x2000, totalBanks: 4);

        Assert.Empty(cart.BankRegisterWrites);

        Assert.Equal(
            [(RfcaOpcode.NesCpuRead, 0x8000u, 0x2000),
             (RfcaOpcode.NesCpuRead, 0xE000u, 0x2000)],
            cart.Reads);
    }
}

/// <summary>
/// 書き込みは、直後に $8000 から 8 バイト読まないと反映されない。
///
/// アダプタは受理応答を返すので、送れていないことが応答からは分からない。
/// 症状は「バンクが切り替わらない」としてだけ現れ、マッパーの選択を
/// 疑う方向へ誘導される。実際 MMC1・UxROM・MMC3 が揃って落ちていた。
///
/// 根拠は参照実装の NesScriptBase.CpuWrite。
/// NesCpuWrite の直後に必ず NesCpuRead(0x8000, 8) を送っている。
/// </summary>
public sealed class NesWritePokeTests
{
    [Fact]
    public void 書き込みの直後に8000から8バイト読む()
    {
        var cart = new FakeNesCartridge(new byte[0x8000], []) { AllowWrites = true };

        new NesBus(cart).CpuWrite(0x8001, 0x42);

        Assert.Equal([(0x8001u, (byte)0x42)], cart.BankRegisterWrites);
        Assert.Equal([(RfcaOpcode.NesCpuRead, 0x8000u, 8)], cart.Reads);
    }

    /// <summary>つつきは書き込みの後に送ること。先に送っても意味がない。</summary>
    [Fact]
    public void つつきは書き込みの後に送る()
    {
        var order = new List<string>();
        var cart = new FakeNesCartridge(new byte[0x8000], []) { AllowWrites = true };

        cart.OnWriteBankRegister = () => order.Add("write");
        cart.OnRead = () => order.Add("read");

        new NesBus(cart).CpuWrite(0x8000, 0x06);

        Assert.Equal(["write", "read"], order);
    }
}
