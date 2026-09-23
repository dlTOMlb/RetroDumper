using RetroDumper.Core.Gba;
using RetroDumper.Core.Transport;
using Xunit;

namespace RetroDumper.Core.Tests;

/// <summary>
/// GBA セーブデータの読み書き。
///
/// 利用者の指示で「セーブ領域だけ」書き込みを解禁した。
/// 解禁の範囲が守られること、書けたつもりで壊れていないことを固定する。
/// </summary>
public sealed class GbaSaveWriteTests
{
    private static byte[] Pattern(int size)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = (byte)(i * 31 + (i >> 8) + 1);
        return data;
    }

    [Fact]
    public void 書き込んだ内容がそのまま読み戻せる()
    {
        var cart = new FakeGbaSaveCartridge(32768) { AllowSaveWrites = true };
        var data = Pattern(32768);

        GbaSave.Write(cart, GbaSaveType.Sram, data);

        Assert.Equal(data, cart.Snapshot());
        Assert.Equal(data, GbaSave.Read(cart, GbaSaveType.Sram));
    }

    /// <summary>許可していなければ、1 バイトもバスに出ないこと。</summary>
    [Fact]
    public void 許可していなければ書き込めない()
    {
        var cart = new FakeGbaSaveCartridge(32768);
        var before = cart.Snapshot();

        Assert.Throws<RfcaWriteBlockedException>(
            () => GbaSave.Write(cart, GbaSaveType.Sram, Pattern(32768)));

        Assert.Equal(before, cart.Snapshot());
    }

    /// <summary>許可していなくても、吸い出しは行えること。</summary>
    [Fact]
    public void 許可していなくても吸い出せる()
    {
        var cart = new FakeGbaSaveCartridge(32768);
        cart.Preset(Pattern(32768));

        Assert.Equal(Pattern(32768), GbaSave.Read(cart, GbaSaveType.Sram));
    }

    /// <summary>
    /// 書けたつもりで壊れているのがいちばん困る。
    /// 読み戻して 1 バイトでも違えば失敗として扱うこと。
    /// </summary>
    [Fact]
    public void 読み戻しが違えば失敗として扱う()
    {
        var cart = new FakeGbaSaveCartridge(32768) { AllowSaveWrites = true, CorruptAt = 1234 };

        var error = Assert.Throws<RfcaException>(
            () => GbaSave.Write(cart, GbaSaveType.Sram, Pattern(32768)));

        Assert.Contains("照合に失敗", error.Message);
    }

    [Fact]
    public void 大きさが合わなければ書き込まない()
    {
        var cart = new FakeGbaSaveCartridge(32768) { AllowSaveWrites = true };

        var error = Assert.Throws<RfcaException>(
            () => GbaSave.Write(cart, GbaSaveType.Sram, Pattern(1024)));

        Assert.Contains("大きさが合いません", error.Message);
        Assert.Equal(new byte[32768], cart.Snapshot());
    }

    [Fact]
    public void 種類が分からなければ何もしない()
    {
        var cart = new FakeGbaSaveCartridge(32768) { AllowSaveWrites = true };

        Assert.Throws<RfcaException>(() => GbaSave.Read(cart, GbaSaveType.None));
        Assert.Throws<RfcaException>(() => GbaSave.Write(cart, GbaSaveType.None, []));
    }

    /// <summary>EEPROM は 512 バイトずつ書く。分割しても内容は変わらないこと。</summary>
    [Fact]
    public void EEPROMは分割して書いても内容が変わらない()
    {
        var cart = new FakeGbaSaveCartridge(8192) { AllowSaveWrites = true };
        var data = Pattern(8192);

        GbaSave.Write(cart, GbaSaveType.Eeprom64k, data);

        Assert.Equal(data, cart.Snapshot());
    }

    /// <summary>フラッシュは 4KB ずつ書く。</summary>
    [Fact]
    public void フラッシュは分割して書いても内容が変わらない()
    {
        var cart = new FakeGbaSaveCartridge(65536) { AllowSaveWrites = true };
        var data = Pattern(65536);

        GbaSave.Write(cart, GbaSaveType.Flash512k, data);

        Assert.Equal(data, cart.Snapshot());
    }
}
