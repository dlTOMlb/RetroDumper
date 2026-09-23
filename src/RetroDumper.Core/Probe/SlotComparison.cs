using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Probe;

/// <summary>
/// 同じ測定を、カートリッジの挿し方を変えながら繰り返して見比べる。
///
/// ここまでの探索は「どのコマンドでスロットを起こすか」ばかりを追っていた。
/// しかし、その前提として確かめるべきことを一度も測っていない。
///
///   1. スロットを空にしたとき、状態要求は何を返すのか。
///      種別 0x06 が本当に「GBA を検出した」という意味なのか、
///      それとも挿していなくても返る既定値なのかが分かっていない。
///      後者なら「アダプタがカートリッジを検出できていない」が根本原因であり、
///      コマンドをいくら探しても解決しない。
///
///   2. 今この瞬間、SFC はまだ読めるのか。
///      USB のハング・抜き差し・こちらのコード変更を経たあと、
///      既知の正常経路が生きているかを確認していない。
///      SFC も壊れているなら、原因は GBA 固有ではない。
///
/// この道具は未知の opcode を一切送らない。判明済みのリードと状態要求だけを使う。
/// アダプタが落ちる危険はない。
/// </summary>
public static class SlotComparison
{
    /// <summary>判明済みの読み出しコマンドだけを並べる。未知の opcode は使わない。</summary>
    private static readonly (string Name, uint Opcode, uint HeaderField, uint Address, int Size)[] KnownReads =
    [
        ("SFC   $00:FFC0", RfcaOpcode.SnesRead,      0x08, 0x00FFC0, 64),
        ("SFC   $C0:0000", RfcaOpcode.SnesRead,      0x08, 0xC00000, 64),
        ("MD    先頭",     RfcaOpcode.MegaDriveRead, 0x08, 0x000000, 64),
        ("GBA   先頭",     RfcaOpcode.GbaRomRead, 0x00, 0x000000, 512),
        ("SMS   先頭",     RfcaOpcode.SmsRead,       0x08, 0x000000, 64),
    ];

    public static void Run(RfcaLink link, string label, ProbeJournal journal)
    {
        journal.Blank();
        journal.Write("================================================================");
        journal.Write($"■ {label}");
        journal.Write($"  {DateTime.Now:yyyy-MM-dd HH:mm:ss}   ポート {link.PortName}");
        journal.Write("================================================================");

        link.EnsureAlive();

        // 自動ウェイクアップを止める。素の状態を見たいので、
        // 読み出しの直前に 0x2F が勝手に飛ぶと測定にならない。
        bool savedAutoWake = link.AutoWake;
        link.AutoWake = false;

        try
        {
            // --- 状態要求。生のバイト列をそのまま残す ---
            journal.Write("");
            journal.Write("[状態要求 opcode 0x06]");

            for (int i = 1; i <= 3; i++)
            {
                var status = link.GetStatusWithRetry();
                journal.Write(
                    $"  {i} 回目: {status}" +
                    $"  → 種別 0x{(byte)status.Kind:X2} ({status.Kind.ToDisplayName()})");
            }

            // --- 判明済みのリードを一通り ---
            journal.Write("");
            journal.Write("[判明済みリード（ウェイクアップなし）]");

            foreach (var (name, opcode, headerField, address, size) in KnownReads)
            {
                link.EnsureAlive(opcode);
                journal.Write($"  {name}: {DescribeRead(link, opcode, headerField, address, size)}");
            }

            // --- SFC のウェイクアップが今も通るか（対照実験）---
            journal.Write("");
            journal.Write("[対照: 0x2F param=1 （SFC スロット）]");
            journal.Write("  SFC が挿さっていれば受理され、読み出しが実データに変わるはず。");

            string wakeAck = SendWake(link, parameter: 1);
            journal.Write($"  応答: {wakeAck}");

            link.EnsureAlive(RfcaOpcode.SlotWakeup);
            journal.Write(
                $"  SFC $00:FFC0 再読み: " +
                $"{DescribeRead(link, RfcaOpcode.SnesRead, 0x08, 0x00FFC0, 64)}");
            journal.Write(
                $"  GBA 先頭 再読み  : " +
                $"{DescribeRead(link, RfcaOpcode.GbaRomRead, 0x00, 0x000000, 512)}");

            journal.Write("");
        }
        finally
        {
            try { link.AutoWake = savedAutoWake; }
            catch (ObjectDisposedException) { }
        }
    }

    private static string SendWake(RfcaLink link, uint parameter)
    {
        try
        {
            var ack = link.SendControl(
                RfcaOpcode.SlotWakeup, address: 0, size: 0, parameter: parameter);

            Thread.Sleep(500);

            string text = Convert.ToHexString(ack);

            if (ack.Length >= 4 && BitConverter.ToUInt32(ack, 0) == 0xFFFFFFFF)
                return text + " (拒否)";

            return text + " (受理)";
        }
        catch (RfcaDisconnectedException)
        {
            throw;
        }
        catch (RfcaException ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string DescribeRead(
        RfcaLink link, uint opcode, uint headerField, uint address, int size)
    {
        try
        {
            var data = link.Read(opcode, address, size, headerField);

            if (data.Length == 0) return "0 バイト";

            string head = Convert.ToHexString(data.AsSpan(0, Math.Min(16, data.Length)));

            bool allSame = true;
            for (int i = 1; i < data.Length; i++)
                if (data[i] != data[0]) { allSame = false; break; }

            return allSame
                ? $"全バイト 0x{data[0]:X2}"
                : $"{head}  ★実データ";
        }
        catch (RfcaDisconnectedException)
        {
            throw;
        }
        catch (RfcaException ex)
        {
            return ex is RfcaNakException ? "拒否" : ex.GetType().Name;
        }
    }
}
