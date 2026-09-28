using RetroDumper.Core.Dumping;
using RetroDumper.Core.Transport;
using RetroDumper.Core.Util;

namespace RetroDumper.Core.Snes;

/// <summary>
/// スーパーファミコン / SNES の吸い出し。
///
/// RFCA のリード opcode 0x07 はバスアドレスをそのまま受け取るので、
/// バンク $C0-$FF を直接読める。SA-1 と S-DD1 はどちらも Super MMC が
/// 1MB ブロック 4 本をバンク $C0-$FF に貼り付ける構造で、
/// 電源投入直後は 0,1,2,3 が選択されている。つまり 4MB までの ROM は
/// バンクレジスタに触れずにそのまま連続して読み出せる。
/// </summary>
public sealed class SnesDumper : ICartridgeDumper
{
    public string Name => "スーパーファミコン / SNES";
    public CartridgeKind Kind => CartridgeKind.SuperFamicom;
    public bool IsReady => true;
    public string ReadinessDetail => "";

    /// <summary>SA-1 Super MMC のバンクレジスタ CXB/DXB/EXB/FXB。</summary>
    private const uint Sa1MmcBase = 0x002220;

    /// <summary>S-DD1 のバンクレジスタ。</summary>
    private const uint Sdd1MmcBase = 0x004804;

    /// <summary>MMC が一度に貼れる容量。1MB × 4 本。</summary>
    private const long MmcWindowSize = 4 * SnesAddressMap.MmcBlockSize;

    public CartridgeInfo Identify(IRfcaLink link, DumpOptions options)
    {
        var best = FindHeader(link, out var allCandidates);
        if (best is null)
            throw new RfcaException("SFC の内部ヘッダを特定できませんでした。カセットの接触を確認してください。");

        var mapper = options.SnesMapperOverride ?? best.ResolveMapper();

        var knownSize = best.KnownSizeOverride();
        long romSize = options.RomSizeOverride ?? knownSize?.Size ?? best.DeclaredRomSize;
        if (romSize <= 0) romSize = 4 * 1024 * 1024;

        long clamped = ClampToAddressableSize(mapper, romSize);

        // **SaveSize と SaveMemorySize は別物。**
        // SaveSize は「この吸い出しに含めるか」で、既定はオフ。
        // SaveMemorySize はカートリッジに載っている容量そのもので、
        // セーブだけを読み書きする画面がこちらを見る。
        // 両方を同じ値にしていたため、チェックを入れない限り
        // セーブの吸い出しが「セーブ RAM がありません」で止まっていた。
        long saveSize = options.IncludeSaveRam ? best.DeclaredRamSize : 0;

        var info = new CartridgeInfo
        {
            Kind = CartridgeKind.SuperFamicom,
            Title = best.Title,
            RomSize = clamped,
            SaveSize = saveSize,
            SaveMemorySize = best.DeclaredRamSize,
            SnesMapping = best.Mapper,
            Mapper = DescribeMapper(mapper, best),
            RomExtension = ".sfc",
            RawHeader = best.Raw,
        };

        info.Details["ヘッダ位置"] = $"バス 0x{best.BusAddress:X6}";
        info.Details["マップモード"] = $"0x{best.MapModeByte:X2} ({(best.FastRom ? "FastROM" : "SlowROM")})";
        info.Details["カートリッジ種別"] = $"0x{best.CartTypeByte:X2}";
        info.Details["コプロセッサ"] = best.CoprocessorName;
        info.Details["バッテリバックアップ"] = best.HasBattery ? "あり" : "なし";
        info.Details["ROM サイズ(ヘッダ申告)"] = FormatSize(best.DeclaredRomSize);
        info.Details["SRAM サイズ(ヘッダ申告)"] = FormatSize(best.DeclaredRamSize);
        info.Details["チェックサム"] = $"0x{best.Checksum:X4} / 補数 0x{best.ChecksumComplement:X4}" +
                                       (best.ChecksumPairValid ? " (整合)" : " (不整合)");
        info.Details["バージョン"] = $"1.{best.Version}";
        info.Details["候補スコア"] = string.Join(", ",
            allCandidates.Select(c => $"0x{c.BusAddress:X6}:{c.Score}"));

        if (!best.ChecksumPairValid)
            info.Warnings.Add("ヘッダのチェックサムと補数が整合しません。ヘッダ位置の判定が誤っている可能性があります。");

        if (options.RomSizeOverride is not null)
            info.Warnings.Add($"ROM サイズをヘッダ申告ではなく指定値 {FormatSize(romSize)} で吸い出します。");
        else if (knownSize is var (_, reason))
            info.Warnings.Add(
                $"{reason}。ヘッダ申告 ({FormatSize(best.DeclaredRomSize)}) ではなく " +
                $"{FormatSize(romSize)} で吸い出します。");

        if (mapper is SnesMapper.Sa1 or SnesMapper.Sdd1 && romSize > MmcWindowSize)
            info.Warnings.Add(
                $"{mapper} で 4MB を超えるため、MMC バンクレジスタへの書き込みが必須になります。" +
                "書き込みが効かない場合は 4MB 以降が正しく読めません。");

        if (mapper == SnesMapper.Spc7110 && romSize > Spc7110DirectSize)
            info.Warnings.Add(
                "SPC7110 で 4MB を超えるため、窓を貼り替えるレジスタへの書き込みが必須になります。" +
                "書き込みが効かない場合は 4MB 以降が正しく読めません。" +
                "**この経路は実機で確認できていません。**" +
                "吸い出したら No-Intro と一致するか必ず確かめてください。");

        if (clamped != romSize)
            info.Warnings.Add(
                $"{mapper} でバス上から読める上限は {FormatSize(clamped)} です。" +
                $"要求された {FormatSize(romSize)} を切り詰めました。" +
                "ヘッダのサイズ欄は 2 のべき乗しか表現できないため、" +
                "実容量が半端なカセットではサイズの手動指定が必要です。");

        return info;
    }

    /// <summary>
    /// マッパーごとに、バス上から連続して読み出せる上限。
    ///
    /// ヘッダのサイズ欄は 2 のべき乗しか表現できないので、
    /// 6MB のカセットが 8MB と申告することがある。そのまま読もうとすると
    /// WRAM のバンク ($7E/$7F) に踏み込んで壊れたデータを掴むため、
    /// ここで頭打ちにする。
    /// </summary>
    private static long ClampToAddressableSize(SnesMapper mapper, long requested) => mapper switch
    {
        // バンク $00-$7F の $8000-$FFFF。32KB × 128 = 4MB。
        SnesMapper.LoRom => Math.Min(requested, 4 * 1024 * 1024),

        // バンク $C0-$FF の 64KB × 64 = 4MB。これを超えるものは ExHiROM。
        SnesMapper.HiRom => Math.Min(requested, 4 * 1024 * 1024),

        // SPC7110 は 1MB の窓を貼り替えられるので 8MB まで。
        SnesMapper.Spc7110 => Math.Min(requested, 8 * 1024 * 1024),

        // 前半 4MB がバンク $C0-$FF、後半がバンク $40-$7D。
        // $7E/$7F は WRAM なので後半は 62 バンク = 3.875MB まで。
        SnesMapper.ExHiRom => Math.Min(requested, 0x7E0000),

        // MMC で 4MB ウィンドウを 2 枚貼れるので 8MB まで。
        SnesMapper.Sa1 or SnesMapper.Sdd1 => Math.Min(requested, 8 * 1024 * 1024),

        _ => requested,
    };

    public DumpResult Dump(
        IRfcaLink link,
        CartridgeInfo info,
        DumpOptions options,
        IProgress<DumpProgress>? progress,
        CancellationToken cancellationToken)
    {
        var mapper = options.SnesMapperOverride ?? ResolveMapperFrom(info);

        byte[] rom = mapper is SnesMapper.Sa1 or SnesMapper.Sdd1
            ? DumpViaMmc(link, mapper, info.RomSize, options, progress, cancellationToken)
            : mapper == SnesMapper.Spc7110
            ? DumpViaSpc7110(link, info.RomSize, options, progress, cancellationToken)
            : BulkReader.Read(
                link, RfcaOpcode.SnesRead, info.RomSize,
                offset => SnesAddressMap.ToBusAddress(mapper, offset),
                options, "ROM 読み出し", progress, cancellationToken,
                maxChunkAlignment: (int)SnesAddressMap.ChunkAlignment(mapper));

        byte[]? save = null;
        if (options.IncludeSaveRam && info.SaveSize > 0)
            save = DumpSaveRam(link, mapper, info.SaveSize, options, progress, cancellationToken);

        bool? checksumOk = null;
        string detail = "";
        if (options.VerifyChecksum && info.RawHeader.Length >= 0x20)
        {
            ushort expected = (ushort)(info.RawHeader[0x1E] | (info.RawHeader[0x1F] << 8));
            ushort actual = Checksums.SnesChecksum(rom);
            checksumOk = expected == actual;
            detail = $"ヘッダ 0x{expected:X4} / 実データ 0x{actual:X4}";

            if (checksumOk == false)
                detail += "。ROM サイズ指定が違うか、マッパー判定が誤っている可能性があります。";
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

    // ------------------------------------------------------------------
    // SPC7110
    // ------------------------------------------------------------------

    /// <summary>4MB。ここまでは貼り替えずに読める。</summary>
    private const long Spc7110DirectSize = SnesAddressMap.Spc7110DirectSize;

    /// <summary>
    /// SPC7110 経由の吸い出し。32KB ずつ、必要に応じて窓を貼り替えながら読む。
    ///
    /// 先頭 4MB はバンク $C0 以降にそのまま並んでいるので貼り替えは要らない。
    /// それを超える分（天外魔境ZERO の 5MB など）は、1MB ごとに
    /// $4831 を書いてバンク $D0 に貼り、そこから読む。
    ///
    /// **この経路は実機で確認できていない。**手順は動作実績のある実装から
    /// 写したもので、該当カセットが手元に無く試せていない。
    /// </summary>
    private static byte[] DumpViaSpc7110(
        IRfcaLink link,
        long romSize,
        DumpOptions options,
        IProgress<DumpProgress>? progress,
        CancellationToken cancellationToken)
    {
        bool needsPaging = romSize > Spc7110DirectSize;

        // 4MB で収まるカセットには一切書き込まない。
        // 書き込み保護を有効にしたままでも吸い出せる状態を保つ。
        if (needsPaging || options.ForceMmcInit)
            InitializeSpc7110(link);

        var rom = new byte[romSize];
        long done = 0;
        byte page = 0;

        for (int bank = 0; done < romSize; bank++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte wanted = SnesAddressMap.Spc7110PageFor(bank);

            // 同じ窓が続く間は書き直さない。1MB ごとに 1 回で済む。
            if (needsPaging && wanted != page)
            {
                link.WriteByte(RfcaOpcode.SnesWrite, SnesAddressMap.Spc7110PageRegister, wanted);
                page = wanted;
            }

            long length = Math.Min(SnesAddressMap.Spc7110BankSize, romSize - done);
            uint at = SnesAddressMap.Spc7110BusAddress(bank);
            long captured = done;

            byte[] part = BulkReader.Read(
                link, RfcaOpcode.SnesRead, length,
                offsetInBank => at + (uint)offsetInBank,
                options, "ROM 読み出し (SPC7110)",
                new WindowProgress(progress, captured, romSize),
                cancellationToken,
                maxChunkAlignment: (int)SnesAddressMap.Spc7110BankSize);

            part.CopyTo(rom, done);
            done += length;
        }

        // 貼り替えたままだと以後の読み出しがずれる。既定へ戻す。
        if (needsPaging)
            link.WriteByte(RfcaOpcode.SnesWrite, SnesAddressMap.Spc7110PageRegister, 0);

        return rom;
    }

    /// <summary>
    /// SPC7110 を吸い出せる状態にする。
    ///
    /// **この並びは動作実績のある実装から写したもの。**
    /// 各レジスタの意味までは分かっていないため、順序も回数も変えない。
    /// $4830 への 0x80 / 0x00 の叩きが何を起こしているかは不明。
    /// </summary>
    private static void InitializeSpc7110(IRfcaLink link)
    {
        void Write(uint address, byte value)
            => link.WriteByte(RfcaOpcode.SnesWrite, address, value);

        Write(0x004834, 0x02);

        Write(0x004830, 0x80);
        Write(0x004830, 0x00);
        Write(0x004830, 0x80);

        for (int i = 0; i < 6; i++)
        {
            Write(0x004830, 0x80);
            Write(0x004830, 0x00);
        }

        Write(0x004830, 0x80);

        for (int i = 0; i < 2; i++)
        {
            Write(0x004830, 0x80);
            Write(0x004830, 0x00);
        }

        Write(0x004831, 0x00);
        Write(0x004832, 0x01);
        Write(0x004833, 0x02);
    }

    // ------------------------------------------------------------------
    // SA-1 / S-DD1
    // ------------------------------------------------------------------

    /// <summary>
    /// Super MMC 経由の吸い出し。4MB ごとにバンクレジスタを貼り替えながら
    /// バンク $C0-$FF を読む。
    ///
    /// 先頭 4MB は電源投入直後の既定値 (0,1,2,3) がそのまま使えるため、
    /// ForceMmcInit が false ならレジスタ書き込みを行わない。バス書き込みが
    /// 効かない個体でも市販の SA-1 カセット（最大 4MB）は吸い出せる。
    /// </summary>
    private static byte[] DumpViaMmc(
        IRfcaLink link,
        SnesMapper mapper,
        long romSize,
        DumpOptions options,
        IProgress<DumpProgress>? progress,
        CancellationToken cancellationToken)
    {
        uint mmcBase = mapper == SnesMapper.Sa1 ? Sa1MmcBase : Sdd1MmcBase;
        var rom = new byte[romSize];
        long done = 0;

        for (int window = 0; done < romSize; window++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long remaining = romSize - done;
            long windowLength = Math.Min(MmcWindowSize, remaining);

            // 2 枚目以降のウィンドウは必ず貼り替えが要る。
            // 1 枚目はリセット既定値と同じなので、明示指定時のみ書く。
            if (window > 0 || options.ForceMmcInit)
                SelectMmcWindow(link, mmcBase, window);

            long captured = done;
            byte[] part = BulkReader.Read(
                link, RfcaOpcode.SnesRead, windowLength,
                offsetInWindow => SnesAddressMap.MmcWindow(offsetInWindow),
                options,
                $"ROM 読み出し (MMC ウィンドウ {window + 1})",
                new WindowProgress(progress, captured, romSize),
                cancellationToken,
                maxChunkAlignment: (int)SnesAddressMap.BankSize);

            part.CopyTo(rom, done);
            done += windowLength;
        }

        // 貼り替えたままだと以後の読み出しがずれるので既定値へ戻す。
        if (romSize > MmcWindowSize || options.ForceMmcInit)
            SelectMmcWindow(link, mmcBase, 0);

        return rom;
    }

    /// <summary>MMC のバンクレジスタ 4 本に、指定ウィンドウの 1MB ブロック番号を書く。</summary>
    private static void SelectMmcWindow(IRfcaLink link, uint mmcBase, int window)
    {
        for (uint i = 0; i < 4; i++)
        {
            byte block = (byte)(window * 4 + i);
            link.WriteByte(RfcaOpcode.SnesWrite, mmcBase + i, block);
        }
    }

    /// <summary>ウィンドウ単位の進捗を、ROM 全体の進捗へ読み替える。</summary>
    private sealed class WindowProgress(IProgress<DumpProgress>? inner, long baseOffset, long total)
        : IProgress<DumpProgress>
    {
        public void Report(DumpProgress value)
            => inner?.Report(new DumpProgress(value.Stage, baseOffset + value.BytesDone, total));
    }

    // ------------------------------------------------------------------
    // セーブ RAM
    // ------------------------------------------------------------------

    private static byte[] DumpSaveRam(
        IRfcaLink link,
        SnesMapper mapper,
        long saveSize,
        DumpOptions options,
        IProgress<DumpProgress>? progress,
        CancellationToken cancellationToken)
    {
        var layout = SnesAddressMap.SramLayout(mapper)
            ?? throw new RfcaException($"{mapper} のセーブ RAM 配置が未定義です。");

        return BulkReader.Read(
            link, layout.ReadOpcode, saveSize,
            offset => SnesAddressMap.SramBusAddress(layout, offset),
            options, "セーブ RAM 読み出し", progress, cancellationToken,
            maxChunkAlignment: layout.BytesPerBank);
    }

    // ------------------------------------------------------------------
    // ヘッダ探索
    // ------------------------------------------------------------------

    /// <summary>
    /// SA-1 カセットの下準備。
    ///
    /// SA-1 は電源投入直後、バンク $C0 側へのアクセスが一度もないと
    /// ヘッダ領域が正しく読めない。既存の吸い出し機も同じ理由で
    /// ヘッダ読み出し前にバンク $C0 のダミーリードを 1024 バイト行っている。
    /// SA-1 以外のカセットでは単なる無害な読み捨てになる。
    /// </summary>
    private static void PrimeSa1(IRfcaLink link)
    {
        try
        {
            link.Read(RfcaOpcode.SnesRead, 0xC00000, 1024);
        }
        catch (RfcaException)
        {
            // バンク $C0 が読めないカセットもある。ここで失敗しても続行する。
        }
    }

    private static SnesHeader? FindHeader(IRfcaLink link, out List<SnesHeader> candidates)
    {
        candidates = [];

        PrimeSa1(link);

        foreach (var candidate in SnesHeader.Candidates)
        {
            try
            {
                var raw = link.Read(
                    RfcaOpcode.SnesRead, candidate.BusAddress, SnesHeader.HeaderReadSize);
                candidates.Add(SnesHeader.Parse(raw, candidate));
            }
            catch (RfcaException)
            {
                // ExHiROM 候補はバンク $40 が存在しないカセットで読めないことがある。
                // 候補が 1 つでも取れれば続行する。
            }
        }

        if (candidates.Count == 0) return null;

        // LoROM カセットは A15 が ROM に配線されていないため、
        // バス 0x007FC0 と 0x00FFC0 が同じ内容を返す。これは HiROM では起きない。
        var lo = candidates.FirstOrDefault(c => c.BusAddress == 0x007FC0);
        var hi = candidates.FirstOrDefault(c => c.BusAddress == 0x00FFC0);
        bool loRomMirrored = lo is not null && hi is not null && lo.Raw.AsSpan().SequenceEqual(hi.Raw);

        return candidates
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => loRomMirrored && c.BusAddress == 0x007FC0 ? 1 : 0)
            .ThenBy(c => c.BusAddress)
            .First();
    }

    private static SnesMapper ResolveMapperFrom(CartridgeInfo info)
    {
        // Parse はリセットベクタ ($3C) まで読む。0x20 で通すと配列外になる。
        if (info.RawHeader.Length < 0x40)
            return SnesMapper.LoRom;

        // 識別時に確定したヘッダなので、位置による絞り込みはもう不要。
        // マップモードとチップ欄だけからマッパーを決める。
        var header = SnesHeader.Parse(info.RawHeader, 0, Enum.GetValues<SnesMapper>());
        return header.ResolveMapper();
    }

    private static string DescribeMapper(SnesMapper mapper, SnesHeader header)
    {
        string baseName = mapper switch
        {
            SnesMapper.LoRom => "LoROM",
            SnesMapper.HiRom => "HiROM",
            SnesMapper.ExHiRom => "ExHiROM",
            SnesMapper.Sa1 => "LoROM + SA-1",
            SnesMapper.Sdd1 => "LoROM + S-DD1",
            SnesMapper.Spc7110 => "HiROM + SPC7110",
            _ => mapper.ToString(),
        };

        if (header.Coprocessor is not SnesCoprocessor.None
            and not SnesCoprocessor.Sa1
            and not SnesCoprocessor.Sdd1
            and not SnesCoprocessor.Spc7110)
        {
            baseName += $" + {header.CoprocessorName}";
        }

        return baseName;
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        0 => "なし",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.##} MB",
    };
}
