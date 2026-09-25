using System.Buffers.Binary;
using System.Text;
using RetroDumper.Core.Dumping;
using RetroDumper.Core.Transport;
using RetroDumper.Core.Util;

namespace RetroDumper.Core.Md;

/// <summary>
/// メガドライブ / Genesis の吸い出し。opcode 0x0D、アドレスは 68000 バス上の
/// バイトアドレス（ROM は 0 番地から連続）。
///
/// ヘッダ 0x1A4 の「ROM 終端アドレス」は信用しきれない。ウルトラコアのように
/// 実容量より小さい値を書いているカセットがあるため、ミラー検出による
/// 実測値も併記し、食い違う場合は警告する。
/// </summary>
public sealed class MdDumper : ICartridgeDumper
{
    public string Name => "メガドライブ / Genesis";
    public CartridgeKind Kind => CartridgeKind.MegaDrive;
    public bool IsReady => true;
    public string ReadinessDetail => "";

    private const int HeaderOffset = 0x100;
    private const int HeaderSize = 0x100;

    /// <summary>ミラー検出で試す容量。メガドライブは 128KB～8MB。</summary>
    private static readonly long[] CandidateSizes =
    [
        128 << 10, 256 << 10, 512 << 10, 1 << 20, 2 << 20, 4 << 20, 8 << 20,
    ];

    public CartridgeInfo Identify(IRfcaLink link, DumpOptions options)
    {
        var header = link.Read(RfcaOpcode.MegaDriveRead, HeaderOffset, HeaderSize);

        string console = DecodeAscii(header.AsSpan(0x00, 16));
        string copyright = DecodeAscii(header.AsSpan(0x10, 16));
        string domesticName = DecodeAscii(header.AsSpan(0x20, 48));
        string overseasName = DecodeAscii(header.AsSpan(0x50, 48));
        string serial = DecodeAscii(header.AsSpan(0x80, 14));
        ushort headerChecksum = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0x8E));

        uint romStart = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0xA0));
        uint romEnd = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0xA4));
        long declaredSize = romEnd >= romStart ? romEnd - romStart + 1 : 0;

        long measuredSize = MeasureRomSize(link, options);
        long romSize = options.RomSizeOverride ?? Math.Max(declaredSize, measuredSize);
        if (romSize <= 0) romSize = 1 << 20;

        // SRAM 情報 ("RA" マーカー)
        long saveSize = 0;
        uint saveStart = 0;
        bool hasSram = header[0xB0] == (byte)'R' && header[0xB1] == (byte)'A';
        if (hasSram)
        {
            saveStart = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0xB4));
            uint saveEnd = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0xB8));
            if (saveEnd >= saveStart) saveSize = saveEnd - saveStart + 1;
        }

        string title = !string.IsNullOrWhiteSpace(overseasName) ? overseasName : domesticName;

        var info = new CartridgeInfo
        {
            Kind = CartridgeKind.MegaDrive,
            Title = title,
            RomSize = romSize,
            SaveSize = options.IncludeSaveRam ? saveSize : 0,

            // 載っている容量は、吸い出しに含めるかとは無関係に申告する。
            // これが 0 だと、セーブの読み書きの画面が「セーブ RAM が
            // ありません」と言って止まる。
            SaveMemorySize = saveSize,

            Mapper = "メガドライブ (リニア)",
            RomExtension = ".md",
            RawHeader = header,
        };

        info.Details["システム名"] = console;
        info.Details["著作権表記"] = copyright;
        info.Details["国内タイトル"] = domesticName;
        info.Details["海外タイトル"] = overseasName;
        info.Details["シリアル"] = serial;
        info.Details["ヘッダチェックサム"] = $"0x{headerChecksum:X4}";
        info.Details["ROM 範囲(ヘッダ申告)"] = $"0x{romStart:X6}-0x{romEnd:X6} ({SizeText(declaredSize)})";
        info.Details["ROM サイズ(ミラー実測)"] = measuredSize > 0 ? SizeText(measuredSize) : "判定不能";
        info.Details["SRAM"] = hasSram ? $"0x{saveStart:X6} から {SizeText(saveSize)}" : "なし";

        if (measuredSize > declaredSize && declaredSize > 0)
        {
            info.Warnings.Add(
                $"ヘッダ申告は {SizeText(declaredSize)} ですが、実測では {SizeText(measuredSize)} あります。" +
                "ヘッダが誤っているカセット（ウルトラコア等）の可能性が高いため、実測値で吸い出します。");
        }

        if (!console.Contains("SEGA", StringComparison.OrdinalIgnoreCase))
            info.Warnings.Add("ヘッダ先頭に SEGA の識別文字列がありません。接触不良の可能性があります。");

        return info;
    }

    public DumpResult Dump(
        IRfcaLink link,
        CartridgeInfo info,
        DumpOptions options,
        IProgress<DumpProgress>? progress,
        CancellationToken cancellationToken)
    {
        byte[] rom = BulkReader.Read(
            link, RfcaOpcode.MegaDriveRead, info.RomSize,
            offset => (uint)offset,
            options, "ROM 読み出し", progress, cancellationToken);

        byte[]? save = null;
        if (options.IncludeSaveRam && info.SaveSize > 0 && info.RawHeader.Length >= 0xBC)
        {
            uint saveStart = BinaryPrimitives.ReadUInt32BigEndian(info.RawHeader.AsSpan(0xB4));
            try
            {
                // SRAM は 68000 バス上で偶数/奇数バイトのどちらかにしか
                // 繋がっていない構成が多い。ここでは生のバイト列をそのまま保存する。
                save = BulkReader.Read(
                    link, RfcaOpcode.MegaDriveRead, info.SaveSize,
                    offset => (uint)(saveStart + offset),
                    options, "セーブ読み出し", progress, cancellationToken);
            }
            catch (RfcaException ex)
            {
                info.Warnings.Add($"SRAM を読み出せませんでした: {ex.Message}");
            }
        }

        bool? checksumOk = null;
        string detail = "";
        if (options.VerifyChecksum && info.RawHeader.Length >= 0x90 && rom.Length > 0x200)
        {
            ushort expected = BinaryPrimitives.ReadUInt16BigEndian(info.RawHeader.AsSpan(0x8E));
            ushort actual = ComputeChecksum(rom);
            checksumOk = expected == actual;
            detail = $"ヘッダ 0x{expected:X4} / 実データ 0x{actual:X4}";
        }

        return new DumpResult
        {
            Info = info,
            Rom = rom,
            Save = save,
            ChecksumOk = checksumOk,
            ChecksumDetail = detail,
            Crc32 = Checksums.Crc32(rom),
        };
    }

    /// <summary>メガドライブのチェックサム: 0x200 以降を 16bit ビッグエンディアンで加算。</summary>
    public static ushort ComputeChecksum(ReadOnlySpan<byte> rom)
    {
        uint sum = 0;
        for (int i = 0x200; i + 1 < rom.Length; i += 2)
            sum += (uint)((rom[i] << 8) | rom[i + 1]);
        return (ushort)(sum & 0xFFFF);
    }

    /// <summary>
    /// ROM 容量の実測。各候補サイズの位置を読み、先頭と同じ内容なら
    /// アドレスが折り返している ＝ そこが終端と判断する。
    /// </summary>
    private static long MeasureRomSize(IRfcaLink link, DumpOptions options)
    {
        byte[] head;
        try
        {
            head = link.Read(RfcaOpcode.MegaDriveRead, 0x100, 0x40);
        }
        catch (RfcaException)
        {
            return 0;
        }

        foreach (long size in CandidateSizes)
        {
            try
            {
                var probe = link.Read(RfcaOpcode.MegaDriveRead, (uint)(size + 0x100), 0x40);
                if (probe.AsSpan().SequenceEqual(head))
                    return size;
            }
            catch (RfcaException)
            {
                return size;
            }
        }

        return CandidateSizes[^1];
    }

    private static string DecodeAscii(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : ' ');
        return sb.ToString().Trim();
    }

    private static string SizeText(long bytes) => bytes switch
    {
        0 => "不明",
        < 1024 * 1024 => $"{bytes / 1024} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.##} MB",
    };
}
