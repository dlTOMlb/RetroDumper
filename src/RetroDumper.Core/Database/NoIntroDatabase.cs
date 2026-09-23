using System.Globalization;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace RetroDumper.Core.Database;

/// <summary>
/// No-Intro の DAT ファイル（XML）を読み、吸い出した ROM を照合する。
///
/// DAT は本ソフトには同梱しない。利用者が自分で用意したものを読む。
/// 配布物ではないデータを勝手に再配布しないための方針で、
/// RetroFreakDumper も同じく *.dat をフォルダから読む作りになっている。
///
/// 入手先: https://datomatic.no-intro.org/  （Download → Daily）
/// 置き場所: exe と同じ場所の DataBase フォルダ
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
    /// フォルダ内の *.dat をすべて読む。
    /// フォルダが無い・DAT が 1 つも無い場合は空のまま返る（エラーにしない）。
    /// </summary>
    public static NoIntroDatabase Load(string? directory = null, Action<string>? log = null)
    {
        var db = new NoIntroDatabase();
        string dir = directory ?? DefaultDirectory;

        if (!Directory.Exists(dir))
        {
            log?.Invoke($"DAT フォルダがありません: {dir}");
            return db;
        }

        foreach (string path in Directory.GetFiles(dir, "*.dat").OrderBy(p => p))
        {
            try
            {
                int added = db.LoadFile(path);
                db.LoadedFiles.Add(Path.GetFileName(path));
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

    private int LoadFile(string path)
    {
        var doc = XDocument.Load(path);
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
