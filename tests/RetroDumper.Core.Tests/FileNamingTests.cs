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
