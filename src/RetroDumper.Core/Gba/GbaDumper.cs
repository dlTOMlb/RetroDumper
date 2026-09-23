using System.Text;
using RetroDumper.Core.Dumping;
using RetroDumper.Core.Transport;
using RetroDumper.Core.Util;

namespace RetroDumper.Core.Gba;

/// <summary>
/// ゲームボーイアドバンスの吸い出し。
///
/// GBA スロットのリード opcode は未確定のため、
/// <see cref="RfcaOpcode.GbaRead"/> に探索結果を設定してから使う。
///
/// GBA カセットは ROM サイズを申告するヘッダ欄を持たない。
/// 容量判定はミラー検出（ROM 末尾を超えるとアドレスが折り返して
/// 先頭と同じ内容が出る）と、オープンバス検出（A/D バスの残留値として
/// アドレス/2 が読める）の 2 段構えで行う。
/// </summary>
public sealed class GbaDumper : ICartridgeDumper
{
    public string Name => "ゲームボーイアドバンス";

    /// <summary>
    /// GBA の種別コード。状態応答 byte[8] が 0x06 になることを実機で確認済み。
    /// </summary>
    public static CartridgeKind DetectedKind { get; set; } = CartridgeKind.GameBoyAdvance;

    public CartridgeKind Kind => DetectedKind;

    public bool IsReady => RfcaOpcode.GbaRead is not null;

    public string ReadinessDetail => IsReady
        ? ""
        : "GBA スロットのリード opcode が未確定です。先に opcode 探索を実行してください。";

    /// <summary>
    /// カートリッジバス上での ROM 先頭アドレス。実機で確認済み。
    ///
    /// GBA の CPU から見ると ROM は 0x08000000 に見えるが、それは
    /// メモリマップ上の話であって、カートリッジのコネクタには
    /// 0x08000000 という番地は存在しない。RFCA は他機種と同じく
    /// カートリッジバス上のアドレスを受け取るので、起点は 0。
    /// </summary>
    public const uint DefaultRomBase = 0x00000000;

    /// <summary>
    /// セーブ領域 (SRAM/Flash) の先頭アドレス。未検証。
    /// CPU 側のメモリマップでは 0x0E000000 だが、
    /// ROM 側が 0 起点だったことを踏まえると別の値の可能性が高い。
    /// </summary>
    public const uint SaveBase = 0x0E000000;

    public const int HeaderSize = 0xC0;

    /// <summary>ミラー検出で試す容量。GBA カセットは 1MB～32MB。</summary>
    private static readonly long[] CandidateSizes =
    [
        1 << 20, 2 << 20, 4 << 20, 8 << 20, 16 << 20, 32 << 20,
    ];

    public CartridgeInfo Identify(IRfcaLink link, DumpOptions options)
    {
        RequireReady();

        uint romBase = options.GbaRomBase ?? DefaultRomBase;
        uint opcode = RfcaOpcode.GbaRead!.Value;

        var header = ReadHeaderBlock(link, opcode, romBase);

        string title = DecodeAscii(header.AsSpan(0xA0, 12));
        string gameCode = DecodeAscii(header.AsSpan(0xAC, 4));
        string makerCode = DecodeAscii(header.AsSpan(0xB0, 2));

        bool magicOk = header[0xB2] == 0x96;
        byte expectedChecksum = header[0xBD];
        byte actualChecksum = Checksums.GbaHeaderChecksum(header);
        bool logoOk = LogoChecksum(header) == NintendoLogoChecksum;

        long romSize = options.RomSizeOverride
            ?? DetectRomSize(link, opcode, romBase, header, options);

        var info = new CartridgeInfo
        {
            Kind = Kind,
            Title = title,
            RomSize = romSize,
            SaveSize = 0,
            Mapper = "GBA (リニア)",
            RomExtension = ".gba",
            RawHeader = header,
        };

        info.Details["ゲームコード"] = gameCode;
        info.Details["メーカーコード"] = makerCode;
        info.Details["任天堂ロゴ"] = logoOk
            ? "一致"
            : $"不一致 (チェックサム 0x{LogoChecksum(header):X4} / 期待値 0x{NintendoLogoChecksum:X4})";
        info.Details["固定値 0x96"] = magicOk ? "一致" : $"不一致 (0x{header[0xB2]:X2})";
        info.Details["ヘッダチェックサム"] =
            $"0x{expectedChecksum:X2} / 実算出 0x{actualChecksum:X2}" +
            (expectedChecksum == actualChecksum ? " (整合)" : " (不整合)");
        info.Details["バージョン"] = $"1.{header[0xBC]}";
        info.Details["ROM サイズ判定"] = options.RomSizeOverride is not null
            ? $"手動指定 {SizeText(romSize)}"
            : $"自動判定 {SizeText(romSize)}";

        if (!logoOk)
            info.Warnings.Add(
                "ヘッダ先頭の任天堂ロゴが一致しません。読み出し自体が成立していない可能性が高いので、" +
                "opcode と ROM 先頭アドレスの指定を確認してください。");

        if (!magicOk)
            info.Warnings.Add("ヘッダの固定値 0x96 が一致しません。GBA カセットとして認識できていない可能性があります。");

        if (expectedChecksum != actualChecksum)
            info.Warnings.Add("ヘッダチェックサムが一致しません。読み出しが不安定な可能性があります。");

        if (options.RomSizeOverride is null)
            info.Warnings.Add(
                "GBA カセットは容量を申告するヘッダ欄を持ちません。ここでの判定はミラー検出による" +
                "推定です。既知の容量と食い違う場合は手動指定してください。");

        return info;
    }

    /// <summary>
    /// ヘッダを読む。ロゴが一致しなければ読み直す。
    ///
    /// 実機では、ウェイクアップ直後の最初の読み出しでデータが化けることがある。
    /// ロゴは固定バイト列なので、化けたかどうかをその場で判定できる。
    /// 最後まで一致しなければ最後に読めたものを返し、呼び出し側が警告を出す。
    /// </summary>
    private static byte[] ReadHeaderBlock(IRfcaLink link, uint opcode, uint romBase)
    {
        byte[] header = [];

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            var block = link.Read(
                opcode, romBase, RfcaOpcode.GbaBlockSize, RfcaOpcode.RequestHeaderField);
            header = block[..HeaderSize];

            if (LogoChecksum(header) == NintendoLogoChecksum) return header;
        }

        return header;
    }

    /// <summary>
    /// ヘッダ 0x04-0x9F の任天堂ロゴ 156 バイトの単純和。
    /// 正しく読めていれば必ず <see cref="NintendoLogoChecksum"/> になる。
    /// opcode やアドレス起点が違っていればまず一致しないので、
    /// 読み出しが成立しているかの判定に使える。
    /// </summary>
    public const ushort NintendoLogoChecksum = 0x4B1B;

    public static ushort LogoChecksum(ReadOnlySpan<byte> header)
    {
        int sum = 0;
        for (int i = 0x04; i < 0xA0; i++) sum += header[i];
        return (ushort)(sum & 0xFFFF);
    }

    public DumpResult Dump(
        IRfcaLink link,
        CartridgeInfo info,
        DumpOptions options,
        IProgress<DumpProgress>? progress,
        CancellationToken cancellationToken)
    {
        RequireReady();

        uint romBase = options.GbaRomBase ?? DefaultRomBase;
        uint opcode = RfcaOpcode.GbaRead!.Value;

        // 転送は 32KB ブロック。ヘッダ値は全スロット共通の 8。
        var gbaOptions = CloneWithBlockSize(options);

        byte[] rom = BulkReader.Read(
            link, opcode, info.RomSize,
            offset => (uint)(romBase + offset),
            gbaOptions, "ROM 読み出し", progress, cancellationToken,
            headerField: RfcaOpcode.RequestHeaderField);

        // セーブ種別は ROM 中の識別文字列から判定するのが確実。
        string saveType = DetectSaveType(rom);
        info.Details["セーブ種別"] = saveType;

        byte[]? save = null;
        if (options.IncludeSaveRam)
        {
            int saveSize = SaveSizeFor(saveType);
            if (saveSize > 0 && saveType.StartsWith("EEPROM"))
            {
                info.Warnings.Add(
                    "EEPROM セーブは専用のシリアル手順が必要なため、このバージョンでは吸い出しません。");
            }
            else if (saveSize > 0)
            {
                try
                {
                    save = BulkReader.Read(
                        link, opcode, saveSize,
                        offset => (uint)(SaveBase + offset),
                        CloneWithBlockSize(options), "セーブ読み出し", progress, cancellationToken,
                        headerField: RfcaOpcode.RequestHeaderField);
                }
                catch (RfcaException ex)
                {
                    info.Warnings.Add($"セーブ領域を読み出せませんでした: {ex.Message}");
                }
            }
        }

        return new DumpResult
        {
            Info = info,
            Rom = rom,
            Save = save,
            Crc32 = Checksums.Crc32(rom),
        };
    }

    // ------------------------------------------------------------------
    // 容量判定
    // ------------------------------------------------------------------

    /// <summary>
    /// ミラー検出とオープンバス検出で ROM 容量を求める。
    /// どちらも当たらない場合は最大容量として扱う。
    /// </summary>
    private static long DetectRomSize(
        IRfcaLink link, uint opcode, uint romBase, byte[] header, DumpOptions options)
    {
        var probe = header.AsSpan(0, 0x40);

        foreach (long size in CandidateSizes)
        {
            byte[] atSize;
            try
            {
                atSize = link.Read(
                    opcode, (uint)(romBase + size),
                    RfcaOpcode.GbaBlockSize, RfcaOpcode.RequestHeaderField)[..0x40];
            }
            catch (RfcaException)
            {
                // その先が読めない ＝ そこが ROM の終端。
                return size;
            }

            // 折り返して先頭と同じ内容が出たら、そこが終端。
            if (atSize.AsSpan().SequenceEqual(probe))
                return size;

            // 何も刺さっていない領域は A/D バスの残留値（アドレス/2）を返す。
            if (LooksLikeOpenBus(atSize, size))
                return size;
        }

        return CandidateSizes[^1];
    }

    /// <summary>
    /// GBA の ROM 終端より先は、16bit 単位で「ワードアドレスの下位 16bit」が読める。
    /// この並びになっていればデータではなくオープンバス。
    /// </summary>
    private static bool LooksLikeOpenBus(ReadOnlySpan<byte> data, long offset)
    {
        for (int i = 0; i + 1 < data.Length; i += 2)
        {
            ushort actual = (ushort)(data[i] | (data[i + 1] << 8));
            ushort expected = (ushort)(((offset + i) >> 1) & 0xFFFF);
            if (actual != expected) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------
    // セーブ種別
    // ------------------------------------------------------------------

    /// <summary>
    /// ROM 内に埋め込まれたセーブライブラリの識別文字列を探す。
    /// 任天堂公式のセーブライブラリが必ずこの文字列を含むため、
    /// 事実上すべての市販ソフトで判定できる。
    /// </summary>
    public static string DetectSaveType(ReadOnlySpan<byte> rom)
    {
        // 長いものから順に見る。"FLASH1M_V" は "FLASH_V" を含まないが
        // "FLASH512_V" と "FLASH_V" は区別が要る。
        string[] markers =
        [
            "EEPROM_V", "SRAM_V", "SRAM_F_V", "FLASH1M_V", "FLASH512_V", "FLASH_V",
        ];

        var found = new List<string>();
        foreach (string marker in markers)
        {
            if (IndexOfAscii(rom, marker) >= 0)
                found.Add(marker);
        }

        if (found.Count == 0) return "不明 (セーブなし?)";

        // FLASH1M_V が見つかったら FLASH_V の一致は無視する。
        if (found.Contains("FLASH1M_V")) return "FLASH 1Mbit (128KB)";
        if (found.Contains("FLASH512_V")) return "FLASH 512kbit (64KB)";
        if (found.Contains("FLASH_V")) return "FLASH 512kbit (64KB)";
        if (found.Contains("SRAM_F_V")) return "FRAM 256kbit (32KB)";
        if (found.Contains("SRAM_V")) return "SRAM 256kbit (32KB)";
        return "EEPROM (容量は要判定)";
    }

    private static int SaveSizeFor(string saveType) => saveType switch
    {
        "FLASH 1Mbit (128KB)" => 128 * 1024,
        "FLASH 512kbit (64KB)" => 64 * 1024,
        "SRAM 256kbit (32KB)" => 32 * 1024,
        "FRAM 256kbit (32KB)" => 32 * 1024,
        _ => 0,
    };

    private static int IndexOfAscii(ReadOnlySpan<byte> haystack, string needle)
    {
        Span<byte> pattern = stackalloc byte[needle.Length];
        for (int i = 0; i < needle.Length; i++) pattern[i] = (byte)needle[i];
        return haystack.IndexOf(pattern);
    }

    private static string DecodeAscii(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
        {
            if (b == 0) break;
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '?');
        }
        return sb.ToString().TrimEnd();
    }

    private static string SizeText(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024 / 1024} MB" : $"{bytes / 1024} KB";

    /// <summary>
    /// GBA のリードは 32KB ブロック単位。利用者が指定した転送サイズは使わず、
    /// ここで必ず揃える（RetroFreakDumper も 32768 固定）。
    /// </summary>
    private static DumpOptions CloneWithBlockSize(DumpOptions source) => new()
    {
        ChunkSize = RfcaOpcode.GbaBlockSize,
        RetryCount = source.RetryCount,
        RomSizeOverride = source.RomSizeOverride,
        SnesMapperOverride = source.SnesMapperOverride,
        ForceMmcInit = source.ForceMmcInit,
        IncludeSaveRam = source.IncludeSaveRam,
        VerifyChecksum = source.VerifyChecksum,
        GbaRomBase = source.GbaRomBase,
    };

    private void RequireReady()
    {
        if (!IsReady) throw new RfcaException(ReadinessDetail);
    }
}
