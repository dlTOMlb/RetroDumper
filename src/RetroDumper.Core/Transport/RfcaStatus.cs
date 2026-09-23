namespace RetroDumper.Core.Transport;

/// <summary>状態要求（opcode 0x06）に対する 12 バイト応答。</summary>
public readonly struct RfcaStatus
{
    public byte[] Raw { get; }

    public RfcaStatus(byte[] raw) => Raw = raw;

    /// <summary>
    /// アダプタから正規の 12 バイト応答が返ってきたか。
    ///
    /// これが false のときは「カセットが挿さっていない」のではなく
    /// 「アダプタと会話できていない」。両者はまったく別の問題なので
    /// 必ず区別すること。
    /// </summary>
    public bool HasResponse => Raw.Length >= 9;

    /// <summary>応答が 1 バイトも返ってこなかった。</summary>
    public bool IsSilent => Raw.Length == 0;

    /// <summary>
    /// 応答 byte[8]。挿入されているカートリッジの種別。
    /// <see cref="HasResponse"/> が false のときは意味を持たない。
    /// </summary>
    public CartridgeKind Kind =>
        HasResponse ? (CartridgeKind)Raw[8] : CartridgeKind.None;

    /// <summary>先頭 4 バイトが 0xFFFFFFFF ならアダプタ側がエラー状態。</summary>
    public bool IsFault =>
        Raw.Length >= 4 && Raw[0] == 0xFF && Raw[1] == 0xFF && Raw[2] == 0xFF && Raw[3] == 0xFF;

    /// <summary>画面に出す状態の説明。応答なしと未接続をはっきり分ける。</summary>
    public string Describe()
    {
        if (IsSilent)
            return "応答なし（アダプタから返信がありません）";

        if (!HasResponse)
            return $"応答不完全（{Raw.Length} バイトしか返りません）";

        return Kind.ToDisplayName();
    }

    public override string ToString() =>
        Raw.Length == 0 ? "(応答なし)" : Convert.ToHexString(Raw);
}
