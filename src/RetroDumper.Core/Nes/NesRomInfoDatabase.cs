using System.Xml.Linq;

namespace RetroDumper.Core.Nes;

/// <summary>
/// ファミコンのカセットを同定するデータベース。
///
/// 【なぜ必要か】
/// ファミコンのカートリッジはヘッダを持たず、マッパー番号も容量も申告しません。
/// 吸い出す前に分かるのは、**リセット直後に読める固定領域の中身**だけです。
///
///   CPU $8000 から 1KB … PRG-ROM の先頭
///   CPU $FC00 から 1KB … PRG-ROM の末尾（多くのマッパーで固定バンク）
///
/// この SHA-1 を鍵に、マッパー番号と PRG/CHR の容量を引きます。
///
/// 【データについて】
/// 本ソフトはデータを同梱しません。参照実装が使っている
/// RomInfoList は同社の exe に埋め込まれた独自のもので、
/// 公開配布されているデータではないため再配布できません。
/// 形式だけ実装してあるので、同等のファイルを用意すれば動きます。
///
/// 置き場所: exe と同じ場所の DataBase フォルダ (*.xml)
///
/// 形式:
///   &lt;ArrayOfRomInfo&gt;
///     &lt;RomInfo sha1="..."&gt;
///       &lt;prg size="32768" first1k_sha1="..." last1k_sha1="..." mapper="1"/&gt;
///       &lt;chr size="8192"/&gt;
///     &lt;/RomInfo&gt;
/// </summary>
public sealed class NesRomInfoDatabase
{
    private readonly Dictionary<string, NesRomInfo> _byFirst1K = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NesRomInfo> _byLast1K = new(StringComparer.OrdinalIgnoreCase);

    public int EntryCount { get; private set; }

    public bool IsEmpty => EntryCount == 0;

    public static string DefaultDirectory =>
        Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory(),
            "DataBase");

    public static NesRomInfoDatabase Load(string? directory = null, Action<string>? log = null)
    {
        var db = new NesRomInfoDatabase();
        string dir = directory ?? DefaultDirectory;

        if (!Directory.Exists(dir)) return db;

        foreach (string path in Directory.GetFiles(dir, "*.xml").OrderBy(p => p))
        {
            try
            {
                int added = db.LoadFile(path);
                if (added > 0) log?.Invoke($"FC データベースを読み込みました: {Path.GetFileName(path)} ({added} 件)");
            }
            catch (Exception ex)
            {
                log?.Invoke($"FC データベースを読めませんでした: {Path.GetFileName(path)} — {ex.Message}");
            }
        }

        return db;
    }

    private int LoadFile(string path)
    {
        var doc = XDocument.Load(path);
        int added = 0;

        foreach (var node in doc.Descendants("RomInfo"))
        {
            var prg = node.Element("prg");
            if (prg is null) continue;

            var chr = node.Element("chr");

            var info = new NesRomInfo(
                (string?)node.Attribute("name") ?? (string?)node.Attribute("sha1") ?? "",
                ParseInt((string?)prg.Attribute("mapper") ?? (string?)node.Attribute("mapper")),
                ParseLong((string?)prg.Attribute("size")),
                ParseLong((string?)chr?.Attribute("size")),
                Normalize((string?)prg.Attribute("first1k_sha1")),
                Normalize((string?)prg.Attribute("last1k_sha1")));

            if (info.First1KSha1 is { } f) _byFirst1K[f] = info;
            if (info.Last1KSha1 is { } l) _byLast1K[l] = info;

            added++;
            EntryCount++;
        }

        return added;
    }

    /// <summary>
    /// 先頭 1KB の SHA-1 で引く。見つからなければ末尾 1KB でも試す。
    /// </summary>
    public NesRomInfo? Find(string first1KSha1, string? last1KSha1 = null)
    {
        if (_byFirst1K.TryGetValue(first1KSha1, out var hit)) return hit;

        if (last1KSha1 is not null && _byLast1K.TryGetValue(last1KSha1, out var byLast))
            return byLast;

        return null;
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static int? ParseInt(string? value)
        => int.TryParse(value, out int v) ? v : null;

    private static long ParseLong(string? value)
        => long.TryParse(value, out long v) ? v : 0;
}

/// <summary>データベースの 1 エントリ。</summary>
public sealed record NesRomInfo(
    string Name,
    int? MapperNumber,
    long PrgSize,
    long ChrSize,
    string? First1KSha1,
    string? Last1KSha1);
