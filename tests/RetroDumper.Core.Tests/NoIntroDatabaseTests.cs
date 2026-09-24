using System.Security.Cryptography;
using RetroDumper.Core.Database;
using RetroDumper.Core.Util;
using Xunit;

namespace RetroDumper.Core.Tests;

/// <summary>
/// No-Intro DAT の読み込みと照合。
///
/// DAT 本体は同梱しない。利用者が用意したものを読む前提なので、
/// 「無くても落ちない」ことと「あれば正しく引ける」ことの両方を確かめる。
/// </summary>
public sealed class NoIntroDatabaseTests : IDisposable
{
    private readonly string _dir;

    public NoIntroDatabaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rdnointro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static byte[] SampleRom()
    {
        var rom = new byte[4096];
        for (int i = 0; i < rom.Length; i++) rom[i] = (byte)((i * 7 + 3) & 0xFF);
        return rom;
    }

    private string WriteDat(byte[] rom, string gameName = "Test Game (Japan)")
    {
        string crc = Checksums.Crc32(rom).ToString("X8");
        string md5 = Convert.ToHexString(MD5.HashData(rom));
        string sha1 = Convert.ToHexString(SHA1.HashData(rom));

        string path = Path.Combine(_dir, "test.dat");

        File.WriteAllText(path,
            $"""
             <?xml version="1.0"?>
             <datafile>
               <header><name>Test</name></header>
               <game name="{gameName}">
                 <description>{gameName}</description>
                 <rom name="{gameName}.gba" size="{rom.Length}" crc="{crc}" md5="{md5}" sha1="{sha1}"/>
               </game>
             </datafile>
             """);

        return path;
    }

    /// <summary>
    /// clrmamepro 形式の DAT も読めること。
    ///
    /// No-Intro の配布は XML だが、libretro-database が配っているものは
    /// 括弧の入れ子になった素のテキスト。どちらも crc / md5 / sha1 を持つので、
    /// 照合には同じように使える。入手先で形式が違うだけなので読む側で吸収する。
    /// </summary>
    [Fact]
    public void clrmamepro形式のDATも読める()
    {
        var rom = SampleRom();

        string crc = Checksums.Crc32(rom).ToString("X8");
        string md5 = Convert.ToHexString(MD5.HashData(rom));
        string sha1 = Convert.ToHexString(SHA1.HashData(rom));

        File.WriteAllText(Path.Combine(_dir, "libretro.dat"),
            $"""
             clrmamepro (
                 name "Super Nintendo"
             )
             game (
                 name "Super Mario World (Japan)"
                 region "Japan"
                 rom ( name "Super Mario World (Japan).sfc" size {rom.Length} crc {crc} md5 {md5} sha1 {sha1} )
             )
             """);

        var db = NoIntroDatabase.Load(_dir, null, includeEmbedded: false);

        Assert.Equal(1, db.EntryCount);

        var hit = db.Match(rom, rom.Length);

        Assert.NotNull(hit);
        Assert.Equal("Super Mario World (Japan)", hit!.GameName);
        Assert.Equal(rom.Length, hit.Size);
    }

    /// <summary>1 つの game に rom が複数並ぶことがある。すべて拾うこと。</summary>
    [Fact]
    public void clrmamepro形式で1つのgameに複数のromがあっても拾う()
    {
        var first = SampleRom();
        var second = new byte[2048];

        for (int i = 0; i < second.Length; i++) second[i] = (byte)(i * 13 + 5);

        string Rom(byte[] rom, string name) =>
            $"""rom ( name "{name}" size {rom.Length} crc {Checksums.Crc32(rom):X8} md5 {Convert.ToHexString(MD5.HashData(rom))} sha1 {Convert.ToHexString(SHA1.HashData(rom))} )""";

        File.WriteAllText(Path.Combine(_dir, "multi.dat"),
            $"""
             game (
                 name "Two Discs (Japan)"
                 {Rom(first, "a.sfc")}
                 {Rom(second, "b.sfc")}
             )
             """);

        var db = NoIntroDatabase.Load(_dir, null, includeEmbedded: false);

        Assert.Equal(2, db.EntryCount);
        Assert.Equal("Two Discs (Japan)", db.Match(second, second.Length)?.GameName);
    }

    /// <summary>
    /// 容量ごとの収録件数を引けること。
    ///
    /// 照合が外れたとき、原因が「DAT に未収録」なのか
    /// 「こちらの容量判定が違う」のかを分けるために使う。
    /// その容量のソフトが 1 本も無いなら、中身ではなく容量を疑えばよい。
    /// </summary>
    [Fact]
    public void 容量ごとの収録件数を引ける()
    {
        var rom = SampleRom();
        WriteDat(rom);

        var db = NoIntroDatabase.Load(_dir, null, includeEmbedded: false);

        Assert.Equal(1, db.CountWithSize(rom.Length));
        Assert.Equal(0, db.CountWithSize(rom.Length + 1));
    }

    [Fact]
    public void DATが無くても空で返り例外にならない()
    {
        var db = NoIntroDatabase.Load(Path.Combine(_dir, "存在しない"), includeEmbedded: false);

        Assert.True(db.IsEmpty);
        Assert.Equal(0, db.EntryCount);
        Assert.Null(db.Match(SampleRom(), 4096));
    }

    [Fact]
    public void 一致するROMを引ける()
    {
        var rom = SampleRom();
        WriteDat(rom);

        var db = NoIntroDatabase.Load(_dir, includeEmbedded: false);

        Assert.Equal(1, db.EntryCount);

        var match = db.Match(rom, rom.Length);

        Assert.NotNull(match);
        Assert.Equal("Test Game (Japan)", match.GameName);
        Assert.Equal(rom.Length, match.Size);
    }

    [Fact]
    public void 内容が違えば一致しない()
    {
        var rom = SampleRom();
        WriteDat(rom);

        var db = NoIntroDatabase.Load(_dir, includeEmbedded: false);

        var altered = (byte[])rom.Clone();
        altered[100] ^= 0xFF;

        Assert.Null(db.Match(altered, altered.Length));
    }

    /// <summary>
    /// 容量判定がずれて短く切れた場合、一致しないこと。
    /// 「一致した ＝ 容量も含めて正しい」と言えることが、この照合の価値。
    /// </summary>
    [Fact]
    public void 長さが違えば一致しない()
    {
        var rom = SampleRom();
        WriteDat(rom);

        var db = NoIntroDatabase.Load(_dir, includeEmbedded: false);

        Assert.Null(db.Match(rom, rom.Length / 2));
    }

    [Fact]
    public void CRC32だけでも引ける()
    {
        var rom = SampleRom();
        WriteDat(rom);

        var db = NoIntroDatabase.Load(_dir, includeEmbedded: false);

        Assert.Single(db.FindByCrc(Checksums.Crc32(rom)));
        Assert.Empty(db.FindByCrc(0xDEADBEEF));
    }

    [Fact]
    public void 壊れたDATがあっても他のDATは読める()
    {
        var rom = SampleRom();
        WriteDat(rom);
        File.WriteAllText(Path.Combine(_dir, "broken.dat"), "<datafile><game>");

        var log = new List<string>();
        var db = NoIntroDatabase.Load(_dir, log.Add, includeEmbedded: false);

        Assert.Equal(1, db.EntryCount);
        Assert.Contains(log, l => l.Contains("読めませんでした"));
    }

    [Fact]
    public void md5やsha1が無いDATでもCRC32で照合できる()
    {
        var rom = SampleRom();
        string crc = Checksums.Crc32(rom).ToString("X8");

        File.WriteAllText(Path.Combine(_dir, "minimal.dat"),
            $"""
             <?xml version="1.0"?>
             <datafile>
               <game name="Minimal">
                 <rom name="Minimal.gba" size="{rom.Length}" crc="{crc}"/>
               </game>
             </datafile>
             """);

        var db = NoIntroDatabase.Load(_dir, includeEmbedded: false);

        Assert.Equal("Minimal", db.Match(rom, rom.Length)?.GameName);
    }
}

/// <summary>
/// ファミコンの照合は iNES ヘッダを外して行う必要がある。
///
/// No-Intro には Headered と Headerless の 2 種類がある。
/// 当アプリが付ける iNES ヘッダはミラーリングの向きなどを推測で埋めており、
/// カセットからは読めない。そのため Headered の DAT とは
/// PRG/CHR が完全に正しくても一致しないことがある。
/// Headerless なら ROM 本体だけを比較するので、吸い出しの正否を判定できる。
/// </summary>
public sealed class NesHeaderlessMatchTests : IDisposable
{
    private readonly string _dir;

    public NesHeaderlessMatchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rdnes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static byte[] RomBody()
    {
        var body = new byte[0x8000];
        for (int i = 0; i < body.Length; i++) body[i] = (byte)((i * 13 + 5) & 0xFF);
        return body;
    }

    private void WriteHeaderlessDat(byte[] body)
    {
        string crc = RetroDumper.Core.Util.Checksums.Crc32(body).ToString("X8");
        string md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(body));
        string sha1 = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(body));

        File.WriteAllText(Path.Combine(_dir, "nes.dat"),
            $"""
             <?xml version="1.0"?>
             <datafile>
               <game name="Test NES Game (Japan)">
                 <rom name="Test NES Game (Japan).nes" size="{body.Length}" crc="{crc}" md5="{md5}" sha1="{sha1}"/>
               </game>
             </datafile>
             """);
    }

    [Fact]
    public void ヘッダを外せばHeaderlessのDATと一致する()
    {
        var body = RomBody();
        WriteHeaderlessDat(body);

        var file = RetroDumper.Core.Nes.NesDumper.BuildINesFile(0, body, []);
        var db = NoIntroDatabase.Load(_dir, includeEmbedded: false);

        // ファイル全体では一致しない（ヘッダ 16 バイトが余分）。
        Assert.Null(db.Match(file, file.Length));

        // ヘッダを外せば一致する。
        var match = db.Match(file.AsSpan(16), file.Length - 16);
        Assert.NotNull(match);
        Assert.Equal("Test NES Game (Japan)", match.GameName);
    }

    /// <summary>ヘッダの内容が違っても、ROM 本体が同じなら一致すること。</summary>
    [Fact]
    public void ヘッダのマッパー指定が違っても本体が同じなら一致する()
    {
        var body = RomBody();
        WriteHeaderlessDat(body);

        var db = NoIntroDatabase.Load(_dir, includeEmbedded: false);

        foreach (int mapper in new[] { 0, 1, 4, 66 })
        {
            var file = RetroDumper.Core.Nes.NesDumper.BuildINesFile(mapper, body, []);
            Assert.NotNull(db.Match(file.AsSpan(16), file.Length - 16));
        }
    }
}

/// <summary>
/// exe に埋め込んだ DAT。
///
/// 利用者が何も用意しなくても照合が効くように、GBA と NES(Headerless) の
/// DAT を gzip で埋め込んである。ビルド設定を壊すと黙って照合が無効になり、
/// 「一致なし」としか出なくなるため、埋め込みが生きていることを固定する。
/// </summary>
public sealed class EmbeddedDatabaseTests
{
    [Fact]
    public void 埋め込みDATが読み込まれる()
    {
        // 存在しないフォルダを指定して、埋め込み分だけを見る。
        var db = NoIntroDatabase.Load(
            Path.Combine(Path.GetTempPath(), "rd-nonexistent-" + Guid.NewGuid().ToString("N")));

        Assert.False(db.IsEmpty);
        Assert.True(db.EntryCount > 5000, $"件数が少なすぎます: {db.EntryCount}");
    }

    [Fact]
    public void GBAとNESの両方が入っている()
    {
        var db = NoIntroDatabase.Load(
            Path.Combine(Path.GetTempPath(), "rd-nonexistent-" + Guid.NewGuid().ToString("N")));

        Assert.Contains(db.LoadedFiles, f => f.Contains("Game Boy Advance"));
        Assert.Contains(db.LoadedFiles, f => f.Contains("Nintendo Entertainment System"));
    }

    /// <summary>
    /// NES は Headerless でなければならない。
    /// Headered だと、当アプリが付ける iNES ヘッダとの差で一致しなくなる。
    /// </summary>
    [Fact]
    public void NESはHeaderless版が入っている()
    {
        var db = NoIntroDatabase.Load(
            Path.Combine(Path.GetTempPath(), "rd-nonexistent-" + Guid.NewGuid().ToString("N")));

        Assert.Contains(db.LoadedFiles, f => f.Contains("Headerless"));
        Assert.DoesNotContain(db.LoadedFiles, f => f.Contains("(Headered)"));
    }

    /// <summary>実際に吸い出した ROM が引けること（Crash Bandicoot Advance）。</summary>
    [Fact]
    public void 実機で吸い出したROMのCRCが引ける()
    {
        var db = NoIntroDatabase.Load(
            Path.Combine(Path.GetTempPath(), "rd-nonexistent-" + Guid.NewGuid().ToString("N")));

        var hit = db.FindByCrc(0x64767B34);

        Assert.NotEmpty(hit);
        Assert.Contains(hit, e => e.GameName.Contains("Crash Bandicoot"));
        Assert.Equal(8388608, hit[0].Size);
    }
}
