using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Nes;

/// <summary>
/// ファミコンの 2 本のバスへのアクセス。
///
/// ファミコンは PRG-ROM（CPU バス）と CHR-ROM（PPU バス）が物理的に別配線で、
/// opcode も別に割り当てられている。他機種のように 1 本のアドレス空間ではない。
///
///   CPU $6000-$7FFF  バッテリーバックアップ WRAM（セーブ）
///   CPU $8000-$FFFF  PRG-ROM / マッパーのラッチ
///   PPU $0000-$1FFF  CHR-ROM
/// </summary>
public sealed class NesBus(IRfcaLink link)
{
    private readonly IRfcaLink _link = link;

    /// <summary>CPU バスから読む。</summary>
    public byte[] CpuRead(uint address, int size)
        => _link.Read(RfcaOpcode.NesCpuRead, address, size);

    /// <summary>PPU バスから読む。</summary>
    public byte[] PpuRead(uint address, int size)
        => _link.Read(RfcaOpcode.NesPpuRead, address, size);

    /// <summary>
    /// マッパーのラッチへ 1 バイト書く。
    ///
    /// $8000-$FFFF は PRG-ROM が見えている領域だが、ROM は読み出し専用なので
    /// 書き込みはマッパーのラッチに入るだけで内容は変わらない。
    /// セーブ用 WRAM ($6000-$7FFF) は範囲外として弾かれる。
    /// </summary>
    public void CpuWrite(uint address, byte value)
        => _link.WriteBankRegister(
            CartridgeKind.Famicom, RfcaOpcode.NesCpuWrite, address, value);

    /// <summary>
    /// バスコンフリクトのあるマッパー（UxROM / CNROM）用の書き込み。
    ///
    /// これらはマッパーのラッチと PRG-ROM が同時にバスを駆動するため、
    /// **書きたい値と同じ値が入っている番地**に書かないと値が化ける。
    /// ROM を読んで該当する番地を探してから書く。
    /// </summary>
    public void SearchAndWrite(uint start, uint end, byte value)
    {
        uint at = FindByte(start, end, value)
            ?? throw new RfcaException(
                $"バスコンフリクト対策のため 0x{value:X2} が入っている番地を " +
                $"${start:X4}-${end:X4} から探しましたが見つかりませんでした。");

        CpuWrite(at, value);
    }

    /// <summary>指定範囲から、その値が入っている最初の番地を探す。</summary>
    public uint? FindByte(uint start, uint end, byte value)
    {
        const int block = 1024;

        for (uint at = start; at <= end; at += block)
        {
            int size = (int)Math.Min(block, end - at + 1);
            var data = CpuRead(at, size);

            int index = Array.IndexOf(data, value);
            if (index >= 0) return at + (uint)index;
        }

        return null;
    }
}
