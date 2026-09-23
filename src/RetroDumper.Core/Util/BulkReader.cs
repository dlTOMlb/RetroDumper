using RetroDumper.Core.Dumping;
using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Util;

/// <summary>
/// アドレス変換を差し込みながら連続領域を読み出す共通ループ。
/// チャンク分割・リトライ・進捗通知・キャンセルをここに集約する。
/// </summary>
public static class BulkReader
{
    /// <summary>
    /// ROM オフセット <paramref name="offset"/> を受け取り、
    /// そのオフセットを読むためのバス上のアドレスを返すデリゲート。
    /// </summary>
    public delegate uint AddressMapper(long offset);

    /// <summary>
    /// チャンク境界で呼ばれるフック。バンク切り替えなど、
    /// 読み出し前にバスの状態を変える必要があるマッパー用。
    /// </summary>
    public delegate void PreChunkHook(long offset);

    public static byte[] Read(
        IRfcaLink link,
        uint opcode,
        long length,
        AddressMapper map,
        DumpOptions options,
        string stage,
        IProgress<DumpProgress>? progress,
        CancellationToken cancellationToken,
        PreChunkHook? preChunk = null,
        int? maxChunkAlignment = null,
        uint headerField = 0x08)
    {
        var buffer = new byte[length];
        long done = 0;

        progress?.Report(new DumpProgress(stage, 0, length));

        while (done < length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int chunk = (int)Math.Min(options.ChunkSize, length - done);

            // バンク境界などをまたがないようにチャンクを切り詰める。
            if (maxChunkAlignment is int align && align > 0)
            {
                long distanceToBoundary = align - (done % align);
                chunk = (int)Math.Min(chunk, distanceToBoundary);
            }

            preChunk?.Invoke(done);

            uint address = map(done);
            ReadChunkWithRetry(
                link, opcode, address, buffer.AsSpan((int)done, chunk), options, headerField);

            done += chunk;
            progress?.Report(new DumpProgress(stage, done, length));
        }

        return buffer;
    }

    private static void ReadChunkWithRetry(
        IRfcaLink link, uint opcode, uint address, Span<byte> destination,
        DumpOptions options, uint headerField)
    {
        RfcaException? last = null;

        for (int attempt = 0; attempt <= options.RetryCount; attempt++)
        {
            try
            {
                link.Read(opcode, address, destination, headerField);
                return;
            }
            catch (RfcaException ex)
            {
                last = ex;
                // 通信が乱れたあとは少し落ち着かせてから再試行する。
                Thread.Sleep(20 * (attempt + 1));
            }
        }

        throw new RfcaException(
            $"アドレス 0x{address:X6} の読み出しに {options.RetryCount + 1} 回失敗しました: {last?.Message}",
            last!);
    }
}
