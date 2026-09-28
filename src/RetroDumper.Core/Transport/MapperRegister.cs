namespace RetroDumper.Core.Transport;

/// <summary>
/// バンク切り替えレジスタの範囲。
///
/// 【なぜ分ける必要があるか】
/// 「カートリッジへの書き込みを禁止」は、**カセットの内容が書き換わること**を
/// 防ぐための機能。セーブデータやフラッシュを壊さないためのもの。
///
/// 一方、ゲームボーイの MBC やマークIII のマッパーは、
/// バンクを選ぶために制御レジスタへ書き込む必要がある。
/// これは揮発性のレジスタで、**カセットの内容は一切変わらない**。
/// 電源を切れば消える。読み出しのための操作であって、書き換えではない。
///
/// 両者を同じ扱いにしていたため、書き込み禁止のまま GB を吸い出せなかった。
/// ここで範囲を明示し、レジスタへの書き込みだけは保護下でも通す。
///
/// 【絶対の前提】
/// GBA はこの仕組みの対象外。GBA の ROM 読み出しに書き込みは一切不要で、
/// <see cref="IsBankRegister"/> は GBA に対して必ず false を返す。
/// </summary>
public static class MapperRegister
{
    /// <summary>
    /// <paramref name="address"/> が、内容を変えないバンク切り替えレジスタか。
    /// </summary>
    public static bool IsBankRegister(CartridgeKind kind, uint address) => kind switch
    {
        // MBC1/2/3/5 の制御レジスタ。
        //   $0000-$1FFF 外部 RAM の有効・無効
        //   $2000-$3FFF ROM バンク番号
        //   $4000-$5FFF RAM バンク番号 / ROM バンク上位
        //   $6000-$7FFF バンクモード
        // $A000-$BFFF は外部 RAM の実体なので**含めない**（セーブが壊れる）。
        CartridgeKind.GameBoy => address <= 0x7FFF,

        // セガのマッパーレジスタ。$FFFC 制御 / $FFFD-$FFFF 各スロットのバンク。
        CartridgeKind.MarkIIIOrGameGear => address is >= 0xFFFC and <= 0xFFFF,

        // ファミコンのマッパーレジスタは $8000-$FFFF。
        // そこは PRG-ROM が見えている領域でもあるが、ROM は読み出し専用なので
        // 書き込みはマッパーのラッチに入るだけで、ROM の内容は変わらない。
        // $6000-$7FFF はバッテリーバックアップ WRAM（セーブ）なので**含めない**。
        CartridgeKind.Famicom => address is >= 0x8000 and <= 0xFFFF,

        // PC エンジンの SF2 ダッシュ。$1FF0-$1FF7 で上位バンクを差し替える。
        // ROM しか無い普通の Hu カードでは、ここへ書いても何も変わらない。
        CartridgeKind.PcEngineHuCard => address is >= 0x1FF0 and <= 0x1FF7,

        // GBA は読み出しに書き込みを必要としない。例外を作らない。
        CartridgeKind.GameBoyAdvance => false,

        // SFC のマッパーチップが持つ、窓を貼り替えるレジスタ。
        //
        // GB の MBC とまったく同じ性質で、選ぶだけで内容は変わらない。
        // 以前はここを通していなかった。市販の SA-1 が最大 4MB で、
        // 電源投入時の既定値のまま読めてしまうため必要が無かったから。
        //
        // **天外魔境ZERO (5MB) でその前提が崩れた。**4MB を超える分を
        // 読むには貼り替えが要るのに、通さないままだったため
        // 「書き込みを許可」を外させることになっていた。
        // GB を通して SFC を通さないのは筋が通らない。
        //
        // 開けるのは 13 番地だけ。GB の $0000-$7FFF (32KB) より
        // はるかに狭く、セーブ RAM の窓とは完全に別の場所にある。
        CartridgeKind.SuperFamicom => IsSnesMapperRegister(address),

        _ => false,
    };

    /// <summary>
    /// SFC のマッパーチップのレジスタか。
    ///
    /// | チップ | 番地 | 役割 |
    /// |---|---|---|
    /// | SA-1 | $00:2220-$2223 | Super MMC の 1MB ブロック 4 本 |
    /// | S-DD1 | $00:4804-$4807 | 同上 |
    /// | SPC7110 | $00:4830-$4834 | 1MB の窓 3 本と制御 |
    ///
    /// いずれもセーブ RAM の窓（$70 台 / $F0 台 / $30:6000 など）とは
    /// 重ならない。<see cref="SaveMemory.IsSnesSram"/> と照らして確かめてある。
    /// </summary>
    private static bool IsSnesMapperRegister(uint address) => address switch
    {
        >= 0x002220 and <= 0x002223 => true,   // SA-1
        >= 0x004804 and <= 0x004807 => true,   // S-DD1
        >= 0x004830 and <= 0x004834 => true,   // SPC7110
        _ => false,
    };

    /// <summary>
    /// セーブデータの実体がある範囲。ここへの書き込みは保護下では絶対に通さない。
    /// </summary>
    public static bool IsSaveMemory(CartridgeKind kind, uint address) => kind switch
    {
        CartridgeKind.GameBoy => address is >= 0xA000 and <= 0xBFFF,

        // ファミコンのバッテリーバックアップ WRAM。
        CartridgeKind.Famicom => address is >= 0x6000 and <= 0x7FFF,

        _ => false,
    };
}
