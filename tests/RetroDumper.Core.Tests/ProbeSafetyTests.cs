using Xunit;
using RetroDumper.Core.Transport;
using RetroDumper.Core.Probe;

namespace RetroDumper.Core.Tests;

/// <summary>
/// 探索を安全に行うための仕組みの検証。
///
/// 実際に起きた事故:
///   全 256 opcode の総当たりでアダプタが USB バスから消え、
///   レポートは最後にまとめて保存する作りだったため記録が 1 バイトも残らなかった。
///   どの opcode で落ちたのか分からず、原因を特定できなかった。
/// </summary>
public sealed class ProbeSafetyTests : IDisposable
{
    private readonly string _dir;

    public ProbeSafetyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rdprobe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void 書いた行はDisposeを待たずにディスクへ残る()
    {
        string path = Path.Combine(_dir, "journal.txt");

        using var journal = new ProbeJournal(path);
        journal.Write("0x10 を送ります");
        journal.Write("0x11 を送ります");

        // 探索中に横から覗く。Dispose は呼ばない。
        string onDisk = ReadWhileOpen(path);

        Assert.Contains("0x10 を送ります", onDisk);
        Assert.Contains("0x11 を送ります", onDisk);
    }

    /// <summary>
    /// 書き込み中のファイルを読む。File.ReadAllText は FileShare.Read で開くため、
    /// 書き込みハンドルが生きていると共有違反になる。メモ帳などと同じ開き方をする。
    /// </summary>
    private static string ReadWhileOpen(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void 例外で抜けても直前に送ったopcodeがファイルに残る()
    {
        string path = Path.Combine(_dir, "crash.txt");

        var thrown = Assert.Throws<InvalidOperationException>(() =>
        {
            using var journal = new ProbeJournal(path);

            for (uint opcode = 0x10; opcode <= 0x13; opcode++)
            {
                journal.Write($"→ 0x{opcode:X2} を送ります");

                // 0x13 でアダプタが落ちた、という想定。
                if (opcode == 0x13) throw new InvalidOperationException("切断");
            }
        });

        Assert.Equal("切断", thrown.Message);

        string onDisk = File.ReadAllText(path);
        Assert.Contains("→ 0x13 を送ります", onDisk);
    }

    [Fact]
    public void 親フォルダが無くても作られる()
    {
        string path = Path.Combine(_dir, "nested", "deep", "journal.txt");

        using (var journal = new ProbeJournal(path))
            journal.Write("行");

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void エコー先にも同じ行が渡る()
    {
        var echoed = new List<string>();

        using (var journal = new ProbeJournal(Path.Combine(_dir, "echo.txt"), echoed.Add))
        {
            journal.Write("あ");
            journal.Blank();
            journal.Write("い");
        }

        Assert.Equal(["あ", "", "い"], echoed);
    }

    /// <summary>
    /// **ファイルを作らない記録では、1 バイトも書き出さないこと。**
    ///
    /// 断りなく EXE の横へログのテキストファイルを置かないための経路。
    /// 中身は画面のログ（echo 先）とメモリにだけ残る。
    /// </summary>
    [Fact]
    public void メモリだけの記録はファイルを作らない()
    {
        var before = Directory.GetFiles(_dir);
        var echoed = new List<string>();

        using (var journal = ProbeJournal.InMemory(echoed.Add))
        {
            journal.Write("0x10 を送ります");
            journal.Blank();
            journal.Write("0x11 を送ります");

            Assert.Null(journal.Path);
            Assert.Contains("0x11 を送ります", journal.Text);
        }

        Assert.Equal(before, Directory.GetFiles(_dir));
        Assert.Equal(["0x10 を送ります", "", "0x11 を送ります"], echoed);
    }

    [Fact]
    public void 危険opcodeは記録され次回の探索から外れる()
    {
        string path = Path.Combine(_dir, "danger.txt");

        // 1 回目の探索でアダプタを落とした、という想定。
        File.WriteAllText(path, "# 記録\n0x4A  # 2026-09-23 送信後に USB から消えた\n0xB2\n");

        var loaded = ParseFile(path);

        Assert.Contains(0x4Au, loaded);
        Assert.Contains(0xB2u, loaded);
        Assert.DoesNotContain(0x20u, loaded);
    }

    [Fact]
    public void コメントと空行は無視される()
    {
        string path = Path.Combine(_dir, "comments.txt");
        File.WriteAllText(path, "# まるごとコメント\n\n   \n0x7F # 末尾コメント\n80\n");

        var loaded = ParseFile(path);

        Assert.Equal<uint[]>([0x7F, 0x80], [.. loaded.OrderBy(v => v)]);
    }

    /// <summary>
    /// <see cref="DangerousOpcodes"/> と同じ読み取り規則。
    /// 本体は exe の隣の固定パスを見るため、テストからは差し替えられない。
    /// 書式の解釈だけをここで固定しておく。
    /// </summary>
    private static HashSet<uint> ParseFile(string path)
    {
        var set = new HashSet<uint>();

        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash].Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                line = line[2..];

            if (uint.TryParse(line, System.Globalization.NumberStyles.HexNumber, null, out uint v))
                set.Add(v);
        }

        return set;
    }
}

/// <summary>
/// 制御コマンドのフレーム 2 つ目のフィールド（ヘッダ値）の扱い。
///
/// 実際に起きた不具合:
///   SendControl がこの値を 0x08 に決め打ちしていた。
///   GBA スロットはリードで 0x08 を拒否し 0x00 でないと受け付けないため、
///   0x2F は GBA に対して「必ず拒否される値」でしか送られていなかった。
///   param を 6 / 1 / 2 と変えても応答がすべて同じ FFFFFFFF だったのは
///   そのため。テストがこの値を捨てていたので気付けなかった。
/// </summary>
public sealed class ControlHeaderFieldTests
{
    [Fact]
    public void 指定したヘッダ値がそのまま制御コマンドに乗る()
    {
        var link = new FakeLinearCartridge(new byte[0x100], RfcaOpcode.GbaRomRead);

        link.SendControl(0x2F, parameter: 6, headerField: 0x00);

        var (opcode, parameter, headerField) = Assert.Single(link.ControlCommands);
        Assert.Equal(0x2Fu, opcode);
        Assert.Equal(6u, parameter);
        Assert.Equal(0x00u, headerField);
    }

    [Fact]
    public void 省略時のヘッダ値は従来どおり0x08()
    {
        var link = new FakeLinearCartridge(new byte[0x100], RfcaOpcode.GbaRomRead);

        link.SendControl(0x2F, parameter: 1);

        // SFC はこの値で動いている。既定を変えると既存の経路が壊れる。
        Assert.Equal(0x08u, Assert.Single(link.ControlCommands).HeaderField);
    }

    [Fact]
    public void ヘッダ値が違えば別の組み合わせとして記録される()
    {
        var link = new FakeLinearCartridge(new byte[0x100], RfcaOpcode.GbaRomRead);

        link.SendControl(0x2F, parameter: 6, headerField: 0x08);
        link.SendControl(0x2F, parameter: 6, headerField: 0x00);

        Assert.Equal(2, link.ControlCommands.Count);
        Assert.Equal<uint[]>(
            [0x08, 0x00],
            [.. link.ControlCommands.Select(c => c.HeaderField)]);
    }
}

/// <summary>
/// SFC の初期化手順を勝手に変えないための歯止め。
///
/// 2026-09-23、参照実装の正規手順（0x04(1) → 0x2F(1) → 0x05 ×2）に
/// 合わせたところ、動いていたカービィ3 を認識しなくなった。
/// 本家は初期化後に 16KB の捨て読みと最大 5 回のリトライを行っており、
/// 0x04 / 0x05 だけを真似ても等価にはならない。
///
/// SFC は 0x2F のみで動作実績がある。逆コンパイル結果が
/// 「より正しく見える」という理由だけで置き換えない。
/// </summary>
public sealed class SnesWakeContractTests
{
    [Fact]
    public void SFCのウェイクアップは0x2Fだけを送る()
    {
        var link = new FakeLinearCartridge(
            new byte[0x100], RfcaOpcode.SnesRead, kind: CartridgeKind.SuperFamicom);

        link.SendControl(RfcaOpcode.SlotWakeup, parameter: 1);

        var sent = Assert.Single(link.ControlCommands);
        Assert.Equal(RfcaOpcode.SlotWakeup, sent.Opcode);
        Assert.Equal(1u, sent.Parameter);
        Assert.Equal(0x08u, sent.HeaderField);
    }

    [Fact]
    public void SFCのウェイクアップparamは種別コードと同じ1()
    {
        // ここが一致しているのは SFC だけ。GBA では成り立たない
        // （種別 0x06 だが 0x2F は param を問わず拒否される）。
        Assert.Equal(1, (byte)CartridgeKind.SuperFamicom);
    }

    [Fact]
    public void SFCのライトopcodeは0x08()
    {
        // 以前は 0x0C を使っていたが、0x0C は SnesEx2Write（別物）。
        Assert.Equal(0x08u, RfcaOpcode.SnesWrite);
        Assert.Equal(0x0Cu, RfcaOpcode.SnesEx2Write);
    }
}
