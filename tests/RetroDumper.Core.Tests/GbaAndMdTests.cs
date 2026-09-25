using System.Buffers.Binary;
using System.Text;
using RetroDumper.Core.Dumping;
using RetroDumper.Core.Gba;
using RetroDumper.Core.Md;
using RetroDumper.Core.Transport;
using Xunit;

namespace RetroDumper.Core.Tests;

/// <summary>
/// RfcaOpcode.GbaRead は静的な可変状態なので、これを書き換えるテストクラスは
/// 直列に動かす必要がある。xunit は既定でクラス単位に並列実行するため、
/// 同じコレクションに入れて競合を防ぐ。
/// </summary>
[CollectionDefinition("GbaOpcodeState", DisableParallelization = true)]
public sealed class GbaOpcodeStateCollection { }

[Collection("GbaOpcodeState")]
public class GbaDumperTests
{
    private const uint ProbedOpcode = 0x33;

    private static DumpOptions Options(Action<DumpOptions>? configure = null)
    {
        var options = new DumpOptions { ChunkSize = 1024, IncludeSaveRam = false };
        configure?.Invoke(options);
        return options;
    }

    /// <summary>
    /// GBA ROM を組み立てる。ロゴはチェックサムが 0x4B1B になるよう
    /// 調整した合成データ（本物のロゴ画像そのものではない）。
    /// </summary>
    private static byte[] BuildGbaRom(long size, string title = "TESTGAME", string? saveMarker = null)
    {
        var rom = new byte[size];
        for (long i = 0; i < size; i++)
            rom[i] = (byte)((i ^ (i >> 9) ^ 0xA5) & 0xFF);

        // エントリポイント (ARM の B 命令)
        rom[0x03] = 0xEA;

        // 任天堂ロゴ相当。156 バイトの総和が 0x4B1B になるよう埋める。
        Array.Clear(rom, 0x04, 0x9C);
        int target = GbaDumper.NintendoLogoChecksum;
        int i2 = 0x04;
        while (target > 0 && i2 < 0xA0)
        {
            int put = Math.Min(255, target);
            rom[i2++] = (byte)put;
            target -= put;
        }

        Encoding.ASCII.GetBytes(title.PadRight(12, '\0')).CopyTo(rom, 0xA0);
        Encoding.ASCII.GetBytes("BTSJ").CopyTo(rom, 0xAC);
        Encoding.ASCII.GetBytes("01").CopyTo(rom, 0xB0);
        rom[0xB2] = 0x96;
        rom[0xBC] = 0x00;

        // ヘッダチェックサム
        int sum = 0;
        for (int i = 0xA0; i <= 0xBC; i++) sum -= rom[i];
        rom[0xBD] = (byte)((sum - 0x19) & 0xFF);

        if (saveMarker is not null)
            Encoding.ASCII.GetBytes(saveMarker).CopyTo(rom, size / 2);

        return rom;
    }

    [Fact]
    public void Identify_RequiresProbedOpcode()
    {
        RfcaOpcode.GbaRead = null;
        var dumper = new GbaDumper();

        Assert.False(dumper.IsReady);
        Assert.Contains("opcode", dumper.ReadinessDetail);
    }

    [Theory]
    [InlineData(4 * 1024 * 1024)]
    [InlineData(8 * 1024 * 1024)]
    [InlineData(16 * 1024 * 1024)]
    public void Identify_DetectsRomSizeByMirroring(long size)
    {
        RfcaOpcode.GbaRead = ProbedOpcode;
        var rom = BuildGbaRom(size);
        var cart = new FakeLinearCartridge(
            rom, ProbedOpcode, GbaDumper.DefaultRomBase,
            FakeLinearCartridge.BeyondEnd.Mirror, CartridgeKind.GameBoyAdvance);

        var info = new GbaDumper().Identify(cart, Options());

        Assert.Equal(size, info.RomSize);
        Assert.Equal("一致", info.Details["任天堂ロゴ"]);
        Assert.Equal("TESTGAME", info.Title);
    }

    [Fact]
    public void Identify_DetectsRomSizeByOpenBus()
    {
        RfcaOpcode.GbaRead = ProbedOpcode;
        const long size = 8 * 1024 * 1024;
        var rom = BuildGbaRom(size);
        var cart = new FakeLinearCartridge(
            rom, ProbedOpcode, GbaDumper.DefaultRomBase,
            FakeLinearCartridge.BeyondEnd.OpenBus, CartridgeKind.GameBoyAdvance);

        var info = new GbaDumper().Identify(cart, Options());

        Assert.Equal(size, info.RomSize);
    }

    [Fact]
    public void Dump_RoundTripsExactly()
    {
        RfcaOpcode.GbaRead = ProbedOpcode;
        const long size = 4 * 1024 * 1024;
        var rom = BuildGbaRom(size);
        var cart = new FakeLinearCartridge(
            rom, ProbedOpcode, GbaDumper.DefaultRomBase,
            FakeLinearCartridge.BeyondEnd.Mirror, CartridgeKind.GameBoyAdvance);

        var dumper = new GbaDumper();
        var options = Options();
        var info = dumper.Identify(cart, options);
        var result = dumper.Dump(cart, info, options, null, CancellationToken.None);

        Assert.Equal(rom, result.Rom);
    }

    [Theory]
    [InlineData("EEPROM_V124", "EEPROM (容量は要判定)")]
    [InlineData("SRAM_V113", "SRAM 256kbit (32KB)")]
    [InlineData("SRAM_F_V102", "FRAM 256kbit (32KB)")]
    [InlineData("FLASH_V126", "FLASH 512kbit (64KB)")]
    [InlineData("FLASH512_V130", "FLASH 512kbit (64KB)")]
    [InlineData("FLASH1M_V103", "FLASH 1Mbit (128KB)")]
    public void DetectSaveType_ReadsLibraryMarker(string marker, string expected)
    {
        var rom = BuildGbaRom(1024 * 1024, saveMarker: marker);
        Assert.Equal(expected, GbaDumper.DetectSaveType(rom));
    }

    [Fact]
    public void DetectSaveType_ReportsUnknownWhenNoMarker()
        => Assert.Equal("不明 (セーブなし?)", GbaDumper.DetectSaveType(BuildGbaRom(1024 * 1024)));

    /// <summary>
    /// opcode やアドレス起点が違うと、読めたように見えても中身は別物。
    /// ロゴのチェックサムでそれを検出できること。
    /// </summary>
    [Fact]
    public void Identify_WarnsWhenLogoChecksumMismatches()
    {
        RfcaOpcode.GbaRead = ProbedOpcode;
        var rom = BuildGbaRom(4 * 1024 * 1024);
        rom[0x10] ^= 0xFF;  // ロゴを 1 バイト壊す

        var cart = new FakeLinearCartridge(
            rom, ProbedOpcode, GbaDumper.DefaultRomBase,
            FakeLinearCartridge.BeyondEnd.Mirror, CartridgeKind.GameBoyAdvance);

        var info = new GbaDumper().Identify(cart, Options());

        Assert.Contains(info.Warnings, w => w.Contains("任天堂ロゴ"));
    }
}

public class MdDumperTests
{
    private static DumpOptions Options(Action<DumpOptions>? configure = null)
    {
        var options = new DumpOptions { ChunkSize = 1024, IncludeSaveRam = false };
        configure?.Invoke(options);
        return options;
    }

    /// <summary>
    /// メガドライブ ROM を組み立てる。
    /// <paramref name="declaredEnd"/> にヘッダが申告する終端アドレスを指定できる
    /// （実容量と食い違うカセットを再現するため）。
    /// </summary>
    private static byte[] BuildMdRom(long size, long? declaredEnd = null, string name = "TEST GAME")
    {
        var rom = new byte[size];
        for (long i = 0; i < size; i++)
            rom[i] = (byte)((i ^ (i >> 11) ^ 0x3C) & 0xFF);

        void Ascii(int offset, string text, int length)
        {
            for (int i = 0; i < length; i++)
                rom[offset + i] = (byte)(i < text.Length ? text[i] : ' ');
        }

        Ascii(0x100, "SEGA MEGA DRIVE ", 16);
        Ascii(0x110, "(C)TEST 2026.SEP", 16);
        Ascii(0x120, name, 48);
        Ascii(0x150, name, 48);
        Ascii(0x180, "GM 00000000-00", 14);

        BinaryPrimitives.WriteUInt32BigEndian(rom.AsSpan(0x1A0), 0);
        BinaryPrimitives.WriteUInt32BigEndian(
            rom.AsSpan(0x1A4), (uint)((declaredEnd ?? size) - 1));

        // ヘッダチェックサム (0x200 以降を 16bit BE で加算)
        BinaryPrimitives.WriteUInt16BigEndian(rom.AsSpan(0x18E), MdDumper.ComputeChecksum(rom));

        return rom;
    }

    [Fact]
    public void Dump_RoundTripsExactly()
    {
        const long size = 2 * 1024 * 1024;
        var rom = BuildMdRom(size);
        var cart = new FakeLinearCartridge(rom, RfcaOpcode.MegaDriveRead);

        var dumper = new MdDumper();
        var options = Options();
        var info = dumper.Identify(cart, options);
        var result = dumper.Dump(cart, info, options, null, CancellationToken.None);

        Assert.Equal(size, info.RomSize);
        Assert.Equal(rom, result.Rom);
        Assert.True(result.ChecksumOk, result.ChecksumDetail);
    }

    /// <summary>
    /// ウルトラコアのように、ヘッダが実容量より小さい終端を申告するカセット。
    /// ミラー実測のほうを採って全体を吸い出せること。
    /// </summary>
    [Fact]
    public void Identify_PrefersMeasuredSizeWhenHeaderUnderreports()
    {
        const long actual = 2 * 1024 * 1024;
        const long declared = 1 * 1024 * 1024;

        var rom = BuildMdRom(actual, declaredEnd: declared);
        var cart = new FakeLinearCartridge(rom, RfcaOpcode.MegaDriveRead);

        var info = new MdDumper().Identify(cart, Options());

        Assert.Equal(actual, info.RomSize);
        Assert.Contains(info.Warnings, w => w.Contains("ヘッダが誤っている"));
    }

    [Fact]
    public void Identify_ReadsTitleAndChecksum()
    {
        var rom = BuildMdRom(1024 * 1024, name: "ULTRACORE");
        var cart = new FakeLinearCartridge(rom, RfcaOpcode.MegaDriveRead);

        var info = new MdDumper().Identify(cart, Options());

        Assert.Equal("ULTRACORE", info.Title);
        Assert.Contains("SEGA", info.Details["システム名"]);
    }
}

/// <summary>
/// GBA のフレーム仕様を固定する。実機で確認した値から外れたら落ちる。
///
/// ・リード opcode        : 0x20
/// ・フレーム 2 つ目の値  : 0x00（他機種の 0x08 では拒否される）
/// ・転送サイズ           : 512 固定（2 / 16 / 1024 はいずれも拒否された）
/// ・ROM 先頭アドレス     : 0（0x08000000 は CPU 側のメモリマップで、バス上には無い）
/// </summary>
[Collection("GbaOpcodeState")]
public class GbaFrameContractTests
{
    private static byte[] BuildRom(long size)
    {
        var rom = new byte[size];
        for (long i = 0; i < size; i++) rom[i] = (byte)((i ^ 0x5A) & 0xFF);

        rom[0x03] = 0xEA;
        Array.Clear(rom, 0x04, 0x9C);
        int target = GbaDumper.NintendoLogoChecksum;
        int p = 0x04;
        while (target > 0 && p < 0xA0)
        {
            int put = Math.Min(255, target);
            rom[p++] = (byte)put;
            target -= put;
        }
        rom[0xB2] = 0x96;
        return rom;
    }

    /// <summary>実機と同じ制約（ヘッダ値 8・32KB ブロック）を課したアダプタ。</summary>
    private static FakeLinearCartridge StrictCart(byte[] rom) =>
        new(rom, RfcaOpcode.GbaRomRead, GbaDumper.DefaultRomBase,
            FakeLinearCartridge.BeyondEnd.Mirror, CartridgeKind.GameBoyAdvance)
        {
            RequiredHeaderField = RfcaOpcode.RequestHeaderField,
            RequiredSize = RfcaOpcode.GbaBlockSize,
        };

    /// <summary>
    /// opcode 表は参照実装の逆コンパイルで確定したもの。
    ///
    /// 以前ここには 0x20 / ヘッダ値 0x00 / 512 バイトと書いてあったが、
    /// **3 つとも間違い**だった。0x20 は GBA の SRAM リードで、
    /// セーブが空なら全バイト 0xFF を返す。ROM リードは 0x1F。
    /// ヘッダ値は全スロット共通で 8、ブロックは 32KB。
    /// 誤った値を「実機で確認済み」としてテストに固定したせいで、
    /// 間違いが長く温存された。
    /// </summary>
    [Fact]
    public void ConfirmedConstants_MatchHardware()
    {
        Assert.Equal(0x1Fu, RfcaOpcode.GbaRomRead);
        Assert.Equal(0x20u, RfcaOpcode.GbaSramRead);
        Assert.Equal(0x08u, RfcaOpcode.RequestHeaderField);
        Assert.Equal(32768, RfcaOpcode.GbaBlockSize);
        Assert.Equal(0x00000000u, GbaDumper.DefaultRomBase);
        Assert.Equal(CartridgeKind.GameBoyAdvance, (CartridgeKind)0x06);
    }

    /// <summary>ROM リードとセーブ系は別の opcode であること。取り違えた実績がある。</summary>
    [Fact]
    public void RomReadIsDistinctFromSaveReads()
    {
        Assert.NotEqual(RfcaOpcode.GbaRomRead, RfcaOpcode.GbaSramRead);
        Assert.NotEqual(RfcaOpcode.GbaRomRead, RfcaOpcode.GbaEepromRead);
        Assert.NotEqual(RfcaOpcode.GbaRomRead, RfcaOpcode.GbaFlashRead);
    }

    /// <summary>
    /// 利用者が転送サイズに 1024 を指定しても、GBA では 512 に矯正されること。
    /// 矯正しないと実機が拒否する。
    /// </summary>
    [Fact]
    public void Dump_ForcesFiveTwelveByteBlocksAndZeroHeaderField()
    {
        RfcaOpcode.GbaRead = RfcaOpcode.GbaRomRead;

        const long size = 1024 * 1024;
        var rom = BuildRom(size);
        var cart = StrictCart(rom);

        var dumper = new GbaDumper();
        var options = new DumpOptions
        {
            ChunkSize = 1024,          // わざと実機が受け付けない値を指定する
            IncludeSaveRam = false,
            VerifyChecksum = false,
            RomSizeOverride = size,
        };

        var info = dumper.Identify(cart, options);
        var result = dumper.Dump(cart, info, options, null, CancellationToken.None);

        Assert.Equal(rom, result.Rom);
        Assert.NotEmpty(cart.Reads);
        Assert.All(cart.Reads, r =>
        {
            Assert.Equal(RfcaOpcode.RequestHeaderField, r.HeaderField);
            Assert.Equal(RfcaOpcode.GbaBlockSize, r.Size);
        });
    }

    /// <summary>
    /// 誤った opcode（SRAM リード 0x20）を ROM リードのつもりで使うと弾かれること。
    ///
    /// 実機ではこれが「受理されるのに全バイト 0xFF」という形で現れ、
    /// バスが死んでいるようにしか見えなかった。
    /// </summary>
    [Fact]
    public void StrictCart_RejectsSramOpcodeAsRomRead()
    {
        var cart = StrictCart(BuildRom(1024 * 1024));

        Assert.Throws<RfcaNakException>(
            () => cart.Read(RfcaOpcode.GbaSramRead, 0,
                            RfcaOpcode.GbaBlockSize, RfcaOpcode.RequestHeaderField));
    }
}

/// <summary>
/// ウェイクアップ直後の読み出しが化ける実機の挙動への耐性。
///
/// 実機で、スロットを起こした直後の最初の読み出しだけデータが壊れる現象を
/// 確認した（SFC ではヘッダ先頭 1 バイト、GBA ではヘッダ全体）。
/// 任天堂ロゴは固定バイト列なので、化けたことをその場で判定して読み直せる。
/// </summary>
[Collection("GbaOpcodeState")]
public class GbaFirstReadCorruptionTests
{
    private static byte[] BuildRom(long size)
    {
        var rom = new byte[size];
        for (long i = 0; i < size; i++) rom[i] = (byte)((i ^ 0x37) & 0xFF);

        rom[0x03] = 0xEA;
        Array.Clear(rom, 0x04, 0x9C);
        int target = GbaDumper.NintendoLogoChecksum;
        int p = 0x04;
        while (target > 0 && p < 0xA0)
        {
            int put = Math.Min(255, target);
            rom[p++] = (byte)put;
            target -= put;
        }

        Encoding.ASCII.GetBytes("REALTITLE\0\0\0").CopyTo(rom, 0xA0);
        Encoding.ASCII.GetBytes("AXVJ").CopyTo(rom, 0xAC);
        rom[0xB2] = 0x96;
        return rom;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Identify_RecoversWhenFirstReadsAreCorrupted(int corruptCount)
    {
        RfcaOpcode.GbaRead = RfcaOpcode.GbaRomRead;

        const long size = 4 * 1024 * 1024;
        var rom = BuildRom(size);
        var cart = new FakeLinearCartridge(
            rom, RfcaOpcode.GbaRomRead, GbaDumper.DefaultRomBase,
            FakeLinearCartridge.BeyondEnd.Mirror, CartridgeKind.GameBoyAdvance)
        {
            CorruptFirstReads = corruptCount,
        };

        var info = new GbaDumper().Identify(
            cart, new DumpOptions { IncludeSaveRam = false, RomSizeOverride = size });

        Assert.Equal("一致", info.Details["任天堂ロゴ"]);
        Assert.Equal("REALTITLE", info.Title);
        Assert.Equal("AXVJ", info.Details["ゲームコード"]);
        Assert.DoesNotContain(info.Warnings, w => w.Contains("任天堂ロゴ"));
    }

    /// <summary>
    /// 読み直しても直らない場合は、黙って壊れたデータを返さず警告を出すこと。
    /// </summary>
    [Fact]
    public void Identify_WarnsWhenCorruptionPersists()
    {
        RfcaOpcode.GbaRead = RfcaOpcode.GbaRomRead;

        const long size = 4 * 1024 * 1024;
        var cart = new FakeLinearCartridge(
            BuildRom(size), RfcaOpcode.GbaRomRead, GbaDumper.DefaultRomBase,
            FakeLinearCartridge.BeyondEnd.Mirror, CartridgeKind.GameBoyAdvance)
        {
            CorruptFirstReads = 100,
        };

        var info = new GbaDumper().Identify(
            cart, new DumpOptions { IncludeSaveRam = false, RomSizeOverride = size });

        Assert.Contains(info.Warnings, w => w.Contains("任天堂ロゴ"));
    }
}

/// <summary>
/// GBA の容量判定。
///
/// 実機（Crash Bandicoot Advance / ACUJ）で確認した事実:
///   ・実体 8MB。8MB〜16MB は 100% 0xFF。16MB 以降はオープンバスの繰り返し。
///   ・ミラー（先頭への折り返し）は起きない。
///   ・ワードアドレスの残留値も読めない。
///
/// 以前の実装はミラーとワードアドレス残留値だけを見ていたため、
/// **どちらにも当たらず常に最大の 32MB を返していた**。
/// 実際に 32MB のファイルが出力され、後半 24MB が無意味なデータだった。
/// </summary>
[Collection("GbaOpcodeState")]
public class GbaSizeDetectionTests
{
    private static byte[] BuildRom(long size)
    {
        var rom = new byte[size];
        for (long i = 0; i < size; i++) rom[i] = (byte)((i * 31 + 7) & 0xFF);

        rom[0x03] = 0xEA;
        Array.Clear(rom, 0x04, 0x9C);

        int target = GbaDumper.NintendoLogoChecksum;
        int p = 0x04;
        while (target > 0 && p < 0xA0)
        {
            int put = Math.Min(255, target);
            rom[p++] = (byte)put;
            target -= put;
        }

        rom[0xB2] = 0x96;
        return rom;
    }

    private static FakeLinearCartridge Cart(byte[] rom, FakeLinearCartridge.BeyondEnd beyond) =>
        new(rom, RfcaOpcode.GbaRomRead, GbaDumper.DefaultRomBase, beyond,
            CartridgeKind.GameBoyAdvance);

    private static readonly DumpOptions Options = new()
    {
        IncludeSaveRam = false,
        VerifyChecksum = false,
    };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void 終端より先が0xFFなら実体の容量を返す(int megabytes)
    {
        RfcaOpcode.GbaRead = RfcaOpcode.GbaRomRead;

        long size = megabytes * 1024L * 1024L;
        var cart = Cart(BuildRom(size), FakeLinearCartridge.BeyondEnd.Blank);

        var info = new GbaDumper().Identify(cart, Options);

        Assert.Equal(size, info.RomSize);
    }

    /// <summary>ミラーする機材でも従来どおり判定できること。</summary>
    [Fact]
    public void ミラーする場合も実体の容量を返す()
    {
        RfcaOpcode.GbaRead = RfcaOpcode.GbaRomRead;

        long size = 4 * 1024 * 1024;
        var cart = Cart(BuildRom(size), FakeLinearCartridge.BeyondEnd.Mirror);

        Assert.Equal(size, new GbaDumper().Identify(cart, Options).RomSize);
    }

    /// <summary>
    /// 32MB ちょうどのカセットは、終端の先が存在しないので最大容量のままでよい。
    /// </summary>
    [Fact]
    public void 上限いっぱいのカセットは32MBを返す()
    {
        RfcaOpcode.GbaRead = RfcaOpcode.GbaRomRead;

        long size = 32L * 1024 * 1024;
        var cart = Cart(BuildRom(size), FakeLinearCartridge.BeyondEnd.Mirror);

        Assert.Equal(size, new GbaDumper().Identify(cart, Options).RomSize);
    }

    /// <summary>手動指定は自動判定より優先されること。</summary>
    [Fact]
    public void 手動指定は自動判定より優先される()
    {
        RfcaOpcode.GbaRead = RfcaOpcode.GbaRomRead;

        var cart = Cart(BuildRom(8 * 1024 * 1024), FakeLinearCartridge.BeyondEnd.Blank);

        var info = new GbaDumper().Identify(cart, new DumpOptions
        {
            IncludeSaveRam = false,
            VerifyChecksum = false,
            RomSizeOverride = 4 * 1024 * 1024,
        });

        Assert.Equal(4 * 1024 * 1024, info.RomSize);
    }
}
