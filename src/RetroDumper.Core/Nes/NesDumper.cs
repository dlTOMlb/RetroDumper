using System.Security.Cryptography;
using RetroDumper.Core.Dumping;
using RetroDumper.Core.Transport;
using RetroDumper.Core.Util;

namespace RetroDumper.Core.Nes;

/// <summary>
/// ファミコンの吸い出し。
///
/// **カートリッジにヘッダがありません。**
/// 他機種はカセット自身がタイトル・容量・マッパーを申告しますが、
/// ファミコンは何も申告しません。マッパー番号も PRG/CHR の容量も
/// カセットからは読めないため、次のどちらかで決める必要があります。
///
///   1. 利用者が選ぶ（<see cref="DumpOptions.NesMapperOverride"/>）
///   2. データベースで同定する（<see cref="NesRomInfoDatabase"/>、
///      鍵は PRG-ROM 先頭 1KB の SHA-1）
///   3. 総当たりで特定する（対応マッパーを順に試し、
///      No-Intro DAT と一致したものを正解とする）
///
/// 容量はバンクの折り返しから実測できるので、マッパーさえ決まれば自動で決まります。
/// 出力は iNES ヘッダ (16 バイト) を付けた .nes 形式です。
/// </summary>
public sealed partial class NesDumper : ICartridgeDumper
{
    public string Name => "ファミコン";
    public CartridgeKind Kind => CartridgeKind.Famicom;
    public bool IsReady => true;
    public string ReadinessDetail => "";

    /// <summary>同定に使う PRG 先頭 1KB。</summary>
    public const int SignatureSize = 1024;

    public CartridgeInfo Identify(IRfcaLink link, DumpOptions options)
    {
        var bus = new NesBus(link);

        // マッパーが分からなくても、$8000 と $FC00 の 1KB は読める。
        byte[] firstPrg = bus.CpuRead(0x8000, SignatureSize);
        byte[] lastPrg = bus.CpuRead(0xFC00, SignatureSize);

        string firstSha1 = Convert.ToHexString(SHA1.HashData(firstPrg));
        string lastSha1 = Convert.ToHexString(SHA1.HashData(lastPrg));

        var db = NesRomInfoDatabase.Load();
        var known = db.Find(firstSha1, lastSha1);

        int mapperNo = options.NesMapperOverride
            ?? known?.MapperNumber
            ?? -1;

        long prgSize = options.NesPrgSize ?? known?.PrgSize ?? 0;
        long chrSize = options.NesChrSize ?? known?.ChrSize ?? 0;

        var mapper = mapperNo >= 0 ? NesMapper.ForNumber(mapperNo) : null;

        var info = new CartridgeInfo
        {
            Kind = Kind,
            Title = known?.Name ?? "",
            RomSize = prgSize + chrSize,
            SaveSize = 0,
            Mapper = mapper is not null
                ? $"マッパー {mapper.Number} ({mapper.Name})"
                : mapperNo >= 0
                    ? $"マッパー {mapperNo}（未対応）"
                    : "不明",
            RomExtension = ".nes",
            RawHeader = firstPrg[..64],

            // 吸い出しへ引き継ぐ。自動（データベース）で識別したときは
            // options に値が入っていないため、ここで持たせておかないと
            // 吸い出し時にマッパーが分からなくなる。
            NesMapperNumber = mapperNo >= 0 ? mapperNo : null,
            NesPrgSize = prgSize,
            NesChrSize = chrSize,
        };

        info.Details["PRG $8000 先頭 16 バイト"] = Convert.ToHexString(firstPrg.AsSpan(0, 16));
        info.Details["PRG 先頭 1KB SHA-1"] = firstSha1;
        info.Details["PRG 末尾 1KB SHA-1"] = lastSha1;
        info.Details["同定"] = known is not null
            ? $"データベース一致: {known.Name}"
            : db.IsEmpty
                ? "データベースがありません"
                : "データベースに一致なし";
        info.Details["PRG-ROM"] = prgSize > 0 ? $"{prgSize / 1024} KB" : "吸い出し時に実測";
        info.Details["CHR-ROM"] = chrSize > 0 ? $"{chrSize / 1024} KB" : "吸い出し時に実測";

        if (mapper is not null)
        {
            info.Details["PRG 有効範囲"] = Range(mapper.PrgSizeRange);
            info.Details["CHR 有効範囲"] = mapper.ChrSizeRange.Max == 0
                ? "なし（CHR-RAM のみ）"
                : Range(mapper.ChrSizeRange);
        }

        // 読み出しが成立しているかを先に見る。
        // 全バイトが同じ値なら、バスからデータが返っていない。
        // GBA で接触不良をソフトの問題と誤認して長時間を費やした経緯がある。
        if (IsFlat(firstPrg) && IsFlat(lastPrg))
            info.Warnings.Add(
                $"PRG-ROM の読み出しが全バイト 0x{firstPrg[0]:X2} です。" +
                "カートリッジがバスを駆動していません。" +
                "まずカセットを挿し直してください。端子の清掃も試してください。" +
                "アダプタは接触不良でも種別コードだけは正しく返すため、" +
                "「認識しているのに読めない」ように見えます。");

        if (mapperNo < 0)
            info.Warnings.Add(
                "マッパーは未確定です。ファミコンのカセットはマッパー番号を申告しないためです。" +
                "吸い出しを実行すると、対応マッパーを順に試して No-Intro DAT と一致したものを" +
                "正解として採用します。分かっている場合は「ファミコン詳細」で指定すると速く済みます。");
        else if (mapper is null)
            info.Warnings.Add(
                $"マッパー {mapperNo} には未対応です。現在対応しているのは " +
                string.Join(" / ", NesMapper.All.Select(m => $"{m.Number} ({m.Name})")) + " です。");



        return info;
    }

    /// <summary>全バイトが同じ値か。読み出しが成立していないことの目印。</summary>
    private static bool IsFlat(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return true;

        foreach (byte b in data)
            if (b != data[0]) return false;

        return true;
    }

    private static string Range((long Min, long Max) r)
        => r.Min == r.Max ? $"{r.Min / 1024} KB" : $"{r.Min / 1024} 〜 {r.Max / 1024} KB";

    public DumpResult Dump(
        IRfcaLink link,
        CartridgeInfo info,
        DumpOptions options,
        IProgress<DumpProgress>? progress,
        CancellationToken cancellationToken)
    {
        var bus = new NesBus(link);

        // 手動指定を優先し、無ければ識別で確定した値を使う。
        int? mapperNo = options.NesMapperOverride ?? info.NesMapperNumber;

        AutoDetectResult? auto = null;
        byte[] rom;

        if (mapperNo is int chosen)
        {
            var mapper = NesMapper.ForNumber(chosen)
                ?? throw new RfcaException(
                    $"マッパー {chosen} には未対応です。現在対応しているのは " +
                    string.Join(" / ", NesMapper.All.Select(m => $"{m.Number} ({m.Name})")) +
                    " です。");

            rom = DumpWith(
                bus, mapper,
                options.NesPrgSize is > 0 ? options.NesPrgSize.Value : info.NesPrgSize,
                options.NesChrSize is > 0 ? options.NesChrSize.Value : info.NesChrSize,
                progress, cancellationToken);
        }
        else
        {
            // マッパーが分からないので総当たりで特定する。
            auto = AutoDetectAndDump(bus, progress, cancellationToken);
            rom = auto.Rom;
        }

        return new DumpResult
        {
            Info = info,
            Rom = rom,
            Crc32 = Checksums.Crc32(rom),

            // ファミコンのカセットはチェックサムを持たないので、
            // 正否の判断は No-Intro DAT との照合に頼るしかない。
            ChecksumOk = auto is null ? null : auto.Matched is not null,
            ChecksumDetail = auto is null
                ? "ファミコンのカセットはチェックサムを持ちません。" +
                  "No-Intro DAT との照合で正否を確かめてください。"
                : auto.Matched is not null
                    ? $"{auto.Mapper} で No-Intro と一致: {auto.Matched}"
                    : "どのマッパーでも No-Intro と一致しませんでした。" +
                      "吸い出したデータは保存します。" + Environment.NewLine + auto.Log,
        };
    }

    /// <summary>
    /// バンクの折り返しから容量を実測する。
    ///
    /// ROM に載っていないバンク番号を指定すると、上位アドレス線が繋がっていない
    /// ぶんだけ番号が丸められ、先頭のバンクと同じ内容が読める。
    /// バンク 0 と一致する最小の 2 の冪が、そのまま総バンク数になる。
    ///
    /// ファミコンのカセットは容量を申告しないので、これが唯一の手段。
    /// </summary>
    private static long DetectSize(NesBus bus, NesMapper mapper, bool isPrg)
    {
        int bankSize = isPrg ? mapper.PrgBankSize : mapper.ChrBankSize;
        var (min, max) = isPrg ? mapper.PrgSizeRange : mapper.ChrSizeRange;

        if (bankSize <= 0 || max <= 0) return 0;

        // 取りうる容量が 1 つしかないなら測る必要がない。
        if (min == max) return min;

        const int probe = 256;                 // 比較に使う先頭バイト数

        int maxBanks = (int)(max / bankSize);
        if (maxBanks <= 0) return 0;

        byte[]? first = ReadProbe(bus, mapper, 0, bankSize, probe, isPrg);
        if (first is null) return Math.Max(min, 0);

        for (int banks = 1; banks <= maxBanks; banks <<= 1)
        {
            byte[]? at = ReadProbe(bus, mapper, banks, bankSize, probe, isPrg);

            // 読めない、または折り返してバンク 0 と同じ内容が見えたら、そこが終端。
            if (at is null || at.AsSpan().SequenceEqual(first))
                return Clamp((long)banks * bankSize, min, max);
        }

        return Clamp((long)maxBanks * bankSize, min, max);
    }

    /// <summary>
    /// 実測値をマッパーが取りうる範囲に収める。
    ///
    /// 範囲は sanni/cartreader の mapsize テーブルに合わせてある。
    /// 折り返しの検出は ROM の内容次第で外すことがあるので、
    /// あり得ない値をそのまま採用しないための歯止め。
    /// </summary>
    private static long Clamp(long value, long min, long max)
        => value < min ? min : value > max ? max : value;

    private static byte[]? ReadProbe(
        NesBus bus, NesMapper mapper, int bank, int bankSize, int probe, bool isPrg)
    {
        try
        {
            byte[]? data = isPrg
                ? mapper.ReadPrgBank(bus, bank, bankSize, bank + 1)
                : mapper.ReadChrBank(bus, bank, bankSize);

            return data is null || data.Length < probe ? null : data[..probe];
        }
        catch (RfcaException)
        {
            return null;
        }
    }

    private static byte[] ReadBanks(
        NesBus bus, NesMapper mapper, long totalSize, int bankSize, bool isPrg,
        IProgress<DumpProgress>? progress, ref long done, long grandTotal,
        CancellationToken cancellationToken)
    {
        var result = new byte[totalSize];
        int banks = (int)(totalSize / bankSize);
        string stage = isPrg ? "PRG-ROM" : "CHR-ROM";

        for (int bank = 0; bank < banks; bank++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[]? data = isPrg
                ? mapper.ReadPrgBank(bus, bank, bankSize, banks)
                : mapper.ReadChrBank(bus, bank, bankSize);

            if (data is null) break;

            data.AsSpan(0, bankSize).CopyTo(result.AsSpan(bank * bankSize));

            done += bankSize;
            progress?.Report(new DumpProgress(stage, done, grandTotal));
        }

        return result;
    }

    /// <summary>
    /// iNES ヘッダ (16 バイト) を付けた .nes ファイルを組み立てる。
    ///
    /// ミラーリングの向きはカセットから読めないため 0（横）にしている。
    /// 正しい値はデータベース照合か、エミュレータ側の判断に委ねる。
    /// </summary>
    public static byte[] BuildINesFile(int mapperNumber, byte[] prg, byte[] chr)
    {
        var file = new byte[16 + prg.Length + chr.Length];

        file[0] = (byte)'N';
        file[1] = (byte)'E';
        file[2] = (byte)'S';
        file[3] = 0x1A;
        file[4] = (byte)(prg.Length / 0x4000);      // PRG を 16KB 単位で
        file[5] = (byte)(chr.Length / 0x2000);      // CHR を 8KB 単位で
        file[6] = (byte)((mapperNumber & 0x0F) << 4);
        file[7] = (byte)(mapperNumber & 0xF0);

        prg.CopyTo(file.AsSpan(16));
        chr.CopyTo(file.AsSpan(16 + prg.Length));

        return file;
    }
}
