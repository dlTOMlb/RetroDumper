using RetroDumper.Core.Dumping;
using RetroDumper.Core.Nes;
using RetroDumper.Core.Transport;
using Xunit;

namespace RetroDumper.Core.Tests;

/// <summary>
/// iNES ヘッダの組み立て。
/// エミュレータはこのヘッダだけを見てマッパーと容量を判断するので、
/// ここが違うと吸い出しが正しくてもソフトが動かない。
/// </summary>
public sealed class INesHeaderTests
{
    [Fact]
    public void 先頭はNESマジックで始まる()
    {
        var file = NesDumper.BuildINesFile(0, new byte[0x8000], new byte[0x2000]);

        Assert.Equal((byte)'N', file[0]);
        Assert.Equal((byte)'E', file[1]);
        Assert.Equal((byte)'S', file[2]);
        Assert.Equal(0x1A, file[3]);
    }

    [Fact]
    public void PRGは16KB単位CHRは8KB単位で記録される()
    {
        var file = NesDumper.BuildINesFile(0, new byte[0x8000], new byte[0x2000]);

        Assert.Equal(2, file[4]);   // 32KB / 16KB
        Assert.Equal(1, file[5]);   //  8KB /  8KB
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(66)]
    public void マッパー番号が上下ニブルに分かれて入る(int mapper)
    {
        var file = NesDumper.BuildINesFile(mapper, new byte[0x4000], []);

        int decoded = (file[6] >> 4) | (file[7] & 0xF0);
        Assert.Equal(mapper, decoded);
    }

    [Fact]
    public void ヘッダのあとにPRGCHRの順で並ぶ()
    {
        var prg = new byte[0x4000];
        var chr = new byte[0x2000];
        prg[0] = 0xAA; prg[^1] = 0xBB;
        chr[0] = 0xCC; chr[^1] = 0xDD;

        var file = NesDumper.BuildINesFile(0, prg, chr);

        Assert.Equal(16 + prg.Length + chr.Length, file.Length);
        Assert.Equal(0xAA, file[16]);
        Assert.Equal(0xBB, file[16 + prg.Length - 1]);
        Assert.Equal(0xCC, file[16 + prg.Length]);
        Assert.Equal(0xDD, file[^1]);
    }

    [Fact]
    public void CHRRAMのカセットはCHR0として記録される()
    {
        var file = NesDumper.BuildINesFile(2, new byte[0x20000], []);

        Assert.Equal(0, file[5]);
        Assert.Equal(16 + 0x20000, file.Length);
    }
}

/// <summary>
/// ファミコンのバンクレジスタ範囲。
///
/// $8000-$FFFF は PRG-ROM が見えている領域だが、ROM は読み出し専用なので
/// 書き込みはマッパーのラッチに入るだけで内容は変わらない。
/// 一方 $6000-$7FFF はバッテリーバックアップ WRAM で、
/// ここへ書くとセーブデータが壊れる。
/// </summary>
public sealed class NesMapperRegisterTests
{
    [Theory]
    [InlineData(0x8000)]
    [InlineData(0x8001)]
    [InlineData(0xA000)]
    [InlineData(0xC000)]
    [InlineData(0xE000)]
    [InlineData(0xFFFF)]
    public void マッパーのラッチ範囲は許可される(int address)
        => Assert.True(MapperRegister.IsBankRegister(CartridgeKind.Famicom, (uint)address));

    [Theory]
    [InlineData(0x6000)]
    [InlineData(0x7000)]
    [InlineData(0x7FFF)]
    public void セーブ用WRAMは拒否される(int address)
    {
        Assert.False(MapperRegister.IsBankRegister(CartridgeKind.Famicom, (uint)address));
        Assert.True(MapperRegister.IsSaveMemory(CartridgeKind.Famicom, (uint)address));
    }

    [Fact]
    public void 範囲より下は拒否される()
        => Assert.False(MapperRegister.IsBankRegister(CartridgeKind.Famicom, 0x5FFF));
}

/// <summary>対応マッパーの登録内容。</summary>
public sealed class NesMapperRegistryTests
{
    [Theory]
    [InlineData(0, "NROM")]
    [InlineData(1, "MMC1")]
    [InlineData(2, "UxROM")]
    [InlineData(3, "CNROM")]
    [InlineData(4, "MMC3")]
    public void 番号から引ける(int number, string name)
        => Assert.Equal(name, NesMapper.ForNumber(number)?.Name);

    [Fact]
    public void 未対応の番号はnullを返す()
        => Assert.Null(NesMapper.ForNumber(999));

    /// <summary>UxROM は CHR-RAM なので CHR-ROM を吸い出さない。</summary>
    [Fact]
    public void UxROMはCHRROMを持たない()
        => Assert.Equal(0, NesMapper.ForNumber(2)!.ChrBankSize);

    /// <summary>MMC3 は PRG 8KB / CHR 1KB 単位。</summary>
    [Fact]
    public void MMC3のバンク単位()
    {
        var mmc3 = NesMapper.ForNumber(4)!;
        Assert.Equal(0x2000, mmc3.PrgBankSize);
        Assert.Equal(0x0400, mmc3.ChrBankSize);
    }
}

/// <summary>
/// ファミコンの容量判定と吸い出し。
///
/// カセットは容量を申告しないので、バンクの折り返しから実測するしかない。
/// ROM に載っていないバンク番号は上位アドレス線が繋がっておらず、
/// 番号が丸められて先頭のバンクと同じ内容が見える。
/// </summary>
public sealed class NesDumpTests
{
    /// <summary>
    /// バンクごとに異なる内容の ROM。
    ///
    /// 単純な線形生成 (i * 17 + 3 など) は 256 バイト周期になるため、
    /// 16KB 境界でバイト列が完全に一致してしまい、
    /// 「折り返し」と「別のバンク」を区別できない。
    /// バンク番号を混ぜて、バンクごとに必ず違う内容にする。
    /// </summary>
    private static byte[] Rom(int kb, int salt)
    {
        var rom = new byte[kb * 1024];

        for (int i = 0; i < rom.Length; i++)
        {
            int bank = i >> 10;                       // 1KB ごとに変える
            rom[i] = (byte)((bank * 131 + i * 17 + salt) & 0xFF);
        }

        return rom;
    }

    private static byte[] Prg(int kb) => Rom(kb, 3);

    private static byte[] Chr(int kb) => Rom(kb, 11);

    private static readonly DumpOptions Auto = new()
    {
        NesMapperOverride = 0,
        IncludeSaveRam = false,
        VerifyChecksum = false,
    };

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void PRG容量を実測できる(int kb)
    {
        var cart = new FakeNesCartridge(Prg(kb), Chr(8)) { AllowWrites = false };
        var dumper = new NesDumper();

        var info = dumper.Identify(cart, Auto);
        var result = dumper.Dump(cart, info, Auto, null, CancellationToken.None);

        // iNES ヘッダの PRG 欄（16KB 単位）
        Assert.Equal(kb / 16, result.Rom[4]);
    }

    [Fact]
    public void CHR容量を実測できる()
    {
        var cart = new FakeNesCartridge(Prg(32), Chr(8)) { AllowWrites = false };
        var dumper = new NesDumper();

        var info = dumper.Identify(cart, Auto);
        var result = dumper.Dump(cart, info, Auto, null, CancellationToken.None);

        Assert.Equal(1, result.Rom[5]);   // 8KB / 8KB
    }

    [Fact]
    public void 吸い出した内容がROMと一致する()
    {
        var prg = Prg(32);
        var chr = Chr(8);
        var cart = new FakeNesCartridge(prg, chr) { AllowWrites = false };
        var dumper = new NesDumper();

        var info = dumper.Identify(cart, Auto);
        var result = dumper.Dump(cart, info, Auto, null, CancellationToken.None);

        Assert.Equal(prg, result.Rom.AsSpan(16, prg.Length).ToArray());
        Assert.Equal(chr, result.Rom.AsSpan(16 + prg.Length, chr.Length).ToArray());
    }

    /// <summary>NROM はバンク切り替えを必要としない。</summary>
    [Fact]
    public void NROMは書き込みを一切行わない()
    {
        var cart = new FakeNesCartridge(Prg(32), Chr(8)) { AllowWrites = false };
        var dumper = new NesDumper();

        var info = dumper.Identify(cart, Auto);
        dumper.Dump(cart, info, Auto, null, CancellationToken.None);

        Assert.Empty(cart.Writes);
        Assert.Empty(cart.BankRegisterWrites);
    }

    /// <summary>手動指定は実測より優先されること。</summary>
    [Fact]
    public void 手動指定は実測より優先される()
    {
        var cart = new FakeNesCartridge(Prg(32), Chr(8)) { AllowWrites = false };
        var dumper = new NesDumper();

        var options = new DumpOptions
        {
            NesMapperOverride = 0,
            NesPrgSize = 16 * 1024,
            NesChrSize = 8 * 1024,
            IncludeSaveRam = false,
            VerifyChecksum = false,
        };

        var info = dumper.Identify(cart, options);
        var result = dumper.Dump(cart, info, options, null, CancellationToken.None);

        Assert.Equal(1, result.Rom[4]);   // 16KB
    }

    /// <summary>識別は PRG 先頭・末尾 1KB の SHA-1 を出すこと。同定の鍵になる。</summary>
    [Fact]
    public void 識別で同定用のSHA1が得られる()
    {
        var cart = new FakeNesCartridge(Prg(32), Chr(8));
        var info = new NesDumper().Identify(cart, Auto);

        Assert.Equal(40, info.Details["PRG 先頭 1KB SHA-1"].Length);
        Assert.Equal(40, info.Details["PRG 末尾 1KB SHA-1"].Length);
    }

    /// <summary>マッパー未指定なら、その旨を警告すること。</summary>
    [Fact]
    public void マッパー未指定は警告される()
    {
        var cart = new FakeNesCartridge(Prg(32), Chr(8));

        var info = new NesDumper().Identify(cart, new DumpOptions
        {
            IncludeSaveRam = false,
            VerifyChecksum = false,
        });

        Assert.Contains(info.Warnings, w => w.Contains("マッパー"));
    }
}

/// <summary>
/// マッパーごとの容量範囲。
///
/// 値は sanni/cartreader の mapsize テーブル（Cart_Reader/NES.ino）に合わせてある。
/// 同ツールもマッパーは利用者が選ぶ方式で、容量はこの範囲から選ばせている。
/// 範囲を間違えると、実測の折り返し検出があり得ない値を返したときに
/// そのまま採用してしまう。
/// </summary>
public sealed class NesMapperSizeRangeTests
{
    [Theory]
    [InlineData(0, 16, 32)]      // NROM
    [InlineData(1, 32, 512)]     // MMC1
    [InlineData(2, 64, 256)]     // UxROM
    [InlineData(3, 16, 32)]      // CNROM
    [InlineData(4, 32, 512)]     // MMC3
    public void PRGの範囲がcartreaderの表と一致する(int mapper, int minKb, int maxKb)
    {
        var (min, max) = NesMapper.ForNumber(mapper)!.PrgSizeRange;

        Assert.Equal(minKb * 1024L, min);
        Assert.Equal(maxKb * 1024L, max);
    }

    [Theory]
    [InlineData(0, 0, 8)]        // NROM   CHR-RAM 構成もある
    [InlineData(1, 0, 128)]      // MMC1
    [InlineData(2, 0, 0)]        // UxROM  必ず CHR-RAM
    [InlineData(3, 0, 2048)]     // CNROM
    [InlineData(4, 0, 256)]      // MMC3
    public void CHRの範囲がcartreaderの表と一致する(int mapper, int minKb, int maxKb)
    {
        var (min, max) = NesMapper.ForNumber(mapper)!.ChrSizeRange;

        Assert.Equal(minKb * 1024L, min);
        Assert.Equal(maxKb * 1024L, max);
    }

    /// <summary>UxROM は CHR-ROM を持たないので、吸い出そうとしないこと。</summary>
    [Fact]
    public void UxROMはCHRを吸い出さない()
    {
        var m = NesMapper.ForNumber(2)!;

        Assert.Equal(0, m.ChrBankSize);
        Assert.Equal(0, m.MaxChrBanks);
        Assert.Equal(0L, m.ChrSizeRange.Max);
    }

    /// <summary>バンク数の上限は範囲から導かれること。</summary>
    [Theory]
    [InlineData(0, 2)]           // NROM  32KB / 16KB
    [InlineData(2, 16)]          // UxROM 256KB / 16KB
    [InlineData(4, 64)]          // MMC3  512KB / 8KB
    public void PRGバンク数の上限(int mapper, int expected)
        => Assert.Equal(expected, NesMapper.ForNumber(mapper)!.MaxPrgBanks);
}

/// <summary>
/// 読み出しが成立していないことの検出。
///
/// GBA で「アダプタは種別を返すのにバスが死んでいる」状態を
/// ソフトの問題と誤認し、長時間を費やした経緯がある。
/// ファミコンでも同じ見落としをしないよう、識別の時点で警告する。
/// </summary>
public sealed class NesDeadBusTests
{
    private static byte[] Flat(int size, byte value)
    {
        var rom = new byte[size];
        Array.Fill(rom, value);
        return rom;
    }

    private static readonly DumpOptions Options = new()
    {
        NesMapperOverride = 0,
        IncludeSaveRam = false,
        VerifyChecksum = false,
    };

    [Theory]
    [InlineData(0xFF)]
    [InlineData(0x00)]
    public void 全バイト同じ値なら警告する(byte value)
    {
        var cart = new FakeNesCartridge(Flat(32 * 1024, value), []);

        var info = new NesDumper().Identify(cart, Options);

        Assert.Contains(info.Warnings, w => w.Contains("バスを駆動していません"));
        Assert.Contains(info.Warnings, w => w.Contains("挿し直"));
    }

    [Fact]
    public void 実データが読めていれば警告しない()
    {
        var rom = new byte[32 * 1024];
        for (int i = 0; i < rom.Length; i++) rom[i] = (byte)((i >> 10) * 131 + i * 17);

        var cart = new FakeNesCartridge(rom, []);

        var info = new NesDumper().Identify(cart, Options);

        Assert.DoesNotContain(info.Warnings, w => w.Contains("バスを駆動していません"));
    }
}

/// <summary>
/// マッパーの総当たり特定。
///
/// ファミコンのカセットはマッパー番号を申告しないので、外から知る方法がない。
/// 対応マッパーを順に試し、吸い出した結果が No-Intro DAT と一致したものを
/// 正解とする。「当てて、答え合わせをする」しかない。
/// </summary>
public sealed class NesAutoDetectTests : IDisposable
{
    private readonly string _dir;

    public NesAutoDetectTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rdnesauto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static byte[] Rom(int kb, int salt)
    {
        var rom = new byte[kb * 1024];

        for (int i = 0; i < rom.Length; i++)
            rom[i] = (byte)(((i >> 10) * 131 + i * 17 + salt) & 0xFF);

        return rom;
    }

    private static readonly DumpOptions AutoMapper = new()
    {
        NesMapperOverride = null,     // 総当たりさせる
        IncludeSaveRam = false,
        VerifyChecksum = false,
    };

    /// <summary>
    /// 照合できなくても、吸い出せたデータは捨てないこと。
    ///
    /// 「吸い出しは成立したが DAT に一致しない」ときに例外で落として
    /// データを破棄していた。未収録のソフトや未対応マッパーでも、
    /// まず手元にファイルが残るほうがよい。
    /// </summary>
    [Fact]
    public void 照合できなくても吸い出したデータを返す()
    {
        var cart = new FakeNesCartridge(Rom(32, 3), Rom(8, 11));
        var dumper = new NesDumper();
        var info = dumper.Identify(cart, AutoMapper);

        var result = dumper.Dump(cart, info, AutoMapper, null, CancellationToken.None);

        Assert.NotEmpty(result.Rom);
        Assert.Equal(false, result.ChecksumOk);

        // 何を試したのかが分かること。黙って諦めない。
        Assert.Contains("NROM", result.ChecksumDetail);
        Assert.Contains("一致しませんでした", result.ChecksumDetail);
    }

    /// <summary>誤ったマッパーを試してもカセットへの書き込みは起きないこと。</summary>
    [Fact]
    public void 総当たり中もセーブ領域には書き込まない()
    {
        var cart = new FakeNesCartridge(Rom(32, 3), Rom(8, 11));
        var dumper = new NesDumper();
        var info = dumper.Identify(cart, AutoMapper);

        dumper.Dump(cart, info, AutoMapper, null, CancellationToken.None);

        // 内容を書き換えるライトは 1 件も起きない。
        Assert.Empty(cart.Writes);

        // マッパーのラッチへの書き込みは、すべて $8000-$FFFF に収まる。
        Assert.All(cart.BankRegisterWrites, w =>
            Assert.True(w.Address >= 0x8000, $"0x{w.Address:X4} は範囲外です"));
    }

    /// <summary>マッパーを指定すれば総当たりせず、その設定で吸い出すこと。</summary>
    [Fact]
    public void 指定があれば総当たりしない()
    {
        var prg = Rom(32, 3);
        var cart = new FakeNesCartridge(prg, Rom(8, 11));

        var options = new DumpOptions
        {
            NesMapperOverride = 0,
            IncludeSaveRam = false,
            VerifyChecksum = false,
        };

        var dumper = new NesDumper();
        var info = dumper.Identify(cart, options);
        var result = dumper.Dump(cart, info, options, null, CancellationToken.None);

        Assert.Equal(prg, result.Rom.AsSpan(16, prg.Length).ToArray());
    }

    /// <summary>識別の警告が、総当たりで特定する旨を伝えること。</summary>
    [Fact]
    public void 識別は総当たりで特定する旨を伝える()
    {
        var cart = new FakeNesCartridge(Rom(32, 3), Rom(8, 11));

        var info = new NesDumper().Identify(cart, AutoMapper);

        Assert.Contains(info.Warnings, w => w.Contains("順に試して"));
    }
}

/// <summary>
/// CHR-RAM の検出。
///
/// CHR-RAM のカセットには CHR-ROM が載っていない。PPU バスを読んでも
/// RAM の不定値か開放バスが見えるだけで、多くは全バイトが同じ値になる。
///
/// これを見ないと、存在しない CHR-ROM を 8KB 付けてしまい、
/// 吸い出し自体は成立しているのに No-Intro と一致しなくなる。
/// </summary>
public sealed class NesChrRamTests
{
    private static byte[] Prg(int kb)
    {
        var rom = new byte[kb * 1024];

        for (int i = 0; i < rom.Length; i++)
            rom[i] = (byte)(((i >> 10) * 131 + i * 17 + 3) & 0xFF);

        return rom;
    }

    private static readonly DumpOptions Options = new()
    {
        NesMapperOverride = 0,
        IncludeSaveRam = false,
        VerifyChecksum = false,
    };

    [Fact]
    public void CHRROMが無ければCHR0として記録される()
    {
        // CHR を載せていないカセット。PPU バスは開放バス (0xFF)。
        var cart = new FakeNesCartridge(Prg(32), []);
        var dumper = new NesDumper();

        var info = dumper.Identify(cart, Options);
        var result = dumper.Dump(cart, info, Options, null, CancellationToken.None);

        Assert.Equal(0, result.Rom[5]);                 // iNES の CHR 欄
        Assert.Equal(16 + 32 * 1024, result.Rom.Length); // ヘッダ + PRG のみ
    }

    [Fact]
    public void CHRROMがあれば従来どおり記録される()
    {
        var chr = new byte[8 * 1024];
        for (int i = 0; i < chr.Length; i++) chr[i] = (byte)((i * 29 + 11) & 0xFF);

        var cart = new FakeNesCartridge(Prg(32), chr);
        var dumper = new NesDumper();

        var info = dumper.Identify(cart, Options);
        var result = dumper.Dump(cart, info, Options, null, CancellationToken.None);

        Assert.Equal(1, result.Rom[5]);
        Assert.Equal(chr, result.Rom.AsSpan(16 + 32 * 1024).ToArray());
    }
}
