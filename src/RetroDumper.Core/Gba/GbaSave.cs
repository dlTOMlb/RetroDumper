using System.Text;
using RetroDumper.Core.Dumping;
using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Gba;

/// <summary>GBA のセーブ装置の種類。</summary>
public enum GbaSaveType
{
    None,
    Sram,
    Fram,
    Eeprom4k,
    Eeprom64k,
    Flash512k,
    Flash1M,
}

/// <summary>
/// GBA のセーブデータの吸い出しと書き込み。
///
/// GBA のカートリッジはセーブ装置の種類をヘッダで申告しない。
/// 代わりに、ROM の中に開発キットのライブラリが残した目印の文字列がある。
/// "SRAM_V" や "FLASH1M_V" といった並びで、これを探して種類を決める。
/// 手順は参照実装の SaveDataController 各実装に合わせた。
///
/// **書き込みはセーブ専用の opcode だけを使う。**
/// GBA は ROM とセーブで opcode が別系統になっており、
/// ROM へ書く opcode はそもそも存在しない。
/// したがってこの経路から ROM を壊すことはできない。
/// </summary>
public static class GbaSave
{
    /// <summary>ROM 内に残る目印。先に一致したものを採る（順序に意味がある）。</summary>
    private static readonly (string Signature, GbaSaveType Type)[] Signatures =
    [
        ("SRAM_F_V", GbaSaveType.Fram),       // SRAM_V より先に見ること
        ("SRAM_V", GbaSaveType.Sram),
        ("EEPROM_V", GbaSaveType.Eeprom64k),
        ("FLASH1M_V", GbaSaveType.Flash1M),   // FLASH_V より先に見ること
        ("FLASH512_V", GbaSaveType.Flash512k),
        ("FLASH_V", GbaSaveType.Flash512k),
    ];

    /// <summary>その種類のセーブ装置の容量（バイト）。</summary>
    public static int SizeOf(GbaSaveType type) => type switch
    {
        GbaSaveType.Sram => 32 * 1024,
        GbaSaveType.Fram => 32 * 1024,
        GbaSaveType.Eeprom4k => 512,
        GbaSaveType.Eeprom64k => 8 * 1024,
        GbaSaveType.Flash512k => 64 * 1024,
        GbaSaveType.Flash1M => 128 * 1024,
        _ => 0,
    };

    public static string DisplayName(GbaSaveType type) => type switch
    {
        GbaSaveType.Sram => "SRAM 32KB",
        GbaSaveType.Fram => "FRAM 32KB",
        GbaSaveType.Eeprom4k => "EEPROM 512B",
        GbaSaveType.Eeprom64k => "EEPROM 8KB",
        GbaSaveType.Flash512k => "フラッシュ 64KB",
        GbaSaveType.Flash1M => "フラッシュ 128KB",
        _ => "なし",
    };

    /// <summary>
    /// ROM の中から目印を探して種類を決める。
    ///
    /// 目印が無いソフトもある（セーブしない、または独自実装）。
    /// その場合は None を返す。利用者が手で指定できる余地を残すこと。
    /// </summary>
    public static GbaSaveType Detect(ReadOnlySpan<byte> rom)
    {
        foreach (var (signature, type) in Signatures)
            if (IndexOf(rom, Encoding.ASCII.GetBytes(signature)) >= 0)
                return type;

        return GbaSaveType.None;
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
        => haystack.IndexOf(needle);

    /// <summary>
    /// EEPROM のもう一方の容量。区別が付かないとき用。
    ///
    /// 目印は 4kbit も 64kbit も同じ "EEPROM_V" で、**容量が書かれていない。**
    /// 参照実装も一律 64kbit と判定し、4kbit は利用者の手動選択に任せている。
    /// EEPROM 以外は目印で容量まで決まるので、ここでは扱わない。
    /// </summary>
    public static GbaSaveType? AlternateEeprom(GbaSaveType type) => type switch
    {
        GbaSaveType.Eeprom64k => GbaSaveType.Eeprom4k,
        GbaSaveType.Eeprom4k => GbaSaveType.Eeprom64k,
        _ => null,
    };

    /// <summary>
    /// EEPROM の容量を読んで絞り込んだ結果。
    ///
    /// Determined が false のときは根拠が得られていない。
    /// その場合 Type は既定の 64kbit のままで、呼び出し側は別の手掛かり
    /// （書き込むファイルの大きさなど）を使ってよい。
    /// </summary>
    public readonly record struct EepromProbe(GbaSaveType Type, bool Determined, string Reason);

    /// <summary>
    /// EEPROM の容量を、読んだ内容から判定する。
    ///
    /// 目印は 4kbit も 64kbit も同じ "EEPROM_V" で、**容量が書かれていない。**
    /// 参照実装も一律 64kbit と判定し、4kbit は利用者の手動選択に任せている。
    /// だが読めば分かることが多い。4kbit の石を 8KB として読むと、
    /// アドレスの上位が無視されるぶん、次のどちらかの形で現れる。
    ///
    ///   ・512 バイトごとに同じ内容が折り返して見える
    ///   ・先頭 512 バイトにだけ内容があり、その後ろは全部同じ値になる
    ///
    /// どちらも 64kbit では起きにくい形なので、根拠として使える。
    /// 逆に 512 バイトより後ろに違う内容があれば 64kbit と断定できる。
    ///
    /// 中身が空（全バイト同じ）のときだけは、どちらでも同じに見えるため
    /// 判断できない。**読むだけで、書き込みは一切行わない。**
    /// </summary>
    public static EepromProbe ProbeEepromSize(
        IRfcaLink link, GbaSaveType type, CancellationToken cancellationToken = default)
    {
        if (AlternateEeprom(type) is null)
            return new EepromProbe(type, true, "");

        byte[] data;

        try
        {
            data = Read(link, GbaSaveType.Eeprom64k, null, cancellationToken);
        }
        catch (RfcaException ex)
        {
            return new EepromProbe(
                GbaSaveType.Eeprom64k, false, $"読み出しに失敗しました ({ex.Message})");
        }

        int small = SizeOf(GbaSaveType.Eeprom4k);

        if (IsFlat(data))
            return new EepromProbe(
                GbaSaveType.Eeprom64k, false,
                "セーブの中身が空のため、512B と 8KB を読み分けられません");

        if (RepeatsEvery(data, small))
            return new EepromProbe(
                GbaSaveType.Eeprom4k, true,
                $"{small} バイトごとに同じ内容が折り返しています");

        if (IsFlat(data.AsSpan(small)))
            return new EepromProbe(
                GbaSaveType.Eeprom4k, true,
                $"先頭 {small} バイトにだけ内容があり、その後ろは空です");

        return new EepromProbe(
            GbaSaveType.Eeprom64k, true,
            $"{small} バイトより後ろにも内容があります");
    }

    private static bool IsFlat(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return true;

        foreach (byte b in data)
            if (b != data[0]) return false;

        return true;
    }

    /// <summary>その長さごとに同じ内容が繰り返しているか。</summary>
    private static bool RepeatsEvery(ReadOnlySpan<byte> data, int period)
    {
        if (period <= 0 || data.Length <= period) return false;

        var first = data[..period];

        for (int at = period; at + period <= data.Length; at += period)
            if (!data.Slice(at, period).SequenceEqual(first)) return false;

        return true;
    }

    /// <summary>セーブデータを読む。書き込みは一切行わない。</summary>
    public static byte[] Read(
        IRfcaLink link, GbaSaveType type,
        IProgress<DumpProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int size = SizeOf(type);

        if (size == 0)
            throw new RfcaException(
                "セーブ装置の種類が分かりません。" +
                "ROM に目印が無いため、種類を手で指定してください。");

        // 参照実装はセーブの読み書きの前に必ずスロットを選び直す。
        link.ReinitializeSlot();

        uint opcode = ReadOpcodeFor(type);
        var result = new byte[size];
        int block = ReadBlockSize(type);
        int done = 0;

        while (done < size)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int length = Math.Min(block, size - done);
            link.Read(opcode, (uint)done, result.AsSpan(done, length));

            done += length;
            progress?.Report(new DumpProgress($"セーブ ({DisplayName(type)})", done, size));
        }

        return result;
    }

    /// <summary>
    /// セーブデータを書き込み、読み戻して照合する。
    ///
    /// 照合まで済ませて初めて成功とする。書けたつもりで壊れているのが
    /// いちばん困るため、参照実装も同じく 1 バイトずつ突き合わせている。
    /// </summary>
    public static void Write(
        IRfcaLink link, GbaSaveType type, byte[] data,
        IProgress<DumpProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int size = SizeOf(type);

        if (size == 0)
            throw new RfcaException("セーブ装置の種類が分かりません。");


        if (data.Length > size)
            throw new RfcaException(
                $"セーブデータが大きすぎます。" +
                $"{DisplayName(type)} は {size} バイトですが、{data.Length} バイト渡されました。");

        // 装置より小さいファイルは、その分だけ書く。残りは触らない。
        // 参照実装も同じ扱いで、短いファイルを拒まない。
        //
        // ただし EEPROM は別。容量によって通信のアドレス幅が変わるため、
        // 「8KB の装置に 512 バイトだけ書く」という操作は成立しない。
        // 書きは 512 バイトのつもり、読み戻しは 8KB のつもり、と
        // 噛み合わなくなり、照合が必ず失敗する（2026-09-24 実機）。
        if (IsEeprom(type))
        {
            if (data.Length != size)
                throw new RfcaException(
                    $"EEPROM は容量ごとに通信の仕方が変わるため、" +
                    $"途中までの書き込みができません。" +
                    $"{DisplayName(type)} には {size} バイトちょうどが要りますが、" +
                    $"{data.Length} バイト渡されました。");
        }
        else
        {
            size = data.Length;
        }

        // 参照実装はセーブの読み書きの前に必ずスロットを選び直す。
        link.ReinitializeSlot();

        // **書く前に、読み出しが安定しているかを確かめる。**
        //
        // 2026-09-24 の実機で、同じカセットを 2 回読んで 2 バイト違った。
        // 読み出しが揺れていると照合そのものが成立せず、
        // 「書けたのか壊したのか」を判断できないまま書くことになる。
        // それは利用者のセーブを賭けるに値しない。安定してから書く。
        // 装置を問わず、書く前に読み出しが安定しているかを確かめる。
        {
            var (stable, differences) = CheckReadStability(link, type, cancellationToken);

            if (!stable)
                throw new RfcaException(
                    $"読み出しが安定していないため、書き込みを中止しました。" +
                    Environment.NewLine +
                    $"同じ内容を 2 回読んで {differences} バイト違いました。" +
                    Environment.NewLine +
                    "この状態で書くと、書けたのか壊したのかを判断できません。" +
                    "カートリッジを挿し直し、端子を清掃してからお試しください。");

            progress?.Report(new DumpProgress("読み出しの安定を確認しました", 0, size));
        }

        if (type is GbaSaveType.Flash512k or GbaSaveType.Flash1M)
        {
            EnsureKnownFlash(link, type);

            // **ID を読んだらスロットを選び直す。**
            //
            // ID の読み出しは石を ID モードに入れる。そのまま書き込むと、
            // 要求には受理応答が返るのに本体を引き取ってもらえず、
            // 送信が詰まったままアダプタが USB から落ちる（2026-09-24 実機）。
            // ID を読まずに書けば通ることで、原因がここだと確かめた。
            // 選び直し (0x04 → 0x05 → 200ms) を挟めば、ID を確認したうえで書ける。
            // ポケモン エメラルド（Sanyo 0x1362、128KB）で照合まで通ることを確認済み。
            link.ReinitializeSlot();
        }

        if (type == GbaSaveType.Eeprom4k)
        {
            WriteEeprom4k(link, data, progress, cancellationToken);
        }
        else
        {
            uint opcode = WriteOpcodeFor(type);
            int block = WriteBlockSize(type);
            int done = 0;

            while (done < size)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int length = Math.Min(block, size - done);

                link.WriteSaveMemory(
                    CartridgeKind.GameBoyAdvance, opcode, (uint)done,
                    data.AsSpan(done, length));

                done += length;
                progress?.Report(new DumpProgress($"セーブ書き込み ({DisplayName(type)})", done, size));
            }
        }

        progress?.Report(new DumpProgress("書き込んだ内容を照合中", 0, size));

        // 読み戻しは装置いっぱいで行い、書いた分だけを突き合わせる。
        var readBack = Read(link, type, progress, cancellationToken);

        int at = FirstDifference(readBack, data, size);
        if (at < 0) return;

        // 食い違った。書き込みの失敗と、読み出しの揺れを区別する。
        // もう一度読んで、2 回の読み出しが一致するかを見る。
        link.ReinitializeSlot();

        var again = Read(link, type, progress, cancellationToken);

        if (FirstDifference(again, readBack, size) >= 0)
            throw new RfcaException(
                "書き込みは終えましたが、読み出しが安定しないため確認できません。" +
                Environment.NewLine +
                "同じ内容を 2 回読んで結果が違いました。" +
                "カートリッジのセーブが壊れているかどうかは、この結果からは分かりません。" +
                Environment.NewLine +
                "挿し直してから、もう一度書き込んでください。");

        int mismatch = FirstDifference(again, data, size);

        if (mismatch < 0) return;

        throw new RfcaException(
            $"照合に失敗しました。{mismatch:X5} 番地は 0x{data[mismatch]:X2} を書いたはずですが " +
            $"0x{again[mismatch]:X2} が読めました（2 回読んで同じ結果）。" +
            "カートリッジのセーブデータが中途半端な状態になっている可能性があります。");
    }

    /// <summary>最初に食い違う位置。すべて同じなら -1。</summary>
    private static int FirstDifference(byte[] left, byte[] right, int length)
    {
        for (int i = 0; i < length; i++)
            if (left[i] != right[i]) return i;

        return -1;
    }

    /// <summary>
    /// 読み出しが安定しているかを見る。2 回読んで突き合わせるだけ。
    ///
    /// 書き込みは一切行わない。カートリッジの内容も変えない。
    /// </summary>
    public static (bool Stable, int Differences) CheckReadStability(
        IRfcaLink link, GbaSaveType type, CancellationToken cancellationToken = default)
    {
        var first = Read(link, type, null, cancellationToken);

        link.ReinitializeSlot();

        var second = Read(link, type, null, cancellationToken);

        int differences = 0;

        for (int i = 0; i < first.Length; i++)
            if (first[i] != second[i]) differences++;

        return (differences == 0, differences);
    }

    /// <summary>
    /// 知らないフラッシュに書かない。
    ///
    /// 石ごとに書き込み手順が違うため、対応表に無い ID のものへ書くと
    /// 書けたように見えて壊れることがある。ID は参照実装の対応表に合わせた。
    /// </summary>
    private static void EnsureKnownFlash(IRfcaLink link, GbaSaveType type)
    {
        // ID の読み出しは取りこぼすことがある。
        //
        // ポケモン エメラルド（Sanyo 0x1362）を 5 回読んだところ、
        // 0x1362 と 0x6262 が交互に出た。0x6262 は 2 バイト目が
        // 1 バイト目（メーカー番号 0x62）の繰り返しになったもので、
        // 石が変わったのではなく読み取りの取りこぼし。
        // 1 回で決めると、対応している石を弾いてしまう。
        var seen = new List<int>();

        for (int attempt = 0; attempt < FlashIdAttempts; attempt++)
        {
            int id = link.ReadGbaFlashId();
            seen.Add(id);

            if (IsKnownFlash(type, id)) return;
        }

        throw new RfcaException(
            $"対応していないフラッシュです (ID {string.Join(" / ", seen.Select(v => $"0x{v:X4}"))})。" +
            "書き込むと壊すおそれがあるため中止しました。吸い出しは行えます。");
    }

    /// <summary>ID の読み直し回数。取りこぼしても既知の値が出れば認める。</summary>
    private const int FlashIdAttempts = 5;

    /// <summary>対応表にある石か。値は参照実装の対応表に合わせた。</summary>
    private static bool IsKnownFlash(GbaSaveType type, int id) => type switch
    {
        GbaSaveType.Flash512k => id is 0x1B32 or 0x3D1F or 0xD4BF,
        GbaSaveType.Flash1M => id is 0x09C2 or 0x1362,
        _ => false,
    };

    /// <summary>EEPROM の 1 ブロック。GBA の EEPROM は 64 ビット単位で読み書きする。</summary>
    private const int EepromBlockSize = 8;

    /// <summary>
    /// 4kbit の EEPROM へ書く。**アダプタの癖を逆手に取る。**
    ///
    /// 2026-09-24、実機で次のことが分かった。
    /// アダプタは EEPROM の書き込みで、**常に 64kbit 用の
    /// 14 ビットアドレス**を送っている。4kbit の石はアドレスを
    /// 6 ビットしか見ないため、こうなる。
    ///
    ///   ・上位 6 ビットだけがブロック番号として使われる
    ///   ・余った下位 8 ビットが、データの 1 バイト目として取り込まれる
    ///   ・本体のデータは 1 バイト分押し出され、最後の 1 バイトが落ちる
    ///
    /// そのまま送ると、どのブロックを指定してもブロック 0 にしか書けない
    /// （ブロック番号 n の 14 ビット値は n で、上位 6 ビットは常に 0 のため）。
    /// 実際、64 ブロックを書くと最後の 1 つだけがブロック 0 に残った。
    ///
    /// ならばアドレス欄をこう組み立てればよい。
    ///
    ///   上位 6 ビット = 書きたいブロック番号
    ///   下位 8 ビット = 書きたい 1 バイト目
    ///
    /// 本体には 2 バイト目以降の 7 バイトだけを渡す。
    /// 実機で全ブロックに狙いどおり書けることを確認済み。
    ///
    /// 64kbit の石ではアダプタのアドレス幅と石が一致するので、
    /// この細工は要らない。そちらは素直に書く。
    /// </summary>
    private static void WriteEeprom4k(
        IRfcaLink link, byte[] data,
        IProgress<DumpProgress>? progress, CancellationToken cancellationToken)
    {
        int blocks = data.Length / EepromBlockSize;

        for (int block = 0; block < blocks; block++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var content = data.AsSpan(block * EepromBlockSize, EepromBlockSize);

            // 14 ビットのアドレス欄に、ブロック番号と 1 バイト目を詰める。
            uint field = ((uint)block << 8) | content[0];

            link.WriteSaveMemory(
                CartridgeKind.GameBoyAdvance, RfcaOpcode.GbaEepromWrite,
                field * EepromBlockSize, content[1..]);

            progress?.Report(new DumpProgress(
                "セーブ書き込み (EEPROM 512B)",
                (block + 1) * EepromBlockSize, data.Length));
        }
    }

    /// <summary>EEPROM か。容量で通信の仕方が変わる唯一の装置。</summary>
    public static bool IsEeprom(GbaSaveType type)
        => type is GbaSaveType.Eeprom4k or GbaSaveType.Eeprom64k;

    /// <summary>
    /// 書き込むファイルの大きさに合う EEPROM の型へ読み替える。
    ///
    /// EEPROM は ROM の目印では容量が分からない。ファイルの大きさが
    /// もう一方の容量にちょうど一致するなら、そちらが正しい可能性が高い。
    /// 読み替えないまま書くと、書きと読み戻しでアドレス幅が食い違う。
    /// </summary>
    public static GbaSaveType MatchEepromToSize(GbaSaveType type, int length)
    {
        if (!IsEeprom(type) || length == SizeOf(type)) return type;

        return AlternateEeprom(type) is GbaSaveType other && length == SizeOf(other)
            ? other
            : type;
    }

    private static uint ReadOpcodeFor(GbaSaveType type) => type switch
    {
        GbaSaveType.Sram or GbaSaveType.Fram => RfcaOpcode.GbaSramRead,
        GbaSaveType.Eeprom4k or GbaSaveType.Eeprom64k => RfcaOpcode.GbaEepromRead,
        GbaSaveType.Flash512k or GbaSaveType.Flash1M => RfcaOpcode.GbaFlashRead,
        _ => throw new RfcaException("セーブ装置の種類が分かりません。"),
    };

    private static uint WriteOpcodeFor(GbaSaveType type) => type switch
    {
        GbaSaveType.Sram or GbaSaveType.Fram => RfcaOpcode.GbaSramWrite,
        GbaSaveType.Eeprom4k or GbaSaveType.Eeprom64k => RfcaOpcode.GbaEepromWrite,
        GbaSaveType.Flash512k or GbaSaveType.Flash1M => RfcaOpcode.GbaFlashWrite,
        _ => throw new RfcaException("セーブ装置の種類が分かりません。"),
    };

    /// <summary>読み出しは一括でよい。</summary>
    private static int ReadBlockSize(GbaSaveType type) => SizeOf(type);

    /// <summary>
    /// 書き込みの単位。参照実装に合わせる。
    /// EEPROM は 512 バイト、フラッシュは 4KB ごと。SRAM/FRAM は一括。
    /// </summary>
    private static int WriteBlockSize(GbaSaveType type) => type switch
    {
        GbaSaveType.Eeprom4k or GbaSaveType.Eeprom64k => 512,
        GbaSaveType.Flash512k or GbaSaveType.Flash1M => 4096,

        // SRAM / FRAM は一括ではなく 8192 バイトずつ。
        // 参照実装の Command.Write が、どの装置でも本体を 8192 で区切っている。
        _ => Math.Min(SizeOf(type), MaxPayload),
    };

    /// <summary>1 コマンドで送る本体の上限。参照実装の Command.Write に合わせた。</summary>
    private const int MaxPayload = 8192;
}
