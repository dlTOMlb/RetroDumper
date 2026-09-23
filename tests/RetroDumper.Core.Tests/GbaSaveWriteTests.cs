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

    /// <summary>装置に収まらないものは書かない。</summary>
    [Fact]
    public void 装置より大きければ書き込まない()
    {
        var cart = new FakeGbaSaveCartridge(32768) { AllowSaveWrites = true };

        var error = Assert.Throws<RfcaException>(
            () => GbaSave.Write(cart, GbaSaveType.Sram, Pattern(65536)));

        Assert.Contains("大きすぎます", error.Message);
        Assert.Equal(new byte[32768], cart.Snapshot());
    }

    /// <summary>
    /// 装置より小さいファイルは、その分だけ書く。残りは触らない。
    /// 参照実装 (RetroFreakDumper) も短いファイルを拒まない。
    /// </summary>
    [Fact]
    public void 装置より小さければその分だけ書く()
    {
        var cart = new FakeGbaSaveCartridge(32768) { AllowSaveWrites = true };

        var before = new byte[32768];
        Array.Fill(before, (byte)0xAB);
        cart.Preset(before);

        var data = Pattern(1024);

        GbaSave.Write(cart, GbaSaveType.Sram, data);

        var after = cart.Snapshot();

        Assert.Equal(data, after[..1024]);

        // 残りは触らないこと。
        Assert.All(after[1024..], b => Assert.Equal(0xAB, b));
    }

    /// <summary>
    /// **EEPROM だけは途中までの書き込みができない。**
    ///
    /// 容量によって通信のアドレス幅が変わるため、8KB の装置に
    /// 512 バイトだけ書くという操作が成立しない。書きは 512 バイトのつもり、
    /// 読み戻しは 8KB のつもり、と噛み合わなくなり照合が必ず失敗する
    /// （2026-09-24 実機で発生）。中途半端に書く前に止めること。
    /// </summary>
    /// <summary>
    /// ファイルの大きさに合う EEPROM の型へ読み替えられること。
    /// 読み替えないまま書くと、書きと読み戻しでアドレス幅が食い違う。
    /// </summary>
    [Theory]
    [InlineData(GbaSaveType.Eeprom64k, 512, GbaSaveType.Eeprom4k)]
    [InlineData(GbaSaveType.Eeprom4k, 8192, GbaSaveType.Eeprom64k)]
    [InlineData(GbaSaveType.Eeprom64k, 8192, GbaSaveType.Eeprom64k)]
    public void ファイルの大きさに合うEEPROMへ読み替える(
        GbaSaveType type, int length, GbaSaveType expected)
        => Assert.Equal(expected, GbaSave.MatchEepromToSize(type, length));

    /// <summary>EEPROM 以外は読み替えない。容量で通信は変わらない。</summary>
    [Fact]
    public void EEPROM以外は読み替えない()
        => Assert.Equal(GbaSaveType.Sram, GbaSave.MatchEepromToSize(GbaSaveType.Sram, 512));

    /// <summary>
    /// **書く前に、読み出しが安定しているかを確かめること。**
    ///
    /// 2026-09-24 の実機で、同じカセットを 2 回読んで 2 バイト違った。
    /// 読み出しが揺れていると照合そのものが成立せず、
    /// 「書けたのか壊したのか」を判断できないまま書くことになる。
    /// その状態では書かずに止める。
    /// </summary>
    [Fact]
    public void 読み出しが不安定なら書き込まない()
    {
        var cart = new FakeGbaSaveCartridge(512)
        {
            AllowSaveWrites = true, UnstableAt = 100, Eeprom4kQuirk = true,
        };
        var before = cart.Snapshot();

        var error = Assert.Throws<RfcaException>(
            () => GbaSave.Write(cart, GbaSaveType.Eeprom4k, Pattern(512)));

        Assert.Contains("読み出しが安定していない", error.Message);
        Assert.Equal(before, cart.Snapshot());
    }

    /// <summary>
    /// **4kbit の EEPROM に、アダプタの癖を越えて書けること。**
    ///
    /// アダプタは EEPROM の書き込みで常に 64kbit 用の 14 ビットアドレスを送る。
    /// 4kbit の石はアドレスを 6 ビットしか見ないため、余った下位 8 ビットが
    /// データの 1 バイト目として取り込まれ、本体は 1 バイト押し出される。
    /// 素直に送ると、どのブロックを指定してもブロック 0 にしか書けない。
    ///
    /// アドレス欄に「上位 6 ビット＝ブロック番号 / 下位 8 ビット＝1 バイト目」を
    /// 詰め、本体には 2 バイト目以降だけを送ることで、狙いどおりに書ける。
    /// 2026-09-24 に実機で確認した（512 バイト全体の書き戻しが照合まで通った）。
    /// </summary>
    [Fact]
    public void アダプタの癖を越えて4kbitEEPROMに書ける()
    {
        var cart = new FakeGbaSaveCartridge(512) { AllowSaveWrites = true, Eeprom4kQuirk = true };
        var data = Pattern(512);

        GbaSave.Write(cart, GbaSaveType.Eeprom4k, data);

        Assert.Equal(data, cart.Snapshot());
    }

    /// <summary>
    /// 細工をせずに素直に送ると、どのブロックもブロック 0 に落ちる。
    /// 実機で起きたことを、シミュレータが同じように再現していることの確認。
    /// これが再現できていないと、上のテストは何も守っていない。
    /// </summary>
    [Fact]
    public void 素直に送るとブロック0にしか書けない()
    {
        var cart = new FakeGbaSaveCartridge(512) { AllowSaveWrites = true, Eeprom4kQuirk = true };
        var data = Pattern(512);

        // 細工をしない送り方（ブロック番号をそのままアドレスにする）。
        for (int block = 0; block < 64; block++)
            cart.WriteSaveMemory(
                CartridgeKind.GameBoyAdvance, RfcaOpcode.GbaEepromWrite,
                (uint)(block * 8), data.AsSpan(block * 8, 8));

        var after = cart.Snapshot();

        // 最後に書いたブロック 63 の内容が、ブロック 0 に残る。
        Assert.Equal(63, after[0]);
        Assert.Equal(data[63 * 8], after[1]);

        // ブロック 1 以降は手つかず。
        Assert.All(after[8..].ToArray(), b => Assert.Equal(0, b));
    }

    /// <summary>読み出しの安定性だけを見る。内容は変えないこと。</summary>
    [Fact]
    public void 安定性の確認は内容を変えない()
    {
        var cart = new FakeGbaSaveCartridge(512);
        cart.Preset(Pattern(512));

        var (stable, differences) = GbaSave.CheckReadStability(cart, GbaSaveType.Eeprom4k);

        Assert.True(stable);
        Assert.Equal(0, differences);
        Assert.Equal(Pattern(512), cart.Snapshot());
    }

    [Fact]
    public void 揺れているバイト数を数える()
    {
        var cart = new FakeGbaSaveCartridge(512) { UnstableAt = 7 };

        var (stable, differences) = GbaSave.CheckReadStability(cart, GbaSaveType.Eeprom4k);

        Assert.False(stable);
        Assert.Equal(1, differences);
    }

    /// <summary>塞いでいるのは書き込みだけ。吸い出しは行えること。</summary>
    [Fact]
    public void EEPROMも吸い出しは行える()
    {
        var cart = new FakeGbaSaveCartridge(512);
        cart.Preset(Pattern(512));

        Assert.Equal(Pattern(512), GbaSave.Read(cart, GbaSaveType.Eeprom4k));
    }

    [Fact]
    public void 種類が分からなければ何もしない()
    {
        var cart = new FakeGbaSaveCartridge(32768) { AllowSaveWrites = true };

        Assert.Throws<RfcaException>(() => GbaSave.Read(cart, GbaSaveType.None));
        Assert.Throws<RfcaException>(() => GbaSave.Write(cart, GbaSaveType.None, []));
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
