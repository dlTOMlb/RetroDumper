using System.Text;
using RetroDumper.Core.Dumping;
using RetroDumper.Core.Transport;
using RetroDumper.Core.Util;

namespace RetroDumper.Core.Sms;

/// <summary>
/// セガ・マークIII / マスターシステム / ゲームギアの吸い出し。
/// リード opcode 0x2B、ライト opcode 0x2C、アドレスは Z80 のバス空間。
///
/// セガマッパーは 16KB のバンクを 3 つのフレームに貼る。
/// 全バンクをフレーム 2 ($8000-$BFFF) 経由で順に読むのが一番素直。
/// </summary>
public sealed class SmsDumper : ICartridgeDumper
{
    public string Name => "マークIII / マスターシステム / ゲームギア";
    public CartridgeKind Kind => CartridgeKind.MarkIIIOrGameGear;
    public bool IsReady => true;
    public string ReadinessDetail => "";

    private const int BankSize = 0x4000;
    private const uint Frame2Base = 0x8000;

    // セガマッパーの制御レジスタ
    private const uint RegControl = 0xFFFC;
    private const uint RegFrame1 = 0xFFFE;
    private const uint RegFrame2 = 0xFFFF;

    /// <summary>ヘッダ "TMR SEGA" が置かれうる位置。</summary>
    private static readonly uint[] HeaderCandidates = [0x7FF0, 0x3FF0, 0x1FF0];

    public CartridgeInfo Identify(IRfcaLink link, DumpOptions options)
    {
        byte[]? header = null;
        uint headerAddress = 0;

        foreach (uint candidate in HeaderCandidates)
        {
            try
            {
                var raw = link.Read(RfcaOpcode.SmsRead, candidate, 16);
                if (DecodeAscii(raw.AsSpan(0, 8)) == "TMR SEGA")
                {
                    header = raw;
                    headerAddress = candidate;
                    break;
                }
            }
            catch (RfcaException)
            {
                // 次の候補へ
            }
        }

        long romSize;
        string sizeSource;

        if (header is not null)
        {
            // 0x0F 番地の下位ニブルが ROM サイズコード。
            romSize = RomSizeFromCode((byte)(header[0x0F] & 0x0F));
            sizeSource = $"ヘッダ (コード 0x{header[0x0F] & 0x0F:X1})";
        }
        else
        {
            romSize = 0;
            sizeSource = "ヘッダなし";
        }

        if (romSize <= 0)
        {
            romSize = MeasureRomSize(link, options);
            sizeSource = "ミラー実測";
        }

        romSize = options.RomSizeOverride ?? romSize;

        var info = new CartridgeInfo
        {
            Kind = CartridgeKind.MarkIIIOrGameGear,
            Title = header is not null ? $"TMR SEGA (製品コード {ProductCode(header)})" : "(ヘッダなし)",
            RomSize = romSize,
            SaveSize = 0,
            Mapper = "セガマッパー (16KB バンク)",
            RomExtension = ".sms",
            RawHeader = header ?? [],
        };

        info.Details["ヘッダ位置"] = header is not null ? $"0x{headerAddress:X4}" : "見つかりません";
        info.Details["ROM サイズ判定"] = $"{sizeSource} → {SizeText(romSize)}";

        if (header is null)
            info.Warnings.Add(
                "TMR SEGA ヘッダが見つかりませんでした。初期の一部ソフトはヘッダを持たないため、" +
                "サイズは実測値または手動指定で吸い出してください。");

        return info;
    }

    public DumpResult Dump(
        IRfcaLink link,
        CartridgeInfo info,
        DumpOptions options,
        IProgress<DumpProgress>? progress,
        CancellationToken cancellationToken)
    {
        int bankCount = (int)((info.RomSize + BankSize - 1) / BankSize);
        var rom = new byte[info.RomSize];
        long done = 0;

        for (int bank = 0; bank < bankCount; bank++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SelectFrame2Bank(link, bank);

            int length = (int)Math.Min(BankSize, info.RomSize - done);
            long captured = done;

            byte[] part = BulkReader.Read(
                link, RfcaOpcode.SmsRead, length,
                offsetInBank => (uint)(Frame2Base + offsetInBank),
                options, $"ROM 読み出し (バンク {bank}/{bankCount})",
                new BankProgress(progress, captured, info.RomSize),
                cancellationToken);

            part.CopyTo(rom, done);
            done += length;
        }

        return new DumpResult
        {
            Info = info,
            Rom = rom,
            Crc32 = Checksums.Crc32(rom),
        };
    }

    /// <summary>フレーム 2 ($8000-$BFFF) に指定バンクを貼る。</summary>
    private static void SelectFrame2Bank(IRfcaLink link, int bank)
    {
        link.WriteByte(RfcaOpcode.SmsWrite, RegControl, 0x80);
        link.WriteByte(RfcaOpcode.SmsWrite, RegFrame2, (byte)bank);
    }

    /// <summary>フレーム 1 ($4000-$7FFF) に指定バンクを貼る。</summary>
    public static void SelectFrame1Bank(IRfcaLink link, int bank)
    {
        link.WriteByte(RfcaOpcode.SmsWrite, RegControl, 0x80);
        link.WriteByte(RfcaOpcode.SmsWrite, RegFrame1, (byte)bank);
    }

    private sealed class BankProgress(IProgress<DumpProgress>? inner, long baseOffset, long total)
        : IProgress<DumpProgress>
    {
        public void Report(DumpProgress value)
            => inner?.Report(new DumpProgress(value.Stage, baseOffset + value.BytesDone, total));
    }

    /// <summary>ヘッダ 0x0F 番地のサイズコード。</summary>
    private static long RomSizeFromCode(byte code) => code switch
    {
        0x0A => 8 * 1024,
        0x0B => 16 * 1024,
        0x0C => 32 * 1024,
        0x0D => 48 * 1024,
        0x0E => 64 * 1024,
        0x0F => 128 * 1024,
        0x00 => 256 * 1024,
        0x01 => 512 * 1024,
        0x02 => 1024 * 1024,
        _ => 0,
    };

    private static long MeasureRomSize(IRfcaLink link, DumpOptions options)
    {
        byte[] head;
        try
        {
            SelectFrame2Bank(link, 0);
            head = link.Read(RfcaOpcode.SmsRead, Frame2Base, 0x40);
        }
        catch (RfcaException)
        {
            return 32 * 1024;
        }

        // バンク数を倍々に試し、折り返したところが終端。
        for (int banks = 2; banks <= 64; banks *= 2)
        {
            try
            {
                SelectFrame2Bank(link, banks);
                var probe = link.Read(RfcaOpcode.SmsRead, Frame2Base, 0x40);
                if (probe.AsSpan().SequenceEqual(head))
                    return (long)banks * BankSize;
            }
            catch (RfcaException)
            {
                return (long)banks * BankSize;
            }
        }

        return 64L * BankSize;
    }

    private static string ProductCode(byte[] header)
    {
        // 0x0C-0x0D が BCD の製品番号下 4 桁、0x0E の上位ニブルが上 2 桁。
        int high = header[0x0E] >> 4;
        return $"{high:X1}{header[0x0D]:X2}{header[0x0C]:X2}";
    }

    private static string DecodeAscii(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : ' ');
        return sb.ToString();
    }

    private static string SizeText(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024 / 1024} MB" : $"{bytes / 1024} KB";
}
