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

/// <summary>
/// GBA の EEPROM は、ROM の目印では容量が分からない。
///
/// 4kbit (512B) も 64kbit (8KB) も目印は同じ "EEPROM_V" で、
/// 容量が書かれていない。参照実装も一律 64kbit と判定し、
/// 4kbit は利用者の手動選択に任せている。
/// こちらは読んだ内容の折り返しから絞り込む。
/// </summary>
public sealed class GbaEepromSizeTests
{
    [Fact]
    public void 目印だけでは容量を区別できない()
    {
        var rom = new byte[4096];
        System.Text.Encoding.ASCII.GetBytes("EEPROM_V122").CopyTo(rom, 100);

        // 4kbit のカセットでも、目印からは 64kbit と判定される。
        Assert.Equal(GbaSaveType.Eeprom64k, GbaSave.Detect(rom));
    }

    [Theory]
    [InlineData(GbaSaveType.Eeprom64k, GbaSaveType.Eeprom4k)]
    [InlineData(GbaSaveType.Eeprom4k, GbaSaveType.Eeprom64k)]
    public void もう一方の容量を引ける(GbaSaveType type, GbaSaveType expected)
        => Assert.Equal(expected, GbaSave.AlternateEeprom(type));

    [Theory]
    [InlineData(GbaSaveType.Sram)]
    [InlineData(GbaSaveType.Flash512k)]
    [InlineData(GbaSaveType.Flash1M)]
    public void EEPROM以外にもう一方は無い(GbaSaveType type)
        => Assert.Null(GbaSave.AlternateEeprom(type));

    /// <summary>
    /// 512 バイトごとに同じ内容が繰り返していれば、4kbit の石が
    /// 折り返して見えている。
    /// </summary>
    [Fact]
    public void 折り返していれば512バイトと判断する()
    {
        var cart = new FakeGbaSaveCartridge(8192);
        var block = new byte[512];

        for (int i = 0; i < block.Length; i++) block[i] = (byte)(i * 7 + 1);

        var whole = new byte[8192];
        for (int at = 0; at < whole.Length; at += 512) block.CopyTo(whole, at);

        cart.Preset(whole);

        var probe = GbaSave.ProbeEepromSize(cart, GbaSaveType.Eeprom64k);

        Assert.Equal(GbaSaveType.Eeprom4k, probe.Type);
        Assert.True(probe.Determined);
    }

    /// <summary>
    /// 折り返さず、先頭 512 バイトにだけ内容があって後ろが空の形もある。
    /// アダプタや石によってどちらの見え方になるか変わるので、両方を根拠にする。
    /// </summary>
    [Fact]
    public void 後ろが空でも512バイトと判断する()
    {
        var cart = new FakeGbaSaveCartridge(8192);
        var data = new byte[8192];

        Array.Fill(data, (byte)0xFF);
        for (int i = 0; i < 512; i++) data[i] = (byte)(i * 7 + 1);

        cart.Preset(data);

        var probe = GbaSave.ProbeEepromSize(cart, GbaSaveType.Eeprom64k);

        Assert.Equal(GbaSaveType.Eeprom4k, probe.Type);
        Assert.True(probe.Determined);
    }

    [Fact]
    public void 折り返していなければ8KBのまま()
    {
        var cart = new FakeGbaSaveCartridge(8192);
        var data = new byte[8192];

        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 31 + (i >> 8) + 1);

        cart.Preset(data);

        var probe = GbaSave.ProbeEepromSize(cart, GbaSaveType.Eeprom64k);

        Assert.Equal(GbaSaveType.Eeprom64k, probe.Type);
        Assert.True(probe.Determined);
    }

    /// <summary>
    /// 中身が空のときは、どちらでも同じに見えるので判断できない。
    /// **決められなかったことを申告すること。**
    /// 勝手に小さいほうへ倒さず、呼び出し側が別の手掛かりを使えるようにする。
    /// </summary>
    [Fact]
    public void 中身が空なら決められないと申告する()
    {
        var cart = new FakeGbaSaveCartridge(8192);
        cart.Preset(Enumerable.Repeat((byte)0xFF, 8192).ToArray());

        var probe = GbaSave.ProbeEepromSize(cart, GbaSaveType.Eeprom64k);

        Assert.Equal(GbaSaveType.Eeprom64k, probe.Type);
        Assert.False(probe.Determined);
        Assert.Contains("空", probe.Reason);
    }

    /// <summary>EEPROM 以外は読みに行かないこと。</summary>
    [Fact]
    public void EEPROM以外は読まない()
    {
        var cart = new FakeGbaSaveCartridge(32768);

        var probe = GbaSave.ProbeEepromSize(cart, GbaSaveType.Sram);

        Assert.Equal(GbaSaveType.Sram, probe.Type);
        Assert.True(probe.Determined);
    }
}
