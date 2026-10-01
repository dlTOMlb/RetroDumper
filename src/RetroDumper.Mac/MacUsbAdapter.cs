using RetroDumper.Core.Transport;

namespace RetroDumper.Mac;

/// <summary>
/// macOS の USB 経路への入口。
///
/// <see cref="MacUsbRfcaChannel"/> 自体は <c>[SupportedOSPlatform("macos")]</c> が
/// 付いているため、そのまま呼ぶと Windows 向けのビルドで CA1416 になる。
/// 呼び出し側に <c>OperatingSystem.IsMacOS()</c> を書かせずに済むよう、
/// プラットフォーム判定をここに閉じ込める。
///
/// **Windows では IsPresent が常に false を返し、Create は呼ばれない。**
/// </summary>
public static class MacUsbAdapter
{
    /// <summary>画面のポート一覧に出す名前。</summary>
    public const string EntryName = "USB (RetroFreak PCB-B)";

    /// <summary>
    /// USB にアダプタが見えているか。
    ///
    /// 探索は VID/PID なので、USB ハブ経由でも直結でも同じように見つかる。
    /// </summary>
    public static bool IsPresent()
        => OperatingSystem.IsMacOS() && MacUsbRfcaChannel.IsPresent();

    /// <summary>一覧で選ばれた名前が USB 経路のものか。</summary>
    public static bool Matches(string? entry)
        => OperatingSystem.IsMacOS() && entry == EntryName;

    /// <summary>USB 経路の通り道を作る。</summary>
    public static IRfcaByteChannel Create()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("USB 経路は macOS 専用です。");

        return new MacUsbRfcaChannel();
    }
}
