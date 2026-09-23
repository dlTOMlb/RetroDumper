using RetroDumper.Core.Transport;
using RetroDumper.Core.Util;

namespace RetroDumper.Core.Probe;

/// <summary>
/// ファミコンのマッパーへの書き込みが、実際にカートリッジへ届いているかを測る。
///
/// ワルキューレの冒険（マッパー 206 相当）で、次のことがログから確定した。
///
///   ・PRG は正しく読めている（先頭に "COPYRIGHT 1986 NAMCO LTD." が読める）
///   ・$8000 と $A000 の内容は違う（＝バンクは実在し、区別が付く）
///   ・MMC3 / 206 の手順で R6 に 0 と 1 を書いても、$8000 の内容が変わらない
///
/// R6 は $8000 に見える 8KB バンクを選ぶレジスタなので、書き込みが届いて
/// いれば内容は必ず変わる。変わらないということは、
/// **アダプタは受理応答を返すのに、実際にはバスへ書いていない**。
///
/// 原因がフレームの組み立て方なのか、スロットの状態なのかは、
/// 送り方を変えて試すしか確かめようがない。ここではその送り方を
/// 何通りか用意し、読み戻して効果の有無を見る。
///
/// 送る先は $8000 と $8001 だけに限る。セーブ領域 ($6000-$7FFF) には
/// 一切触れない。読み戻しの比較対象があるので、結果は明確に出る。
/// </summary>
public static class NesWriteProbe
{
    private const uint Command = 0x8000;      // Namcot 108 / MMC3 のコマンドレジスタ
    private const uint Data = 0x8001;         // 同 データレジスタ
    private const byte SelectPrgAt8000 = 0x06;   // R6 = $8000 の 8KB バンク
    private const int BankSize = 0x2000;

    public static void Run(IRfcaLink link, ProbeJournal journal)
    {
        journal.Write("=== ファミコン 書き込み検査 ===");
        journal.Write($"日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        journal.Write("");
        journal.Write("R6（$8000 に見える 8KB バンクを選ぶレジスタ）に 1 を書き、");
        journal.Write("$8000 の内容が $A000 の内容に入れ替わるかを見ます。");
        journal.Write("入れ替われば書き込みは届いています。");
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
    /// 違うのはフレームへの値の載せ方だけ。
    /// </summary>
    private static IEnumerable<(string Name, Action<IRfcaLink, uint, byte> Send)> Encodings()
    {
        // 現状の手順。要求を送り、応答を待ってから本体 1 バイトを送る。
        yield return ("現状（要求 + データ 1 バイト）",
            static (link, address, value) =>
                link.WriteBankRegister(CartridgeKind.Famicom, RfcaOpcode.NesCpuWrite, address, value));

        // 値を要求のパラメータ欄に載せ、本体は送らない。
        yield return ("パラメータに値を載せる（本体なし）",
            static (link, address, value) =>
                link.SendControl(RfcaOpcode.NesCpuWrite, address, size: 0, parameter: value));

        // 同上、ヘッダ欄を 0x00 にする。
        yield return ("パラメータに値を載せる（ヘッダ欄 0x00）",
            static (link, address, value) =>
                link.SendControl(RfcaOpcode.NesCpuWrite, address, size: 0, parameter: value,
                                 headerField: 0x00));

        // 値をパラメータに載せ、サイズも 1 と申告する（本体は送らない）。
        yield return ("パラメータに値を載せ、サイズ 1 と申告",
            static (link, address, value) =>
                link.SendControl(RfcaOpcode.NesCpuWrite, address, size: 1, parameter: value));
    }

    private static uint Crc(IRfcaLink link, uint address)
        => Checksums.Crc32(link.Read(RfcaOpcode.NesCpuRead, address, BankSize));
}
