using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace RetroDumper.Ui;

/// <summary>ダイアログの見た目。警告と危険で色を変える。</summary>
public enum DialogKind
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// 問いかけと、ファイルの選択。
///
/// Avalonia には MessageBox がないので、必要な分だけ自前で持つ。
/// 外部パッケージを増やすより、90 行書くほうが見通しがよい。
///
/// **WPF と違い、すべて非同期**。呼ぶ側は await する必要がある。
/// 同期の MessageBox は UI スレッドを止めて結果を返せるが、
/// Avalonia のダイアログはそれをしない。
/// </summary>
public static class Dialogs
{
    public static Task Info(Window owner, string title, string message)
        => Show(owner, title, message, DialogKind.Info, ["OK"]);

    public static Task Warn(Window owner, string title, string message)
        => Show(owner, title, message, DialogKind.Warning, ["OK"]);

    public static Task Error(Window owner, string title, string message)
        => Show(owner, title, message, DialogKind.Error, ["OK"]);

    /// <summary>「はい」を選んだときだけ true。</summary>
    public static async Task<bool> Confirm(
        Window owner, string title, string message, DialogKind kind = DialogKind.Warning)
        => await Show(owner, title, message, kind, ["はい", "いいえ"]) == 0;

    private static async Task<int> Show(
        Window owner, string title, string message, DialogKind kind, string[] buttons)
    {
        var (border, back) = kind switch
        {
            DialogKind.Error => (Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0xFE, 0xF2, 0xF2)),
            DialogKind.Warning => (Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromRgb(0xFF, 0xFB, 0xEB)),
            _ => (Color.FromRgb(0x94, 0xA3, 0xB8), Color.FromRgb(0xF8, 0xFA, 0xFC)),
        };

        int answer = buttons.Length - 1;   // 閉じられたら最後の選択肢とみなす

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.Height,
            Width = 520,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var button = new Button
            {
                Content = buttons[i],
                MinWidth = 88,
                IsDefault = i == 0,
                IsCancel = i == buttons.Length - 1,
            };

            button.Click += (_, _) =>
            {
                answer = index;
                dialog.Close();
            };

            row.Children.Add(button);
        }

        dialog.Content = new Border
        {
            Background = new SolidColorBrush(back),
            BorderBrush = new SolidColorBrush(border),
            BorderThickness = new Avalonia.Thickness(0, 4, 0, 0),
            Padding = new Avalonia.Thickness(20),
            Child = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    row,
                },
            },
        };

        await dialog.ShowDialog(owner);

        return answer;
    }

    // ------------------------------------------------------------------
    // ファイルの選択
    // ------------------------------------------------------------------

    /// <summary>保存先を選ばせる。取り消されたら null。</summary>
    public static async Task<string?> SaveFile(
        Window owner, string title, string suggestedName, string extension, string description)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices = [Filter(description, extension), Filter("すべてのファイル", "*")],
        });

        return file?.TryGetLocalPath();
    }

    /// <summary>開くファイルを選ばせる。取り消されたら null。</summary>
    public static async Task<string?> OpenFile(
        Window owner, string title, string extension, string description)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [Filter(description, extension), Filter("すべてのファイル", "*")],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private static FilePickerFileType Filter(string name, string extension)
        => new(name) { Patterns = [extension == "*" ? "*" : $"*.{extension}"] };
}
