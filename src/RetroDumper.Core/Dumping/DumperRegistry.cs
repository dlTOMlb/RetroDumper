using RetroDumper.Core.Gb;
using RetroDumper.Core.Gba;
using RetroDumper.Core.Md;
using RetroDumper.Core.Nes;
using RetroDumper.Core.Snes;
using RetroDumper.Core.Sms;
using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Dumping;

/// <summary>機種ごとの吸い出し実装を種別コードから引く。</summary>
public static class DumperRegistry
{
    public static IReadOnlyList<ICartridgeDumper> All { get; } =
    [
        new SnesDumper(),
        new MdDumper(),
        new NesDumper(),
        new SmsDumper(),
        new GbDumper(),
        new GbaDumper(),
    ];

    /// <summary>
    /// 状態応答の種別コードに対応する実装を返す。
    /// GBA の種別コードは未確認なので <see cref="GbaDumper.DetectedKind"/> 次第で変わる。
    /// </summary>
    public static ICartridgeDumper? ForKind(CartridgeKind kind)
        => All.FirstOrDefault(d => d.Kind == kind);

    /// <summary>
    /// 種別コードに対応する実装がない場合に、手動で選ばせるための一覧。
    /// </summary>
    public static IReadOnlyList<ICartridgeDumper> Selectable => All;
}
