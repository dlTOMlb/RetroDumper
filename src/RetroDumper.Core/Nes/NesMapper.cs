using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Nes;

/// <summary>
/// ファミコンのマッパーごとのバンク切り替え手順。
///
/// ファミコンのカートリッジには**ヘッダがありません**。
/// SFC や GBA と違い、マッパー番号も PRG/CHR の容量もカセットから読めないため、
/// マッパーは利用者が選ぶか、データベースで同定する必要があります。
///
/// バンク切り替えはマッパーのラッチ（$8000-$FFFF）への書き込みで行います。
/// そこは PRG-ROM が見えている領域でもありますが、ROM は読み出し専用なので
/// 内容は変わりません。$6000-$7FFF のセーブ用 WRAM には書きません。
/// </summary>
public abstract class NesMapper
{
    public abstract int Number { get; }
    public abstract string Name { get; }

    /// <summary>PRG-ROM の 1 バンクの大きさ。</summary>
    public virtual int PrgBankSize => 0x4000;

    /// <summary>CHR-ROM の 1 バンクの大きさ。0 なら CHR-RAM（CHR-ROM なし）。</summary>
    public virtual int ChrBankSize => 0x2000;

    /// <summary>
    /// このマッパーが取りうる PRG-ROM 容量の範囲（バイト）。
    ///
    /// 容量はバンクの折り返しで実測するが、上限を超えた番号を指定しても
    /// 意味のある値は返らないため、マッパーごとに探索範囲を区切る必要がある。
    /// NROM のようにバンク切り替えを持たないものは特に重要で、
    /// 範囲を設けないと折り返しが見つからず容量を誤る。
    ///
    /// 値は sanni/cartreader の mapsize テーブル（Cart_Reader/NES.ino）に合わせた。
    /// 同ツールも利用者がマッパーを選ぶ方式で、この範囲内から容量を選ばせている。
    /// </summary>
    public virtual (long Min, long Max) PrgSizeRange => (16 * 1024, 512 * 1024);

    /// <summary>
    /// 取りうる CHR-ROM 容量の範囲（バイト）。
    /// Min が 0 なら CHR-ROM を持たない構成（CHR-RAM）があり得る。
    /// </summary>
    public virtual (long Min, long Max) ChrSizeRange => (0, 256 * 1024);

    public int MaxPrgBanks => PrgBankSize <= 0 ? 0 : (int)(PrgSizeRange.Max / PrgBankSize);

    public int MaxChrBanks => ChrBankSize <= 0 ? 0 : (int)(ChrSizeRange.Max / ChrBankSize);

    /// <summary>吸い出しの前に 1 度だけ行う初期化。</summary>
    public virtual void Initialize(NesBus bus) { }

    /// <summary>PRG-ROM の指定バンクを読む。</summary>
    public abstract byte[] ReadPrgBank(NesBus bus, int bank, int size, int totalBanks);

    /// <summary>CHR-ROM の指定バンクを読む。CHR-ROM が無ければ null。</summary>
    public virtual byte[]? ReadChrBank(NesBus bus, int bank, int size) => null;

    public static IReadOnlyList<NesMapper> All { get; } =
    [
        new NromMapper(),
        new Mmc1Mapper(),
        new UxRomMapper(),
        new CnRomMapper(),
        new Mmc3Mapper(),
    ];

    public static NesMapper? ForNumber(int number)
        => All.FirstOrDefault(m => m.Number == number);
}

/// <summary>マッパー 0。バンク切り替えなし。書き込みを一切行わない。</summary>
public sealed class NromMapper : NesMapper
{
    public override int Number => 0;
    public override string Name => "NROM";

    // バンク切り替えを持たない。CHR-RAM 構成（CHR-ROM なし）もある。
    public override (long, long) PrgSizeRange => (16 * 1024, 32 * 1024);
    public override (long, long) ChrSizeRange => (0, 8 * 1024);

    public override byte[] ReadPrgBank(NesBus bus, int bank, int size, int totalBanks)
        => bus.CpuRead(bank >= 1 ? 0xC000u : 0x8000u, size);

    public override byte[] ReadChrBank(NesBus bus, int bank, int size)
        => bus.PpuRead(0x0000, size);
}

/// <summary>
/// マッパー 1。MMC1。
///
/// レジスタはシリアル。1 バイトを 5 回に分けて bit0 から送り込む。
/// bit7 を立てて書くとシフトレジスタが初期化される。
/// </summary>
public sealed class Mmc1Mapper : NesMapper
{
    public override int Number => 1;
    public override string Name => "MMC1";
    public override int ChrBankSize => 0x1000;

    public override (long, long) PrgSizeRange => (32 * 1024, 512 * 1024);
    public override (long, long) ChrSizeRange => (0, 128 * 1024);

    private static void Reset(NesBus bus)
        => bus.CpuWrite(0x8000, 0x80);

    private static void WriteRegister(NesBus bus, uint address, byte value)
    {
        for (int i = 0; i < 5; i++)
            bus.CpuWrite(address, (byte)((value >> i) & 0x01));
    }

    public override void Initialize(NesBus bus)
    {
        Reset(bus);

        // 制御レジスタ: PRG は $8000 固定 16KB 切り替え、CHR は 4KB 切り替え。
        WriteRegister(bus, 0x8000, 0x1C);
        WriteRegister(bus, 0xA000, 0x00);
        WriteRegister(bus, 0xC000, 0x00);
        WriteRegister(bus, 0xE000, 0x10);
    }

    public override byte[] ReadPrgBank(NesBus bus, int bank, int size, int totalBanks)
    {
        // 上位ビットは CHR レジスタ側に載る（512KB 品対応）。
        WriteRegister(bus, 0xA000, (byte)(bank & 0x10));
        WriteRegister(bus, 0xC000, (byte)(bank & 0x10));
        WriteRegister(bus, 0xE000, (byte)((bank & 0x0F) | 0x10));

        return bus.CpuRead(0x8000, size);
    }

    public override byte[] ReadChrBank(NesBus bus, int bank, int size)
    {
        WriteRegister(bus, 0xA000, (byte)bank);
        return bus.PpuRead(0x0000, size);
    }
}

/// <summary>
/// マッパー 2。UxROM。CHR は RAM なので吸い出さない。
///
/// バスコンフリクトがあるため、書きたい値と同じ値が入っている番地を探して書く。
/// </summary>
public sealed class UxRomMapper : NesMapper
{
    public override int Number => 2;
    public override string Name => "UxROM";
    public override int ChrBankSize => 0;

    public override (long, long) PrgSizeRange => (64 * 1024, 256 * 1024);
    public override (long, long) ChrSizeRange => (0, 0);    // 必ず CHR-RAM

    public override byte[] ReadPrgBank(NesBus bus, int bank, int size, int totalBanks)
    {
        // 最終バンクは $C000 に固定されているので、切り替えずに読む。
        if (bank == totalBanks - 1) return bus.CpuRead(0xC000, size);

        bus.SearchAndWrite(0xC000, 0xFFFF, (byte)bank);
        return bus.CpuRead(0x8000, size);
    }
}

/// <summary>
/// マッパー 3。CNROM。PRG は固定、CHR だけ切り替える。
/// UxROM と同じくバスコンフリクト対策が要る。
/// </summary>
public sealed class CnRomMapper : NesMapper
{
    public override int Number => 3;
    public override string Name => "CNROM";

    public override (long, long) PrgSizeRange => (16 * 1024, 32 * 1024);
    public override (long, long) ChrSizeRange => (0, 2048 * 1024);

    public override byte[] ReadPrgBank(NesBus bus, int bank, int size, int totalBanks)
        => bus.CpuRead(bank >= 1 ? 0xC000u : 0x8000u, size);

    public override byte[] ReadChrBank(NesBus bus, int bank, int size)
    {
        // 上位ビットを立てておくと、ROM 内に同じ値が見つかりやすい。
        bus.SearchAndWrite(0x8000, 0xFFFF, (byte)(bank | 0xFC));
        return bus.PpuRead(0x0000, size);
    }
}

/// <summary>
/// マッパー 4。MMC3。$8000 に対象レジスタ番号、$8001 に値。
/// PRG は 8KB、CHR は 1KB 単位。
/// </summary>
public sealed class Mmc3Mapper : NesMapper
{
    public override int Number => 4;
    public override string Name => "MMC3";
    public override int PrgBankSize => 0x2000;
    public override int ChrBankSize => 0x0400;

    public override (long, long) PrgSizeRange => (32 * 1024, 512 * 1024);
    public override (long, long) ChrSizeRange => (0, 256 * 1024);

    public override byte[] ReadPrgBank(NesBus bus, int bank, int size, int totalBanks)
    {
        bus.CpuWrite(0x8000, 0x06);          // R6 = $8000 の PRG バンク
        bus.CpuWrite(0x8001, (byte)bank);
        return bus.CpuRead(0x8000, size);
    }

    public override byte[] ReadChrBank(NesBus bus, int bank, int size)
    {
        bus.CpuWrite(0x8000, 0x02);          // R2 = $1000 の CHR バンク
        bus.CpuWrite(0x8001, (byte)bank);
        return bus.PpuRead(0x1000, size);
    }
}
