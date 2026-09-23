using System.Globalization;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace RetroDumper.Core.Database;

/// <summary>
/// No-Intro の DAT ファイル（XML）を読み、吸い出した ROM を照合する。
///
/// GBA と NES(Headerless) の DAT は exe に埋め込んである（gzip、計 939KB）。
/// そのまま使えるので、利用者が用意しなくても照合が効く。
///
/// 更新版や他機種の DAT を使いたい場合は、exe と同じ場所の DataBase フォルダに
/// *.dat を置く。埋め込みと同じ名前のファイルはフォルダ側が優先される。
///
/// 入手先: https://datomatic.no-intro.org/
/// NES は **Headerless** を選ぶこと。当アプリが付ける iNES ヘッダは
/// ミラーリングの向きなどを推測で埋めており、Headered とは一致しない。
///
/// 用途は 2 つ:
///   1. 吸い出した ROM が既知の正規ダンプと一致するかの検証
///   2. 容量判定の裏取り
///      「ROM 終端の先は 0xFF」という推定は、末尾を 0xFF で埋めた
///      カセットでは小さく出る。DAT に一致する長さが見つかれば、
///      そちらを正解として採用できる。
///
/// DAT の構造:
///   &lt;datafile&gt;&lt;game name="..."&gt;&lt;rom name="..." size="..." crc="..." md5="..." sha1="..."/&gt;
/// </summary>
public sealed class NoIntroDatabase
{
    /// <summary>CRC32（大文字 8 桁）→ 該当エントリ。</summary>
    private readonly Dictionary<string, List<NoIntroEntry>> _byCrc = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>読み込んだ DAT ファイル名。</summary>
    public List<string> LoadedFiles { get; } = [];

    public int EntryCount { get; private set; }

    public bool IsEmpty => EntryCount == 0;

    /// <summary>exe と同じ場所の DataBase フォルダ。</summary>
    public static string DefaultDirectory =>
        Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory(),
            "DataBase");

    /// <summary>
    /// 埋め込みの DAT と、フォルダ内の *.dat を読む。
    ///
    /// 埋め込み分を先に読み、そのあとフォルダ分を読む。
    /// 同じ CRC32 のエントリはフォルダ側で上書きされるので、
    /// 新しい DAT を DataBase フォルダに置けば、再ビルドせずに更新できる。
    ///
    /// フォルダが無い・DAT が 1 つも無い場合でもエラーにはしない。
    /// </summary>
    /// <param name="includeEmbedded">
    /// 埋め込みの DAT も読むか。テストでフォルダの内容だけを見たいときに false にする。
    /// </param>
    public static NoIntroDatabase Load(
        string? directory = null, Action<string>? log = null, bool includeEmbedded = true)
    {
        var db = new NoIntroDatabase();

        if (includeEmbedded) db.LoadEmbedded(log);

        string dir = directory ?? DefaultDirectory;

        if (!Directory.Exists(dir))
        {
            log?.Invoke($"DAT フォルダがありません: {dir}");
            return db;
        }

        foreach (string path in Directory.GetFiles(dir, "*.dat").OrderBy(p => p))
        {
            string fileName = Path.GetFileName(path);

            // 埋め込みと同じ DAT はフォルダ側を優先し、二重に読まない。
            if (db.LoadedFiles.Remove(fileName))
                log?.Invoke($"内蔵 DAT より新しい同名ファイルを使います: {fileName}");

            try
            {
                int added = db.LoadFile(path);
                db.LoadedFiles.Add(fileName);
                log?.Invoke($"DAT を読み込みました: {Path.GetFileName(path)} ({added} 件)");
            }
            catch (Exception ex)
            {
                log?.Invoke($"DAT を読めませんでした: {Path.GetFileName(path)} — {ex.Message}");
            }
        }

        if (db.IsEmpty) log?.Invoke($"DAT が見つかりませんでした: {dir}");

        return db;
    }

    /// <summary>
    /// アセンブリに埋め込まれた DAT（gzip 圧縮）を読む。
    /// 3.2MB の XML がそのままだと exe が膨らむので gzip で持つ。
    /// </summary>
    private void LoadEmbedded(Action<string>? log)
    {
        var assembly = typeof(NoIntroDatabase).Assembly;

        foreach (string name in assembly.GetManifestResourceNames()
                     .Where(n => n.EndsWith(".dat.gz", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n))
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null) continue;

                using var gzip = new System.IO.Compression.GZipStream(
                    stream, System.IO.Compression.CompressionMode.Decompress);

                int added = LoadStream(gzip);
                LoadedFiles.Add(ShortName(name));
                log?.Invoke($"内蔵 DAT を読み込みました: {ShortName(name)} ({added} 件)");
            }
            catch (Exception ex)
            {
                log?.Invoke($"内蔵 DAT を読めませんでした: {ShortName(name)} — {ex.Message}");
            }
        }
    }

    /// <summary>リソース名から機種名の部分だけ取り出す。</summary>
    private static string ShortName(string resourceName)
    {
        string s = resourceName;

        const string marker = "Embedded.";
        int i = s.IndexOf(marker, StringComparison.Ordinal);
        if (i >= 0) s = s[(i + marker.Length)..];

        return s.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? s[..^3] : s;
    }

    private int LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return LoadStream(stream);
    }

    private int LoadStream(Stream stream)
    {
        var doc = XDocument.Load(stream);
        int added = 0;

        foreach (var game in doc.Root?.Elements("game") ?? [])
        {
            string gameName = (string?)game.Attribute("name") ?? "";

            foreach (var rom in game.Elements("rom"))
            {
                string? crc = (string?)rom.Attribute("crc");
                if (string.IsNullOrWhiteSpace(crc)) continue;

                long size = 0;
                if (long.TryParse((string?)rom.Attribute("size"), out long parsed)) size = parsed;

                var entry = new NoIntroEntry(
                    gameName,
                    (string?)rom.Attribute("name") ?? gameName,
                    size,
                    crc.Trim().ToUpperInvariant(),
                    ((string?)rom.Attribute("md5"))?.Trim().ToUpperInvariant(),
                    ((string?)rom.Attribute("sha1"))?.Trim().ToUpperInvariant());

                if (!_byCrc.TryGetValue(entry.Crc32, out var list))
                    _byCrc[entry.Crc32] = list = [];

                list.Add(entry);
                added++;
                EntryCount++;
            }
        }

        return added;
    }

    /// <summary>CRC32 で引く。見つからなければ空。</summary>
    public IReadOnlyList<NoIntroEntry> FindByCrc(uint crc32)
        => _byCrc.TryGetValue(crc32.ToString("X8", CultureInfo.InvariantCulture), out var list)
            ? list
            : [];

    /// <summary>
    /// データの先頭 <paramref name="length"/> バイトを照合する。
    /// MD5 / SHA-1 まで一致したものだけを返す（CRC32 の衝突を避けるため）。
    /// </summary>
    public NoIntroEntry? Match(ReadOnlySpan<byte> data, int length)
    {
        if (IsEmpty || length <= 0 || length > data.Length) return null;

        var slice = data[..length];
        var candidates = FindByCrc(Util.Checksums.Crc32(slice));

        if (candidates.Count == 0) return null;

        string md5 = Convert.ToHexString(MD5.HashData(slice));
        string sha1 = Convert.ToHexString(SHA1.HashData(slice));

        foreach (var entry in candidates)
        {
            bool md5Ok = entry.Md5 is null || entry.Md5 == md5;
            bool sha1Ok = entry.Sha1 is null || entry.Sha1 == sha1;

            if (md5Ok && sha1Ok) return entry;
        }

        return null;
    }
}

/// <summary>DAT の 1 エントリ。</summary>
public sealed record NoIntroEntry(
    string GameName,
    string RomName,
    long Size,
    string Crc32,
    string? Md5,
    string? Sha1);
