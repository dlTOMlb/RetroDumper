namespace RetroDumper.Core.Transport;

/// <summary>
/// RFCA に対する操作。<see cref="RfcaLink"/> が実機用の実装。
///
/// 吸い出しロジックをこのインターフェースに対して書いておくことで、
/// 実機がなくてもシミュレータを差し替えてマッピングを検証できる。
/// </summary>
public interface IRfcaLink
{
    /// <summary>
    /// 書き込みを許可するか。既定は false（＝書き込み禁止）。
    ///
    /// false の間は <see cref="Write"/> / <see cref="WriteByte"/> /
    /// <see cref="CompletePendingWrite"/> が <see cref="RfcaWriteBlockedException"/> を投げ、
    /// バスには 1 バイトも出ない。バンク切り替えが必要な機種
    /// （GB の MBC、マークIII のマッパー）や SA-1 の MMC 貼り替えを
    /// 使うときだけ、呼び出し側が明示的に true にする。
    ///
    /// GBA の吸い出しは書き込みを一切必要としないため、常に false のままでよい。
    /// </summary>
    bool AllowWrites { get; set; }

    /// <summary>状態要求を送り、挿入中カートリッジの種別を得る。</summary>
    RfcaStatus GetStatus();

    /// <summary>
    /// データ本体を伴わない制御コマンドを送り、8 バイトの応答を返す。
    /// スロットのウェイクアップ (<see cref="RfcaOpcode.SlotWakeup"/>) など、
    /// リードでもライトでもないコマンド用。
    ///
    /// カートリッジのメモリへ書き込むものではないため、
    /// <see cref="AllowWrites"/> による遮断の対象外。
    /// </summary>
    byte[] SendControl(uint opcode, uint address = 0, uint size = 0, uint parameter = 0,
                       uint headerField = 0x08);

    /// <summary>
    /// セーブデータの書き込みを許可するか。既定は false。
    ///
    /// 「GBA には絶対に書き込まない」という条件を、利用者の判断で
    /// 「セーブ領域だけは書いてよい」に緩めるための唯一の入口。
    /// true にしても、<see cref="SaveMemory.IsSaveWrite"/> が認めた宛先にしか通らない。
    /// ROM 領域へは、この値に関わらず書き込めない。
    /// </summary>
    bool AllowSaveWrites { get; set; }

    /// <summary>
    /// セーブ領域へ書く。<see cref="AllowSaveWrites"/> と
    /// <see cref="SaveMemory.IsSaveWrite"/> の両方を満たすときだけ通る。
    /// </summary>
    void WriteSaveMemory(CartridgeKind kind, uint opcode, uint address, ReadOnlySpan<byte> data);

    /// <summary>
    /// GBA フラッシュの ID を読む。取得できなければ -1。
    /// シミュレータは実装しなくてよい。
    /// </summary>
    int ReadGbaFlashId() => -1;

    /// <summary>
    /// バンク切り替えレジスタへ 1 バイト書く。書き込み保護下でも通る。
    /// 対象は <see cref="MapperRegister.IsBankRegister"/> が認める範囲だけで、
    /// GBA は含まれない。
    /// </summary>
    void WriteBankRegister(CartridgeKind kind, uint opcode, uint address, byte value,
                           uint headerField = 0x08);

    /// <summary>
    /// 指定 opcode のバスから読み出す。
    ///
    /// <paramref name="headerField"/> はリクエストの 2 つ目のフィールド。
    /// 通常は 0x08。実機では 0x00 も受理されるため、機種による制約ではない。
    /// </summary>
    byte[] Read(uint opcode, uint address, int size, uint headerField = 0x08);

    /// <summary>指定 opcode のバスから、呼び出し側のバッファへ直接読み出す。</summary>
    void Read(uint opcode, uint address, Span<byte> destination, uint headerField = 0x08);

    /// <summary>指定 opcode のバスへ書き込む。</summary>
    void Write(uint opcode, uint address, ReadOnlySpan<byte> data);

    /// <summary>1 バイト書き込み。マッパーレジスタ操作の定番。</summary>
    void WriteByte(uint opcode, uint address, byte value);

    /// <summary>
    /// ライト要求として受理されてしまったリクエストを完了させ、通信の同期を取り戻す。
    /// opcode 探索でのみ使う。
    /// </summary>
    void CompletePendingWrite(int size, byte filler = 0xFF);
}
