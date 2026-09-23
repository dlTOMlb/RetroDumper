using RetroDumper.Core.Transport;
using RetroDumper.Core.Util;

namespace RetroDumper.Core.Probe;

/// <summary>
/// ファミコンのマッパーへの書き込みが、実際にカートリッジへ届いているかを測る。
///
/// ワルキューレの冒険（2026-09-24 実機）で次が確定した。
///
///   ・PRG は正しく読めている（先頭に "COPYRIGHT 1986 NAMCO LTD." が読める）
///   ・$8000 と $A000 の内容は違う（＝バンクは実在し、区別が付く）
///   ・R6 に 1 を書いても $8000 の内容が変わらない
///
/// R6 は $8000 に見える 8KB バンクを選ぶレジスタなので、書き込みが届いて
/// いれば内容は必ず変わる。**アダプタは受理応答を返すのに、実際には
/// バスへ書いていない。** MMC1 も UxROM も MMC3 も揃って
/// 「バンクが切り替わらない」のは、マッパーの選択ではなくこれが原因。
///
/// ここでは送り方を変えて試し、読み戻して効果の有無を見る。
/// 送る先は $8000 と $8001 だけに限る。セーブ領域 ($6000-$7FFF) には触れない。
///
/// **1 つ試すごとに応答が正常かを確かめること。**
/// 初回の測定では、ある送り方でアダプタの応答が乱れ、それ以降の結果が
/// すべて巻き添えで失敗した。乱れた状態で測った値は何の証拠にもならない。
/// </summary>
public static class NesWriteProbe
{
    private const uint Command = 0x8000;         // Namcot 108 / MMC3 のコマンドレジスタ
    private const uint Data = 0x8001;            // 同 データレジスタ
    private const byte SelectPrgAt8000 = 0x06;   // R6 = $8000 の 8KB バンク
    private const int BankSize = 0x2000;

    private delegate void Send(IRfcaLink link, uint address, byte value);

    public static void Run(IRfcaLink link, ProbeJournal journal)
    {
        journal.Write("=== ファミコン 書き込み検査 ===");
        journal.Write($"日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        journal.Write("");
        journal.Write("R6（$8000 に見える 8KB バンクを選ぶレジスタ）に 1 を書き、");
        journal.Write("$8000 の内容が $A000 の内容に入れ替わるかを見ます。");
        journal.Write("入れ替われば書き込みは届いています。");
        journal.Write("");

        // 読みに行く前に、何が挿さっているかを確かめる。
        var status = link.GetStatus();

        journal.Write($"カートリッジ種別: {status.Kind.ToDisplayName()} (0x{(byte)status.Kind:X2})");

        if (status.Kind != CartridgeKind.Famicom)
        {
            journal.Write("");
            journal.Write("！！ ファミコンのカセットが挿さっていません。");
            journal.Write("　　 挿し直して「種別再取得」を押してから、もう一度お試しください。");
            return;
        }

        journal.Write("");

        uint at8000 = Crc(link, 0x8000);
        uint atA000 = Crc(link, 0xA000);

        journal.Write($"基準 $8000 の CRC32: {at8000:X8}");
        journal.Write($"基準 $A000 の CRC32: {atA000:X8}");

        if (at8000 == atA000)
        {
            journal.Write("");
            journal.Write("！！ $8000 と $A000 の内容が同じです。");
            journal.Write("　　 この検査は区別が付かないため意味を成しません。");
            journal.Write("　　 別のカセットでお試しください。");
            return;
        }

        journal.Write("");

        foreach (var (name, send) in Encodings())
        {
            journal.Write($"--- 送り方: {name}");

            if (!IsHealthy(link, at8000, atA000))
            {
                journal.Write("　 アダプタの応答が乱れています。");
                journal.Write("");
                journal.Write("ここで打ち切ります。乱れた状態で測った値は証拠になりません。");
                journal.Write("アダプタを挿し直してから、もう一度お試しください。");
                return;
            }

            uint after;

            try
            {
                send(link, Command, SelectPrgAt8000);
                send(link, Data, 0x01);
                after = Crc(link, 0x8000);
            }
            catch (Exception ex) when (ex is RfcaException or InvalidOperationException)
            {
                journal.Write($"　 送れませんでした: {ex.GetType().Name}: {ex.Message}");
                journal.Write("");
                continue;
            }

            journal.Write($"　 書き込み後の $8000 の CRC32: {after:X8}");

            journal.Write(after == atA000
                ? "　 ★ バンクが入れ替わりました。**この送り方なら書き込みが届きます。**"
                : after == at8000
                    ? "　 変化なし。この送り方では書き込みが届いていません。"
                    : "　 別の内容になりました。バンクは動いていますが番号が想定と違います。");

            // 元に戻す。戻せなくても読み出しは壊れないが、次の試行のために揃える。
            try
            {
                send(link, Command, SelectPrgAt8000);
                send(link, Data, 0x00);
            }
            catch (RfcaException) { }

            journal.Write("");
        }

        journal.Write("=== 検査終了 ===");
    }

    /// <summary>
    /// 試す送り方。どれも $8000 / $8001 への 1 バイト書き込みで、
    /// 違うのは要求の組み立て方と、書いた後に送るものだけ。
    ///
    /// 並びは穏やかなものから。前の測定で、値を要求のパラメータ欄に
    /// 載せる送り方はアダプタの応答を乱した。後ろに置く。
    /// </summary>
    private static IEnumerable<(string Name, Send Send)> Encodings()
    {
        // 現状の手順。要求を送り、応答を待ってから本体 1 バイトを送る。
        yield return ("現状（要求 + データ 1 バイト）", Plain);

        // 書き込みの直後に状態要求を送る。
        // リードでは ACK の後に状態要求を送らないとデータが流れてこない。
        // ライトにも同じ「つつき」が要るのではないか、という見込み。
        yield return ("書き込みごとに状態要求 (0x06)", (link, address, value) =>
        {
            Plain(link, address, value);
            link.GetStatus();
        });

        // 書き込みの直後にスロット確定を送る。
        yield return ("書き込みごとにスロット確定 (0x05)", (link, address, value) =>
        {
            Plain(link, address, value);
            link.SendControl(RfcaOpcode.SlotCommit);
        });

        // ヘッダ欄を 0x00 にして本体を送る。
        yield return ("ヘッダ欄 0x00 + データ 1 バイト", (link, address, value) =>
            link.WriteBankRegister(
                CartridgeKind.Famicom, RfcaOpcode.NesCpuWrite, address, value, headerField: 0x00));

        // 値を要求のパラメータ欄に載せ、本体は送らない。
        yield return ("パラメータに値を載せる（本体なし）", (link, address, value) =>
            link.SendControl(RfcaOpcode.NesCpuWrite, address, size: 0, parameter: value));
    }

    private static void Plain(IRfcaLink link, uint address, byte value)
        => link.WriteBankRegister(CartridgeKind.Famicom, RfcaOpcode.NesCpuWrite, address, value);

    /// <summary>
    /// アダプタがまだまともに応答するか。
    /// 既知のどちらかのバンクが読めていれば正常とみなす。
    /// </summary>
    private static bool IsHealthy(IRfcaLink link, uint at8000, uint atA000)
    {
        try
        {
            uint now = Crc(link, 0x8000);
            return now == at8000 || now == atA000;
        }
        catch (RfcaException)
        {
            return false;
        }
    }

    /// <summary>
    /// 1 バンク読んで CRC32 を取る。1 度だけ読み直す。
    ///
    /// 最初の 1 回はスロットが起き切る前に当たることがある。
    /// そこで諦めると、原因が書き込みなのか読み出しなのか分からなくなる。
    /// </summary>
    private static uint Crc(IRfcaLink link, uint address)
    {
        try
        {
            return Checksums.Crc32(link.Read(RfcaOpcode.NesCpuRead, address, BankSize));
        }
        catch (RfcaTimeoutException)
        {
            Thread.Sleep(300);
            return Checksums.Crc32(link.Read(RfcaOpcode.NesCpuRead, address, BankSize));
        }
    }
}
