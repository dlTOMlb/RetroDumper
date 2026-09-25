namespace RetroDumper.Core.Transport;

/// <summary>
/// セーブデータの読み書きを利用者に出してよい機種。
///
/// **実機で確かめた機種だけを認める。**
///
/// 判断の分かれ目は「未検証だから念のため」ではなく、
/// **読み戻しによる照合が信用できるか**にある。
///
/// セーブの書き込みは、書いたあとに読み戻して 1 バイトずつ照合している。
/// これが唯一の安全網で、壊したかどうかはこれでしか分からない。
/// ところがスーパーファミコンでは、セーブ RAM の窓でバスが浮いており、
/// 読みが「直前にバスへ流れた最後の 1 バイト」を返す（2026-09-26 実機、
/// スーパーメトロイド）。一様なデータを書くと、1 バイトも届いていないのに
/// 照合が通る。安全網が静かに外れている状態で、
/// 「書けました」と報告してしまう。
///
/// 読み出しも同じ理由で出せない。一様な値が返るだけなのに、
/// 利用者はそれを控えとして保存し、取れたつもりになる。
///
/// メガドライブ・マークIII / ゲームギアは実機で試せていない。
/// これまで実機に当てた機種は、例外なく何かしら食い違いが出ている
/// （ファミコンは書き込み後のつつき、GBA EEPROM はアドレス幅、
/// GBA フラッシュは ID モード、GB はスロット初期化）。
/// 当たっていないものを当たったことにはしない。
///
/// 実装とテストは残してある。実機で確かめたら、ここへ足すだけで出る。
/// </summary>
public static class SaveSupport
{
    /// <summary>
    /// セーブの読み書きを画面に出してよい機種か。
    ///
    /// ここが false の機種は、セーブの吸い出し・書き込み・消去を
    /// 画面から行えない。吸い出した ROM にセーブを付ける指定も効かない。
    /// </summary>
    public static bool IsVerified(CartridgeKind kind) => kind switch
    {
        // MBC1 / MBC3 / MBC5 / HuC1 を実機で確認済み。
        CartridgeKind.GameBoy => true,

        // SRAM 32KB / EEPROM 512B・8KB / フラッシュ 64KB・128KB を確認済み。
        CartridgeKind.GameBoyAdvance => true,

        _ => false,
    };

    /// <summary>
    /// 出せない理由。画面に出す文言。
    /// </summary>
    public static string ReasonNotVerified(CartridgeKind kind) => kind switch
    {
        CartridgeKind.SuperFamicom =>
            "スーパーファミコンのセーブの読み書きは、まだ出せる状態にありません。" +
            "セーブ RAM の窓でバスが浮いており、読みが直前に書いた値を返してしまうため、" +
            "書けたかどうかを確かめられません。",

        CartridgeKind.MegaDrive or CartridgeKind.MarkIIIOrGameGear =>
            $"{kind.ToDisplayName()} のセーブの読み書きは実機で確認できていないため、" +
            "画面には出していません。",

        _ =>
            $"{kind.ToDisplayName()} のセーブの読み書きには対応していません。",
    };
}
