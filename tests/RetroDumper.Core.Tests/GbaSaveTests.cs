using RetroDumper.Core.Gba;
using RetroDumper.Core.Transport;
using Xunit;

namespace RetroDumper.Core.Tests;

/// <summary>
/// GBA のセーブ装置。
///
/// カートリッジは種類を申告しないので、ROM に残る目印から決める。
/// 目印には包含関係があり、見る順序を誤ると別の種類だと判定してしまう。
/// </summary>
public sealed class GbaSaveTypeTests
{
    private static byte[] RomWith(string signature)
    {
        var rom = new byte[4096];
        var mark = System.Text.Encoding.ASCII.GetBytes(signature);
        mark.CopyTo(rom, 1000);
        return rom;
    }

    [Theory]
    [InlineData("SRAM_V113", GbaSaveType.Sram)]
    [InlineData("EEPROM_V122", GbaSaveType.Eeprom64k)]
    [InlineData("FLASH512_V130", GbaSaveType.Flash512k)]
    [InlineData("FLASH_V126", GbaSaveType.Flash512k)]
    [InlineData("FLASH1M_V102", GbaSaveType.Flash1M)]
    public void 目印から種類を判定する(string signature, GbaSaveType expected)
        => Assert.Equal(expected, GbaSave.Detect(RomWith(signature)));

    /// <summary>
    /// "SRAM_F_V" は "SRAM_V" を含まないが、紛らわしいので順序を固定しておく。
    /// FRAM を SRAM と誤ると、読めはするが装置が違う扱いになる。
    /// </summary>
    [Fact]
    public void FRAMをSRAMと取り違えない()
        => Assert.Equal(GbaSaveType.Fram, GbaSave.Detect(RomWith("SRAM_F_V103")));

    /// <summary>
    /// "FLASH1M_V" は "FLASH_V" を含まない（間に 1M が入る）が、
    /// "FLASH512_V" より先に見ないと取り違える余地がある。
    /// 容量を誤ると、128KB の石を 64KB と思って半分しか読めない。
    /// </summary>
    [Fact]
    public void フラッシュ1Mを512Kと取り違えない()
        => Assert.Equal(GbaSaveType.Flash1M, GbaSave.Detect(RomWith("FLASH1M_V103")));

    [Fact]
    public void 目印が無ければなしと判定する()
        => Assert.Equal(GbaSaveType.None, GbaSave.Detect(new byte[4096]));

    [Theory]
    [InlineData(GbaSaveType.Sram, 32768)]
    [InlineData(GbaSaveType.Fram, 32768)]
    [InlineData(GbaSaveType.Eeprom4k, 512)]
    [InlineData(GbaSaveType.Eeprom64k, 8192)]
    [InlineData(GbaSaveType.Flash512k, 65536)]
    [InlineData(GbaSaveType.Flash1M, 131072)]
    [InlineData(GbaSaveType.None, 0)]
    public void 種類ごとの容量(GbaSaveType type, int expected)
        => Assert.Equal(expected, GbaSave.SizeOf(type));
}

/// <summary>
/// セーブ領域の判定。**ここが緩むとカートリッジを壊す。**
///
/// 「GBA には絶対に書き込まない」という条件を、利用者の判断で
/// 「セーブ領域だけは書いてよい」に緩めた。緩めた範囲が正しいことを
/// ここで固定しておく。
/// </summary>
public sealed class SaveMemoryTests
{
    [Theory]
    [InlineData(RfcaOpcode.GbaSramWrite)]
    [InlineData(RfcaOpcode.GbaEepromWrite)]
    [InlineData(RfcaOpcode.GbaFlashWrite)]
    public void GBAのセーブ専用opcodeは通る(uint opcode)
        => Assert.True(SaveMemory.IsSaveWrite(CartridgeKind.GameBoyAdvance, opcode, 0));

    /// <summary>
    /// GBA は ROM とセーブで opcode が別系統なので、
    /// セーブ専用以外は**番地に関わらず**通さない。
    /// </summary>
    [Theory]
    [InlineData(RfcaOpcode.GbaRomRead)]
    [InlineData(RfcaOpcode.SnesWrite)]
    [InlineData(RfcaOpcode.NesCpuWrite)]
    [InlineData(RfcaOpcode.GameBoyWrite)]
    public void GBAはセーブ専用opcode以外を通さない(uint opcode)
    {
        Assert.False(SaveMemory.IsSaveWrite(CartridgeKind.GameBoyAdvance, opcode, 0));
        Assert.False(SaveMemory.IsSaveWrite(CartridgeKind.GameBoyAdvance, opcode, 0xA000));
        Assert.False(SaveMemory.IsSaveWrite(CartridgeKind.GameBoyAdvance, opcode, 0x700000));
    }

    /// <summary>GB は ROM もセーブも同じ opcode なので、番地で分ける。</summary>
    [Theory]
    [InlineData(0xA000u, true)]
    [InlineData(0xBFFFu, true)]
    [InlineData(0x9FFFu, false)]
    [InlineData(0xC000u, false)]
    [InlineData(0x0000u, false)]
    public void GBは外部RAMの範囲だけ通る(uint address, bool expected)
        => Assert.Equal(expected,
            SaveMemory.IsSaveWrite(CartridgeKind.GameBoy, RfcaOpcode.GameBoyWrite, address));

    [Theory]
    [InlineData(0x6000u, true)]
    [InlineData(0x7FFFu, true)]
    [InlineData(0x8000u, false)]
    [InlineData(0x5FFFu, false)]
    public void ファミコンはWRAMの範囲だけ通る(uint address, bool expected)
        => Assert.Equal(expected,
            SaveMemory.IsSaveWrite(CartridgeKind.Famicom, RfcaOpcode.NesCpuWrite, address));

    /// <summary>
    /// SFC の SRAM はマッパーごとに窓が違う。
    /// LoROM の読みは $70 以降、S-DD1 / スーパー FX も同じ窓を使う。
    /// $7E-$7F は本体側の WRAM なので含めない。
    /// $68-$6F は ST010/ST011 の窓なので通る（ここを境界に使わないこと）。
    /// </summary>
    [Theory]
    [InlineData(0x700000u, true)]
    [InlineData(0x7DFFFFu, true)]
    [InlineData(0x7E0000u, false)]
    [InlineData(0x600000u, false)]
    public void SFCのLoROM_SRAM(uint address, bool expected)
        => Assert.Equal(expected,
            SaveMemory.IsSaveWrite(CartridgeKind.SuperFamicom, RfcaOpcode.SnesWrite, address));

    [Theory]
    [InlineData(0x306000u, true)]
    [InlineData(0x3F7FFFu, true)]
    [InlineData(0x305FFFu, false)]
    [InlineData(0x308000u, false)]
    [InlineData(0x406000u, false)]
    public void SFCのHiROM_SRAM(uint address, bool expected)
        => Assert.Equal(expected,
            SaveMemory.IsSaveWrite(CartridgeKind.SuperFamicom, RfcaOpcode.SnesExWrite, address));

    /// <summary>SFC の ROM 領域はセーブ扱いしない。</summary>
    [Fact]
    public void SFCのROM領域は通さない()
        => Assert.False(
            SaveMemory.IsSaveWrite(CartridgeKind.SuperFamicom, RfcaOpcode.SnesWrite, 0x008000));
}
