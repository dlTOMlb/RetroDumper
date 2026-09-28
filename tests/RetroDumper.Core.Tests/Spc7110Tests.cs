using RetroDumper.Core.Dumping;
using RetroDumper.Core.Snes;
using RetroDumper.Core.Transport;
using Xunit;

namespace RetroDumper.Core.Tests;

/// <summary>
/// SPC7110 の吸い出し。
///
/// SFC のバスに一度に見えるのは 4MB まで。
/// それを超える分は、カセット上の SPC7110 が 1MB の窓を
/// バンク $D0 に貼り替えて見せる。
///
/// **実機では確かめていない。**該当カセット（天外魔境ZERO と
/// 少年ジャンプの章の 2 本だけ）が手元に無い。
/// 手順は動作実績のある実装から写したもので、ここで縛っているのは
/// 「写したとおりに動くか」であって「実機で通るか」ではない。
/// </summary>
public sealed class Spc7110Tests
{
    /// <summary>位置で変わる中身。**256 バイト周期にしないこと。**</summary>
    private static byte[] Rom(long size)
    {
        var data = new byte[size];

        for (long i = 0; i < size; i++)
            data[i] = (byte)(i * 31 + (i >> 8) * 7 + (i >> 15) * 61 + 1);

        return data;
    }

    private static byte[] Dump(byte[] rom, FakeSpc7110Cart? cart = null)
    {
        cart ??= new FakeSpc7110Cart(rom);

        var info = new CartridgeInfo
        {
            Kind = CartridgeKind.SuperFamicom,
            Title = "TEST",
            RomSize = rom.Length,
            Mapper = "SPC7110",
            SnesMapping = SnesMapper.Spc7110,
            RomExtension = ".sfc",
            RawHeader = new byte[0x20],
        };

        // 見たいのは吸い出しの経路。ヘッダ解析は他のテストが縛っている。
        var options = new DumpOptions
        {
            ChunkSize = 1024,
            VerifyChecksum = false,
            SnesMapperOverride = SnesMapper.Spc7110,
        };

        return new SnesDumper().Dump(cart, info, options, null, CancellationToken.None).Rom;
    }

    /// <summary>
    /// **書き込み保護を有効にしたままでも 5MB を最後まで読めること。**
    ///
    /// 窓の貼り替えは GB の MBC と同じ揮発性のレジスタ操作で、
    /// ROM もセーブも変わらない。以前はここを通していなかったため、
    /// 天外魔境ZERO を吸い出すのに「書き込みを許可」を外させていた。
    /// </summary>
    [Fact]
    public void 書き込み保護のままでも読める()
    {
        var rom = Rom(5 * 1024 * 1024);
        var cart = new FakeSpc7110Cart(rom) { AllowWrites = false };

        Assert.Equal(rom, Dump(rom, cart));
    }

    /// <summary>
    /// **開けた穴はレジスタだけ**。セーブ RAM の窓は通さない。
    ///
    /// 穴を広げすぎていないことを、番地で直接縛る。
    /// </summary>
    [Theory]
    [InlineData(0x002220u, true)]    // SA-1 MMC
    [InlineData(0x002223u, true)]
    [InlineData(0x004804u, true)]    // S-DD1 MMC
    [InlineData(0x004807u, true)]
    [InlineData(0x004830u, true)]    // SPC7110
    [InlineData(0x004834u, true)]
    [InlineData(0x00221Fu, false)]   // 境界のすぐ外
    [InlineData(0x002224u, false)]
    [InlineData(0x004835u, false)]
    [InlineData(0x700000u, false)]   // LoROM のセーブ RAM
    [InlineData(0xF00000u, false)]   // その書き込み側
    [InlineData(0x306000u, false)]   // HiROM のセーブ RAM
    [InlineData(0x400000u, false)]   // SA-1 の BW-RAM
    [InlineData(0x000000u, false)]   // ROM 領域
    public void 通すのはレジスタだけ(uint address, bool expected)
        => Assert.Equal(expected,
            MapperRegister.IsBankRegister(CartridgeKind.SuperFamicom, address));

    /// <summary>
    /// **レジスタとセーブ RAM が重ならないこと。**
    ///
    /// 片方だけ直すと、セーブの実体をレジスタと誤認して
    /// 保護下で書けてしまう。両者を突き合わせて縛る。
    /// </summary>
    [Fact]
    public void レジスタはセーブ領域と重ならない()
    {
        foreach (uint address in new uint[]
                 {
                     0x002220, 0x002221, 0x002222, 0x002223,
                     0x004804, 0x004805, 0x004806, 0x004807,
                     0x004830, 0x004831, 0x004832, 0x004833, 0x004834,
                 })
        {
            Assert.True(MapperRegister.IsBankRegister(CartridgeKind.SuperFamicom, address));
            Assert.False(SaveMemory.IsSnesSram(address),
                $"0x{address:X6} がセーブ領域と重なっています");
        }
    }

    /// <summary>4MB までは貼り替えずに読める。書き込みも起きない。</summary>
    [Fact]
    public void 四メガバイトまでは書き込まずに読める()
    {
        var rom = Rom(4 * 1024 * 1024);
        var cart = new FakeSpc7110Cart(rom) { AllowWrites = false };

        Assert.Equal(rom, Dump(rom, cart));
        Assert.Equal(0, cart.PageWrites);
        Assert.False(cart.Initialized);
    }

    /// <summary>
    /// **5MB を最後まで読めること。**天外魔境ZERO がこの大きさ。
    /// 以前は 4MB で切り詰めていた。
    /// </summary>
    [Fact]
    public void 五メガバイトを最後まで読める()
    {
        var rom = Rom(5 * 1024 * 1024);

        Assert.Equal(rom, Dump(rom));
    }

    [Fact]
    public void 上限の八メガバイトまで読める()
    {
        var rom = Rom(8 * 1024 * 1024);

        Assert.Equal(rom, Dump(rom));
    }

    /// <summary>4MB を超えるときは、先に初期化の並びを送ること。</summary>
    [Fact]
    public void 超えるときは初期化してから読む()
    {
        var rom = Rom(5 * 1024 * 1024);
        var cart = new FakeSpc7110Cart(rom);

        Dump(rom, cart);

        Assert.True(cart.Initialized);
    }

    /// <summary>
    /// **窓の貼り替えは 1MB ごとに 1 回**。32KB ごとに書き直さない。
    ///
    /// 5MB のときの $4831 への書き込みは 3 回:
    ///   1. 初期化の並びの中で 0
    ///   2. 4MB を超えた最初のバンクで 3
    ///   3. 読み終わりに既定へ戻して 0
    ///
    /// 残り 31 バンクは同じ窓なので書き直さない。
    /// ここが 1MB ごとでなく 32KB ごとになると、書き込みが 160 回に増える。
    /// </summary>
    [Fact]
    public void 貼り替えは一メガバイトごと()
    {
        var rom = Rom(5 * 1024 * 1024);
        var cart = new FakeSpc7110Cart(rom);

        Dump(rom, cart);

        Assert.Equal(3, cart.PageWrites);
    }

    /// <summary>読み終わったら既定へ戻すこと。貼ったまま放置しない。</summary>
    [Fact]
    public void 終わったら窓を戻す()
    {
        var rom = Rom(5 * 1024 * 1024);
        var cart = new FakeSpc7110Cart(rom);

        Dump(rom, cart);

        Assert.Equal(0, cart.Page);
    }

    /// <summary>バンク番号とバスアドレスの対応。参照実装の式と一致すること。</summary>
    [Theory]
    [InlineData(0, 0xC00000u, 0)]
    [InlineData(1, 0xC08000u, 0)]
    [InlineData(127, 0xFF8000u, 0)]     // 直接見える最後
    [InlineData(128, 0xD00000u, 3)]     // 貼り替えの最初
    [InlineData(159, 0xDF8000u, 3)]     // 1 枚目の最後
    [InlineData(160, 0xD00000u, 4)]     // 2 枚目の先頭
    [InlineData(255, 0xDF8000u, 6)]     // 8MB の最後
    public void バンクの割り付け(int bank, uint address, byte page)
    {
        Assert.Equal(address, SnesAddressMap.Spc7110BusAddress(bank));
        Assert.Equal(page, SnesAddressMap.Spc7110PageFor(bank));
    }
}
