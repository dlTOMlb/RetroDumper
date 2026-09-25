using RetroDumper.Core.Gb;
using RetroDumper.Core.Gba;
using RetroDumper.Core.Sms;
using Xunit;

namespace RetroDumper.Core.Tests;

/// <summary>
/// セーブの消去。全面を同じ値で埋める。
///
/// 埋める値は利用者が選ぶ。既定は 0xFF。
/// フラッシュと EEPROM は 0xFF が消去済みの状態そのもので、
/// 0x00 を書くのは全ビットを焼くことになり、消去の逆になる。
/// SRAM・FRAM には消去状態が無いので、どちらでも等価。
///
/// 一様なデータは、位置がずれても内容が同じに見える。
/// 途中までしか書けていない取りこぼしを、
/// 内容の比較では見つけられないということ。
/// ここでは「全部が埋まったか」を長さごと確かめる。
/// </summary>
public sealed class SaveEraseTests
{
    private const byte Mbc3WithRam = 0x13;
    private const byte Mbc2WithRam = 0x06;

    /// <summary>消したあとに何か書き込まれていた痕跡が残っていないこと。</summary>
    private static void AssertAllAre(byte expected, byte[] actual, int length)
    {
        Assert.Equal(length, actual.Length);

        for (int i = 0; i < actual.Length; i++)
            if (actual[i] != expected)
                Assert.Fail($"{i:X4} 番地が 0x{actual[i]:X2} で、0x{expected:X2} になっていません");
    }

    [Theory]
    [InlineData((byte)0xFF)]
    [InlineData((byte)0x00)]
    public void GBAのSRAMを全面埋められる(byte filler)
    {
        var cart = new FakeGbaSaveCartridge(32768) { AllowSaveWrites = true };
        var blank = new byte[32768];
        Array.Fill(blank, filler);

        GbaSave.Write(cart, GbaSaveType.Sram, blank);

        AssertAllAre(filler, cart.Snapshot(), 32768);
    }

    [Theory]
    [InlineData((byte)0xFF)]
    [InlineData((byte)0x00)]
    public void GBのセーブを全面埋められる(byte filler)
    {
        var cart = new FakeGbSaveCartridge(8192, Mbc3WithRam) { AllowSaveWrites = true };
        var blank = new byte[8192];
        Array.Fill(blank, filler);

        GbSave.Write(cart, Mbc3WithRam, blank);

        AssertAllAre(filler, cart.Snapshot(), 8192);
    }

    /// <summary>
    /// **MBC2 に 0x00 を書いても 0x00 にはならない。**
    ///
    /// MBC2 の RAM は 4bit しかなく、上位は存在しない。
    /// 書き込み側で 0xF0 を埋めてから送るので、
    /// 消したあとに読めるのは 0xF0 になる。
    /// 意味のある下位 4bit は 0 で、ゲームからは消えて見える。
    /// </summary>
    [Fact]
    public void MBC2は0x00で消すと0xF0になる()
    {
        var cart = new FakeGbSaveCartridge(512, Mbc2WithRam) { AllowSaveWrites = true };

        GbSave.Write(cart, Mbc2WithRam, new byte[512]);

        AssertAllAre(0xF0, cart.Snapshot(), 512);
    }

    [Theory]
    [InlineData((byte)0xFF)]
    [InlineData((byte)0x00)]
    public void マークIIIのセーブを全面埋められる(byte filler)
    {
        var cart = new FakeSmsCart(new byte[0x8000]) { AllowSaveWrites = true };
        var blank = new byte[0x8000];
        Array.Fill(blank, filler);

        SmsSave.Write(cart, blank);

        AssertAllAre(filler, cart.Snapshot(), 0x8000);
    }

    /// <summary>
    /// **消す大きさは、読み戻せた長さに合わせること。**
    ///
    /// マークIII / ゲームギアは容量を申告しない。
    /// 上限の 32KB を決め打ちで書きにいくと、8KB しか載っていない
    /// カートリッジでは折り返して同じ場所を 4 回なぞることになる。
    /// 読んでから、その長さだけ消す。
    /// </summary>
    [Fact]
    public void 載っている分だけ消せる()
    {
        var cart = new FakeSmsCart(new byte[0x2000]) { AllowSaveWrites = true };

        int size = SmsSave.Read(cart).Length;

        Assert.Equal(0x2000, size);

        var blank = new byte[size];
        Array.Fill(blank, (byte)0xFF);

        SmsSave.Write(cart, blank);

        AssertAllAre(0xFF, cart.Snapshot(), 0x2000);
    }

    /// <summary>許可していなければ消せないこと。消去も書き込みの一種。</summary>
    [Fact]
    public void 許可していなければ消せない()
    {
        var cart = new FakeGbaSaveCartridge(32768);
        var blank = new byte[32768];
        Array.Fill(blank, (byte)0xFF);

        Assert.Throws<RetroDumper.Core.Transport.RfcaWriteBlockedException>(
            () => GbaSave.Write(cart, GbaSaveType.Sram, blank));
    }
}
