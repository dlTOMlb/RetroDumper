using RetroDumper.Core.Dumping;
using RetroDumper.Core.Gb;
using RetroDumper.Core.Transport;
using Xunit;

namespace RetroDumper.Core.Tests;

/// <summary>
/// ROM のバンク切り替えが、MBC ごとに正しい番地へ行くこと。
///
/// 【実際にあった取りこぼし】
/// ポケットカメラ (0xFC) と HuC3 (0xFE) に分岐が無く、MBC1 用の手順に
/// 落ちていた。MBC1 は $2000 に下位 5bit、$4000 に上位 2bit を書き分けるが、
/// この 2 つは $4000 が RAM バンク選択なので上位が入らない。
/// 効くのは $2000 の 5bit だけで、バンク 31（512KB）より先は
/// 前半の繰り返しになって出てくる。
/// ポケットカメラは 1MB、HuC3 のソフトも 1MB 以上ある。
///
/// HuC1 (0xFF) も同じ取りこぼしをしていた。こちらは実機で確かめてある。
/// ポケモンカードGB (HuC1 / 1MB) が No-Intro に一致せず、
/// バンク 32 = バンク 0、33 = 1、…、63 = 31 になっていた（2026-10-01 実機）。
/// $2000 に 7bit を書くようにしたら一致した（CRC32 1926F570）。
///
/// セーブ側は <see cref="GbSave"/> で MBC3 と同じ段取りに揃えてあり、
/// ROM 側だけが揃っていなかった。
///
/// **HuC3 とポケットカメラは実機で確かめていない。**この 2 つについて
/// ここで固定しているのは「MBC3 と同じ番地へ書く」という段取りだけ。
/// </summary>
public sealed class GbRomBankTests
{
    private const byte Mbc1 = 0x01;
    private const byte Mbc3 = 0x13;
    private const byte Mbc5 = 0x19;
    private const byte PocketCamera = 0xFC;
    private const byte HuC3 = 0xFE;
    private const byte HuC1 = 0xFF;

    /// <summary>1MB。ポケットカメラの容量で、5bit では足りない。</summary>
    private const int BanksFor1Mb = 64;

    private static readonly DumpOptions Options = new()
    {
        IncludeSaveRam = false,
        VerifyChecksum = false,
    };

    /// <summary>
    /// バンクごとに違う模様を入れた ROM。
    ///
    /// **一様な値や単純な連番にしない。**同じバンクを 2 回読んでも気付けない。
    /// バンク番号を混ぜ、256 バイトで一周しないようにしてある。
    /// </summary>
    private static byte[] BuildRom(int banks, byte cartType)
    {
        var rom = new byte[banks * FakeGbCartridge.BankSize];

        for (int b = 0; b < banks; b++)
            for (int i = 0; i < FakeGbCartridge.BankSize; i++)
                rom[b * FakeGbCartridge.BankSize + i] =
                    (byte)((b * 131 + i * 7 + (i >> 8) * 29) & 0xFF);

        foreach (var (i, c) in "TESTROM".Select((c, i) => (i, c)))
            rom[0x134 + i] = (byte)c;

        rom[0x147] = cartType;
        rom[0x148] = SizeCode(banks);
        rom[0x149] = 0x00;                      // RAM なし。ここでは ROM だけを見る

        byte sum = 0;
        for (int i = 0x134; i <= 0x14C; i++) sum = (byte)(sum - rom[i] - 1);
        rom[0x14D] = sum;

        return rom;
    }

    /// <summary>ヘッダ 0x148 の ROM サイズ欄。32KB &lt;&lt; code がバイト数になる。</summary>
    private static byte SizeCode(int banks)
    {
        byte code = 0;
        while (2 << code < banks) code++;
        return code;
    }

    private static (byte[] Rom, FakeGbCartridge Cart, byte[] Dumped) Dump(int banks, byte cartType)
    {
        var rom = BuildRom(banks, cartType);
        var cart = new FakeGbCartridge(rom) { AllowWrites = false };

        var dumper = new GbDumper();
        var info = dumper.Identify(cart, Options);
        var result = dumper.Dump(cart, info, Options, null, CancellationToken.None);

        return (rom, cart, result.Rom);
    }

    /// <summary>
    /// 512KB を超える分も正しく読めること。
    /// 分岐が無かったときは、バンク 32 以降がバンク 1 の内容になっていた。
    /// </summary>
    [Theory]
    [InlineData(PocketCamera)]
    [InlineData(HuC3)]
    [InlineData(HuC1)]
    [InlineData(Mbc3)]
    [InlineData(Mbc5)]
    public void 一MBのカセットは全バンク読める(byte cartType)
    {
        var (rom, _, dumped) = Dump(BanksFor1Mb, cartType);
        Assert.Equal(rom, dumped);
    }

    /// <summary>
    /// HuC1 / HuC3 / ポケットカメラは MBC3 と同じく $2000 の 1 本だけで選ぶ。
    /// $4000 は RAM バンク選択なので、ROM を読むために書く必要が無い。
    ///
    /// **ここが緩むと 5bit に落ちる。**$4000 へ上位 2bit を書く MBC1 の手順に
    /// 戻すと、バンク 32 以降が化ける（HuC1 は実機で確認済み）。
    /// </summary>
    [Theory]
    [InlineData(PocketCamera)]
    [InlineData(HuC3)]
    [InlineData(HuC1)]
    public void HuC系とポケットカメラはROMバンクを2000だけで選ぶ(byte cartType)
    {
        var (_, cart, _) = Dump(BanksFor1Mb, cartType);

        Assert.NotEmpty(cart.BankRegisterWrites);
        Assert.All(cart.BankRegisterWrites, w =>
            Assert.True(w.Address is >= 0x2000 and <= 0x2FFF,
                $"0x{w.Address:X4} へ書いています。MBC3 と同じなら $2000 だけです"));
    }

    /// <summary>
    /// MBC1 は自分の手順のまま（$6000 → $4000 → $2000）。
    ///
    /// HuC1 を MBC3 側へ移したときに、MBC1 まで一緒に動かさないための留め。
    /// 市販の MBC1 は 512KB までなので、下位 5bit と上位 2bit で足りている。
    /// 実機で確認したカセットはカエルの為に鐘は鳴る。
    /// </summary>
    [Fact]
    public void MBC1は自分の手順のまま()
    {
        var (rom, cart, dumped) = Dump(32, Mbc1);          // 512KB

        Assert.Equal(rom, dumped);
        Assert.Contains(cart.BankRegisterWrites, w => w.Address == 0x6000);
        Assert.Contains(cart.BankRegisterWrites, w => w.Address == 0x4000);
    }

    /// <summary>
    /// どの MBC でも、書くのはバンクレジスタの範囲だけ。
    /// セーブ領域 ($A000-$BFFF) へは触れない。
    /// </summary>
    [Theory]
    [InlineData(PocketCamera)]
    [InlineData(HuC3)]
    [InlineData(HuC1)]
    [InlineData(Mbc1)]
    [InlineData(Mbc3)]
    [InlineData(Mbc5)]
    public void 書くのはバンクレジスタの範囲だけ(byte cartType)
    {
        var (_, cart, _) = Dump(BanksFor1Mb, cartType);

        Assert.Empty(cart.Writes);
        Assert.All(cart.BankRegisterWrites, w =>
        {
            Assert.True(MapperRegister.IsBankRegister(CartridgeKind.GameBoy, w.Address),
                $"0x{w.Address:X4} はバンクレジスタの範囲外です");
            Assert.False(MapperRegister.IsSaveMemory(CartridgeKind.GameBoy, w.Address),
                $"0x{w.Address:X4} はセーブ領域です");
        });
    }
}
