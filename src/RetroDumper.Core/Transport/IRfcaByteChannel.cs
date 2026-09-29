namespace RetroDumper.Core.Transport;

/// <summary>
/// RFCA との間でバイト列を運ぶ道。
///
/// <see cref="RfcaLink"/> はフレームの組み立てと ACK の扱いだけを受け持ち、
/// 実際の入出力はこれに任せる。
///
/// 分けた理由は macOS である。
/// アダプタは CDC-ACM のクラスコードを名乗るが CDC functional descriptor が
/// 1 つも無いため、macOS の ACM ドライバが結合せず
/// <c>/dev/tty.usbmodem*</c> が作られない。シリアルポートが存在しないので、
/// USB のバルク転送を直に使う道が別に要る。
///
/// Windows では <see cref="SerialPortChannel"/> がそのまま SerialPort を包む。
/// **既存の呼び出しを 1 対 1 で写しただけで、挙動は変えていない。**
/// </summary>
public interface IRfcaByteChannel : IDisposable
{
    /// <summary>画面やログに出す名前。シリアルなら "COM3"。</summary>
    string Name { get; }

    bool IsOpen { get; }

    void Open();

    void Close();

    /// <summary>アダプタがまだ繋がっているか。消えていれば探索を即座に止める。</summary>
    bool IsStillPresent { get; }

    void Write(byte[] buffer, int offset, int count);

    /// <summary>
    /// 読めた分だけ返す。要求より少なくてよい。
    /// 待ち時間を過ぎたら <see cref="TimeoutException"/> を投げる
    /// （SerialPort.Read と同じ約束）。
    /// </summary>
    int Read(byte[] buffer, int offset, int count);

    int ReadTimeout { get; set; }

    int WriteTimeout { get; set; }

    /// <summary>未処理の受信バイト数。分からない実装は 0 を返してよい。</summary>
    int BytesToRead { get; }

    /// <summary>送信待ちのバイト数。書き込みが同期的な実装は 0 を返してよい。</summary>
    int BytesToWrite { get; }

    void DiscardInBuffer();

    void DiscardOutBuffer();

    /// <summary>診断用の 1 行。バッファの大きさやタイムアウトを出す。</summary>
    string Describe();
}
