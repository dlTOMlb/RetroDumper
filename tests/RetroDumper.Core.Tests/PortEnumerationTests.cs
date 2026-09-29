using RetroDumper.Core.Transport;
using Xunit;

namespace RetroDumper.Core.Tests;

/// <summary>
/// 画面と自動検出に出すポートの絞り込み。
///
/// macOS の SerialPort.GetPortNames() は、アダプタとは無関係な擬似ポートまで返す。
/// 実測では Bluetooth-Incoming-Port、debug-console、wlan-debug の 3 つが常に並ぶ。
///
/// 自動検出は全ポートへ状態要求を投げるため、Bluetooth のポートを開くと
/// 接続待ちで止まる。アダプタを探す前に無関係なポートで待たされる。
/// </summary>
public sealed class PortEnumerationTests
{
    [Theory]
    [InlineData("/dev/tty.Bluetooth-Incoming-Port")]
    [InlineData("/dev/cu.Bluetooth-Incoming-Port")]
    [InlineData("/dev/tty.debug-console")]
    [InlineData("/dev/cu.debug-console")]
    [InlineData("/dev/tty.wlan-debug")]
    [InlineData("/dev/cu.wlan-debug")]
    public void macOSの擬似ポートは除外する(string name)
        => Assert.False(RfcaLink.IsCandidatePort(name));

    /// <summary>
    /// Windows は名前から中身を判断できないので、COM は素通しにする。
    /// 実機で確認済みの経路を変えない。
    /// </summary>
    [Theory]
    [InlineData("COM1")]
    [InlineData("COM3")]
    [InlineData("COM12")]
    public void WindowsのCOMポートは通す(string name)
        => Assert.True(RfcaLink.IsCandidatePort(name));

    /// <summary>アダプタが CDC として見えた場合の名前。これは通さなければならない。</summary>
    [Theory]
    [InlineData("/dev/tty.usbmodem0123456789AB1")]
    [InlineData("/dev/cu.usbmodem0123456789AB1")]
    [InlineData("/dev/tty.usbserial-1420")]
    public void USBシリアルのポートは通す(string name)
        => Assert.True(RfcaLink.IsCandidatePort(name));

    /// <summary>大文字小文字は問わない。</summary>
    [Theory]
    [InlineData("/dev/tty.bluetooth-incoming-port")]
    [InlineData("/dev/tty.BLUETOOTH-Incoming-Port")]
    public void 除外は大文字小文字を区別しない(string name)
        => Assert.False(RfcaLink.IsCandidatePort(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空の名前は除外する(string? name)
        => Assert.False(RfcaLink.IsCandidatePort(name));
}
