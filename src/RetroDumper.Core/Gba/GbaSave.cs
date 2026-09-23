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
/// 手順は RetroFreakDumper の SaveDataController 各実装に合わせた。
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

        if (data.Length != size)
            throw new RfcaException(
                $"セーブデータの大きさが合いません。" +
                $"{DisplayName(type)} は {size} バイトですが、{data.Length} バイト渡されました。");

        if (type is GbaSaveType.Flash512k or GbaSaveType.Flash1M)
            EnsureKnownFlash(link, type);

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

        progress?.Report(new DumpProgress("書き込んだ内容を照合中", 0, size));

        var readBack = Read(link, type, progress, cancellationToken);

        for (int i = 0; i < size; i++)
            if (readBack[i] != data[i])
                throw new RfcaException(
                    $"照合に失敗しました。{i:X5} 番地は 0x{data[i]:X2} を書いたはずですが " +
                    $"0x{readBack[i]:X2} が読めました。" +
                    "カートリッジのセーブデータが中途半端な状態になっている可能性があります。");
    }

    /// <summary>
    /// 知らないフラッシュに書かない。
    ///
    /// 石ごとに書き込み手順が違うため、対応表に無い ID のものへ書くと
    /// 書けたように見えて壊れることがある。ID は参照実装の対応表に合わせた。
    /// </summary>
    private static void EnsureKnownFlash(IRfcaLink link, GbaSaveType type)
    {
        int id = link.ReadGbaFlashId();

        bool known = type == GbaSaveType.Flash512k
            ? id is 0x1B32 or 0x3D1F or 0xD4BF
            : id is 0x09C2 or 0x1362;

        if (!known)
            throw new RfcaException(
                $"対応していないフラッシュです (ID 0x{id:X4})。" +
                "書き込むと壊すおそれがあるため中止しました。吸い出しは行えます。");
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
        _ => SizeOf(type),
    };
}
