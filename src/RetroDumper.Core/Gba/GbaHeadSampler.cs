using RetroDumper.Core.Probe;
using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Gba;

/// <summary>
/// GBA スロットの先頭を実際に採取して、アドレスと ROM の対応関係を確かめる。
///
/// 2026-09-23、カートリッジを挿し直したところ実データが読めるようになった。
/// それまで全バイト 0xFF だったのは接触不良で、コマンドの問題ではなかった。
/// GBA スロットにウェイクアップは要らない（0x2F は 512 通りすべて拒否されるが、
/// そもそも不要なので正しい挙動）。受理される opcode の列挙でも、
/// GBA 固有の制御コマンドは 1 つも見つかっていない。
///
/// ただしアドレス 0 を読むと ROM 先頭の ARM 分岐命令ではなく
/// タイトル文字列 ("CASTLEVANIA1.00") が返る。GBA ヘッダではタイトルは
/// 0xA0 にあるので、アドレスの対応関係が想定と違う。
///
/// 任天堂ロゴは全 GBA ROM で同一の 156 バイトで、0x24 0xFF 0xAE 0x51 で始まる。
/// これを採取したデータから探せば、ROM 先頭がどのアドレスに対応するかが
/// 一意に決まる。推測せずにこれで確定させる。
/// </summary>
public static class GbaHeadSampler
{
    /// <summary>任天堂ロゴの先頭 8 バイト。全 GBA ROM で共通。</summary>
    private static readonly byte[] LogoHead =
        [0x24, 0xFF, 0xAE, 0x51, 0x69, 0x9A, 0xA2, 0x21];

    /// <summary>ロゴ本体は 0x04〜0x9F の 156 バイト。</summary>
    private const int LogoOffsetInHeader = 0x04;

    public sealed record Result(byte[] Data, int LogoAt, uint RomBase);

    public static Result Run(
        RfcaLink link,
        ProbeJournal journal,
        int length = 0x1000,
        CancellationToken cancellationToken = default)
    {
        journal.Write("===== GBA 先頭の採取 =====");
        journal.Write($"日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        journal.Write($"アドレス 0 から {length} バイトを読み、任天堂ロゴの位置を探します。");
        journal.Blank();

        link.EnsureAlive();

        bool savedAutoWake = link.AutoWake;
        link.AutoWake = false;

        try
        {
            var status = link.GetStatusWithRetry();
            journal.Write($"状態応答: {status}  種別 0x{(byte)status.Kind:X2} ({status.Kind.ToDisplayName()})");
            journal.Blank();

            var data = new byte[length];
            const int chunk = 512;

            for (int offset = 0; offset < length; offset += chunk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                link.EnsureAlive(RfcaOpcode.GbaRomRead);

                int size = Math.Min(chunk, length - offset);

                link.Read(
                    RfcaOpcode.GbaRomRead,
                    (uint)offset,
                    data.AsSpan(offset, size),
                    RfcaOpcode.RequestHeaderField);
            }

            journal.Write("--- 採取した内容 ---");
            DumpHex(journal, data, Math.Min(length, 0x200));

            if (length > 0x200)
            {
                journal.Write("  （以降は省略。全体は .bin に保存してあります）");
                journal.Blank();
            }

            // --- 任天堂ロゴを探す ---
            int logoAt = IndexOf(data, LogoHead);

            journal.Write("--- 任天堂ロゴの位置 ---");

            if (logoAt < 0)
            {
                journal.Write("  見つかりませんでした。");
                journal.Write("  採取範囲に ROM 先頭が含まれていない可能性があります。");
                journal.Write("===== ここまで =====");
                return new Result(data, -1, 0);
            }

            // ロゴはヘッダの 0x04 にあるので、ROM 先頭はその 4 バイト手前。
            int romStart = logoAt - LogoOffsetInHeader;
            journal.Write($"  ロゴ発見: オフセット 0x{logoAt:X4}");
            journal.Write($"  → ROM 先頭はアドレス 0x{romStart:X4}");

            if (romStart < 0)
            {
                journal.Write("  ROM 先頭が採取範囲より手前になります。想定外です。");
                journal.Write("===== ここまで =====");
                return new Result(data, logoAt, 0);
            }

            journal.Blank();
            journal.Write("--- ROM 先頭として解釈した結果 ---");

            var header = data.AsSpan(romStart);

            if (header.Length >= 0xC0)
            {
                journal.Write($"  分岐命令  : {Convert.ToHexString(header[..4])}");
                journal.Write($"  タイトル  : {Ascii(header.Slice(0xA0, 12))}");
                journal.Write($"  ゲームコード: {Ascii(header.Slice(0xAC, 4))}");
                journal.Write($"  メーカー  : {Ascii(header.Slice(0xB0, 2))}");
                journal.Write($"  固定値 0x96: 0x{header[0xB2]:X2} " +
                              (header[0xB2] == 0x96 ? "一致" : "不一致"));

                ushort sum = 0;
                for (int i = 0x04; i < 0xA0; i++) sum += header[i];
                journal.Write($"  ロゴ合計   : 0x{sum:X4} " +
                              (sum == 0x4B1B ? "一致" : "不一致（期待 0x4B1B）"));
            }

            journal.Write("===== ここまで =====");
            return new Result(data, logoAt, (uint)romStart);
        }
        finally
        {
            try { link.AutoWake = savedAutoWake; }
            catch (ObjectDisposedException) { }
        }
    }

    private static void DumpHex(ProbeJournal journal, byte[] data, int length)
    {
        for (int i = 0; i < length; i += 16)
        {
            int n = Math.Min(16, length - i);
            var row = data.AsSpan(i, n);

            journal.Write(
                $"  {i:X4}  {Convert.ToHexString(row),-32}  {Ascii(row)}");
        }

        journal.Blank();
    }

    private static string Ascii(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];

        for (int i = 0; i < bytes.Length; i++)
            chars[i] = bytes[i] is >= 0x20 and < 0x7F ? (char)bytes[i] : '.';

        return new string(chars);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool hit = true;

            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { hit = false; break; }

            if (hit) return i;
        }

        return -1;
    }
}
