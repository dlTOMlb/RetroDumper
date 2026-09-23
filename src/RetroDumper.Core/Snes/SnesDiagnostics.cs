using System.Text;
using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Snes;

/// <summary>
/// SFC が認識できないときの調査用。
///
/// ヘッダ候補や主要なバンクの生バイト列をそのまま吐き出す。
/// 「認識しない」という症状だけでは、状態要求が返っていないのか、
/// バンク $C0 が読めていないのか、ヘッダの採点で外しているのかが
/// 区別できないため、判断材料を一度に揃える。
/// </summary>
public static class SnesDiagnostics
{
    /// <summary>調査対象の番地。SA-1 の切り分けに必要なものを含む。</summary>
    private static readonly (uint Address, string What)[] Probes =
    [
        (0x007FC0, "LoROM ヘッダ候補 ($00:7FC0) — 素の LoROM のみ応答"),
        (0x00FFC0, "汎用ヘッダ候補 ($00:FFC0) — LoROM / HiROM / SA-1 共通"),
        (0x40FFC0, "ExHiROM ヘッダ候補 ($40:FFC0) — SA-1 では BW-RAM"),
        (0xC0FFC0, "バンク $C0 のヘッダ位置 ($C0:FFC0)"),
        (0xC00000, "バンク $C0 先頭 — SA-1 の ROM 先頭が出るはず"),
        (0xC08000, "バンク $C0 の $8000"),
        (0x008000, "バンク $00 の $8000 — LoROM / SA-1 の ROM 先頭"),
        (0x400000, "バンク $40 先頭 — SA-1 の BW-RAM 先頭"),
    ];

    public static string Run(IRfcaLink link, Action<string>? log = null)
    {
        var sb = new StringBuilder();

        void Emit(string line)
        {
            sb.AppendLine(line);
            log?.Invoke(line);
        }

        Emit("===== SFC 診断ダンプ =====");
        Emit($"日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Emit("");

        // --- 状態要求 ---
        Emit("--- 状態要求 (opcode 0x06) ---");
        for (int i = 0; i < 3; i++)
        {
            try
            {
                var status = link.GetStatus();
                Emit($"  [{i + 1}] 生応答: {status}");
                Emit($"       種別バイト: 0x{(byte)status.Kind:X2} → {status.Kind.ToDisplayName()}");
            }
            catch (Exception ex)
            {
                Emit($"  [{i + 1}] 失敗: {ex.Message}");
            }
        }
        Emit("");

        // --- スロットのウェイクアップ ---
        //
        // これを送るまでカートリッジはデータバスを駆動しない。
        // 実機で、送信前は全アドレス 0xFF、送信後は正常に読めることを確認済み。
        Emit("--- スロットのウェイクアップ (opcode 0x2F) ---");
        try
        {
            var ack = link.SendControl(RfcaOpcode.SlotWakeup, 0, 0, 1);
            Emit($"  応答: {Convert.ToHexString(ack)}");
            Thread.Sleep(600);
            Emit("  完了");
        }
        catch (Exception ex)
        {
            Emit($"  失敗: {ex.Message}");
        }
        Emit("");

        // --- SA-1 プライミング ---
        Emit("--- SA-1 プライミング (バンク $C0 を 1024 バイト読み捨て) ---");
        try
        {
            link.Read(RfcaOpcode.SnesRead, 0xC00000, 1024);
            Emit("  成功");
        }
        catch (Exception ex)
        {
            Emit($"  失敗: {ex.Message}");
            Emit("  ※ ここで失敗する場合、バンク $C0 が読めていません。");
            Emit("     SA-1 は ROM がバンク $C0-$FF にしか出ないため、これが致命的です。");
        }
        Emit("");

        // --- 各番地の生ダンプ ---
        foreach (var (address, what) in Probes)
        {
            Emit($"--- 0x{address:X6}  {what} ---");
            try
            {
                var data = link.Read(RfcaOpcode.SnesRead, address, 0x40);
                Emit(HexDump(data, address));
                Emit($"  所見: {Characterize(data)}");
            }
            catch (Exception ex)
            {
                Emit($"  読み出し失敗: {ex.Message}");
            }
            Emit("");
        }

        // --- ウェイクアップの再送で結果が変わらないことの確認 ---
        Emit("--- ウェイクアップ再送の前後比較 ---");
        {
            var before = TryRead(link, 0x00FFC0, 0x20);
            Emit($"  送信前 $00:FFC0 : {Describe(before)}");

            byte[]? ack = null;
            try
            {
                ack = link.SendControl(RfcaOpcode.SlotWakeup, 0, 0, 1);
                Emit($"  0x2F 応答      : {Convert.ToHexString(ack)}");
                Thread.Sleep(500);
            }
            catch (Exception ex)
            {
                Emit($"  0x2F 送信失敗  : {ex.Message}");
            }

            if (ack is not null)
            {
                var after = TryRead(link, 0x00FFC0, 0x20);
                Emit($"  送信後 $00:FFC0 : {Describe(after)}");

                var afterC0 = TryRead(link, 0xC00000, 0x20);
                Emit($"  送信後 $C0:0000 : {Describe(afterC0)}");

                bool changed =
                    (before is not null && after is not null && !before.AsSpan().SequenceEqual(after))
                    || (before is null && after is not null);

                Emit(changed
                    ? "  → 変化あり。読み出しが不安定な可能性があります。"
                    : "  → 変化なし。安定して読めています。");
            }
        }
        Emit("");

        // --- ヘッダ候補の採点 ---
        Emit("--- ヘッダ候補の採点 ---");
        foreach (var candidate in SnesHeader.Candidates)
        {
            try
            {
                var raw = link.Read(
                    RfcaOpcode.SnesRead, candidate.BusAddress, SnesHeader.HeaderReadSize);
                var header = SnesHeader.Parse(raw, candidate);

                Emit($"  {candidate.Description}");
                Emit($"    スコア      : {header.Score}");
                Emit($"    タイトル    : \"{header.Title}\"");
                Emit($"    マップモード: 0x{header.MapModeByte:X2} " +
                     $"→ {SnesHeader.MapperFromMapMode(header.MapModeByte)?.ToString() ?? "解釈不能"}");
                Emit($"    カート種別  : 0x{header.CartTypeByte:X2} → {header.CoprocessorName}");
                Emit($"    ROM サイズ欄: 0x{raw[0x17]:X2} → {header.DeclaredRomSize / 1024} KB");
                Emit($"    RAM サイズ欄: 0x{raw[0x18]:X2} → {header.DeclaredRamSize / 1024} KB");
                Emit($"    チェックサム: 0x{header.Checksum:X4} / 補数 0x{header.ChecksumComplement:X4}" +
                     $" → {(header.ChecksumPairValid ? "整合" : "不整合")}");
                Emit($"    リセットVec : 0x{header.ResetVector:X4}");
                Emit($"    解決マッパー: {header.ResolveMapper()}");
            }
            catch (Exception ex)
            {
                Emit($"  {candidate.Description}");
                Emit($"    読み出し失敗: {ex.Message}");
            }
            Emit("");
        }

        Emit("===== ここまで =====");
        return sb.ToString();
    }

    private static byte[]? TryRead(IRfcaLink link, uint address, int size)
    {
        try
        {
            return link.Read(RfcaOpcode.SnesRead, address, size);
        }
        catch (RfcaException)
        {
            return null;
        }
    }

    private static string Describe(byte[]? data)
        => data is null ? "読み出し失敗" : $"{Convert.ToHexString(data)}  ({Characterize(data)})";

    /// <summary>読めたデータがどういう性質かの一言所見。</summary>
    private static string Characterize(byte[] data)
    {
        if (data.Length == 0) return "データなし";

        bool allSame = true;
        for (int i = 1; i < data.Length; i++)
            if (data[i] != data[0]) { allSame = false; break; }

        if (allSame)
        {
            return data[0] switch
            {
                0xFF => "全バイト 0xFF — 未接続 / バスが応答していない可能性が高い",
                0x00 => "全バイト 0x00 — バスが応答していない可能性が高い",
                _ => $"全バイト 0x{data[0]:X2} — バスが応答していない可能性",
            };
        }

        int printable = 0;
        foreach (byte b in data)
            if (b is >= 0x20 and < 0x7F) printable++;

        if (printable > data.Length / 2)
            return "ASCII が多い — ヘッダのタイトル欄が読めている可能性";

        return "バイト列に変化あり — 何らかのデータが読めている";
    }

    private static string HexDump(byte[] data, uint baseAddress)
    {
        var sb = new StringBuilder();

        for (int row = 0; row < data.Length; row += 16)
        {
            sb.Append($"  {baseAddress + row:X6}  ");

            for (int i = 0; i < 16; i++)
                sb.Append(row + i < data.Length ? $"{data[row + i]:X2} " : "   ");

            sb.Append(' ');
            for (int i = 0; i < 16 && row + i < data.Length; i++)
            {
                byte b = data[row + i];
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            if (row + 16 < data.Length) sb.AppendLine();
        }

        return sb.ToString();
    }
}
