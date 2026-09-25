using System.IO;

namespace RetroDumper.App;

/// <summary>
/// EXE と同じ場所へ、利用者に断らずに残す控えのファイル。
///
/// **デバッグ版でしか残さない。**
///
/// 開発中は、失敗した吸い出しの現物がその場に残っていないと原因を追えない。
/// 保存ダイアログをどう閉じられても手元に一本確保しておきたい。
/// しかし配布版で同じことをすると、利用者が置いた覚えのないファイルが
/// EXE の横に溜まっていく。中身はセーブデータなので、消していいものかも
/// 判断がつかない。断りなく置くものではない。
///
/// そこで、この経路は開発中に限る。配布版では
/// <see cref="Save"/> が何もせず <c>null</c> を返すので、
/// 呼び出し側は戻り値が <c>null</c> のときの案内を用意すること。
/// </summary>
internal static class DebugArtifacts
{
    /// <summary>
    /// 控えを残すかどうか。配布版では false。
    ///
    /// const にすると、これで早く返る呼び出し側が
    /// 「到達できないコード」と言われる。読む側に見せたいのは分岐なので、
    /// 畳まれない形で持つ。
    /// </summary>
    public static bool Enabled { get; } =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>
    /// EXE と同じ場所に <paramref name="prefix"/>-日時<paramref name="extension"/> で残す。
    /// 残せたらその場所を、配布版や書き込みに失敗したときは <c>null</c> を返す。
    /// </summary>
    public static string? Save(byte[] data, string prefix, string extension)
    {
#if !DEBUG
        // 配布版にはこの働きを積まない。
        return null;
#else
        try
        {
            string directory = Path.GetDirectoryName(Environment.ProcessPath)
                ?? Directory.GetCurrentDirectory();

            string path = Path.Combine(
                directory,
                $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}" +
                (extension is { Length: > 0 } ? extension : ".bin"));

            File.WriteAllBytes(path, data);

            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
#endif
    }
}
