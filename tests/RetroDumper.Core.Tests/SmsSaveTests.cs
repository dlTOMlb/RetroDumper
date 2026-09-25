using RetroDumper.Core.Sms;
using RetroDumper.Core.Transport;
using Xunit;

namespace RetroDumper.Core.Tests;

/// <summary>
/// マークIII / ゲームギアのカートリッジ RAM。
///
/// セーブ RAM は $8000-$BFFF に現れ、$FFFC で有効化とバンク選択を行う。
/// 容量は申告されないので、読めた内容から 8KB / 16KB / 32KB を判断する。
/// </summary>
public sealed class SmsSaveTests
{
    /// <summary>
    /// 検証用のデータ。**8KB ごとに違う内容にすること。**
    ///
    /// i * 31 だけだと 256 バイト周期になり、8KB の窓同士が同じ内容になる。
    /// それでは「折り返して同じに見える」のか「本当に同じ」のかを
    /// 区別できず、容量の判定を確かめられない。
    /// </summary>
    private static byte[] Pattern(int size, int seed = 1)
    {
        var data = new byte[size];

        for (int i = 0; i < size; i++)
            data[i] = (byte)(i * 31 + (i >> 8) * 7 + (i >> 13) * 61 + seed);

        return data;
    }

    [Fact]
    public void 同じ内容が2回見えるなら8KB()
    {
        var cart = new FakeSmsCart(Pattern(0x2000));

        Assert.Equal(0x2000, SmsSave.Read(cart).Length);
    }

    /// <summary>バンクを変えても内容が変わらないなら 16KB。</summary>
    [Fact]
    public void バンクが切り替わらないなら16KB()
    {
        var cart = new FakeSmsCart(Pattern(0x4000));

        Assert.Equal(0x4000, SmsSave.Read(cart).Length);
    }

    [Fact]
    public void バンクが切り替わるなら32KB()
    {
        var cart = new FakeSmsCart(Pattern(0x8000));

        Assert.Equal(0x8000, SmsSave.Read(cart).Length);
    }

    [Fact]
    public void 書いた内容がそのまま読み戻せる()
    {
        var cart = new FakeSmsCart(new byte[0x8000]) { AllowSaveWrites = true };
        var data = Pattern(0x8000, seed: 7);

        SmsSave.Write(cart, data);

        Assert.Equal(data, cart.Snapshot());
    }

    /// <summary>
    /// **32KB の前半を書き漏らさないこと。**
    ///
    /// 参照実装はバンク 1 に切り替えてから後半だけを書いており、
    /// 前半 16KB がどこにも書かれない。そのまま真似ると半分しか戻らない。
    /// </summary>
    [Fact]
    public void 大きいセーブでも前半が書かれる()
    {
        var cart = new FakeSmsCart(new byte[0x8000]) { AllowSaveWrites = true };
        var data = Pattern(0x8000, seed: 11);

        SmsSave.Write(cart, data);

        Assert.Equal(data.AsSpan(0, 0x4000).ToArray(), cart.Snapshot().AsSpan(0, 0x4000).ToArray());
    }

    /// <summary>
    /// **全面が同じ値でも、指定した長さだけ読めること。**
    ///
    /// Read は内容を見比べて容量を決めるので、一様なデータでは
    /// 窓の区別がつかず 8KB と答える。消去したあとの読み戻しがこれにあたり、
    /// Read で照合すると 32KB 書いても「8KB しか読めない」と言われる。
    /// 書いた長さが分かっている照合では ReadExact を使う。
    /// </summary>
    [Theory]
    [InlineData(0x2000)]
    [InlineData(0x4000)]
    [InlineData(0x8000)]
    public void 一様でも指定した長さだけ読める(int size)
    {
        var uniform = new byte[0x8000];
        Array.Fill(uniform, (byte)0xFF);

        var cart = new FakeSmsCart(uniform);

        Assert.Equal(size, SmsSave.ReadExact(cart, size).Length);
    }

    /// <summary>同じ理由で、消去の書き込みが照合まで通ること。</summary>
    [Fact]
    public void 全面を同じ値で消せる()
    {
        var cart = new FakeSmsCart(Pattern(0x8000)) { AllowSaveWrites = true };
        var blank = new byte[0x8000];
        Array.Fill(blank, (byte)0xFF);

        SmsSave.Write(cart, blank);

        Assert.Equal(blank, cart.Snapshot());
    }

    /// <summary>終わったら $FFFC を 0 に戻すこと。有効のまま放置しない。</summary>
    [Fact]
    public void 終了時にRAMを無効へ戻す()
    {
        var cart = new FakeSmsCart(Pattern(0x8000));

        SmsSave.Read(cart);

        Assert.Equal(0, cart.Control);
    }

    [Fact]
    public void 失敗してもRAMを無効へ戻す()
    {
        var cart = new FakeSmsCart(new byte[0x8000]);   // 許可していない

        Assert.Throws<RfcaWriteBlockedException>(() => SmsSave.Write(cart, Pattern(0x8000)));
        Assert.Equal(0, cart.Control);
    }

    [Fact]
    public void 大きすぎるものは書き込まない()
    {
        var cart = new FakeSmsCart(new byte[0x8000]) { AllowSaveWrites = true };

        var error = Assert.Throws<RfcaException>(() => SmsSave.Write(cart, Pattern(0x10000)));

        Assert.Contains("大きすぎます", error.Message);
    }

    /// <summary>セーブ領域だけが書ける範囲であること。</summary>
    [Theory]
    [InlineData(0x8000u, true)]
    [InlineData(0xBFFFu, true)]
    [InlineData(0x7FFFu, false)]
    [InlineData(0xC000u, false)]
    [InlineData(0x0000u, false)]
    public void セーブ領域だけ書ける(uint address, bool expected)
        => Assert.Equal(expected,
            SaveMemory.IsSaveWrite(CartridgeKind.MarkIIIOrGameGear, RfcaOpcode.SmsWrite, address));
}
