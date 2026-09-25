using RetroDumper.Core.Transport;

namespace RetroDumper.Core.Pce;

/// <summary>Hu カードのバンク配置。</summary>
public enum PceMapping
{
    /// <summary>素直に先頭から並ぶ。ほとんどの Hu カードがこれ。</summary>
    Linear,

    /// <summary>
    /// 256KB のあと 256KB の空きを挟んで続く配置。
    /// 384KB の Hu カード（天外魔境など）がこの形になる。
    /// </summary>
    Interval,

    /// <summary>
    /// ストリートファイターII' ダッシュ専用。$1FF0-$1FF7 への書き込みで
    /// 上位のバンクを差し替える。2.5MB ある。
    /// </summary>
    Sf2Dash,
}

/// <summary>
/// Hu カードのバンク配置と、バンク番号からバスアドレスへの変換。
///
/// PC エンジンの Hu カードはヘッダを持たない。容量も配置も申告しないので、
/// 読んだ内容の折り返しから判断するしかない。
/// 手順は参照実装の PC エンジン向け実装に合わせた。
/// </summary>
public static class PceMapper
{
    /// <summary>1 バンク 32KB。</summary>
    public const int BankSize = 32768;

    /// <summary>折り返しの判定に使う大きさ。</summary>
    public const int ProbeSize = 8192;

    /// <summary>容量判定を打ち切る単位。128KB ごとに折り返しを見る。</summary>
    public const int GroupSize = 128 * 1024;

    /// <summary>SF2 のバンク切り替えレジスタ。</summary>
    public const uint Sf2RegisterBase = 0x1FF0;

    public static string DisplayName(PceMapping mapping) => mapping switch
    {
        PceMapping.Linear => "リニア",
        PceMapping.Interval => "インターバル (256KB + 空き)",
        PceMapping.Sf2Dash => "SF2 ダッシュ",
        _ => "不明",
    };

    /// <summary>その配置で扱える上限。</summary>
    public static long MaxSize(PceMapping mapping) => mapping switch
    {
        PceMapping.Linear => 1024 * 1024,
        PceMapping.Interval => 512 * 1024,
        PceMapping.Sf2Dash => 8_912_896,
        _ => 0,
    };

    /// <summary>
    /// バンク番号から読み出すバスアドレスを出す。
    ///
    /// インターバルは 256KB を越えたところで 256KB ぶん飛ばす。
    /// SF2 は 32 バンク目以降でレジスタを切り替えたうえ、
    /// 読む位置を 16 バンクの窓へ畳む。
    /// </summary>
    public static uint BankAddress(PceMapping mapping, int bank)
    {
        if (mapping == PceMapping.Interval)
        {
            long at = (long)bank * BankSize;
            if (at >= 256 * 1024) at += 256 * 1024;

            return (uint)at;
        }

        if (mapping == PceMapping.Sf2Dash && bank >= 32)
            return (uint)((bank % 16 + 16) * BankSize);

        return (uint)((long)bank * BankSize);
    }

    /// <summary>
    /// SF2 で、そのバンクを読む前に書き込むレジスタ。
    /// 切り替えが要らなければ null。
    /// </summary>
    public static uint? Sf2Register(PceMapping mapping, int bank)
    {
        if (mapping != PceMapping.Sf2Dash) return null;

        return bank < 32
            ? Sf2RegisterBase
            : Sf2RegisterBase + (uint)((bank - 16) / 16);
    }
}
