namespace RetroDumper.Core.Transport;

/// <summary>
/// セーブデータの領域。**ここ以外への書き込みは、許可の有無に関わらず通さない。**
///
/// 「GBA には絶対に書き込まない」という条件を、利用者の判断で
/// 「セーブ領域だけは書いてよい」に緩めた。緩める範囲を取り違えると
/// カートリッジの中身を壊すので、どこがセーブ領域かをここ 1 か所で定義する。
///
/// 機種で事情が違う。
///
///   GBA は ROM とセーブで **opcode が別系統**になっている。
///   SRAM 0x20/0x21、EEPROM 0x24/0x25、フラッシュ 0x27/0x28 がセーブ専用で、
///   ROM 読みは 0x1F。**GBA の ROM へ書く opcode はそもそも存在しない。**
///   したがって opcode を限定するだけで、ROM を壊す経路が構造的に消える。
///
///   SFC・GB・ファミコンは ROM もセーブも同じ opcode で、番地だけが違う。
///   こちらは番地で判定するしかない。
///
/// 番地と opcode は参照実装の各 SaveDataController に合わせた。
/// </summary>
public static class SaveMemory
{
    /// <summary>
    /// その opcode がセーブ専用か。
    /// これが真なら、番地を問わず ROM には届かない。
    /// </summary>
    public static bool IsDedicatedSaveOpcode(CartridgeKind kind, uint opcode) => kind switch
    {
        CartridgeKind.GameBoyAdvance =>
            opcode is RfcaOpcode.GbaSramRead or RfcaOpcode.GbaSramWrite
                   or RfcaOpcode.GbaEepromRead or RfcaOpcode.GbaEepromWrite
                   or RfcaOpcode.GbaFlashRead or RfcaOpcode.GbaFlashWrite
                   or RfcaOpcode.GbaFlashId,

        CartridgeKind.MegaDrive =>
            opcode is RfcaOpcode.MegaDriveFramRead or RfcaOpcode.MegaDriveFramWrite
                   or RfcaOpcode.MegaDriveEepromRead or RfcaOpcode.MegaDriveEepromWrite,

        CartridgeKind.Famicom =>
            opcode is RfcaOpcode.NesEepromRead or RfcaOpcode.NesEepromWrite,

        CartridgeKind.GameBoy =>
            opcode is RfcaOpcode.GameBoyMbc2ExRamRead or RfcaOpcode.GameBoyMbc2ExRamWrite,

        _ => false,
    };

    /// <summary>
    /// この書き込みがセーブ領域宛てか。
    /// ここが偽なら、セーブ書き込みを許可していても通さない。
    /// </summary>
    public static bool IsSaveWrite(CartridgeKind kind, uint opcode, uint address)
    {
        if (IsDedicatedSaveOpcode(kind, opcode)) return true;

        return kind switch
        {
            // GB の外部 RAM。$A000-$BFFF 以外は ROM かマッパーなので通さない。
            CartridgeKind.GameBoy =>
                opcode == RfcaOpcode.GameBoyWrite && address is >= 0xA000 and <= 0xBFFF,

            // ファミコンのバッテリーバックアップ WRAM。
            CartridgeKind.Famicom =>
                opcode == RfcaOpcode.NesCpuWrite && address is >= 0x6000 and <= 0x7FFF,

            // SFC の SRAM。マッパーごとに窓も opcode も違う。
            // SnesAddressMap.SramLayout の書き込み窓と対応させること。
            // （対応は SnesSaveGateTests が確かめている）
            CartridgeKind.SuperFamicom =>
                (opcode == RfcaOpcode.SnesWrite && IsSnesSram(address))
                || (opcode == RfcaOpcode.SnesExWrite && IsSnesExSram(address)),

            // GBA は専用 opcode 以外を認めない。番地で許すことはしない。
            CartridgeKind.GameBoyAdvance => false,

            _ => false,
        };
    }

    /// <summary>
    /// 通常の opcode (0x08) で書ける SRAM の窓。
    ///
    /// バンクはマッパーごとに違う。
    ///   $F0-$FF  LoROM の書き込み窓
    ///   $70-$7D  S-DD1 / スーパー FX（$7E-$7F は本体 WRAM なので除く）
    ///   $40-$4F  SA-1 の BW-RAM
    ///   $68-$6F  ST010/ST011
    ///   $30-$3F の $6000-$7FFF  SPC7110
    /// </summary>
    public static bool IsSnesSram(uint address)
    {
        uint bank = address >> 16;
        uint offset = address & 0xFFFF;

        if (bank >= 0xF0) return true;
        if (bank is >= 0x70 and <= 0x7D) return true;
        if (bank is >= 0x40 and <= 0x4F) return true;
        if (bank is >= 0x68 and <= 0x6F) return true;

        return bank is >= 0x30 and <= 0x3F && offset is >= 0x6000 and <= 0x7FFF;
    }

    /// <summary>
    /// 拡張 opcode (0x0A) で書ける SRAM の窓。
    /// HiROM はバンク $30-$3F、ExHiROM は $B0-$BF の $6000-$7FFF。
    /// </summary>
    public static bool IsSnesExSram(uint address)
    {
        uint bank = address >> 16;
        uint offset = address & 0xFFFF;

        bool inBank = bank is >= 0x30 and <= 0x3F || bank is >= 0xB0 and <= 0xBF;

        return inBank && offset is >= 0x6000 and <= 0x7FFF;
    }
}
