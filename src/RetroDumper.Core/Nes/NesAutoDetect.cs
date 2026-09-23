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

            byte[] prgData, chrData;

            try
            {
                (prgData, chrData) = ReadParts(bus, mapper, prg, chr, progress, cancellationToken);
            }
            catch (RfcaException ex)
            {
                attempted.Add($"{label}: 読み出しに失敗 ({ex.Message})");
                continue;
            }

            byte[]? asRead = null;

            // 1 回の読み出しを、あり得る解釈それぞれで照合する。
            // 照合は計算だけなので、解釈を増やしても通信時間は増えない。
            foreach (var (p, c, how) in Interpretations(mapper, prgData, chrData))
            {
                var candidate = BuildINesFile(mapper.Number, p, c);

                // 解釈を加えていない最初のものを、照合できなかったときの保存対象にする。
                asRead ??= candidate;

                // DAT は Headerless。iNES ヘッダ 16 バイトを外して照合する。
                if (db.Match(candidate.AsSpan(16), candidate.Length - 16) is not { } hit) continue;

                string detail = how.Length > 0 ? $"{label}（{how}）" : label;

                progress?.Report(new DumpProgress(
                    $"{detail} で確定: {hit.GameName}", candidate.Length, candidate.Length));

                attempted.Add($"{detail}: No-Intro と一致 → {hit.GameName}");

                return new AutoDetectResult(
                    candidate, detail, hit.GameName,
                    string.Join(Environment.NewLine, attempted.Select(a => "  " + a)));
            }

            // 外れた原因が「未収録」なのか「容量の読み違い」なのかを分ける。
            int sameSize = db.CountWithSize(prgData.Length + chrData.Length);

            attempted.Add(
                $"{label}: PRG {prgData.Length / 1024}KB / CHR {chrData.Length / 1024}KB " +
                "を吸い出したが、どの切り方でも DAT に一致せず" +
                (sameSize == 0
                    ? "（この容量のソフトは DAT に 1 本も無い。容量の判定が違う）"
                    : $"（この容量のソフトは DAT に {sameSize} 本ある。中身が違う）"));

            if (plausible is null && asRead is not null)
            {
                plausible = asRead;
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

            return new AutoDetectResult(
                plausible, plausibleMapper, null,
                tried + Environment.NewLine + Environment.NewLine + Diagnose(plausible));
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
    ///
    /// 比較は**バンク全体**で行う。先頭 256 バイトだけを見ていたときは、
    /// 埋め草で始まるバンク同士が一致してしまい、正しいマッパーを
    /// 「切り替わらない」と誤って捨てていた。
    /// </summary>
    private static bool BanksDiffer(NesBus bus, NesMapper mapper, long prgSize)
    {
        int bankSize = mapper.PrgBankSize;
        if (bankSize <= 0 || prgSize <= bankSize) return true;

        int banks = (int)(prgSize / bankSize);

        byte[]? first = ReadBank(bus, mapper, 0, bankSize, isPrg: true);
        byte[]? last = ReadBank(bus, mapper, banks - 1, bankSize, isPrg: true);

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

        var (prg, chr) = ReadParts(bus, mapper, prgSize, chrSize, progress, cancellationToken);

        return BuildINesFile(mapper.Number, prg, chr);
    }

    /// <summary>
    /// PRG と CHR を読み、繋げずにそのまま返す。
    ///
    /// 総当たりでは、同じ読み出し結果を複数の解釈で照合し直したい。
    /// iNES ファイルに組み立ててしまうと分解できないので、部品のまま渡す。
    /// </summary>
    private static (byte[] Prg, byte[] Chr) ReadParts(
        NesBus bus, NesMapper mapper, long prgSize, long chrSize,
        IProgress<DumpProgress>? progress, CancellationToken cancellationToken)
    {
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

        return (prg, chr);
    }

    /// <summary>
    /// 同じ読み出し結果を、あり得る容量の組み合わせで並べる。
    ///
    /// **容量はこちらで決めず、DAT に決めさせる。**
    /// カセットは容量を申告しないので、こちらの実測は当たり外れがある。
    /// 一方 DAT は「その容量・その中身のソフトが実在するか」を確実に答えられる。
    /// ならば、読めたバイト列を切り方を変えて何通りも差し出し、
    /// DAT が受け取ったものを正解とするのが筋が通る。
    ///
    /// 切り方は PRG・CHR とも 2 の冪。実機の容量は必ず 2 の冪で、
    /// 読みすぎた分は折り返しか開放バスなので、前から切れば正しい像になる。
    /// CHR は 0（CHR-RAM）も候補に入れる。CHR-RAM のカセットからも
    /// PPU バスは何かを返すため、それを CHR-ROM と取り違えうる。
    ///
    /// 誤って一致することは考えなくてよい。照合は CRC32 に加えて
    /// MD5 と SHA-1 まで見るので、違う中身が通り抜けることはない。
    /// 照合は計算だけなので、候補を増やしても通信時間は増えない。
    /// 先頭が実測どおりの解釈で、後ろほど小さく切ったものになる。
    /// </summary>
    private static IEnumerable<(byte[] Prg, byte[] Chr, string How)> Interpretations(
        NesMapper mapper, byte[] prg, byte[] chr)
    {
        List<int> prgLengths = [prg.Length];

        for (int len = prg.Length / 2;
             len >= mapper.PrgSizeRange.Min && len >= mapper.PrgBankSize;
             len /= 2)
            prgLengths.Add(len);

        List<int> chrLengths = [chr.Length];

        for (int len = chr.Length / 2; len >= 0x2000; len /= 2)
            chrLengths.Add(len);

        if (chr.Length > 0) chrLengths.Add(0);

        foreach (int p in prgLengths)
            foreach (int c in chrLengths)
            {
                string how = p == prg.Length && c == chr.Length
                    ? ""
                    : $"PRG {p / 1024}KB / " + (c == 0 ? "CHR-RAM" : $"CHR {c / 1024}KB");

                yield return (
                    p == prg.Length ? prg : prg[..p],
                    c == chr.Length ? chr : chr[..c],
                    how);
            }
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

        byte[]? probe = ReadBank(bus, mapper, 0, mapper.ChrBankSize, isPrg: false);

        // 読めない、または全バイト同じ = CHR-ROM が載っていない。
        if (probe is null || IsFlat(probe))
            return min == max ? min : 0;

        return DetectSize(bus, mapper, isPrg: false);
    }

    /// <summary>
    /// 吸い出した内容そのものが妥当かを見る。
    ///
    /// DAT に一致しなかったとき、原因は「未収録」か「読み違い」しかない。
    /// それを切り分けるのに、ファミコンには確実な手掛かりが一つある。
    /// PRG の末尾 6 バイトは 6502 の割り込みベクタで、**必ず $8000 以上**を指す。
    /// ここが壊れていれば、中身ではなく読み出しが失敗している。
    /// </summary>
    private static string Diagnose(byte[] ines)
    {
        int prgLength = ines[4] * 0x4000;
        int chrLength = ines[5] * 0x2000;

        var prg = ines.AsSpan(16, prgLength);
        var chr = ines.AsSpan(16 + prgLength, chrLength);

        var lines = new List<string>
        {
            $"PRG 先頭 16 バイト: {Convert.ToHexString(prg[..Math.Min(16, prg.Length)])}",
            $"PRG 末尾 16 バイト: {Convert.ToHexString(prg[Math.Max(0, prg.Length - 16)..])}",
        };

        if (chrLength > 0)
            lines.Add($"CHR 先頭 16 バイト: {Convert.ToHexString(chr[..Math.Min(16, chr.Length)])}");

        if (prg.Length >= 6)
        {
            var v = prg[^6..];

            ushort nmi = (ushort)(v[0] | (v[1] << 8));
            ushort reset = (ushort)(v[2] | (v[3] << 8));
            ushort irq = (ushort)(v[4] | (v[5] << 8));

            bool sane = nmi >= 0x8000 && reset >= 0x8000 && irq >= 0x8000;

            lines.Add($"割り込みベクタ: NMI ${nmi:X4} / RESET ${reset:X4} / IRQ ${irq:X4}");
            lines.Add(sane
                ? "→ ベクタは妥当です。PRG は正しく読めています。" +
                  "一致しない原因は CHR か容量、または DAT 未収録です。"
                : "→ ベクタが $8000 未満です。**PRG の読み出しが失敗しています。** " +
                  "カセットを挿し直し、端子を清掃してから、もう一度お試しください。");
        }

        return string.Join(Environment.NewLine, lines.Select(l => "  " + l));
    }
}
