using System.Text;

namespace RetroDumper.Core.Probe;

/// <summary>
/// 探索の記録を 1 行ずつディスクに書き切る。
///
/// 以前の探索はレポート文字列をメモリに溜め、最後にまとめて保存していた。
/// そのため探索中にアダプタが USB から消えて例外になると、
/// **記録が 1 バイトも残らなかった**。
/// どの opcode まで進んでいたのかも分からず、原因の特定ができない。
///
/// ここでは 1 行書くたびに flush する。途中で強制終了しても、
/// そこまでの内容は確実にファイルに残る。
/// </summary>
public sealed class ProbeJournal : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly StringBuilder _buffer = new();
    private readonly Action<string>? _echo;
    private bool _disposed;

    /// <summary>書き出し先。ファイルを作らない記録では <c>null</c>。</summary>
    public string? Path { get; }

    /// <summary>これまでに書いた内容。UI のダイアログ表示などに使う。</summary>
    public string Text => _buffer.ToString();

    /// <param name="append">
    /// 既存の内容に書き足すか。
    /// カートリッジを差し替えながら同じ測定を繰り返し、
    /// 1 つのファイルで見比べたいときに使う。
    /// </param>
    public ProbeJournal(string path, Action<string>? echo = null, bool append = false)
    {
        Path = path;
        _echo = echo;

        string? dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // FileShare.ReadWrite で開く。既定の StreamWriter は排他で掴むため、
        // 探索中に別のプログラム（メモ帳など）からログを覗けない。
        // アダプタが落ちて固まったときに中身を確認できることが重要なので、
        // 書いている最中から読めるようにしておく。
        var stream = new FileStream(
            path,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite);

        _writer = new StreamWriter(stream, Encoding.UTF8)
        {
            AutoFlush = true,
        };
    }

    public void Write(string line)
    {
        if (_disposed) return;

        _buffer.AppendLine(line);
        _echo?.Invoke(line);

        try
        {
            _writer?.WriteLine(line);
            _writer?.Flush();
        }
        catch (IOException)
        {
            // 記録できなくても探索自体は続ける。
        }
    }

    public void Blank() => Write(string.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _writer?.Flush(); _writer?.Dispose(); }
        catch (IOException) { }
    }

    /// <summary>
    /// ファイルを作らず、記録をメモリと echo にだけ残す。
    ///
    /// **ディスクに書かないぶん、プロセスごと落ちたら記録も消える。**
    /// 1 行ずつ flush していたのは、探索中にアダプタが USB から消えても
    /// そこまでを残すためだった。例外で止まる分には echo 先（画面のログ）に
    /// 出ているので追えるが、固まって強制終了した場合は何も残らない。
    /// </summary>
    public static ProbeJournal InMemory(Action<string>? echo = null) => new(echo);

    private ProbeJournal(Action<string>? echo)
    {
        Path = null;
        _echo = echo;
        _writer = null;
    }

    // exe と同じ場所へ日時付きの記録を作る CreateNextTo / AppendNextTo は外した。
    // 断りなくログのテキストファイルを置かないため。
    // 保存先を決めて残したいときは、パスを渡す方のコンストラクタを使う。
}
