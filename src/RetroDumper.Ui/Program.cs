using Avalonia;

namespace RetroDumper.Ui;

internal static class Program
{
    // Avalonia は初期化前に何も触らせない。ここは薄く保つ。
    [STAThread]
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
