using RetroDumper.Core.Gba;
using RetroDumper.Core.Transport;

namespace RetroDumper.Lab;

/// <summary>
/// 実機に対する実験を、画面を介さずに行うための道具。
///
/// GBA の EEPROM 書き込みが壊れる件を調べるために作った。
/// GUI を毎回操作してもらうと往復が増えるうえ、1 回ごとに
/// 利用者のセーブを危険にさらすことになる。
///
/// **既定は読み出しのみ。**書き込む操作は名前で区別し、
/// どれだけ書くかを明示させる。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 1;
        }

        string command = args[0];
        string port = args.Length > 1 ? args[1] : "COM3";

        try
        {
            return command switch
            {
                "probe" => Probe(port),
                "dump" => Dump(port, args.Length > 2 ? args[2] : "eeprom.bin"),
                "wblock" => WriteBlock(port, args),
                "write" => WriteFile(port, args),
                "wfill" => WriteFill(port, args),
                "wmark" => WriteMarked(port, args),
                "wtrick" => WriteTrick(port, args),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"失敗: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            使い方: rfcalab <コマンド> [ポート] [引数]

              probe  [COM3]                 状態と EEPROM の読み出し安定性を見る（読むだけ）
              dump   [COM3] [out.bin]       EEPROM を読んでファイルに保存（読むだけ）
              wblock [COM3] <位置> <16進16桁> 8 バイトだけ書いて読み戻す（**書き込む**）
              write  [COM3] <ファイル>       全体を書いて照合する（**書き込む**）
            """);

        return 1;
    }

    private static RfcaLink Open(string port)
    {
        var link = new RfcaLink(port) { Trace = line => Console.WriteLine($"  [{line}]") };

        var status = link.GetStatus();
        Console.WriteLine($"ポート {port} / 種別 {status.Kind.ToDisplayName()}");

        if (status.Kind != CartridgeKind.GameBoyAdvance)
            throw new InvalidOperationException("GBA のカートリッジが挿さっていません。");

        return link;
    }

    /// <summary>読むだけ。EEPROM を 2 回読んで一致するかを見る。</summary>
    private static int Probe(string port)
    {
        using var link = Open(port);

        foreach (var type in new[] { GbaSaveType.Eeprom4k, GbaSaveType.Eeprom64k })
        {
            Console.WriteLine();
            Console.WriteLine($"=== {GbaSave.DisplayName(type)} ===");

            var (stable, differences) = GbaSave.CheckReadStability(link, type);

            var data = GbaSave.Read(link, type);

            Console.WriteLine($"  安定: {(stable ? "はい" : $"いいえ（{differences} バイト違う）")}");
            Console.WriteLine($"  先頭 16 バイト: {Convert.ToHexString(data.AsSpan(0, 16))}");
            Console.WriteLine($"  全バイト同じ: {(AllSame(data) ? "はい" : "いいえ")}");
        }

        return 0;
    }

    private static int Dump(string port, string path)
    {
        using var link = Open(port);

        var data = GbaSave.Read(link, GbaSaveType.Eeprom4k);
        File.WriteAllBytes(path, data);

        Console.WriteLine($"{data.Length} バイトを {path} に保存しました。");
        return 0;
    }

    /// <summary>
    /// 8 バイト（EEPROM の 1 ブロック）だけ書いて読み戻す。
    ///
    /// 書き込みが何をしているかを見るための最小の実験。
    /// 512 バイト全体を書くより、壊す範囲が小さい。
    /// </summary>
    private static int WriteBlock(string port, string[] args)
    {
        if (args.Length < 4)
        {
            Console.WriteLine("wblock <ポート> <位置> <16進16桁>");
            return 1;
        }

        int offset = int.Parse(args[2]);
        byte[] block = Convert.FromHexString(args[3]);

        if (block.Length != 8)
        {
            Console.WriteLine("8 バイト（16 進 16 桁）で指定してください。");
            return 1;
        }

        using var link = Open(port);
        link.AllowSaveWrites = true;

        var before = GbaSave.Read(link, GbaSaveType.Eeprom4k);
        Console.WriteLine($"書く前  : {Convert.ToHexString(before.AsSpan(offset, 8))}");
        Console.WriteLine($"書く内容: {Convert.ToHexString(block)}");

        // --no-reinit を付けると、書き込み直前の選び直しを省く。
        // 読み出しでアダプタ側に容量が latch されているなら、
        // 選び直しでそれが既定 (64kbit) に戻っている疑いがある。
        bool reinit = !args.Contains("--no-reinit");

        Console.WriteLine($"書き込み前の選び直し: {(reinit ? "する" : "しない")}");

        if (reinit) link.ReinitializeSlot();

        link.WriteSaveMemory(
            CartridgeKind.GameBoyAdvance, RfcaOpcode.GbaEepromWrite, (uint)offset, block);

        var after = GbaSave.Read(link, GbaSaveType.Eeprom4k);
        Console.WriteLine($"書いた後: {Convert.ToHexString(after.AsSpan(offset, 8))}");

        int changed = 0;
        for (int i = 0; i < before.Length; i++) if (before[i] != after[i]) changed++;

        Console.WriteLine($"変化したバイト数: {changed} / {before.Length}");

        return 0;
    }

    private static int WriteFile(string port, string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("write <ポート> <ファイル>");
            return 1;
        }

        byte[] data = File.ReadAllBytes(args[2]);

        using var link = Open(port);
        link.AllowSaveWrites = true;

        var type = data.Length == 512 ? GbaSaveType.Eeprom4k : GbaSaveType.Eeprom64k;

        GbaSave.Write(link, type, data);

        Console.WriteLine("書き込みと照合が通りました。");
        return 0;
    }

    /// <summary>
    /// 512 バイトを同じ値で埋めて書き、読み戻して分布を見る。
    ///
    /// 何バイト届いたのか、どのブロックまで書けたのかを、
    /// 中身に左右されずに確かめるための実験。
    /// chunk を指定すると、その大きさに分けて書く。
    /// </summary>
    private static int WriteFill(string port, string[] args)
    {
        byte value = args.Length > 2 ? Convert.ToByte(args[2], 16) : (byte)0xA5;
        int chunk = args.Length > 3 ? int.Parse(args[3]) : 512;

        var data = new byte[512];
        Array.Fill(data, value);

        using var link = Open(port);
        link.AllowSaveWrites = true;

        Console.WriteLine($"0x{value:X2} で 512 バイトを埋めて、{chunk} バイトずつ書きます。");

        for (int at = 0; at < data.Length; at += chunk)
        {
            int length = Math.Min(chunk, data.Length - at);

            link.WriteSaveMemory(
                CartridgeKind.GameBoyAdvance, RfcaOpcode.GbaEepromWrite,
                (uint)at, data.AsSpan(at, length));
        }

        var after = GbaSave.Read(link, GbaSaveType.Eeprom4k);

        int hit = 0;
        for (int i = 0; i < after.Length; i++) if (after[i] == value) hit++;

        Console.WriteLine($"その値になったバイト数: {hit} / {after.Length}");
        Console.WriteLine($"先頭 16 バイト: {Convert.ToHexString(after.AsSpan(0, 16))}");
        Console.WriteLine($"末尾 16 バイト: {Convert.ToHexString(after.AsSpan(^16))}");

        var blocks = new List<int>();
        for (int b = 0; b < 64; b++)
        {
            bool all = true;
            for (int i = 0; i < 8; i++) if (after[b * 8 + i] != value) all = false;
            if (all) blocks.Add(b);
        }

        Console.WriteLine($"完全に書けたブロック: {blocks.Count} / 64");
        if (blocks.Count is > 0 and < 64)
            Console.WriteLine($"  {string.Join(", ", blocks.Take(20))}");

        return 0;
    }

    /// <summary>
    /// ブロック n を値 n で埋めた 512 バイトを書き、どこに何が落ちたかを見る。
    ///
    /// すべてのブロックが同じ値だと、どのブロックの書き込みが
    /// 最後に効いたのかが分からない。区別できる中身で確かめる。
    /// </summary>
    private static int WriteMarked(string port, string[] args)
    {
        int chunk = args.Length > 2 ? int.Parse(args[2]) : 512;

        var data = new byte[512];
        for (int b = 0; b < 64; b++)
            for (int i = 0; i < 8; i++) data[b * 8 + i] = (byte)b;

        using var link = Open(port);
        link.AllowSaveWrites = true;

        Console.WriteLine($"ブロック n を値 n で埋めて、{chunk} バイトずつ書きます。");

        for (int at = 0; at < data.Length; at += chunk)
        {
            int length = Math.Min(chunk, data.Length - at);

            link.WriteSaveMemory(
                CartridgeKind.GameBoyAdvance, RfcaOpcode.GbaEepromWrite,
                (uint)at, data.AsSpan(at, length));
        }

        var after = GbaSave.Read(link, GbaSaveType.Eeprom4k);

        int correct = 0;
        for (int b = 0; b < 64; b++)
        {
            bool ok = true;
            for (int i = 0; i < 8; i++) if (after[b * 8 + i] != b) ok = false;
            if (ok) correct++;
        }

        Console.WriteLine($"正しく書けたブロック: {correct} / 64");
        Console.WriteLine($"ブロック 0 : {Convert.ToHexString(after.AsSpan(0, 8))}");
        Console.WriteLine($"ブロック 1 : {Convert.ToHexString(after.AsSpan(8, 8))}");
        Console.WriteLine($"ブロック63 : {Convert.ToHexString(after.AsSpan(63 * 8, 8))}");

        return 0;
    }

    /// <summary>
    /// アドレス欄の余りを 1 バイト目として使う書き方を試す。
    ///
    /// アダプタは常に 64kbit 用の 14 ビットアドレスを送る。
    /// 4kbit の石は上位 6 ビットだけをアドレスとして使い、
    /// 残り 8 ビットはデータの先頭として取り込まれる。
    ///
    /// ならば、14 ビットのアドレス欄に
    ///   上位 6 ビット = 狙うブロック番号
    ///   下位 8 ビット = 書きたい 1 バイト目
    /// を載せ、本体には 2 バイト目以降の 7 バイトだけを送ればよい。
    /// </summary>
    private static int WriteTrick(string port, string[] args)
    {
        int block = args.Length > 2 ? int.Parse(args[2]) : 1;
        byte[] want = Convert.FromHexString(args.Length > 3 ? args[3] : "1122334455667788");

        using var link = Open(port);
        link.AllowSaveWrites = true;

        // 14 ビットのアドレス欄 = (ブロック番号 << 8) | 1 バイト目
        uint field = ((uint)block << 8) | want[0];
        uint address = field * 8;

        Console.WriteLine($"ブロック {block} に {Convert.ToHexString(want)} を書きます。");
        Console.WriteLine($"  アドレス欄 0x{field:X4} → 要求アドレス {address}");
        Console.WriteLine($"  本体は 2 バイト目以降の 7 バイト: {Convert.ToHexString(want.AsSpan(1))}");

        link.WriteSaveMemory(
            CartridgeKind.GameBoyAdvance, RfcaOpcode.GbaEepromWrite, address, want.AsSpan(1));

        var after = GbaSave.Read(link, GbaSaveType.Eeprom4k);
        var got = after.AsSpan(block * 8, 8);

        Console.WriteLine($"  読み戻し: {Convert.ToHexString(got)}");
        Console.WriteLine(got.SequenceEqual(want) ? "  → 一致しました" : "  → 一致しません");

        return got.SequenceEqual(want) ? 0 : 3;
    }

    private static bool AllSame(byte[] data)
    {
        foreach (byte b in data) if (b != data[0]) return false;
        return true;
    }
}
