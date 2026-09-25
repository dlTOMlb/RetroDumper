using RetroDumper.Core.Dumping;
using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Snes;

/// <summary>
/// スーパーファミコンのセーブ RAM の読み書き。
///
/// 窓の位置も転送量も使う opcode もマッパーごとに違う。
/// 配置は <see cref="SnesAddressMap.SramLayout"/> にまとめてあり、
/// 値は参照実装のマッパーごとの実装に合わせた。
///
/// **LoROM は読みと書きでバンクが違う。**読みは $70 以降、書きは $F0 以降。
/// 同じ SRAM の別の見え方で、参照実装がこの 2 つを使い分けている。
///
/// 書き込みは <see cref="IRfcaLink.WriteSaveMemory"/> を通すので、
/// セーブ領域以外へは許可の有無に関わらず出て行かない。
/// </summary>
public static class SnesSave
{
    /// <summary>そのマッパーで扱えるセーブ RAM の上限。</summary>
    public static long MaxSize(SnesMapper mapper)
        => SnesAddressMap.SramLayout(mapper)?.MaxSize ?? 0;

    public static byte[] Read(
        IRfcaLink link, SnesMapper mapper, long size,
        IProgress<DumpProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var layout = LayoutFor(mapper, size);
        var result = new byte[size];
        long done = 0;

        while (done < size)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int length = (int)Math.Min(layout.BytesPerBank, size - done);
            uint address = SnesAddressMap.SramBusAddress(layout, done);

            link.Read(layout.ReadOpcode, address, result.AsSpan((int)done, length));

            done += length;
            progress?.Report(new DumpProgress("セーブ RAM 読み出し", done, size));
        }

        return result;
    }

    /// <summary>
    /// セーブ RAM に書き、読み戻して照合する。
    ///
    /// 照合まで済ませて初めて成功とする。書けたつもりで壊れているのが
    /// いちばん困るため、参照実装も同じく 1 バイトずつ突き合わせている。
    /// </summary>
    public static void Write(
        IRfcaLink link, SnesMapper mapper, byte[] data,
        IProgress<DumpProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var layout = LayoutFor(mapper, data.Length);
        long size = data.Length;
        long done = 0;

        while (done < size)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int length = (int)Math.Min(layout.BytesPerBank, size - done);
            uint address = SnesAddressMap.SramBusAddress(layout, done, forWrite: true);

            link.WriteSaveMemory(
                CartridgeKind.SuperFamicom, layout.WriteOpcode, address,
                data.AsSpan((int)done, length));

            done += length;
            progress?.Report(new DumpProgress("セーブ RAM 書き込み", done, size));
        }

        progress?.Report(new DumpProgress("書き込んだ内容を照合中", 0, size));

        var readBack = Read(link, mapper, size, progress, cancellationToken);

        for (int i = 0; i < size; i++)
            if (readBack[i] != data[i])
                throw new RfcaException(
                    $"照合に失敗しました。{i:X5} 番地は 0x{data[i]:X2} を書いたはずですが " +
                    $"0x{readBack[i]:X2} が読めました。" +
                    "カートリッジのセーブデータが中途半端な状態になっている可能性があります。");
    }

    private static SnesAddressMap.SramWindow LayoutFor(SnesMapper mapper, long size)
    {
        var layout = SnesAddressMap.SramLayout(mapper)
            ?? throw new RfcaException($"{mapper} のセーブ RAM 配置が分かりません。");

        if (size <= 0)
            throw new RfcaException("このカートリッジにはセーブ RAM がありません。");

        if (size > layout.MaxSize)
            throw new RfcaException(
                $"セーブ RAM が大きすぎます。{mapper} で扱えるのは " +
                $"{layout.MaxSize / 1024}KB までですが、{size / 1024}KB を指定されました。");

        return layout;
    }
}
