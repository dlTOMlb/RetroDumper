namespace RetroDumper.Core.Transport;

/// <summary>
/// RFCA のコマンド opcode。
///
/// 2026-09-23、動作実績のある RetroFreakDumper.exe（.NET アセンブリ）を
/// 逆コンパイルして全表を確定した。値は同ソースの
/// RetroFreak.CommandClass の各クラスが基底に渡している番号そのもの。
/// 以前は実機での総当たり探索に頼っており、いくつか誤った値を使っていた。
///
/// フレームは用途ごとに長さが違う:
///   リード／ライト要求 (20B) : [u32 opcode][u32 8][u32 size][u32 addr][u32 size]
///   状態要求 (12B)           : [u32 0x06][u32 0][u32 4]
///   0x01 / 0x36 (12B)        : [u32 opcode][u32 0][u32 応答長]
///   0x04 (16B)               : [u32 0x04][u32 4][u32 0][u32 param]
///   0x05 (12B)               : [u32 0x05][u32 0][u32 0]
///   0x2F (20B)               : [u32 0x2F][u32 8][u32 0][u32 0][u32 param]
/// </summary>
public static class RfcaOpcode
{
    // ==================================================================
    // 制御コマンド
    // ==================================================================

    /// <summary>デバイス識別。応答 4 バイト（0A 00 37 13 ＝ USB の PID 0x1337）。</summary>
    public const uint Identify = 0x01;

    /// <summary>
    /// スロットの選択・給電。応答 8 バイト。
    ///
    /// param はスロット指定。GBA は 0、SFC は 1。
    /// 吸い出しの最初と最後（終了時は 0）に送る。
    /// </summary>
    public const uint SlotSelect = 0x04;

    /// <summary>
    /// スロットの確定・リセット解除。応答 8 バイト。
    /// <see cref="SlotSelect"/> の直後に送る。
    /// </summary>
    public const uint SlotCommit = 0x05;

    /// <summary>状態要求。応答 12 バイトの byte[8] がカートリッジ種別。</summary>
    public const uint Status = 0x06;

    /// <summary>
    /// SFC 専用のウェイクアップ。param はスロット指定（SFC は 1、終了時 0）。
    ///
    /// **GBA では使わない。** GBA スロットは 0x04 / 0x05 だけで有効化される。
    /// GBA 挿入時に 0x2F を送ると param 0〜255 のすべてが拒否されるが、
    /// これは不要なコマンドなので正しい挙動。
    /// </summary>
    public const uint SlotWakeup = 0x2F;

    /// <summary>応答 20 バイトの情報取得。用途未調査。</summary>
    public const uint Info36 = 0x36;

    // ==================================================================
    // リード／ライト（スロットごとに別番号）
    // ==================================================================

    /// <summary>スーパーファミコン バスリード。</summary>
    public const uint SnesRead = 0x07;

    /// <summary>スーパーファミコン バスライト。</summary>
    public const uint SnesWrite = 0x08;

    /// <summary>SFC 拡張リード／ライト（SF メモリなど）。</summary>
    public const uint SnesExRead = 0x09;
    public const uint SnesExWrite = 0x0A;
    public const uint SnesEx2Read = 0x0B;
    public const uint SnesEx2Write = 0x0C;

    /// <summary>メガドライブ バスリード／ライト。</summary>
    public const uint MegaDriveRead = 0x0D;
    public const uint MegaDriveWrite = 0x0E;
    public const uint MegaDriveFramRead = 0x0F;
    public const uint MegaDriveFramWrite = 0x10;

    /// <summary>ファミコン CPU 空間リード／ライト、PPU リード。</summary>
    public const uint NesCpuRead = 0x11;
    public const uint NesCpuWrite = 0x12;
    public const uint NesPpuRead = 0x13;
    public const uint NesCpuExRead = 0x14;
    public const uint NesCpuExWrite = 0x15;

    /// <summary>ゲームボーイ / GBC バスリード／ライト。</summary>
    public const uint GameBoyRead = 0x1B;
    public const uint GameBoyWrite = 0x1C;
    public const uint GameBoyMbc2ExRamRead = 0x1D;
    public const uint GameBoyMbc2ExRamWrite = 0x1E;

    /// <summary>
    /// **GBA ROM リード。**
    ///
    /// 以前は 0x20 を ROM リードだと誤認していた。0x20 は SRAM のリードで、
    /// セーブが空なら全バイト 0xFF を返す。引数も検証しないため、
    /// 「受理されるのに 0xFF しか返らない」という症状になっていた。
    /// </summary>
    public const uint GbaRomRead = 0x1F;

    /// <summary>GBA SRAM リード／ライト。</summary>
    public const uint GbaSramRead = 0x20;
    public const uint GbaSramWrite = 0x21;

    /// <summary>GBA EEPROM リード／ライト。</summary>
    public const uint GbaEepromRead = 0x24;
    public const uint GbaEepromWrite = 0x25;

    /// <summary>
    /// フラッシュのメーカー ID / デバイス ID を 2 バイト返す。
    /// 要求は 12 バイトで、ヘッダ欄は 0x00、サイズ欄は 2。
    /// 書き込む前に、対応している石かを確かめるために使う。
    /// </summary>
    public const uint GbaFlashId = 0x26;

    /// <summary>GBA フラッシュ リード／ライト。</summary>
    public const uint GbaFlashRead = 0x27;
    public const uint GbaFlashWrite = 0x28;

    /// <summary>マークIII / ゲームギア バスリード／ライト。</summary>
    public const uint SmsRead = 0x2B;
    public const uint SmsWrite = 0x2C;

    /// <summary>PC エンジン Hu カードリード／ライト。</summary>
    public const uint PcEngineRead = 0x2D;
    public const uint PcEngineWrite = 0x2E;

    /// <summary>メガドライブ EEPROM、ファミコン EEPROM。</summary>
    public const uint MegaDriveEepromRead = 0x31;
    public const uint MegaDriveEepromWrite = 0x32;
    public const uint NesEepromRead = 0x33;
    public const uint NesEepromWrite = 0x34;

    // ==================================================================
    // フレームの共通値
    // ==================================================================

    /// <summary>
    /// リード／ライト要求のフレーム 2 つ目のフィールド。
    ///
    /// **全スロット共通で 8。** 以前 GBA だけ 0x00 が必要だと考えていたのは
    /// 誤りで、実機でも 0x00〜0x0F のすべてが受理される。
    /// </summary>
    public const uint RequestHeaderField = 0x08;

    /// <summary>GBA の 1 リクエストあたりの転送サイズ。32KB。</summary>
    public const int GbaBlockSize = 32768;

    /// <summary>GBA ROM の最大容量。32MB。</summary>
    public const int GbaMaxRomSize = 32 * 1024 * 1024;

    // ==================================================================
    // 探索で差し替えられるようにしてあるもの
    // ==================================================================

    /// <summary>GBA ROM リード。既定は確定値。</summary>
    public static uint? GbaRead { get; set; } = GbaRomRead;
}
