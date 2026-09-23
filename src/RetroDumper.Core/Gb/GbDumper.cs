using System.Text;
using RetroDumper.Core.Dumping;
using RetroDumper.Core.Transport;
using RetroDumper.Core.Util;

namespace RetroDumper.Core.Gb;

/// <summary>
/// ゲームボーイ / ゲームボーイカラーの吸い出し。
///
/// リード／ライトの opcode は未確定のため、探索結果を
/// <see cref="RfcaOpcode.GameBoyRead"/> / <see cref="RfcaOpcode.GameBoyWrite"/>
/// に設定してから使う。
///
/// バンク 0 は $0000-$3FFF に固定。バンク 1 以降は MBC のバンクレジスタを
/// 書いてから $4000-$7FFF を読む。
/// </summary>
public sealed class GbDumper : ICartridgeDumper
{
    public string Name => "ゲームボーイ / ゲームボーイカラー";
    public CartridgeKind Kind => CartridgeKind.GameBoy;

    // opcode は RetroFreakDumper.exe の逆コンパイルで確定済み。
    // 以前は探索頼みで未確定だったため、使えるかどうかの分岐が必要だった。
    public bool IsReady => true;

    public string ReadinessDetail => "";

    private const int BankSize = 0x4000;
    private const uint SwitchableBase = 0x4000;
    private const uint SaveBase = 0xA000;
    private const int SaveBankSize = 0x2000;

    public CartridgeInfo Identify(IRfcaLink link, DumpOptions options)
    {
        RequireReady();
        uint read = RfcaOpcode.GameBoyRead;

        var header = link.Read(read, 0x0100, 0x50);

        // ヘッダは $0100 から読んでいるので、$0134 は配列の 0x34 番目。
        byte cartType = header[0x47];
        byte romSizeCode = header[0x48];
        byte ramSizeCode = header[0x49];
        bool cgb = header[0x43] is 0x80 or 0xC0;

        string title = DecodeAscii(header.AsSpan(0x34, cgb ? 15 : 16));
        long romSize = options.RomSizeOverride ?? (32L * 1024 << romSizeCode);
        long saveSize = options.IncludeSaveRam ? RamSizeFromCode(ramSizeCode) : 0;

        var info = new CartridgeInfo
        {
            Kind = CartridgeKind.GameBoy,
            Title = title,
            RomSize = romSize,
            SaveSize = saveSize,
            Mapper = MbcName(cartType),
            RomExtension = cgb ? ".gbc" : ".gb",
            RawHeader = header,
        };

        info.Details["CGB 対応"] = header[0x43] switch
        {
            0x80 => "対応 (GB でも動作)",
            0xC0 => "CGB 専用",
            _ => "モノクロ専用",
        };
        info.Details["カートリッジ種別"] = $"0x{cartType:X2} ({MbcName(cartType)})";
        info.Details["ROM サイズ"] = SizeText(romSize) + $" (コード 0x{romSizeCode:X2})";
        info.Details["RAM サイズ"] = SizeText(RamSizeFromCode(ramSizeCode)) + $" (コード 0x{ramSizeCode:X2})";

        // ヘッダチェックサムは $0134-$014C が対象。読み出しバッファ上では 0x34-0x4C。
        byte expected = header[0x4D];
        int sum = 0;
        for (int i = 0x34; i <= 0x4C; i++) sum = sum - header[i] - 1;
        byte actual = (byte)(sum & 0xFF);
        info.Details["ヘッダチェックサム"] =
            $"0x{expected:X2} / 実算出 0x{actual:X2}" + (expected == actual ? " (整合)" : " (不整合)");

        if (expected != actual)
            info.Warnings.Add("ヘッダチェックサムが一致しません。接触不良の可能性があります。");

        return info;
    }

    public DumpResult Dump(
        IRfcaLink link,
        CartridgeInfo info,
        DumpOptions options,
        IProgress<DumpProgress>? progress,
        CancellationToken cancellationToken)
    {
        RequireReady();

        uint read = RfcaOpcode.GameBoyRead;
        byte cartType = info.RawHeader.Length > 0x47 ? info.RawHeader[0x47] : (byte)0x00;

        int bankCount = (int)((info.RomSize + BankSize - 1) / BankSize);
        var rom = new byte[info.RomSize];
        long done = 0;

        for (int bank = 0; bank < bankCount; bank++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int length = (int)Math.Min(BankSize, info.RomSize - done);
            long captured = done;

            // バンク 0 は固定領域 $0000-$3FFF からそのまま読む。
            uint windowBase;
            if (bank == 0)
            {
                windowBase = 0x0000;
            }
            else
            {
                SelectRomBank(link, cartType, bank);
                windowBase = SwitchableBase;
            }

            byte[] part = BulkReader.Read(
                link, read, length,
                offsetInBank => (uint)(windowBase + offsetInBank),
                options, $"ROM 読み出し (バンク {bank}/{bankCount})",
                new BankProgress(progress, captured, info.RomSize),
                cancellationToken);

            part.CopyTo(rom, done);
            done += length;
        }

        byte[]? save = null;
        if (options.IncludeSaveRam && info.SaveSize > 0)
        {
            try
            {
                save = DumpSaveRam(link, read, cartType, info.SaveSize, options, progress, cancellationToken);
            }
            catch (RfcaException ex)
            {
                info.Warnings.Add($"セーブ RAM を読み出せませんでした: {ex.Message}");
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
    // MBC 制御
    // ------------------------------------------------------------------

    private static void SelectRomBank(IRfcaLink link, byte cartType, int bank)
    {
        uint write = RfcaOpcode.GameBoyWrite;

        switch (cartType)
        {
            // MBC5: $2000 に下位 8bit、$3000 に bit8。
            case >= 0x19 and <= 0x1E:
                link.WriteByte(write, 0x2000, (byte)(bank & 0xFF));
                link.WriteByte(write, 0x3000, (byte)((bank >> 8) & 0x01));
                break;

            // MBC2: $2000-$3FFF、ただしアドレス bit8 が 1 のときだけバンク選択。
            case 0x05 or 0x06:
                link.WriteByte(write, 0x2100, (byte)(bank & 0x0F));
                break;

            // MBC3: $2000 に 7bit まとめて。
            case >= 0x0F and <= 0x13:
                link.WriteByte(write, 0x2000, (byte)(bank & 0x7F));
                break;

            // MBC1: $2000 に下位 5bit、$4000 に上位 2bit（モード 0 のとき）。
            default:
                link.WriteByte(write, 0x6000, 0x00);
                link.WriteByte(write, 0x4000, (byte)((bank >> 5) & 0x03));
                link.WriteByte(write, 0x2000, (byte)(bank & 0x1F));
                break;
        }
    }

    private static byte[] DumpSaveRam(
        IRfcaLink link,
        uint read,
        byte cartType,
        long saveSize,
        DumpOptions options,
        IProgress<DumpProgress>? progress,
        CancellationToken cancellationToken)
    {
        uint write = RfcaOpcode.GameBoyWrite;

        // 外部 RAM を有効化する。
        link.WriteByte(write, 0x0000, 0x0A);

        try
        {
            int bankCount = (int)Math.Max(1, (saveSize + SaveBankSize - 1) / SaveBankSize);
            var save = new byte[saveSize];
            long done = 0;

            for (int bank = 0; bank < bankCount; bank++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                link.WriteByte(write, 0x4000, (byte)bank);

                int length = (int)Math.Min(SaveBankSize, saveSize - done);
                long captured = done;

                byte[] part = BulkReader.Read(
                    link, read, length,
                    offsetInBank => (uint)(SaveBase + offsetInBank),
                    options, $"セーブ読み出し (バンク {bank})",
                    new BankProgress(progress, captured, saveSize),
                    cancellationToken);

                part.CopyTo(save, done);
                done += length;
            }

            return save;
        }
        finally
        {
            // 外部 RAM を無効化して戻す。書き込み事故を避ける。
            link.WriteByte(write, 0x0000, 0x00);
        }
    }

    private sealed class BankProgress(IProgress<DumpProgress>? inner, long baseOffset, long total)
        : IProgress<DumpProgress>
    {
        public void Report(DumpProgress value)
            => inner?.Report(new DumpProgress(value.Stage, baseOffset + value.BytesDone, total));
    }

    private static long RamSizeFromCode(byte code) => code switch
    {
        0x00 => 0,
        0x01 => 2 * 1024,
        0x02 => 8 * 1024,
        0x03 => 32 * 1024,
        0x04 => 128 * 1024,
        0x05 => 64 * 1024,
        _ => 0,
    };

    private static string MbcName(byte cartType) => cartType switch
    {
        0x00 => "ROM のみ",
        0x01 or 0x02 or 0x03 => "MBC1",
        0x05 or 0x06 => "MBC2",
        0x0B or 0x0C or 0x0D => "MMM01",
        0x0F or 0x10 or 0x11 or 0x12 or 0x13 => "MBC3",
        >= 0x19 and <= 0x1E => "MBC5",
        0x20 => "MBC6",
        0x22 => "MBC7",
        0xFE => "HuC3",
        0xFF => "HuC1",
        _ => $"不明 (0x{cartType:X2})",
    };

    private static string DecodeAscii(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
        {
            if (b == 0) break;
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : ' ');
        }
        return sb.ToString().TrimEnd();
    }

    private static string SizeText(long bytes) => bytes switch
    {
        0 => "なし",
        < 1024 * 1024 => $"{bytes / 1024} KB",
        _ => $"{bytes / 1024 / 1024} MB",
    };

    private void RequireReady()
    {
        // opcode は確定済み。確認することはない。
    }
}
