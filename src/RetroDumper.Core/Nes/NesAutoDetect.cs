using RetroDumper.Core.Database;
using RetroDumper.Core.Dumping;
using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Nes;

public sealed partial class NesDumper
{
    /// <summary>
    /// マッパーを総当たりして、No-Intro DAT と一致するものを探す。
    ///
    /// ファミコンのカセットはマッパー番号を申告しないので、外から知る方法がない。
    /// だが吸い出した結果を照合すれば、正しいマッパーだったかは確実に判定できる。
    /// 「当てて、答え合わせをする」しかない。
    ///
    /// 全部を吸い出すと時間がかかるので、先に安上がりな篩にかける。
    /// マッパーが違えばバンク切り替えが効かず、どのバンクも同じ内容に見える。
    /// それを検出したものは吸い出さずに捨てる。
    ///
    /// 誤ったマッパーを試してもカセットは壊れない。書き込むのはマッパーの
    /// ラッチだけで、セーブ領域 ($6000-$7FFF) は遮断されている。
    /// </summary>
    /// <summary>総当たりの結果。特定できたかと、その経過。</summary>
    internal sealed record AutoDetectResult(byte[] Rom, string Mapper, string? Matched, string Log);

    private static AutoDetectResult AutoDetectAndDump(
        NesBus bus, IProgress<DumpProgress>? progress, CancellationToken cancellationToken)
    {
        var db = NoIntroDatabase.Load();
        var attempted = new List<string>();
        byte[]? plausible = null;
        string plausibleMapper = "";

        foreach (var mapper in NesMapper.All)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string label = $"マッパー {mapper.Number} ({mapper.Name})";
            progress?.Report(new DumpProgress($"{label} を試行中", 0, 1));

            long prg, chr;

            try
            {
                mapper.Initialize(bus);
                prg = DetectSize(bus, mapper, isPrg: true);
                chr = mapper.ChrBankSize > 0 ? DetectChrSize(bus, mapper) : 0;
            }
            catch (RfcaException ex)
            {
                attempted.Add($"{label}: 初期化に失敗 ({ex.GetType().Name})");
                continue;
            }

            if (prg <= 0)
            {
                attempted.Add($"{label}: 容量を判定できず");
                continue;
            }

            // バンク切り替えが効いていないものは、吸い出す前に捨てる。
            if (!BanksDiffer(bus, mapper, prg))
            {
                attempted.Add($"{label}: バンクが切り替わらない");
                continue;
            }

            byte[] candidate;

            try
            {
                candidate = DumpWith(bus, mapper, prg, chr, progress, cancellationToken);
            }
            catch (RfcaException ex)
            {
                attempted.Add($"{label}: 読み出しに失敗 ({ex.Message})");
                continue;
            }

            // DAT は Headerless。iNES ヘッダ 16 バイトを外して照合する。
            if (db.Match(candidate.AsSpan(16), candidate.Length - 16) is { } hit)
            {
                progress?.Report(new DumpProgress(
                    $"{label} で確定: {hit.GameName}", candidate.Length, candidate.Length));

                attempted.Add($"{label}: No-Intro と一致 → {hit.GameName}");

                return new AutoDetectResult(
                    candidate, label, hit.GameName,
                    string.Join(Environment.NewLine, attempted.Select(a => "  " + a)));
            }

            attempted.Add(
                $"{label}: PRG {prg / 1024}KB / CHR {chr / 1024}KB を吸い出したが DAT に一致せず");

            if (plausible is null)
            {
                plausible = candidate;
                plausibleMapper = label;
            }
        }

        string tried = string.Join(Environment.NewLine, attempted.Select(a => "  " + a));

        // 吸い出せたものを捨てない。
        // 照合できなかっただけで、データ自体は取れている可能性がある。
        // 未収録のソフトや未対応マッパーでも、まず手元にファイルが残るほうがよい。
        if (plausible is not null)
        {
            progress?.Report(new DumpProgress(
                "照合できませんでした（データは保持しています）",
                plausible.Length, plausible.Length));

            return new AutoDetectResult(plausible, plausibleMapper, null, tried);
        }

        throw new RfcaException(
            "マッパーを特定できませんでした。" + Environment.NewLine + Environment.NewLine +
            tried + Environment.NewLine + Environment.NewLine +
            "カセットが正しく読めていない可能性があります。挿し直してから「識別」を押し、" +
            "PRG の先頭 16 バイトを確認してください。");
    }

    /// <summary>
    /// 先頭バンクと最終バンクが違う内容かを見る。
    ///
    /// 同じならバンク切り替えが効いておらず、マッパーの選択が違う。
    /// 全部を吸い出す前にこれで落とせるので、総当たりが実用的な速さになる。
    /// バンクが 1 つしかない構成では判定できないので true を返す。
    /// </summary>
    private static bool BanksDiffer(NesBus bus, NesMapper mapper, long prgSize)
    {
        int bankSize = mapper.PrgBankSize;
        if (bankSize <= 0 || prgSize <= bankSize) return true;

        int banks = (int)(prgSize / bankSize);

        byte[]? first = ReadProbe(bus, mapper, 0, bankSize, 256, isPrg: true);
        byte[]? last = ReadProbe(bus, mapper, banks - 1, bankSize, 256, isPrg: true);

        if (first is null || last is null) return false;

        return !first.AsSpan().SequenceEqual(last);
    }

    /// <summary>指定のマッパーと容量で吸い出し、iNES ファイルを組み立てる。</summary>
    private static byte[] DumpWith(
        NesBus bus, NesMapper mapper, long prgSize, long chrSize,
        IProgress<DumpProgress>? progress, CancellationToken cancellationToken)
    {
        mapper.Initialize(bus);

        // 指定が無ければ実測する。マッパーさえ決まれば容量は測れる。
        if (prgSize <= 0) prgSize = DetectSize(bus, mapper, isPrg: true);

        if (chrSize <= 0 && mapper.ChrBankSize > 0)
            chrSize = DetectChrSize(bus, mapper);

        if (prgSize <= 0)
            throw new RfcaException("PRG-ROM の容量を判定できませんでした。手動で指定してください。");

        long total = prgSize + chrSize;
        long done = 0;

        var prg = ReadBanks(
            bus, mapper, prgSize, mapper.PrgBankSize, isPrg: true,
            progress, ref done, total, cancellationToken);

        byte[] chr = [];

        if (chrSize > 0 && mapper.ChrBankSize > 0)
            chr = ReadBanks(
                bus, mapper, chrSize, mapper.ChrBankSize, isPrg: false,
                progress, ref done, total, cancellationToken);

        return BuildINesFile(mapper.Number, prg, chr);
    }

    /// <summary>
    /// CHR-ROM の容量を実測する。**CHR-RAM の検出を含む。**
    ///
    /// CHR-RAM のカセットには CHR-ROM が載っていない。
    /// PPU バスを読むと RAM の不定値か開放バスが見えるだけで、
    /// 多くは全バイトが同じ値になる。それを 0KB と判定する。
    ///
    /// これを見ないと、存在しない CHR-ROM を 8KB 付けてしまい、
    /// 吸い出し自体は成立しているのに No-Intro と一致しなくなる。
    /// </summary>
    private static long DetectChrSize(NesBus bus, NesMapper mapper)
    {
        var (min, max) = mapper.ChrSizeRange;
        if (mapper.ChrBankSize <= 0 || max <= 0) return 0;

        byte[]? probe = ReadProbe(bus, mapper, 0, mapper.ChrBankSize, 256, isPrg: false);

        // 読めない、または全バイト同じ = CHR-ROM が載っていない。
        if (probe is null || IsFlat(probe))
            return min == max ? min : 0;

        return DetectSize(bus, mapper, isPrg: false);
    }
}
