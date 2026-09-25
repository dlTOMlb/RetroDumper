using RetroDumper.Core.Gb;
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
                "savetype" => SaveType(port),
                "savetest" => SaveTest(port, args),
                "erase" => Erase(port, args),
                "flashid" => FlashId(port),
                "dbinfo" => DatabaseInfo(),
                "gbhead" => GbHeader(port),
                "survey" => Survey(port),
                "pcedump" => PceDump(port, args),
                "wflash" => WriteFlash(port, args),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"失敗: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 2;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            使い方: rfcalab <コマンド> [ポート] [引数]

              survey [COM3]                 挿さっているカセットの素性を手早く調べる（読むだけ）
              pcedump [COM3] [out.pce]      Hu カードを吸い出して No-Intro と照合（読むだけ）
              erase [COM3] [--value FF|00] [--restore]
                                            セーブを全面同じ値で消す（**書き込む**）
              savetype [COM3]               ROM を読んでセーブ装置の種類を調べる（読むだけ）
              savetest [COM3] [控えの保存先]  吸い出し→同じ内容を書き戻し→照合（**書き込む**）
              probe  [COM3]                 状態と EEPROM の読み出し安定性を見る（読むだけ）
              dump   [COM3] [out.bin]       EEPROM を読んでファイルに保存（読むだけ）
              wblock [COM3] <位置> <16進16桁> 8 バイトだけ書いて読み戻す（**書き込む**）
              write  [COM3] <ファイル>       全体を書いて照合する（**書き込む**）
            """);

        return 1;
    }

    private static RfcaLink Open(string port, CartridgeKind? require = CartridgeKind.GameBoyAdvance)
    {
        var link = new RfcaLink(port) { Trace = line => Console.WriteLine($"  [{line}]") };

        var status = link.GetStatus();
        Console.WriteLine($"ポート {port} / 種別 {status.Kind.ToDisplayName()}");

        if (require is CartridgeKind kind && status.Kind != kind)
            throw new InvalidOperationException(
                $"{kind.ToDisplayName()} のカートリッジが挿さっていません。");

        if (!status.Kind.IsConnected())
            throw new InvalidOperationException("カートリッジが挿さっていません。");

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

        var data = GbaSave.Read(link, DetectType(link));
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

        var type = args.Contains("--flash1m") ? GbaSaveType.Flash1M : DetectType(link);

        if (data.Length != GbaSave.SizeOf(type))
        {
            Console.WriteLine(
                $"ファイルは {data.Length} バイトですが、"
                + $"{GbaSave.DisplayName(type)} は {GbaSave.SizeOf(type)} バイトです。");
            return 3;
        }

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

    /// <summary>
    /// ROM を読んで、セーブ装置の種類を調べる。読むだけ。
    ///
    /// GBA はセーブ装置の種類をヘッダで申告しないので、
    /// ROM の中に残る目印（SRAM_V / FLASH1M_V など）から判定する。
    /// どの種類がまだ実機で確かめられていないかを調べるのに使う。
    /// </summary>
    /// <summary>
    /// セーブを全面同じ値で消す。GUI の「セーブを消去する」と同じ手順。
    ///
    /// 消す前に必ず控えを取る。--restore を付けると、消したあとで
    /// 控えを書き戻して元に戻すところまで確かめる。
    ///
    /// **消す大きさは読み戻せた長さに合わせる。**
    /// マークIII / ゲームギアは容量を申告しないため、
    /// 上限を決め打つと載っていない領域へ折り返す。
    /// </summary>
    private static int Erase(string port, string[] args)
    {
        byte filler = 0xFF;

        int at = Array.IndexOf(args, "--value");
        if (at >= 0 && at + 1 < args.Length)
            filler = Convert.ToByte(args[at + 1], 16);

        bool restore = args.Contains("--restore");

        CartridgeKind kind;

        using (var peek = Open(port, require: null))
            kind = peek.GetStatus().Kind;

        using var link = Open(port, kind);

        var options = new RetroDumper.Core.Dumping.DumpOptions();

        // 機種ごとに、読み書きに必要なものを揃える。
        Func<byte[]> read;
        Action<byte[]> write;
        string label;

        switch (kind)
        {
            case CartridgeKind.GameBoy:
            {
                var dumper = new RetroDumper.Core.Gb.GbDumper();
                var info = dumper.Identify(link, options);

                if (info.GbCartridgeType is not byte cartType || info.SaveMemorySize <= 0)
                {
                    Console.WriteLine("セーブ用の外部 RAM がありません。");
                    return 3;
                }

                label = $"{info.Title} / {info.Mapper} / {info.SaveMemorySize} バイト";
                read = () => GbSave.Read(link, cartType, info.SaveMemorySize);
                write = data => GbSave.Write(link, cartType, data);
                break;
            }

            case CartridgeKind.GameBoyAdvance:
            {
                var dumper = new RetroDumper.Core.Gba.GbaDumper();
                var info = dumper.Identify(link, options);

                Console.WriteLine("ROM を読んでセーブ装置を判定します…");

                var rom = dumper.Dump(link, info, options, null, CancellationToken.None).Rom;
                var type = GbaSave.Detect(rom);

                if (type == GbaSaveType.None)
                {
                    Console.WriteLine("セーブ装置の目印が見つかりませんでした。");
                    return 3;
                }

                if (GbaSave.AlternateEeprom(type) is not null)
                    type = GbaSave.ProbeEepromSize(link, type).Type;

                label = $"{info.Title} / {GbaSave.DisplayName(type)}";
                read = () => GbaSave.Read(link, type);
                write = data => GbaSave.Write(link, type, data);
                break;
            }

            case CartridgeKind.MarkIIIOrGameGear:
            {
                label = "マークIII / ゲームギア";
                read = () => RetroDumper.Core.Sms.SmsSave.Read(link);
                write = data => RetroDumper.Core.Sms.SmsSave.Write(link, data);
                break;
            }

            default:
                Console.WriteLine($"{kind.ToDisplayName()} の消去には対応していません。");
                return 3;
        }

        Console.WriteLine(label);
        Console.WriteLine("消す前に現在のセーブを吸い出します…");

        var original = read();

        string backupPath = $"erase-backup-{DateTime.Now:yyyyMMdd-HHmmss}.sav";
        File.WriteAllBytes(backupPath, original);

        Console.WriteLine($"  {original.Length} バイト。控えを {backupPath} に保存しました。");
        Console.WriteLine($"  先頭 16 バイト: {Convert.ToHexString(original.AsSpan(0, 16))}");

        // 2 回読んで一致するか。接触が怪しいまま書くと被害が大きい。
        var again = read();
        int jitter = 0;

        for (int i = 0; i < Math.Min(original.Length, again.Length); i++)
            if (original[i] != again[i]) jitter++;

        Console.WriteLine($"  読み出しの安定: {(jitter == 0 ? "はい" : $"いいえ（{jitter} バイト違う）")}");

        if (jitter != 0 || original.Length != again.Length)
        {
            Console.WriteLine("読み出しが安定しないため、消去は行いません。");
            return 4;
        }

        Console.WriteLine($"0x{filler:X2} で {original.Length} バイトを消します…");

        link.AllowSaveWrites = true;

        var blank = new byte[original.Length];
        Array.Fill(blank, filler);

        try
        {
            write(blank);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  失敗: {ex.Message}");
            Console.WriteLine($"  控えは {backupPath} にあります。");
            return 5;
        }

        // 書き込み側も照合しているが、独立にもう一度読んで確かめる。
        var after = read();
        int wrong = 0;

        for (int i = 0; i < after.Length; i++) if (after[i] != filler) wrong++;

        Console.WriteLine($"  読み戻し: {after.Length} バイト、" +
                          $"0x{filler:X2} でないもの {wrong} バイト");
        Console.WriteLine($"  先頭 16 バイト: {Convert.ToHexString(after.AsSpan(0, 16))}");

        if (after.Length != original.Length || wrong != 0)
        {
            Console.WriteLine("消去しきれていません。");
            return 6;
        }

        Console.WriteLine("消去できました。");

        if (!restore) return 0;

        Console.WriteLine("控えを書き戻します…");

        try
        {
            write(original);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  書き戻しに失敗: {ex.Message}");
            Console.WriteLine($"  控えは {backupPath} にあります。");
            return 7;
        }

        var restored = read();
        int differs = 0;

        for (int i = 0; i < Math.Min(original.Length, restored.Length); i++)
            if (original[i] != restored[i]) differs++;

        Console.WriteLine($"  書き戻しの一致: {(differs == 0 ? "はい" : $"いいえ（{differs} バイト違う）")}");

        return differs == 0 ? 0 : 8;
    }

    private static int SaveType(string port)
    {
        using var link = Open(port);

        var dumper = new RetroDumper.Core.Gba.GbaDumper();
        var options = new RetroDumper.Core.Dumping.DumpOptions();
        var info = dumper.Identify(link, options);

        Console.WriteLine($"タイトル: {info.Title}");
        Console.WriteLine($"ROM: {info.RomSize / 1024 / 1024} MB");
        Console.WriteLine("ROM を読んでいます…");

        var result = dumper.Dump(link, info, options, null, CancellationToken.None);
        var type = GbaSave.Detect(result.Rom);

        var db = RetroDumper.Core.Database.NoIntroDatabase.Load(log: null);
        var hit = db.Match(result.Rom, result.Rom.Length);

        Console.WriteLine($"No-Intro の名前: {hit?.GameName ?? "（一致なし）"}");
        Console.WriteLine($"セーブ装置: {GbaSave.DisplayName(type)}");

        if (GbaSave.AlternateEeprom(type) is not null)
        {
            var probe = GbaSave.ProbeEepromSize(link, type);

            Console.WriteLine($"  容量の判定: {GbaSave.DisplayName(probe.Type)}"
                + (probe.Determined ? "" : "（決められず）"));
            Console.WriteLine($"  根拠: {probe.Reason}");
        }

        return 0;
    }

    /// <summary>
    /// セーブの読み書きを一通り確かめる。
    ///
    ///   1. ROM を読んでセーブ装置の種類を判定する
    ///   2. セーブを吸い出してファイルに残す（これが控えになる）
    ///   3. **同じ内容を書き戻す**
    ///   4. 読み戻して、吸い出したものと 1 バイトずつ突き合わせる
    ///
    /// 同じ内容を書き戻すので、成功すればカセットの中身は変わらない。
    /// 失敗しても手順 2 の控えが残る。
    /// </summary>
    private static int SaveTest(string port, string[] args)
    {
        string backupPath = args.Length > 2
            ? args[2]
            : $"save-{DateTime.Now:yyyyMMdd-HHmmss}.sav";

        CartridgeKind kind;

        using (var peek = Open(port, require: null))
            kind = peek.GetStatus().Kind;

        if (kind == CartridgeKind.GameBoy)
            return GbSaveTest(port, backupPath, args);

        using var link = Open(port);

        var dumper = new RetroDumper.Core.Gba.GbaDumper();
        var options = new RetroDumper.Core.Dumping.DumpOptions();
        var info = dumper.Identify(link, options);

        Console.WriteLine($"タイトル: {info.Title} / ROM {info.RomSize / 1024 / 1024} MB");
        Console.WriteLine("ROM を読んでセーブ装置を判定します…");

        var rom = dumper.Dump(link, info, options, null, CancellationToken.None).Rom;
        var type = GbaSave.Detect(rom);

        if (type == GbaSaveType.None)
        {
            Console.WriteLine("セーブ装置の目印が見つかりませんでした。");
            return 3;
        }

        if (GbaSave.AlternateEeprom(type) is not null)
        {
            var probe = GbaSave.ProbeEepromSize(link, type);
            Console.WriteLine($"  EEPROM の容量判定: {probe.Reason}");
            type = probe.Type;
        }

        Console.WriteLine($"セーブ装置: {GbaSave.DisplayName(type)} ({GbaSave.SizeOf(type)} バイト)");

        // --- 吸い出し
        Console.WriteLine("セーブを吸い出します…");
        var original = GbaSave.Read(link, type);

        File.WriteAllBytes(backupPath, original);
        Console.WriteLine($"  控えを {backupPath} に保存しました。");

        // --- 読み出しが安定しているか
        var (stable, differences) = GbaSave.CheckReadStability(link, type);

        Console.WriteLine($"  読み出しの安定: {(stable ? "はい" : $"いいえ（{differences} バイト違う）")}");

        if (!stable)
        {
            Console.WriteLine("読み出しが安定しないため、書き込みは行いません。");
            return 4;
        }

        if (type is GbaSaveType.Flash512k or GbaSaveType.Flash1M)
            Console.WriteLine($"  フラッシュ ID: 0x{link.ReadGbaFlashId():X4}");

        if (args.Contains("--readonly"))
        {
            Console.WriteLine("読み出しのみで終了します（書き込みません）。");
            return 0;
        }

        // --- 同じ内容を書き戻す
        Console.WriteLine("同じ内容を書き戻します…");
        link.AllowSaveWrites = true;

        try
        {
            GbaSave.Write(link, type, original);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  失敗: {ex.Message}");
            Console.WriteLine($"  控えは {backupPath} にあります。");
            return 5;
        }

        // --- もう一度読んで突き合わせる
        var after = GbaSave.Read(link, type);

        int bad = 0;
        int firstBad = -1;

        for (int i = 0; i < original.Length; i++)
            if (after[i] != original[i])
            {
                bad++;
                if (firstBad < 0) firstBad = i;
            }

        if (bad == 0)
        {
            Console.WriteLine($"結果: 合格。{GbaSave.DisplayName(type)} の読み書きが通りました。");
            return 0;
        }

        Console.WriteLine($"結果: 不合格。{bad} / {original.Length} バイトが違います。");
        Console.WriteLine($"  最初の食い違い {firstBad:X5}: "
            + $"書いた 0x{original[firstBad]:X2} / 読めた 0x{after[firstBad]:X2}");
        Console.WriteLine($"  控えは {backupPath} にあります。");

        return 6;
    }

    /// <summary>ROM を読んでセーブ装置の種類を決める。</summary>
    private static GbaSaveType DetectType(RfcaLink link)
    {
        var dumper = new RetroDumper.Core.Gba.GbaDumper();
        var options = new RetroDumper.Core.Dumping.DumpOptions();
        var info = dumper.Identify(link, options);

        var rom = dumper.Dump(link, info, options, null, CancellationToken.None).Rom;
        var type = GbaSave.Detect(rom);

        if (GbaSave.AlternateEeprom(type) is not null)
            type = GbaSave.ProbeEepromSize(link, type).Type;

        Console.WriteLine($"セーブ装置: {GbaSave.DisplayName(type)}");
        return type;
    }

    /// <summary>フラッシュの ID を何度か読んで、安定しているかを見る。読むだけ。</summary>
    private static int FlashId(string port)
    {
        using var link = Open(port);

        for (int i = 0; i < 5; i++)
        {
            int id = link.ReadGbaFlashId();
            Console.WriteLine($"  {i + 1} 回目: 0x{id:X4}");
        }

        Console.WriteLine();
        Console.WriteLine("参照実装が認めている ID:");
        Console.WriteLine("  128KB: 0x09C2 (Macronix) / 0x1362 (Sanyo)");
        Console.WriteLine("   64KB: 0x1B32 (Panasonic) / 0x3D1F (Atmel) / 0xD4BF (SST)");

        return 0;
    }

    /// <summary>
    /// フラッシュを指定の分割幅で書き戻し、どこで詰まるかを見る。
    /// 参照実装は 4096 バイトずつ。実機で詰まるので幅を変えて試す。
    /// </summary>
    private static int WriteFlash(string port, string[] args)
    {
        string path = args.Length > 2 ? args[2] : "";
        byte[] data = File.ReadAllBytes(path);

        using var link = Open(port);
        Console.WriteLine($"ポート設定: {link.PortSettings}");

        link.AllowSaveWrites = true;

        if (args.Length > 4 && int.TryParse(args[4], out int timeout))
        {
            link.PayloadWriteTimeout = timeout;
            Console.WriteLine($"本体送信の待ち時間を {timeout}ms にします。");
        }

        var type = data.Length == 65536 ? GbaSaveType.Flash512k : GbaSaveType.Flash1M;

        Console.WriteLine($"{GbaSave.DisplayName(type)} を書き戻します。");

        var progress = new Progress<RetroDumper.Core.Dumping.DumpProgress>(p =>
        {
            if (p.BytesDone % (16 * 1024) == 0)
                Console.WriteLine($"  {p.Stage} {p.BytesDone} / {p.BytesTotal}");
        });

        GbaSave.Write(link, type, data, progress);

        Console.WriteLine("書き込みと照合が通りました。");
        return 0;
    }

    /// <summary>埋め込んだ DAT の読み込み結果を見る。実機は要らない。</summary>
    private static int DatabaseInfo()
    {
        var db = RetroDumper.Core.Database.NoIntroDatabase.Load(
            log: line => Console.WriteLine($"  {line}"));

        Console.WriteLine($"合計 {db.EntryCount} 件");
        return 0;
    }

    /// <summary>
    /// ゲームボーイのセーブを一通り確かめる。
    /// 吸い出し → 同じ内容を書き戻し → 読み戻して突き合わせ。
    /// 同じ内容を書き戻すので、成功すればカセットの中身は変わらない。
    /// </summary>
    private static int GbSaveTest(string port, string backupPath, string[] args)
    {
        using var link = Open(port, CartridgeKind.GameBoy);

        var dumper = new RetroDumper.Core.Gb.GbDumper();
        var options = new RetroDumper.Core.Dumping.DumpOptions();
        var info = dumper.Identify(link, options);

        Console.WriteLine($"タイトル: {info.Title}");
        Console.WriteLine($"MBC: {info.Mapper}（種別 0x{info.GbCartridgeType:X2}）");
        Console.WriteLine($"ROM: {info.RomSize / 1024} KB");
        Console.WriteLine($"セーブ: {info.SaveMemorySize} バイト");

        if (info.GbCartridgeType is not byte cartType || info.SaveMemorySize <= 0)
        {
            Console.WriteLine("セーブ用の外部 RAM がありません。");
            return 3;
        }

        Console.WriteLine("セーブを吸い出します…");

        var original = GbSave.Read(link, cartType, info.SaveMemorySize);
        File.WriteAllBytes(backupPath, original);

        Console.WriteLine($"  控えを {backupPath} に保存しました。");
        Console.WriteLine($"  先頭 16 バイト: {Convert.ToHexString(original.AsSpan(0, 16))}");

        // 2 回読んで一致するかを見る。
        var again = GbSave.Read(link, cartType, info.SaveMemorySize);
        int jitter = 0;

        for (int i = 0; i < original.Length; i++) if (original[i] != again[i]) jitter++;

        Console.WriteLine($"  読み出しの安定: {(jitter == 0 ? "はい" : $"いいえ（{jitter} バイト違う）")}");

        if (jitter != 0)
        {
            Console.WriteLine("読み出しが安定しないため、書き込みは行いません。");
            return 4;
        }

        if (args.Contains("--readonly"))
        {
            Console.WriteLine("読み出しのみで終了します（書き込みません）。");
            return 0;
        }

        Console.WriteLine("同じ内容を書き戻します…");
        link.AllowSaveWrites = true;

        try
        {
            GbSave.Write(link, cartType, original);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  失敗: {ex.Message}");
            Console.WriteLine($"  控えは {backupPath} にあります。");
            return 5;
        }

        var after = GbSave.Read(link, cartType, info.SaveMemorySize);

        int bad = 0;
        int firstBad = -1;

        for (int i = 0; i < original.Length; i++)
            if (after[i] != original[i])
            {
                bad++;
                if (firstBad < 0) firstBad = i;
            }

        if (bad == 0)
        {
            Console.WriteLine($"結果: 合格。{info.Mapper} の読み書きが通りました。");
            return 0;
        }

        Console.WriteLine($"結果: 不合格。{bad} / {original.Length} バイトが違います。");
        Console.WriteLine($"  最初の食い違い {firstBad:X4}: "
            + $"書いた 0x{original[firstBad]:X2} / 読めた 0x{after[firstBad]:X2}");
        Console.WriteLine($"  控えは {backupPath} にあります。");

        return 6;
    }

    /// <summary>ゲームボーイのヘッダ ($0100-$014F) を生のまま見る。読むだけ。</summary>
    private static int GbHeader(string port)
    {
        using var link = Open(port, CartridgeKind.GameBoy);

        // ロゴがどこにあるかを探す。読む位置がずれていれば、ずれ幅が分かる。
        var wide = link.Read(RfcaOpcode.GameBoyRead, 0x0000, 0x4000);
        var logo = new byte[] { 0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B };
        int at = wide.AsSpan().IndexOf(logo);

        Console.WriteLine($"ロゴの位置: {(at < 0 ? "見つからない" : $"0x{at:X4}（本来は 0x0104）")}");
        Console.WriteLine($"0x0000 から 16 バイト: {Convert.ToHexString(wide.AsSpan(0, 16))}");

        if (at >= 0)
        {
            int baseAt = at - 0x104;
            Console.WriteLine($"  ずれ幅: {at - 0x104:+#;-#;0} バイト");
            if (baseAt + 0x150 <= wide.Length && baseAt >= 0)
                Console.WriteLine($"  そこを基準にしたタイトル: " +
                    System.Text.Encoding.ASCII.GetString(wide.AsSpan(baseAt + 0x134, 16)).Replace(' ', '.'));
        }

        // アドレス線が 1 本でも接触していないと、違う番地が同じ内容に見える。
        Console.WriteLine();
        Console.WriteLine("アドレス線の確認（同じ内容なら、その線が効いていない）:");

        var baseline = link.Read(RfcaOpcode.GameBoyRead, 0x0000, 16);

        for (int bit = 0; bit < 14; bit++)
        {
            uint address = 1u << bit;
            var other = link.Read(RfcaOpcode.GameBoyRead, address, 16);
            bool same = other.AsSpan().SequenceEqual(baseline);

            Console.WriteLine($"  A{bit,-2} (0x{address:X4}): {(same ? "同じ ← 怪しい" : "違う")}");
        }

        Console.WriteLine();

        for (int attempt = 1; attempt <= 1; attempt++)
        {
            var head = link.Read(RfcaOpcode.GameBoyRead, 0x0100, 0x50);

            Console.WriteLine($"--- {attempt} 回目");
            Console.WriteLine($"  タイトル欄 (0x134-0x143): {Convert.ToHexString(head.AsSpan(0x34, 16))}");
            Console.WriteLine($"  文字として: {System.Text.Encoding.ASCII.GetString(head.AsSpan(0x34, 16)).Replace(' ', '.')}");
            Console.WriteLine($"  種別 0x147 = 0x{head[0x47]:X2} / ROM 0x148 = 0x{head[0x48]:X2} / RAM 0x149 = 0x{head[0x49]:X2}");
            Console.WriteLine($"  ロゴ (0x104-0x10B): {Convert.ToHexString(head.AsSpan(0x04, 8))}  （正: CEED6666CC0D000B）");
        }

        return 0;
    }

    /// <summary>
    /// 挿さっているカセットの素性を手早く調べる。読むだけ。
    ///
    /// GB / GBC はヘッダ 1 バイトで MBC が決まるので一瞬で終わる。
    /// GBA はセーブ装置の目印を ROM から探す必要があるが、
    /// **見つかった時点で打ち切る**。多くのソフトは目印が前半にあるため、
    /// 全部読むより早く終わる。棚から順に挿して調べるときのための入口。
    /// </summary>
    private static int Survey(string port)
    {
        using var link = Open(port, require: null);

        var kind = link.GetStatus().Kind;

        if (kind == CartridgeKind.GameBoy)
        {
            var gb = new RetroDumper.Core.Gb.GbDumper();
            var info = gb.Identify(link, new RetroDumper.Core.Dumping.DumpOptions());

            Console.WriteLine($"タイトル: {info.Title}");
            Console.WriteLine($"MBC: {info.Mapper}（種別 0x{info.GbCartridgeType:X2}）");
            Console.WriteLine($"ROM: {info.RomSize / 1024} KB");
            Console.WriteLine($"セーブ: {info.SaveMemorySize} バイト");

            return 0;
        }

        if (kind == CartridgeKind.PcEngineHuCard)
        {
            var pce = new RetroDumper.Core.Pce.PceDumper();
            var card = pce.Identify(link, new RetroDumper.Core.Dumping.DumpOptions());

            Console.WriteLine($"バンク配置: {card.Mapper}");
            Console.WriteLine($"先頭 16 バイト: {Convert.ToHexString(card.RawHeader.AsSpan(0, 16))}");

            foreach (string warning in card.Warnings) Console.WriteLine($"  注意: {warning}");

            return 0;
        }

        if (kind != CartridgeKind.GameBoyAdvance)
        {
            Console.WriteLine($"{kind.ToDisplayName()} は survey に未対応です。");
            return 1;
        }

        var dumper = new RetroDumper.Core.Gba.GbaDumper();
        var header = dumper.Identify(link, new RetroDumper.Core.Dumping.DumpOptions());

        Console.WriteLine($"タイトル: {header.Title}");
        Console.WriteLine($"ROM: {header.RomSize / 1024 / 1024} MB");
        Console.Write("セーブ装置の目印を探しています");

        // 目印が見つかるまで 256KB ずつ読む。見つかったら打ち切る。
        const int block = 256 * 1024;
        var found = GbaSaveType.None;

        for (long at = 0; at < header.RomSize && found == GbaSaveType.None; at += block)
        {
            int length = (int)Math.Min(block, header.RomSize - at);

            // 目印が境目をまたいでも拾えるよう、少し戻って読む。
            long from = Math.Max(0, at - 32);
            var part = link.Read(RfcaOpcode.GbaRomRead, (uint)from, (int)(at - from) + length);

            found = GbaSave.Detect(part);
            Console.Write(".");
        }

        Console.WriteLine();

        if (found == GbaSaveType.None)
        {
            Console.WriteLine("セーブ装置: 目印が見つかりません（セーブしないソフトか独自方式）");
            return 0;
        }

        if (GbaSave.AlternateEeprom(found) is not null)
        {
            var probe = GbaSave.ProbeEepromSize(link, found);
            Console.WriteLine($"  EEPROM の容量判定: {probe.Reason}");
            found = probe.Type;
        }

        Console.WriteLine($"セーブ装置: {GbaSave.DisplayName(found)} ({GbaSave.SizeOf(found)} バイト)");
        return 0;
    }

    /// <summary>
    /// Hu カードを吸い出してファイルに保存し、No-Intro と照合する。読むだけ。
    /// Hu カードはタイトルを持たないので、名前は照合でしか分からない。
    /// </summary>
    private static int PceDump(string port, string[] args)
    {
        string path = args.Length > 2 ? args[2] : "hucard.pce";

        using var link = Open(port, CartridgeKind.PcEngineHuCard);

        var dumper = new RetroDumper.Core.Pce.PceDumper();
        var options = new RetroDumper.Core.Dumping.DumpOptions();
        var info = dumper.Identify(link, options);

        Console.WriteLine($"バンク配置: {info.Mapper}");
        Console.WriteLine($"先頭 16 バイト: {Convert.ToHexString(info.RawHeader.AsSpan(0, 16))}");
        Console.WriteLine("吸い出しています…");

        var progress = new Progress<RetroDumper.Core.Dumping.DumpProgress>(p =>
        {
            if (p.BytesDone % (128 * 1024) == 0)
                Console.WriteLine($"  {p.BytesDone / 1024} KB");
        });

        var result = dumper.Dump(link, info, options, progress, CancellationToken.None);

        File.WriteAllBytes(path, result.Rom);

        Console.WriteLine($"{result.Rom.Length / 1024} KB を {path} に保存しました。");
        Console.WriteLine($"CRC32: {result.Crc32:X8}");

        var db = RetroDumper.Core.Database.NoIntroDatabase.Load(log: null);
        var hit = db.Match(result.Rom, result.Rom.Length);

        Console.WriteLine($"No-Intro の名前: {hit?.GameName ?? "（一致なし）"}");

        if (hit is null)
        {
            int sameSize = db.CountWithSize(result.Rom.Length);

            Console.WriteLine(sameSize == 0
                ? $"  この容量 ({result.Rom.Length} バイト) のソフトは DAT に 1 本も無い。容量の判定が違う。"
                : $"  この容量のソフトは DAT に {sameSize} 本ある。中身が違う。");
        }

        return hit is null ? 3 : 0;
    }

    private static bool AllSame(byte[] data)
    {
        foreach (byte b in data) if (b != data[0]) return false;
        return true;
    }
}
