using RetroDumper.Core.Dumping;
using RetroDumper.Core.Transport;
using RetroDumper.Core.Util;

namespace RetroDumper.Core.Pce;

/// <summary>
/// PC エンジン Hu カードの吸い出し。
///
/// **Hu カードはヘッダを持ちません。**タイトルも容量も配置も申告しないので、
/// ファミコンと同じく読んだ内容から判断するしかありません。
///
/// 配置は 3 通りあり、先頭 8KB が別の番地でも同じ内容に見えるかで見分けます。
///
///   $00000 と $40000 が違う  → リニア（ほとんどの Hu カード）
///   $00000 = $40000 ≠ $80000 → インターバル（256KB + 空き + 続き）
///   $00000 = $40000 = $80000 → リニアで 256KB 以下
///
/// 容量は 128KB ずつ読み進め、先頭 8KB と同じ内容が現れたところを
/// 折り返しとみなして打ち切ります。手順は参照実装に合わせました。
/// </summary>
public sealed class PceDumper : ICartridgeDumper
{
    public string Name => "PC エンジン Hu カード";
    public CartridgeKind Kind => CartridgeKind.PcEngineHuCard;
    public bool IsReady => true;
    public string ReadinessDetail => "";

    public CartridgeInfo Identify(IRfcaLink link, DumpOptions options)
    {
        var mapping = DetectMapping(link);
        var head = link.Read(RfcaOpcode.PcEngineRead, 0, 64);

        var info = new CartridgeInfo
        {
            Kind = Kind,
            Title = "",                     // Hu カードはタイトルを持たない
            RomSize = 0,                    // 吸い出し時に実測する
            SaveSize = 0,
            Mapper = PceMapper.DisplayName(mapping),
            RomExtension = ".pce",
            RawHeader = head,
        };

        info.Details["先頭 16 バイト"] = Convert.ToHexString(head.AsSpan(0, 16));
        info.Details["バンク配置"] = PceMapper.DisplayName(mapping);
        info.Details["容量"] = "吸い出し時に実測";

        if (IsFlat(head))
            info.Warnings.Add(
                $"読み出しが全バイト 0x{head[0]:X2} です。カードがバスを駆動していません。" +
                "挿し直し、端子の清掃を試してください。");

        info.Warnings.Add(
            "Hu カードはタイトルを申告しません。吸い出したあと No-Intro と照合して名前を決めます。");

        return info;
    }

    public DumpResult Dump(
        IRfcaLink link,
        CartridgeInfo info,
        DumpOptions options,
        IProgress<DumpProgress>? progress,
        CancellationToken cancellationToken)
    {
        var mapping = DetectMapping(link);
        var rom = DumpAuto(link, mapping, progress, cancellationToken);

        return new DumpResult
        {
            Info = info,
            Rom = rom,
            Crc32 = Checksums.Crc32(rom),

            // Hu カードはチェックサムを持たない。正否は No-Intro との照合で判断する。
            ChecksumOk = null,
            ChecksumDetail =
                $"バンク配置: {PceMapper.DisplayName(mapping)} / {rom.Length / 1024}KB。" +
                "Hu カードはチェックサムを持たないため、No-Intro との照合で確かめてください。",
        };
    }

    /// <summary>
    /// バンク配置を見分ける。読むだけで判定できるところまでを行う。
    ///
    /// SF2 ダッシュの判定にはレジスタへの書き込みが要る。
    /// バンク切り替えが許可されているときだけ試し、そうでなければリニアとみなす。
    /// </summary>
    public static PceMapping DetectMapping(IRfcaLink link)
    {
        var head = Probe(link, 0);

        if (Probe(link, 256 * 1024) is { } at256 && head.AsSpan().SequenceEqual(at256))
        {
            // $40000 が先頭と同じ。$80000 も同じならリニア（折り返している）。
            var at512 = Probe(link, 512 * 1024);

            return head.AsSpan().SequenceEqual(at512)
                ? PceMapping.Linear
                : PceMapping.Interval;
        }

        return DetectSf2(link) ? PceMapping.Sf2Dash : PceMapping.Linear;
    }

    /// <summary>
    /// SF2 ダッシュか。$1FF1 へ書いて $80000 の内容が変われば、そう。
    ///
    /// 普通の Hu カードは ROM しか無いので、書いても何も変わらない。
    /// 書き込みが許可されていなければ判定しない（リニア扱いで返す）。
    /// </summary>
    private static bool DetectSf2(IRfcaLink link)
    {
        try
        {
            var before = Probe(link, 512 * 1024);

            link.WriteBankRegister(
                CartridgeKind.PcEngineHuCard, RfcaOpcode.PcEngineWrite,
                PceMapper.Sf2RegisterBase + 1, 0xFF);

            var after = Probe(link, 512 * 1024);

            // 元の窓へ戻す。
            link.WriteBankRegister(
                CartridgeKind.PcEngineHuCard, RfcaOpcode.PcEngineWrite,
                PceMapper.Sf2RegisterBase, 0xFF);

            return !before.AsSpan().SequenceEqual(after);
        }
        catch (RfcaWriteBlockedException)
        {
            // 書き込みが許可されていない。SF2 かどうかは判定できない。
            return false;
        }
        catch (RfcaException)
        {
            return false;
        }
    }

    /// <summary>
    /// 容量を実測しながら吸い出す。
    ///
    /// 128KB ずつ読み進め、そこまでの先頭 8KB と同じ内容が現れたら
    /// 折り返しとみなして打ち切る。Hu カードは容量を申告しないので、
    /// これ以外に終端を知る方法がない。
    /// </summary>
    private static byte[] DumpAuto(
        IRfcaLink link, PceMapping mapping,
        IProgress<DumpProgress>? progress, CancellationToken cancellationToken)
    {
        long max = PceMapper.MaxSize(mapping);
        var banks = new List<byte[]>();
        var head = Probe(link, 0);

        int perGroup = PceMapper.GroupSize / PceMapper.BankSize;
        long done = 0;

        while (done < max)
        {
            for (int i = 0; i < perGroup && done < max; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                banks.Add(ReadBank(link, mapping, banks.Count));

                done += PceMapper.BankSize;
                progress?.Report(new DumpProgress(
                    $"吸い出し ({PceMapper.DisplayName(mapping)})", done, max));
            }

            if (done >= max) break;

            // 区切りごとに折り返しを見る。
            var here = Probe(link, (uint)done);

            if (head.AsSpan().SequenceEqual(here)) break;

            // 512KB ごとに比較の基準を取り直す。
            if (done % (512 * 1024) == 0) head = here;
        }

        return Flatten(banks, mapping);
    }

    /// <summary>
    /// バンクを繋いで 1 本にする。
    ///
    /// 先頭 128KB がすべて同じ内容だったときは、32KB のカードが
    /// 折り返して見えているだけ。1 バンクぶんに縮める。
    /// </summary>
    private static byte[] Flatten(List<byte[]> banks, PceMapping mapping)
    {
        if (banks.Count == 0) return [];

        bool allSame = mapping != PceMapping.Sf2Dash
            && banks.Count == PceMapper.GroupSize / PceMapper.BankSize
            && banks.Skip(1).All(b => b.AsSpan().SequenceEqual(banks[0]));

        if (allSame) return banks[0];

        var rom = new byte[banks.Count * PceMapper.BankSize];

        for (int i = 0; i < banks.Count; i++)
            banks[i].CopyTo(rom, i * PceMapper.BankSize);

        return rom;
    }

    private static byte[] ReadBank(IRfcaLink link, PceMapping mapping, int bank)
    {
        if (PceMapper.Sf2Register(mapping, bank) is uint register)
            link.WriteBankRegister(
                CartridgeKind.PcEngineHuCard, RfcaOpcode.PcEngineWrite, register, 0xFF);

        return link.Read(
            RfcaOpcode.PcEngineRead, PceMapper.BankAddress(mapping, bank), PceMapper.BankSize);
    }

    private static byte[] Probe(IRfcaLink link, uint address)
        => link.Read(RfcaOpcode.PcEngineRead, address, PceMapper.ProbeSize);

    private static bool IsFlat(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            if (b != data[0]) return false;

        return true;
    }
}
