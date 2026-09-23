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

    [Fact]
    public void DATが無くても空で返り例外にならない()
    {
        var db = NoIntroDatabase.Load(Path.Combine(_dir, "存在しない"));

        Assert.True(db.IsEmpty);
        Assert.Equal(0, db.EntryCount);
        Assert.Null(db.Match(SampleRom(), 4096));
    }

    [Fact]
    public void 一致するROMを引ける()
    {
        var rom = SampleRom();
        WriteDat(rom);

        var db = NoIntroDatabase.Load(_dir);

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

        var db = NoIntroDatabase.Load(_dir);

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

        var db = NoIntroDatabase.Load(_dir);

        Assert.Null(db.Match(rom, rom.Length / 2));
    }

    [Fact]
    public void CRC32だけでも引ける()
    {
        var rom = SampleRom();
        WriteDat(rom);

        var db = NoIntroDatabase.Load(_dir);

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
        var db = NoIntroDatabase.Load(_dir, log.Add);

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

        var db = NoIntroDatabase.Load(_dir);

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
        var db = NoIntroDatabase.Load(_dir);

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

        var db = NoIntroDatabase.Load(_dir);

        foreach (int mapper in new[] { 0, 1, 4, 66 })
        {
            var file = RetroDumper.Core.Nes.NesDumper.BuildINesFile(mapper, body, []);
            Assert.NotNull(db.Match(file.AsSpan(16), file.Length - 16));
        }
    }
}
