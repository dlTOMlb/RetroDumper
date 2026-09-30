using System.Text;

namespace RetroDumper.Core.Util;

/// <summary>
/// 吸い出した ROM のファイル名を組み立てる。
///
/// ROM ヘッダのタイトル欄は固定長で、余りが空白で埋められている。
/// 埋め草に全角空白が使われているカセットがあり、そのままファイル名にすると
/// 見た目では分からない全角空白が混ざる。No-Intro の命名や他ツールと
/// 突き合わせるときに一致しなくなるため、半角スペースへ揃える。
/// </summary>
public static class FileNaming
{
    /// <summary>
    /// 半角スペース以外の空白を、すべて半角スペースに置き換える。
    ///
    /// 対象は全角空白 (U+3000)、ノーブレークスペース (U+00A0)、
    /// タブ、改行など <see cref="char.IsWhiteSpace(char)"/> が真になるもの全部。
    /// </summary>
    public static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new StringBuilder(text.Length);

        foreach (char c in text)
            sb.Append(char.IsWhiteSpace(c) && c != ' ' ? ' ' : c);

        return sb.ToString();
    }

    /// <summary>
    /// ファイル名に使えない記号。制御文字は別に見る。
    ///
    /// <b><see cref="Path.GetInvalidFileNameChars"/> を使ってはいけない</b>。
    /// 戻り値が OS で違うためである。
    ///
    ///   Windows      制御文字 (0x00-0x1F) と " &lt; &gt; | : * ? \ /
    ///   macOS/Linux  NUL と / の 2 つだけ
    ///
    /// そのまま使うと、同じカセットから OS ごとに違う名前が出る。
    /// macOS では「Game: Subtitle (USA).sfc」のように ':' や '?' が残り、
    /// No-Intro との突き合わせが外れるうえ、Windows へ持っていくと開けない。
    ///
    /// **吸い出したものは OS をまたいで持ち歩く。**
    /// どこで吸い出しても同じ名前になるよう、Windows の集合に揃える。
    /// Windows の集合は macOS と Linux の集合を含むので、
    /// これで 3 つの OS すべてで通る名前になる。
    /// </summary>
    private const string InvalidFileNameSymbols = "\"<>|:*?\\/";

    /// <summary>ファイル名に使えない文字か。</summary>
    private static bool IsInvalidForFileName(char c)
        => c < 0x20 || InvalidFileNameSymbols.Contains(c);

    /// <summary>
    /// タイトルからファイル名を作る。
    ///
    /// 手順の順番に意味がある:
    ///   1. 空白を半角スペースに揃える
    ///   2. ファイル名に使えない文字を '_' に置き換える
    ///   3. 前後の空白と、Windows が扱えない末尾のピリオドを落とす
    ///
    /// 1 を 2 より先にやること。使えない文字にはタブや改行などの制御文字が
    /// 含まれるので、順序を逆にするとそれらが半角スペースではなく
    /// '_' になってしまう。
    /// </summary>
    public static string MakeRomFileName(string? title, string extension, string fallback = "cartridge")
    {
        string source = NormalizeWhitespace(title ?? "");

        var sb = new StringBuilder(source.Length);
        foreach (char c in source)
            sb.Append(IsInvalidForFileName(c) ? '_' : c);

        // Windows は末尾の空白とピリオドを扱えない。
        string name = sb.ToString().Trim().TrimEnd('.').Trim();

        if (name.Length == 0) name = fallback;

        return name + extension;
    }
}
