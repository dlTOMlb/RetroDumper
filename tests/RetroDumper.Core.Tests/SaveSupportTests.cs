using RetroDumper.Core.Transport;
using Xunit;

namespace RetroDumper.Core.Tests;

/// <summary>
/// セーブの読み書きを画面に出してよい機種。
///
/// **判断の分かれ目は「未検証だから念のため」ではない。**
/// 書き込みの安全網は、書いたあとの読み戻しによる 1 バイト単位の照合だけ。
/// スーパーファミコンではセーブ RAM の窓でバスが浮いており、
/// 読みが「直前にバスへ流れた最後の 1 バイト」を返す。
/// 一様なデータを書くと、1 バイトも届いていないのに照合が通る。
/// 安全網が外れた状態で「書けました」と報告してしまう。
///
/// ここを緩めるのは、その機種を実機で確かめたときだけ。
/// </summary>
public sealed class SaveSupportTests
{
    [Theory]
    [InlineData(CartridgeKind.GameBoy)]
    [InlineData(CartridgeKind.GameBoyAdvance)]
    public void 実機で確かめた機種は出す(CartridgeKind kind)
        => Assert.True(SaveSupport.IsVerified(kind));

    [Theory]
    [InlineData(CartridgeKind.SuperFamicom)]
    [InlineData(CartridgeKind.MegaDrive)]
    [InlineData(CartridgeKind.MarkIIIOrGameGear)]
    [InlineData(CartridgeKind.Famicom)]
    [InlineData(CartridgeKind.PcEngineHuCard)]
    public void 確かめていない機種は出さない(CartridgeKind kind)
        => Assert.False(SaveSupport.IsVerified(kind));

    /// <summary>断る理由は機種の名前を含めて述べること。黙って何もしないのは困る。</summary>
    [Theory]
    [InlineData(CartridgeKind.SuperFamicom)]
    [InlineData(CartridgeKind.MegaDrive)]
    [InlineData(CartridgeKind.MarkIIIOrGameGear)]
    public void 断る理由を述べる(CartridgeKind kind)
    {
        string reason = SaveSupport.ReasonNotVerified(kind);

        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.EndsWith("。", reason);
    }

    /// <summary>
    /// **書き込みの番地の門は、機種を絞っても緩めない。**
    ///
    /// SaveSupport は画面に出すかどうかの判断で、
    /// SaveMemory は「セーブ領域の外へは書かない」という別の守り。
    /// 画面から出さなくなったからといって、後者を外してはいけない。
    /// </summary>
    [Fact]
    public void 番地の門は残す()
    {
        // SFC の SRAM 窓は通る（実験用の経路が塞がらないこと）
        Assert.True(SaveMemory.IsSaveWrite(
            CartridgeKind.SuperFamicom, RfcaOpcode.SnesWrite, 0x700000));

        // ROM 領域は通さない
        Assert.False(SaveMemory.IsSaveWrite(
            CartridgeKind.SuperFamicom, RfcaOpcode.SnesWrite, 0x000000));
    }
}
