using RetroDumper.Core.Dumping;
using RetroDumper.Core.Gb;
using RetroDumper.Core.Transport;
using Xunit;

namespace RetroDumper.Core.Tests;

/// <summary>
/// ゲームボーイは「カートリッジへの書き込みを禁止」のまま吸い出せること。
///
/// 【実際に起きた不具合】
/// 書き込み保護を有効にしたまま GB を吸い出そうとすると失敗していた。
///
/// 原因は保護の粒度。MBC のバンク切り替えは**揮発性のレジスタ**への書き込みで、
/// カセットの内容は一切変わらない。それを、セーブデータやフラッシュを
/// 壊さないための「内容の書き換え禁止」と同じ扱いにしていた。
///
/// バンクを選べなければ 32KB より大きいカセットは読めないので、
/// 保護したまま吸い出すことが原理的に不可能になっていた。
/// </summary>
public sealed class GbWriteProtectionTests
{
    /// <summary>バンクごとに違う内容を入れた ROM。取り違えれば検出できる。</summary>
    private static byte[] BuildRom(int banks)
    {
        var rom = new byte[banks * FakeGbCartridge.BankSize];

        for (int b = 0; b < banks; b++)
            for (int i = 0; i < FakeGbCartridge.BankSize; i++)
                rom[b * FakeGbCartridge.BankSize + i] = (byte)((b * 7 + i) & 0xFF);

        // ヘッダ: タイトル / カートリッジ種別 MBC5 / ROM サイズ / RAM なし
        foreach (var (i, c) in "TESTROM".Select((c, i) => (i, c)))
            rom[0x134 + i] = (byte)c;

        rom[0x147] = 0x19;                       // MBC5
        rom[0x148] = (byte)(banks switch         // ROM サイズ欄
        {
            <= 2 => 0x00,
            <= 4 => 0x01,
            <= 8 => 0x02,
            <= 16 => 0x03,
            _ => 0x04,
        });
        rom[0x149] = 0x00;                       // RAM なし

        // ヘッダチェックサム ($134-$14C)
        byte sum = 0;
        for (int i = 0x134; i <= 0x14C; i++) sum = (byte)(sum - rom[i] - 1);
        rom[0x14D] = sum;

        return rom;
    }

    private static readonly DumpOptions Protected = new()
    {
        IncludeSaveRam = false,
        VerifyChecksum = false,
    };

    [Fact]
    public void 書き込み禁止のままでも吸い出せる()
    {
        var rom = BuildRom(16);                      // 256KB
        var cart = new FakeGbCartridge(rom) { AllowWrites = false };

        var dumper = new GbDumper();
        var info = dumper.Identify(cart, Protected);
        var result = dumper.Dump(cart, info, Protected, null, CancellationToken.None);

        Assert.Equal(rom, result.Rom);
    }

    [Fact]
    public void 吸い出しても内容を書き換えるライトは一件も起きない()
    {
        var cart = new FakeGbCartridge(BuildRom(16)) { AllowWrites = false };

        var dumper = new GbDumper();
        var info = dumper.Identify(cart, Protected);
        dumper.Dump(cart, info, Protected, null, CancellationToken.None);

        Assert.Empty(cart.Writes);
        Assert.NotEmpty(cart.BankRegisterWrites);
    }

    [Fact]
    public void バンク切り替えはレジスタ範囲にしか書かない()
    {
        var cart = new FakeGbCartridge(BuildRom(16)) { AllowWrites = false };

        var dumper = new GbDumper();
        var info = dumper.Identify(cart, Protected);
        dumper.Dump(cart, info, Protected, null, CancellationToken.None);

        Assert.All(cart.BankRegisterWrites, w =>
        {
            Assert.True(MapperRegister.IsBankRegister(CartridgeKind.GameBoy, w.Address),
                $"0x{w.Address:X4} はバンクレジスタの範囲外です");
            Assert.False(MapperRegister.IsSaveMemory(CartridgeKind.GameBoy, w.Address),
                $"0x{w.Address:X4} はセーブ領域です");
        });
    }

    /// <summary>32KB 以下はバンク切り替え自体が要らない。</summary>
    [Fact]
    public void 三十二KBのカセットはバンク切り替えなしで読める()
    {
        var rom = BuildRom(2);
        var cart = new FakeGbCartridge(rom) { AllowWrites = false };

        var dumper = new GbDumper();
        var info = dumper.Identify(cart, Protected);
        var result = dumper.Dump(cart, info, Protected, null, CancellationToken.None);

        Assert.Equal(rom, result.Rom);
    }
}

/// <summary>
/// バンク切り替えレジスタの範囲判定。
/// ここが緩むとセーブデータを壊しうるので、境界を固定しておく。
/// </summary>
public sealed class MapperRegisterTests
{
    [Theory]
    [InlineData(0x0000)]   // RAM 有効・無効
    [InlineData(0x2000)]   // ROM バンク
    [InlineData(0x3000)]   // ROM バンク上位 (MBC5)
    [InlineData(0x4000)]   // RAM バンク / ROM バンク上位
    [InlineData(0x6000)]   // バンクモード
    [InlineData(0x7FFF)]   // 上限
    public void GBのレジスタ範囲は許可される(int address)
        => Assert.True(MapperRegister.IsBankRegister(CartridgeKind.GameBoy, (uint)address));

    [Theory]
    [InlineData(0xA000)]   // 外部 RAM の先頭
    [InlineData(0xB000)]
    [InlineData(0xBFFF)]   // 外部 RAM の末尾
    public void GBのセーブ領域は拒否される(int address)
    {
        Assert.False(MapperRegister.IsBankRegister(CartridgeKind.GameBoy, (uint)address));
        Assert.True(MapperRegister.IsSaveMemory(CartridgeKind.GameBoy, (uint)address));
    }

    /// <summary>
    /// 利用者の絶対条件。GBA には何があっても書き込まない。
    /// バンク切り替えの仕組みからも GBA を完全に除外する。
    /// </summary>
    [Theory]
    [InlineData(0x0000)]
    [InlineData(0x2000)]
    [InlineData(0x8000)]
    [InlineData(0xFFFF)]
    public void GBAはどのアドレスでも拒否される(int address)
        => Assert.False(
            MapperRegister.IsBankRegister(CartridgeKind.GameBoyAdvance, (uint)address));

    [Theory]
    [InlineData(0xFFFC)]
    [InlineData(0xFFFF)]
    public void マークIIIのレジスタ範囲は許可される(int address)
        => Assert.True(
            MapperRegister.IsBankRegister(CartridgeKind.MarkIIIOrGameGear, (uint)address));

    [Theory]
    [InlineData(0x0000)]
    [InlineData(0x8000)]
    [InlineData(0xFFFB)]
    public void マークIIIの範囲外は拒否される(int address)
        => Assert.False(
            MapperRegister.IsBankRegister(CartridgeKind.MarkIIIOrGameGear, (uint)address));

    [Fact]
    public void SFCはバンクレジスタの対象外()
        => Assert.False(MapperRegister.IsBankRegister(CartridgeKind.SuperFamicom, 0x2000));
}
