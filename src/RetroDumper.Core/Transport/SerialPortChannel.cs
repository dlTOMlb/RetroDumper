using System.IO.Ports;

namespace RetroDumper.Core.Transport;

/// <summary>
/// 仮想 COM ポート経由の通り道。Windows で実機確認済みの経路。
///
/// **SerialPort の呼び出しをそのまま通すだけにしてある。**
/// 待ち時間の既定値やバッファの大きさも、以前 <see cref="RfcaLink"/> が
/// 直接組み立てていたものと同じ値を使う。挙動を変えないためである。
/// </summary>
public sealed class SerialPortChannel : IRfcaByteChannel
{
    private readonly SerialPort _port;

    public SerialPortChannel(string portName, int baudRate, int bufferSize,
                             int readTimeout, int writeTimeout)
    {
        Name = portName;

        // 設定は参照実装に合わせる。
        //
        // バッファの大きさが効く。既定の送信バッファは 2048 バイトしかなく、
        // フラッシュの 4096 バイト書き込みが収まらない。ドライバが吐き出すまで
        // Write がブロックし、1 秒のタイムアウトに掛かって失敗していた。
        // 失敗したまま再試行したところ、アダプタが USB から落ちた（2026-09-24 実機）。
        // 受信バッファも既定 4096 バイトで、大きな読み出しで取りこぼす余地があった。
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadBufferSize = bufferSize,
            WriteBufferSize = bufferSize,
            ReadTimeout = readTimeout,
            WriteTimeout = writeTimeout,
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
        };
    }

    public string Name { get; }

    public bool IsOpen => _port.IsOpen;

    public void Open() => _port.Open();

    public void Close() => _port.Close();

    /// <summary>
    /// ポートが開いていても、デバイスが物理的に消えていれば列挙から外れる。
    /// </summary>
    public bool IsStillPresent
    {
        get
        {
            try
            {
                return SerialPort.GetPortNames()
                    .Any(p => string.Equals(p, Name, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }
    }

    public void Write(byte[] buffer, int offset, int count) => _port.Write(buffer, offset, count);

    public int Read(byte[] buffer, int offset, int count) => _port.Read(buffer, offset, count);

    public int ReadTimeout { get => _port.ReadTimeout; set => _port.ReadTimeout = value; }

    public int WriteTimeout { get => _port.WriteTimeout; set => _port.WriteTimeout = value; }

    public int BytesToRead => _port.BytesToRead;

    public int BytesToWrite => _port.BytesToWrite;

    public void DiscardInBuffer() => _port.DiscardInBuffer();

    public void DiscardOutBuffer() => _port.DiscardOutBuffer();

    public string Describe() =>
        $"送信バッファ {_port.WriteBufferSize} / 受信バッファ {_port.ReadBufferSize} / " +
        $"読み {_port.ReadTimeout}ms / 書き {_port.WriteTimeout}ms";

    public void Dispose()
    {
        try { _port.Close(); } catch { /* 切断済み */ }
        _port.Dispose();
    }
}
