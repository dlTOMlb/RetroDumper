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
    /// タイトルからファイル名を作る。
    ///
    /// 手順の順番に意味がある:
    ///   1. 空白を半角スペースに揃える
    ///   2. ファイル名に使えない文字を '_' に置き換える
    ///   3. 前後の空白と、Windows が扱えない末尾のピリオドを落とす
    ///
    /// 1 を 2 より先にやること。<see cref="Path.GetInvalidFileNameChars"/> には
    /// タブや改行などの制御文字が含まれるので、順序を逆にすると
    /// それらが半角スペースではなく '_' になってしまう。
    /// </summary>
    public static string MakeRomFileName(string? title, string extension, string fallback = "cartridge")
    {
        string name = NormalizeWhitespace(title ?? "");

        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        // Windows は末尾の空白とピリオドを扱えない。
        name = name.Trim().TrimEnd('.').Trim();

        if (name.Length == 0) name = fallback;

        return name + extension;
    }
}
