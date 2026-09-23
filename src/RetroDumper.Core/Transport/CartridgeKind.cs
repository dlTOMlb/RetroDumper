namespace RetroDumper.Core.Transport;

/// <summary>
/// RFCA の状態応答 byte[8] が示すカートリッジ種別。
/// 値は実機通信のキャプチャから判明しているもののみ確定。
/// 0x04 / 0x06 は未確認（GBA がどちらかである可能性が高い）。
/// </summary>
public enum CartridgeKind : byte
{
    None = 0xFF,
    SuperFamicom = 0x01,
    MegaDrive = 0x02,
    Famicom = 0x03,
    /// <summary>未確認。GBA ではないことが実機で判明済み。</summary>
    Unknown04 = 0x04,
    GameBoy = 0x05,
    /// <summary>ゲームボーイアドバンス。実機で確認済み。</summary>
    GameBoyAdvance = 0x06,
    MarkIIIOrGameGear = 0x07,
    PcEngineHuCard = 0x08,
}

public static class CartridgeKindExtensions
{
    public static string ToDisplayName(this CartridgeKind kind) => kind switch
    {
        CartridgeKind.None => "未接続",
        CartridgeKind.SuperFamicom => "スーパーファミコン / SNES",
        CartridgeKind.MegaDrive => "メガドライブ / Genesis",
        CartridgeKind.Famicom => "ファミコン / NES",
        CartridgeKind.GameBoy => "ゲームボーイ / GBC",
        CartridgeKind.MarkIIIOrGameGear => "マークIII / ゲームギア",
        CartridgeKind.PcEngineHuCard => "PCエンジン Huカード",
        CartridgeKind.GameBoyAdvance => "ゲームボーイアドバンス",
        CartridgeKind.Unknown04 => "未確認種別 0x04",
        _ => $"未知の種別 0x{(byte)kind:X2}",
    };

    public static bool IsConnected(this CartridgeKind kind) => kind != CartridgeKind.None;
}
