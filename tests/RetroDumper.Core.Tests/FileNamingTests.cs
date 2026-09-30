using RetroDumper.Core.Util;
using Xunit;

namespace RetroDumper.Core.Tests;

/// <summary>
/// ROM ファイル名の組み立て。
///
/// ROM ヘッダのタイトル欄は固定長で余りが空白埋めされており、
/// 全角空白が使われているカセットがある。そのままファイル名にすると
/// 見た目で判別できない全角空白が混ざり、No-Intro の命名や
/// 他ツールと突き合わせるときに一致しなくなる。
/// </summary>
public sealed class FileNamingTests
{
    [Theory]
    [InlineData("A　B", "A B")]         // 全角空白
    [InlineData("A B", "A B")]         // ノーブレークスペース
    [InlineData("A\tB", "A B")]             // タブ
    [InlineData("A\nB", "A B")]             // 改行
    [InlineData("A B", "A B")]         // EM スペース
    [InlineData("A B", "A B")]              // 半角スペースはそのまま
    public void 半角スペース以外の空白は半角スペースになる(string input, string expected)
        => Assert.Equal(expected, FileNaming.NormalizeWhitespace(input));

    [Fact]
    public void 全角空白を含むタイトルがファイル名で半角になる()
        => Assert.Equal(
            "SUPER GAME.gba",
            FileNaming.MakeRomFileName("SUPER　GAME", ".gba"));

    /// <summary>
    /// 空白の正規化は、使えない文字の置換より先に行うこと。
    ///
    /// Path.GetInvalidFileNameChars() にはタブや改行などの制御文字が含まれる。
    /// 順序を逆にすると、それらが半角スペースではなく '_' になってしまう。
    /// </summary>
    [Fact]
    public void タブはアンダースコアではなく半角スペースになる()
        => Assert.Equal("A B.md", FileNaming.MakeRomFileName("A\tB", ".md"));

    [Fact]
    public void ファイル名に使えない文字はアンダースコアになる()
        => Assert.Equal("A_B_C.sfc", FileNaming.MakeRomFileName("A/B:C", ".sfc"));

    [Fact]
    public void 前後の空白は落とす()
        => Assert.Equal("GAME.gb", FileNaming.MakeRomFileName("　 GAME 　", ".gb"));

    /// <summary>Windows は末尾のピリオドを扱えない。</summary>
    [Fact]
    public void 末尾のピリオドは落とす()
        => Assert.Equal("GAME.gba", FileNaming.MakeRomFileName("GAME...", ".gba"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("　　")]
    public void タイトルが空なら既定の名前になる(string? title)
        => Assert.Equal("cartridge.gba", FileNaming.MakeRomFileName(title, ".gba"));

    /// <summary>
    /// 途中の連続した空白はまとめない。
    /// ヘッダの埋め草をどう扱うかは判断が分かれるので、
    /// 指示された「空白の種類を揃える」以上のことはしない。
    /// </summary>
    [Fact]
    public void 途中の連続した空白はまとめない()
        => Assert.Equal("A   B.md", FileNaming.MakeRomFileName("A　　　B", ".md"));
}

/// <summary>
/// No-Intro で特定した名前からファイル名を作る経路。
///
/// ファミコンのカセットはタイトルを持たないため、吸い出して照合するまで
/// 名前が分からない。保存ダイアログを吸い出しの後ろに置き、
/// 特定できた名前を最初から入れておくことで、利用者が名前を打つ必要をなくした。
/// </summary>
public sealed class NoIntroNamingTests
{
    [Theory]
    [InlineData("Super Mario Bros. (Japan)", ".nes", "Super Mario Bros. (Japan).nes")]
    [InlineData("Crash Bandicoot Advance (Japan)", ".gba", "Crash Bandicoot Advance (Japan).gba")]
    [InlineData("Hoshi no Kirby 3 (Japan)", ".sfc", "Hoshi no Kirby 3 (Japan).sfc")]
    public void No_Intro名がそのままファイル名になる(string game, string ext, string expected)
        => Assert.Equal(expected, FileNaming.MakeRomFileName(game, ext));

    /// <summary>
    /// No-Intro の命名にはコロンやスラッシュを含むものがある。
    /// Windows のファイル名に使えないので置き換える必要がある。
    /// </summary>
    [Theory]
    [InlineData("Game: Subtitle (USA)", "Game_ Subtitle (USA).nes")]
    [InlineData("A / B (Japan)", "A _ B (Japan).nes")]
    [InlineData("What? (Europe)", "What_ (Europe).nes")]
    public void 使えない文字を含む名前でも保存できる(string game, string expected)
        => Assert.Equal(expected, FileNaming.MakeRomFileName(game, ".nes"));

    /// <summary>特定できなかったときは既定の名前になること。</summary>
    [Fact]
    public void 特定できなければ既定の名前になる()
        => Assert.Equal("cartridge.nes", FileNaming.MakeRomFileName("", ".nes"));

    /// <summary>
    /// セーブデータも No-Intro の名前で保存する。
    ///
    /// カートリッジのヘッダにある名前は短く詰められていて
    /// （"POKEMON EMER"、"CRASH"）、あとからファイルを見ても
    /// 何のセーブか分かりにくい。ROM を読んだついでに照合できているなら
    /// そちらの名前を使う。
    /// </summary>
    [Theory]
    [InlineData("Pocket Monsters - Emerald (Japan)", "Pocket Monsters - Emerald (Japan).sav")]
    [InlineData("Crash Bandicoot Advance (Japan)", "Crash Bandicoot Advance (Japan).sav")]
    public void セーブもNo_Intro名で保存する(string game, string expected)
        => Assert.Equal(expected, FileNaming.MakeRomFileName(game, ".sav"));

    /// <summary>
    /// 使えない文字の集合は OS で変えないこと。
    ///
    /// Path.GetInvalidFileNameChars() の戻り値は OS で違う。
    /// Windows は制御文字と " &lt; &gt; | : * ? \ / を返すが、
    /// macOS と Linux は NUL と / の 2 つしか返さない。
    /// それに任せると、同じカセットから OS ごとに違う名前が出る。
    ///
    /// 吸い出したものは OS をまたいで持ち歩く。どこで吸い出しても
    /// 同じ名前になるよう、Windows の集合に揃えてある。
    /// </summary>
    [Theory]
    [InlineData('"')]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData('|')]
    [InlineData(':')]
    [InlineData('*')]
    [InlineData('?')]
    [InlineData('\\')]
    [InlineData('/')]
    public void Windowsで使えない記号はどのOSでも置き換える(char bad)
        => Assert.Equal($"A_B.sfc", FileNaming.MakeRomFileName($"A{bad}B", ".sfc"));

    /// <summary>
    /// 空白でない制御文字も置き換える。
    ///
    /// 空白扱いのもの（タブ・改行）は先に半角スペースへ揃うので、
    /// ここで '_' になるのはそれ以外の制御文字だけである。
    /// </summary>
    [Theory]
    [InlineData('\u0001')]
    [InlineData('\u0007')]
    [InlineData('\u001F')]
    public void 空白でない制御文字は置き換える(char bad)
        => Assert.Equal("A_B.sfc", FileNaming.MakeRomFileName($"A{bad}B", ".sfc"));

    /// <summary>
    /// No-Intro には ':' や '?' を含む名前が実在する。
    /// macOS では以前これがそのまま残り、Windows へ持っていくと開けなかった。
    /// </summary>
    [Theory]
    [InlineData("Ristar: The Shooting Star (USA)", "Ristar_ The Shooting Star (USA).md")]
    [InlineData("Where in the World? (USA)", "Where in the World_ (USA).md")]
    [InlineData("A<B>C|D\"E", "A_B_C_D_E.md")]
    public void 実在する名前で確かめる(string game, string expected)
        => Assert.Equal(expected, FileNaming.MakeRomFileName(game, ".md"));
}
