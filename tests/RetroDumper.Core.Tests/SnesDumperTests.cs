using RetroDumper.Core.Transport;
using RetroDumper.Core.Dumping;
using RetroDumper.Core.Snes;
using Xunit;

namespace RetroDumper.Core.Tests;

public class SnesDumperTests
{
    private const long Mb = 1024 * 1024;

    private static DumpOptions Options(Action<DumpOptions>? configure = null)
    {
        var options = new DumpOptions { ChunkSize = 1024, IncludeSaveRam = false };
        configure?.Invoke(options);
        return options;
    }

    private static (CartridgeInfo Info, DumpResult Result) DumpRoundTrip(
        byte[] rom, SnesMapper mapper, DumpOptions? options = null)
    {
        options ??= Options();
        var cart = new FakeSnesCartridge(rom, mapper);
        var dumper = new SnesDumper();

        var info = dumper.Identify(cart, options);
        var result = dumper.Dump(cart, info, options, null, CancellationToken.None);
        return (info, result);
    }

    // ==================================================================
    // マッパー判定
    // ==================================================================

    [Theory]
    [InlineData(SnesMapper.LoRom, 1 * Mb)]
    [InlineData(SnesMapper.HiRom, 2 * Mb)]
    [InlineData(SnesMapper.Sa1, 4 * Mb)]
    public void Identify_DetectsMapperFromHeader(SnesMapper mapper, long size)
    {
        var rom = SnesRomBuilder.Build(mapper, size);
        var (info, _) = DumpRoundTrip(rom, mapper);

        Assert.Equal(size, info.RomSize);
        Assert.Contains(ExpectedMapperText(mapper), info.Mapper);
    }

    /// <summary>
    /// SA-1 のヘッダは $00:7FC0 では読めない。Super MMC がバンク $00 の
    /// $8000-$FFFF を ROM 先頭に貼るので、$00:FFC0 からしか読めない。
    /// </summary>
    [Fact]
    public void Identify_Sa1_ReadsHeaderFromUniversalCandidate()
    {
        var rom = SnesRomBuilder.Build(SnesMapper.Sa1, 4 * Mb);
        var (info, _) = DumpRoundTrip(rom, SnesMapper.Sa1);

        Assert.Equal("バス 0x00FFC0", info.Details["ヘッダ位置"]);
    }

    /// <summary>素の LoROM は A15 を無視するので $00:7FC0 でも読める。</summary>
    [Fact]
    public void Identify_LoRom_PrefersLoRomCandidateWhenMirrored()
    {
        var rom = SnesRomBuilder.Build(SnesMapper.LoRom, 1 * Mb);
        var (info, _) = DumpRoundTrip(rom, SnesMapper.LoRom);

        Assert.Equal("バス 0x007FC0", info.Details["ヘッダ位置"]);
    }

    // ==================================================================
    // ROM 吸い出しの往復一致
    // ==================================================================

    [Theory]
    [InlineData(SnesMapper.LoRom, 512 * 1024)]
    [InlineData(SnesMapper.LoRom, 1 * Mb)]
    [InlineData(SnesMapper.LoRom, 4 * Mb)]
    [InlineData(SnesMapper.HiRom, 1 * Mb)]
    [InlineData(SnesMapper.HiRom, 2 * Mb)]
    [InlineData(SnesMapper.HiRom, 4 * Mb)]
    public void Dump_RoundTripsExactly(SnesMapper mapper, long size)
    {
        var rom = SnesRomBuilder.Build(mapper, size);
        var (_, result) = DumpRoundTrip(rom, mapper);

        Assert.Equal(rom.Length, result.Rom.Length);
        Assert.Equal(rom, result.Rom);
    }

    /// <summary>
    /// LoROM の 4MB はバンク $7E/$7F（WRAM）に踏み込む。
    /// ミラーの $FE/$FF へ逃がせていなければここで壊れる。
    /// </summary>
    [Fact]
    public void Dump_LoRom_AvoidsWramBanks()
    {
        var rom = SnesRomBuilder.Build(SnesMapper.LoRom, 4 * Mb);
        var (_, result) = DumpRoundTrip(rom, SnesMapper.LoRom);

        // ROM オフセット 0x3F0000 以降がバンク $7E/$7F に当たる領域。
        Assert.Equal(rom[0x3F0000..], result.Rom[0x3F0000..]);
    }

    // ==================================================================
    // SA-1
    // ==================================================================

    /// <summary>
    /// SA-1 の本命。電源投入直後の MMC 既定値 (0,1,2,3) のまま
    /// バンク $C0-$FF を読むだけで 4MB が連続して取れる。
    /// 市販の SA-1 カセットは最大 4MB なので、これで全タイトルを賄える。
    /// </summary>
    [Fact]
    public void Dump_Sa1_FourMegabytes_NeedsNoBankRegisterWrites()
    {
        var rom = SnesRomBuilder.Build(SnesMapper.Sa1, 4 * Mb);
        var cart = new FakeSnesCartridge(rom, SnesMapper.Sa1);
        var dumper = new SnesDumper();
        var options = Options();

        var info = dumper.Identify(cart, options);
        var result = dumper.Dump(cart, info, options, null, CancellationToken.None);

        Assert.Equal(rom, result.Rom);
        Assert.Empty(cart.Writes);
    }

    [Fact]
    public void Dump_Sa1_WithForcedMmcInit_StillRoundTrips()
    {
        var rom = SnesRomBuilder.Build(SnesMapper.Sa1, 4 * Mb);

        // MMC の貼り替えは書き込みを伴うので、明示的に許可する必要がある。
        var cart = new FakeSnesCartridge(rom, SnesMapper.Sa1) { AllowWrites = true };
        var dumper = new SnesDumper();
        var options = Options(o => o.ForceMmcInit = true);

        var info = dumper.Identify(cart, options);
        var result = dumper.Dump(cart, info, options, null, CancellationToken.None);

        Assert.Equal(rom, result.Rom);

        // CXB/DXB/EXB/FXB に 0,1,2,3 を書いているはず。
        // 読み終わりに既定値へ戻すので、同じ並びが 2 回現れる。
        var mmcWrites = cart.Writes.Where(w => w.Address is >= 0x2220 and <= 0x2223).ToList();
        Assert.Equal(8, mmcWrites.Count);
        Assert.Equal([0, 1, 2, 3], mmcWrites.Take(4).Select(w => (int)w.Data[0]));
        Assert.Equal([0, 1, 2, 3], mmcWrites.TakeLast(4).Select(w => (int)w.Data[0]));
    }

    /// <summary>
    /// 4MB を超える SA-1 は MMC を貼り替えないと読めない。
    /// 市販品には存在しないが、貼り替えロジック自体を検証しておく。
    /// </summary>
    [Fact]
    public void Dump_Sa1_BeyondFourMegabytes_RepagesMmc()
    {
        const long size = 6 * Mb;
        var rom = SnesRomBuilder.Build(SnesMapper.Sa1, size, fixChecksum: false);

        // 2 枚目のウィンドウを貼るには書き込みが要る。
        var cart = new FakeSnesCartridge(rom, SnesMapper.Sa1) { AllowWrites = true };
        var dumper = new SnesDumper();

        // ヘッダのサイズ欄は 2 のべき乗しか表現できないため、
        // 6MB のカセットは実際の運用でもサイズ手動指定が要る。
        var options = Options(o =>
        {
            o.VerifyChecksum = false;
            o.RomSizeOverride = size;
        });

        var info = dumper.Identify(cart, options);
        var result = dumper.Dump(cart, info, options, null, CancellationToken.None);

        Assert.Equal(size, result.Rom.LongLength);
        Assert.Equal(rom, result.Rom);

        // 2 枚目のウィンドウでブロック 4,5,6,7 を選んでいるはず。
        var blocks = cart.Writes
            .Where(w => w.Address is >= 0x2220 and <= 0x2223)
            .Select(w => (int)w.Data[0])
            .ToList();
        Assert.Contains(4, blocks);
        Assert.Contains(7, blocks);
    }

    // ==================================================================
    // ExHiROM
    // ==================================================================

    [Fact]
    public void Dump_ExHiRom_SixMegabytes_RoundTrips()
    {
        const long size = 6 * Mb;
        var rom = SnesRomBuilder.Build(SnesMapper.ExHiRom, size, fixChecksum: false);
        var options = Options(o =>
        {
            o.VerifyChecksum = false;
            o.RomSizeOverride = size;
        });
        var (info, result) = DumpRoundTrip(rom, SnesMapper.ExHiRom, options);

        Assert.Contains("ExHiROM", info.Mapper);
        Assert.Equal(rom, result.Rom);
    }

    /// <summary>
    /// ヘッダが 8MB と申告する ExHiROM をそのまま読むと、後半がバンク
    /// $7E/$7F の WRAM に踏み込む。読める範囲で頭打ちにすること。
    /// </summary>
    [Fact]
    public void Identify_ExHiRom_ClampsToAddressableRange()
    {
        var rom = SnesRomBuilder.Build(SnesMapper.ExHiRom, 6 * Mb, fixChecksum: false);
        var cart = new FakeSnesCartridge(rom, SnesMapper.ExHiRom);

        var info = new SnesDumper().Identify(cart, Options(o => o.VerifyChecksum = false));

        // ヘッダのサイズコードは 8MB を指すが、読めるのは 0x7E0000 まで。
        Assert.Equal(0x7E0000, info.RomSize);
        Assert.Contains(info.Warnings, w => w.Contains("切り詰め"));
    }

    // ==================================================================
    // セーブ RAM
    // ==================================================================

    [Fact]
    public void Dump_Sa1_SaveRam_ReadsFromBwRamBanks()
    {
        // BW-RAM はバンク $40-$4F。FakeSnesCartridge は ROM しか持たないので
        // ここではアドレス計算だけを検証する。
        var layout = SnesAddressMap.SramLayout(SnesMapper.Sa1);
        Assert.NotNull(layout);
        Assert.Equal(0x40u, layout!.Value.ReadBank);
        Assert.Equal(0x400000u, SnesAddressMap.SramBusAddress(layout.Value, 0));
        Assert.Equal(0x410000u, SnesAddressMap.SramBusAddress(layout.Value, 0x10000));
    }

    /// <summary>
    /// HiROM の SRAM はバンク $30 以降の $6000-$7FFF。
    ///
    /// 以前は $20 以降としていたが、実機で確かめたものではなかった。
    /// $20 も $30 も同じ SRAM の見え方だが、動作実績のある
    /// RetroFreakDumper が $30 を使っているので、そちらに合わせる。
    /// </summary>
    [Fact]
    public void SramLayout_HiRom_UsesEightKilobyteWindows()
    {
        var layout = SnesAddressMap.SramLayout(SnesMapper.HiRom)!.Value;

        Assert.Equal(0x306000u, SnesAddressMap.SramBusAddress(layout, 0));
        Assert.Equal(0x307FFFu, SnesAddressMap.SramBusAddress(layout, 0x1FFF));
        // 8KB 窓を使い切ったら次のバンクへ。
        Assert.Equal(0x316000u, SnesAddressMap.SramBusAddress(layout, 0x2000));
    }

    /// <summary>
    /// **LoROM は読みと書きでバンクが違う。**
    ///
    /// 読みは $70 以降、書きは $F0 以降。同じ SRAM の別の見え方で、
    /// 参照実装はこの 2 つを使い分けている。
    /// 書きにも $70 を使うと、書き込みが効かない可能性がある。
    /// </summary>
    [Fact]
    public void SramLayout_LoRom_読みと書きでバンクが違う()
    {
        var layout = SnesAddressMap.SramLayout(SnesMapper.LoRom)!.Value;

        Assert.Equal(0x700000u, SnesAddressMap.SramBusAddress(layout, 0));
        Assert.Equal(0xF00000u, SnesAddressMap.SramBusAddress(layout, 0, forWrite: true));
    }

    /// <summary>
    /// 拡張 opcode を使うのは HiROM と ExHiROM だけ。
    /// SPC7110 は窓が HiROM と同じでも通常の opcode を使う。
    /// </summary>
    [Theory]
    [InlineData(SnesMapper.HiRom, true)]
    [InlineData(SnesMapper.ExHiRom, true)]
    [InlineData(SnesMapper.Spc7110, false)]
    [InlineData(SnesMapper.LoRom, false)]
    [InlineData(SnesMapper.Sa1, false)]
    public void SramLayout_使うopcodeがマッパーごとに決まる(SnesMapper mapper, bool useEx)
    {
        var layout = SnesAddressMap.SramLayout(mapper)!.Value;

        Assert.Equal(useEx ? RfcaOpcode.SnesExRead : RfcaOpcode.SnesRead, layout.ReadOpcode);
        Assert.Equal(useEx ? RfcaOpcode.SnesExWrite : RfcaOpcode.SnesWrite, layout.WriteOpcode);
    }

    // ==================================================================
    // アドレス変換の単体検証
    // ==================================================================

    [Theory]
    [InlineData(0x000000, 0x008000)]
    [InlineData(0x007FFF, 0x00FFFF)]
    [InlineData(0x008000, 0x018000)]
    [InlineData(0x3F0000, 0xFE8000)]  // バンク $7E ではなくミラーの $FE
    [InlineData(0x3F8000, 0xFF8000)]
    public void LoRomAddressMap_MapsToExpectedBusAddress(long offset, uint expected)
        => Assert.Equal(expected, SnesAddressMap.LoRom(offset));

    [Theory]
    [InlineData(0x000000, 0xC00000)]
    [InlineData(0x00FFC0, 0xC0FFC0)]
    [InlineData(0x3FFFFF, 0xFFFFFF)]
    public void HiRomAddressMap_MapsToBankC0Onward(long offset, uint expected)
        => Assert.Equal(expected, SnesAddressMap.HiRom(offset));

    [Theory]
    [InlineData(0x000000, 0xC00000)]
    [InlineData(0x3FFFFF, 0xFFFFFF)]
    [InlineData(0x400000, 0x400000)]
    [InlineData(0x40FFC0, 0x40FFC0)]
    public void ExHiRomAddressMap_SplitsHalvesAcrossA23(long offset, uint expected)
        => Assert.Equal(expected, SnesAddressMap.ExHiRom(offset));

    // ==================================================================
    // ヘッダ解析
    // ==================================================================

    [Fact]
    public void Header_KnownSizeOverride_AppliesToSdd1()
    {
        var rom = SnesRomBuilder.Build(SnesMapper.Sdd1, 4 * Mb, fixChecksum: false);
        var cart = new FakeSnesCartridge(rom, SnesMapper.Sdd1);
        var options = Options(o => o.VerifyChecksum = false);

        var info = new SnesDumper().Identify(cart, options);

        // カートリッジ種別 0x43 は S-DD1。容量はヘッダ欄ではなく 32Mbit 固定。
        Assert.Equal(4 * Mb, info.RomSize);
        Assert.Contains(info.Warnings, w => w.Contains("S-DD1"));
    }

    [Fact]
    public void Header_DetectsSa1Coprocessor()
    {
        var rom = SnesRomBuilder.Build(SnesMapper.Sa1, 4 * Mb);
        var (info, _) = DumpRoundTrip(rom, SnesMapper.Sa1);

        Assert.Equal("SA-1", info.Details["コプロセッサ"]);
        Assert.Equal("あり", info.Details["バッテリバックアップ"]);
    }

    // ==================================================================
    // チェックサム
    // ==================================================================

    [Fact]
    public void Dump_VerifiesChecksumForPowerOfTwoRom()
    {
        var rom = SnesRomBuilder.Build(SnesMapper.HiRom, 2 * Mb);
        var (_, result) = DumpRoundTrip(rom, SnesMapper.HiRom, Options(o => o.VerifyChecksum = true));

        Assert.True(result.ChecksumOk, result.ChecksumDetail);
    }

    private static string ExpectedMapperText(SnesMapper mapper) => mapper switch
    {
        SnesMapper.LoRom => "LoROM",
        SnesMapper.HiRom => "HiROM",
        SnesMapper.ExHiRom => "ExHiROM",
        SnesMapper.Sa1 => "SA-1",
        SnesMapper.Sdd1 => "S-DD1",
        _ => mapper.ToString(),
    };
}
