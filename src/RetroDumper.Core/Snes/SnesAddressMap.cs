namespace RetroDumper.Core.Snes;

/// <summary>
/// ROM ファイル上のオフセットを、RFCA に渡すカートリッジバスアドレス
/// (bank &lt;&lt; 16 | offset、24bit) へ変換する。
///
/// RFCA のリード opcode 0x07 はバスアドレスをそのまま受け取るため、
/// 「どのバンクを読めばその ROM オフセットが出てくるか」を
/// マッパーごとに解決してやればよい。
/// </summary>
public static class SnesAddressMap
{
    public const long BankSize = 0x10000;
    public const long LoRomBankSize = 0x8000;

    /// <summary>1MB ブロック。SA-1 / S-DD1 の Super MMC の単位。</summary>
    public const long MmcBlockSize = 0x100000;

    /// <summary>マッパーごとに 1 リクエストで跨いではいけない境界。</summary>
    public static long ChunkAlignment(SnesMapper mapper) => mapper switch
    {
        // LoROM は 32KB ごとにバンクが変わる。
        SnesMapper.LoRom => LoRomBankSize,
        // C0-FF 直マップ系は 64KB ごと。
        SnesMapper.HiRom or SnesMapper.ExHiRom or SnesMapper.Spc7110 => BankSize,
        // MMC 系は 1MB ブロックを跨ぐとページングが変わる。
        SnesMapper.Sa1 or SnesMapper.Sdd1 => BankSize,
        _ => BankSize,
    };

    /// <summary>
    /// ROM オフセット → バスアドレス。
    /// SA-1 / S-DD1 については「現在 MMC で選択されている 4MB ウィンドウ内」の
    /// オフセットとして扱う（ウィンドウ切り替えは呼び出し側が行う）。
    /// </summary>
    public static uint ToBusAddress(SnesMapper mapper, long offset) => mapper switch
    {
        SnesMapper.LoRom => LoRom(offset),
        SnesMapper.HiRom => HiRom(offset),
        SnesMapper.ExHiRom => ExHiRom(offset),
        SnesMapper.Sa1 or SnesMapper.Sdd1 or SnesMapper.Spc7110 => MmcWindow(offset),
        _ => HiRom(offset),
    };

    /// <summary>
    /// LoROM: 32KB ごとにバンクが進み、各バンクの $8000-$FFFF に現れる。
    /// バンク $7E/$7F は WRAM なので、ミラーの $FE/$FF から読む。
    /// </summary>
    public static uint LoRom(long offset)
    {
        long bank = offset >> 15;
        long addr = 0x8000 | (offset & 0x7FFF);
        if (bank >= 0x7E) bank += 0x80;
        return (uint)((bank << 16) | addr);
    }

    /// <summary>
    /// HiROM: バンク $C0-$FF に 64KB 単位でそのまま並ぶ。
    /// $40-$7D 側にも同じものが出るが、$7E/$7F の WRAM 穴を避けられる
    /// $C0 側を使う。
    /// </summary>
    public static uint HiRom(long offset)
    {
        long bank = 0xC0 + (offset >> 16);
        long addr = offset & 0xFFFF;
        return (uint)((bank << 16) | addr);
    }

    /// <summary>
    /// ExHiROM: A23 が反転した HiROM。
    /// 前半 4MB がバンク $C0-$FF、後半がバンク $40-$7D に出る。
    /// </summary>
    public static uint ExHiRom(long offset)
    {
        if (offset < 0x400000)
            return HiRom(offset);

        long rest = offset - 0x400000;
        long bank = 0x40 + (rest >> 16);
        long addr = rest & 0xFFFF;
        return (uint)((bank << 16) | addr);
    }

    /// <summary>
    /// SA-1 / S-DD1 / SPC7110 の共通形。
    /// MMC が選んだ 1MB×4 = 4MB が、バンク $C0-$FF にそのまま並ぶ。
    /// オフセットは 4MB ウィンドウ内の相対位置として渡すこと。
    /// </summary>
    public static uint MmcWindow(long offsetInWindow)
    {
        long bank = 0xC0 + (offsetInWindow >> 16);
        long addr = offsetInWindow & 0xFFFF;
        return (uint)((bank << 16) | addr);
    }

    // ------------------------------------------------------------------
    // セーブ RAM
    // ------------------------------------------------------------------

    /// <summary>
    /// セーブ RAM のアクセス方法。マッパーによってバンクも
    /// 1 バンクあたりの有効バイト数も異なる。
    /// </summary>
    public readonly record struct SramWindow(uint FirstBank, uint OffsetInBank, int BytesPerBank);

    public static SramWindow? SramLayout(SnesMapper mapper) => mapper switch
    {
        // LoROM: バンク $70-$7D の $0000-$7FFF。
        SnesMapper.LoRom or SnesMapper.Sdd1 => new SramWindow(0x70, 0x0000, 0x8000),

        // HiROM / ExHiROM: バンク $20-$3F の $6000-$7FFF（8KB 窓）。
        SnesMapper.HiRom or SnesMapper.ExHiRom or SnesMapper.Spc7110
            => new SramWindow(0x20, 0x6000, 0x2000),

        // SA-1 の BW-RAM: バンク $40-$4F に 64KB 単位で連続して並ぶ。
        SnesMapper.Sa1 => new SramWindow(0x40, 0x0000, 0x10000),

        _ => null,
    };

    /// <summary>セーブ RAM のオフセット → バスアドレス。</summary>
    public static uint SramBusAddress(SramWindow window, long offset)
    {
        long bank = window.FirstBank + offset / window.BytesPerBank;
        long addr = window.OffsetInBank + offset % window.BytesPerBank;
        return (uint)((bank << 16) | addr);
    }
}
