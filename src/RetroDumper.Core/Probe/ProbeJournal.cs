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
    private readonly StreamWriter _writer;
    private readonly StringBuilder _buffer = new();
    private readonly Action<string>? _echo;
    private bool _disposed;

    public string Path { get; }

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
            _writer.WriteLine(line);
            _writer.Flush();
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

        try { _writer.Flush(); _writer.Dispose(); }
        catch (IOException) { }
    }

    /// <summary>dist フォルダ（exe と同じ場所）に日時付きの名前で作る。</summary>
    public static ProbeJournal CreateNextTo(string prefix, Action<string>? echo = null)
    {
        return new ProbeJournal(
            System.IO.Path.Combine(ExeDirectory, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.txt"),
            echo);
    }

    /// <summary>
    /// dist フォルダの固定名ファイルに書き足す。
    /// カートリッジを差し替えて繰り返す測定を 1 つのファイルに集める。
    /// </summary>
    public static ProbeJournal AppendNextTo(string name, Action<string>? echo = null)
        => new(System.IO.Path.Combine(ExeDirectory, $"{name}.txt"), echo, append: true);

    private static string ExeDirectory =>
        System.IO.Path.GetDirectoryName(Environment.ProcessPath)
        ?? Directory.GetCurrentDirectory();
}
