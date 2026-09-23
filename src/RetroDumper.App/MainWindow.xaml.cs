using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using RetroDumper.Core.Database;
using RetroDumper.Core.Dumping;
using RetroDumper.Core.Gb;
using RetroDumper.Core.Gba;
using RetroDumper.Core.Nes;
using RetroDumper.Core.Probe;
using RetroDumper.Core.Snes;
using RetroDumper.Core.Transport;
using RetroDumper.Core.Util;

namespace RetroDumper.App;

public partial class MainWindow : Window
{
    private RfcaLink? _link;
    private CancellationTokenSource? _cts;
    private CartridgeInfo? _info;
    private ICartridgeDumper? _activeDumper;
    private bool _writeWarningShown;

    /// <summary>GBA の ROM 先頭アドレス。採取で判明したら設定する。</summary>
    private uint? _gbaRomBase;

    /// <summary>
    /// XAML の解析が終わって全コントロールが揃ったか。
    ///
    /// CheckBox の IsChecked="True" は解析中に Checked を発火させるため、
    /// XAML 上で後ろに書かれたコントロール（LogBox など）はまだ null。
    /// イベントハンドラはこのフラグが立つまで何もしない。
    /// </summary>
    private bool _uiReady;

    private readonly ObservableCollection<KeyValuePair<string, string>> _details = [];
    private readonly ObservableCollection<string> _warnings = [];

    /// <summary>マッパー上書きコンボの中身。null は「自動判定」。</summary>
    private static readonly (string Label, SnesMapper? Value)[] MapperChoices =
    [
        ("自動判定", null),
        ("LoROM", SnesMapper.LoRom),
        ("HiROM", SnesMapper.HiRom),
        ("ExHiROM", SnesMapper.ExHiRom),
        ("LoROM + SA-1", SnesMapper.Sa1),
        ("LoROM + S-DD1", SnesMapper.Sdd1),
        ("HiROM + SPC7110", SnesMapper.Spc7110),
    ];

    /// <summary>ファミコンのマッパー候補。先頭は「自動（データベース）」。</summary>
    private static readonly (string Label, int? Number)[] NesMapperChoices =
    [
        ("自動（総当たりで特定）", null),
        .. NesMapper.All.Select(m => ($"{m.Number}: {m.Name}", (int?)m.Number)),
    ];

    public MainWindow()
    {
        InitializeComponent();

        if (AppVersion is { Length: > 0 } version)
            Title = $"{Title}  {version}";

        InitializeGbaSaveTypes();

        DetailsGrid.ItemsSource = _details;
        WarningsList.ItemsSource = _warnings;

        DumperCombo.ItemsSource = DumperRegistry.Selectable;
        DumperCombo.SelectedIndex = 0;
        DumperCombo.SelectionChanged += (_, _) => UpdateReadiness();

        SnesMapperCombo.ItemsSource = MapperChoices.Select(c => c.Label).ToList();
        SnesMapperCombo.SelectedIndex = 0;

        NesMapperCombo.ItemsSource = NesMapperChoices.Select(c => c.Label).ToList();
        NesMapperCombo.SelectedIndex = 0;

        _uiReady = true;

        RefreshPorts();
        Log("RetroDumper 起動。レトロフリーク カートリッジアダプタを USB で PC に接続してください。");
        Log("アダプタ背面 2 ポートのうち、吸い出し機側（USB ハブでない方）を PC に挿します。");
        Log("書き込み保護: 有効（カートリッジの内容を書き換えるコマンドを送りません）");
    }

    // ==================================================================
    // 接続
    // ==================================================================

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        string? current = PortCombo.SelectedItem as string;
        var ports = RfcaLink.EnumeratePorts().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        PortCombo.ItemsSource = ports;

        if (current is not null && ports.Contains(current))
            PortCombo.SelectedItem = current;
        else if (ports.Count > 0)
            PortCombo.SelectedIndex = ports.Count - 1;

        Log($"利用可能なシリアルポート: {(ports.Count == 0 ? "なし" : string.Join(", ", ports))}");
    }

    private async void AutoDetectPort_Click(object sender, RoutedEventArgs e)
    {
        if (_link is not null)
        {
            Log("先に切断してください。");
            return;
        }

        SetBusy(true);
        AutoDetectPortButton.IsEnabled = false;

        try
        {
            Log("全 COM ポートに状態要求を投げて RFCA を探します…");

            string? found = await Task.Run(() =>
                RfcaLink.FindAdapterPort(line => Dispatcher.BeginInvoke(() => Log(line))));

            RefreshPorts();

            if (found is null)
            {
                Log("RFCA の応答を返すポートが見つかりませんでした。");
                MessageBox.Show(this,
                    "RFCA が応答するシリアルポートが見つかりませんでした。\n\n" +
                    "・アダプタ背面 2 ポートのうち、吸い出し機側を PC に挿していますか\n" +
                    "  （もう一方は USB ハブです）\n" +
                    "・USB 延長ケーブルは通信対応のものですか\n" +
                    "  （充電専用ケーブルでは通信できません）",
                    "ポート自動検出", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            PortCombo.SelectedItem = found;
            Log($"RFCA を {found} で検出しました。接続します。");
            Connect_Click(sender, e);
        }
        finally
        {
            AutoDetectPortButton.IsEnabled = _link is null;
            SetBusy(false);
        }
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_link is not null)
        {
            Disconnect();
            return;
        }

        if (PortCombo.SelectedItem is not string port)
        {
            MessageBox.Show(this, "COM ポートを選択してください。", "RetroDumper",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _link = new RfcaLink(port)
            {
                Trace = message => Dispatcher.BeginInvoke(() =>
                {
                    if (VerboseTraceCheck.IsChecked == true) Log(message);
                }),
            };

            ApplyWriteProtection();

            Log($"{port} に接続しました ({RfcaLink.BaudRate} bps)。");
            ConnectButton.Content = "切断";
            SetConnectedState(true);
            DetectCartridge();
        }
        catch (Exception ex)
        {
            _link = null;
            Log($"接続に失敗しました: {ex.Message}");
            MessageBox.Show(this, $"{port} を開けませんでした。\n\n{ex.Message}", "RetroDumper",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Disconnect()
    {
        _cts?.Cancel();
        _link?.Dispose();
        _link = null;
        _info = null;

        ConnectButton.Content = "接続";
        CartKindText.Text = "—";
        CartKindText.Foreground = Brushes.Black;
        SetConnectedState(false);
        Log("切断しました。");
    }

    private void SetConnectedState(bool connected)
    {
        DetectButton.IsEnabled = connected;
        IdentifyButton.IsEnabled = connected;
        DiagnoseButton.IsEnabled = connected;
        SlotComparisonButton.IsEnabled = connected;
        NesWriteProbeButton.IsEnabled = connected;
        GbaHeadSampleButton.IsEnabled = connected;
        DumpButton.IsEnabled = connected && _info is not null;
    }

    // ==================================================================
    // 書き込み保護
    // ==================================================================

    private void WriteProtect_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;

        bool locked = WriteProtectCheck.IsChecked == true;

        WriteProtectBadge.Background = locked
            ? new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7))
            : new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2));
        WriteProtectBadge.BorderBrush = locked
            ? new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A))
            : new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));

        if (!locked && !_writeWarningShown)
        {
            _writeWarningShown = true;
            MessageBox.Show(this,
                "書き込み保護を解除しました。\n\n" +
                "これ以降、バンク切り替えでカートリッジへの書き込みが発生する可能性があります。\n\n" +
                "ゲームボーイ・マークIII/ゲームギアの吸い出しには必要ですが、\n" +
                "GBA・SFC・メガドライブの吸い出しには不要です。",
                "書き込み保護の解除", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        ApplyWriteProtection();
        Log(locked
            ? "書き込み保護: 有効（カートリッジの内容を書き換えるコマンドを送りません）"
            : "書き込み保護: 解除");
    }

    private void ApplyWriteProtection()
    {
        if (_link is not null)
            _link.AllowWrites = WriteProtectCheck.IsChecked != true;
    }

    // ==================================================================
    // 種別検出
    // ==================================================================

    private void Detect_Click(object sender, RoutedEventArgs e) => DetectCartridge();

    private void DetectCartridge()
    {
        if (_link is null) return;

        try
        {
            _link.InvalidateWake();
            var status = _link.GetStatusWithRetry();

            CartKindText.Text = status.Describe();
            Log($"状態応答: {status} → {status.Describe()}");

            if (!status.HasResponse)
            {
                CartKindText.Foreground = Brushes.Crimson;
                Log("アダプタから応答がありません。次を確認してください:");
                Log("  1. COM ポートが正しいか（「ポート自動検出」を試してください）");
                Log("  2. アダプタ背面 2 ポートのうち、吸い出し機側を PC に挿しているか");
                Log("  3. USB 延長ケーブルが通信対応か（充電専用では通信できません）");
                return;
            }

            CartKindText.Foreground = status.Kind.IsConnected() ? Brushes.Green : Brushes.DarkOrange;

            if (!status.Kind.IsConnected())
            {
                Log("アダプタは応答していますが、カセットを検出していません。");
                Log("カセットの抜き差しと端子の清掃を試してください。");
                return;
            }

            if (AutoSelectCheck.IsChecked == true)
            {
                var dumper = DumperRegistry.ForKind(status.Kind);

                if (dumper is not null)
                {
                    DumperCombo.SelectedItem = dumper;
                    Log($"機種を自動選択: {dumper.Name}");
                }
                else
                {
                    Log($"種別 0x{(byte)status.Kind:X2} に対応する実装がありません。手動で選んでください。");
                }
            }

            UpdateReadiness();
        }
        catch (Exception ex)
        {
            Log($"種別取得に失敗しました: {ex.Message}");
        }
    }

    private void AutoSelect_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;

        DumperCombo.IsEnabled = AutoSelectCheck.IsChecked != true;
        if (AutoSelectCheck.IsChecked == true) DetectCartridge();
    }

    private void UpdateReadiness()
    {
        if (DumperCombo?.SelectedItem is not ICartridgeDumper dumper)
        {
            if (ReadinessText is not null) ReadinessText.Text = "";
            return;
        }

        ReadinessText.Text = dumper.ReadinessDetail;
        ReadinessText.Visibility = string.IsNullOrEmpty(dumper.ReadinessDetail)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    // ==================================================================
    // 識別・吸い出し
    // ==================================================================

    private async void Identify_Click(object sender, RoutedEventArgs e)
    {
        if (_link is null) return;
        if (DumperCombo.SelectedItem is not ICartridgeDumper dumper) return;
        if (BuildOptions() is not DumpOptions options) return;

        DetectCartridge();
        if (DumperCombo.SelectedItem is ICartridgeDumper refreshed) dumper = refreshed;

        SetBusy(true);

        try
        {
            var link = _link;
            var info = await Task.Run(() => dumper.Identify(link, options));

            _info = info;
            _activeDumper = dumper;

            ShowCartridgeInfo(info);
            DumpButton.IsEnabled = true;
            Log($"識別完了: {info.Title} / {info.Mapper} / {FormatBytes(info.RomSize)}");
        }
        catch (Exception ex)
        {
            Log($"識別に失敗しました: {ex.Message}");
            MessageBox.Show(this, ex.Message, "識別失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowCartridgeInfo(CartridgeInfo info)
    {
        CartTitleText.Text = !string.IsNullOrWhiteSpace(info.Title)
            ? info.Title
            : info.Kind == CartridgeKind.Famicom
                // ファミコンのカセットはタイトルを持たない。読み出しの失敗ではない。
                ? "（ファミコンはタイトルを持ちません／吸い出し後に特定します）"
                : "(タイトルなし)";

        CartSummaryText.Text =
            $"{info.Mapper}    ROM {FormatBytes(info.RomSize)}" +
            (info.SaveSize > 0 ? $"    セーブ {FormatBytes(info.SaveSize)}" : "");

        _details.Clear();
        foreach (var detail in info.Details) _details.Add(detail);

        _warnings.Clear();
        foreach (string warning in info.Warnings) _warnings.Add(warning);
    }

    private async void Dump_Click(object sender, RoutedEventArgs e)
    {
        if (_link is null || _info is null || _activeDumper is null) return;
        if (BuildOptions() is not DumpOptions options) return;

        // 保存先は吸い出したあとに聞く。
        // ファミコンのカセットはタイトルを持たないため、吸い出して
        // No-Intro DAT と照合するまで正しい名前が分からない。
        _cts = new CancellationTokenSource();
        SetBusy(true);
        CancelButton.IsEnabled = true;

        var started = DateTime.Now;

        var progress = new Progress<DumpProgress>(p =>
        {
            DumpProgressBar.Value = p.Ratio;
            ProgressText.Text =
                $"{p.Stage}  {FormatBytes(p.BytesDone)} / {FormatBytes(p.BytesTotal)}  ({p.Ratio:P1})";
        });

        try
        {
            Log($"吸い出し開始: {_info.Title} ({FormatBytes(_info.RomSize)})");

            var link = _link;
            var dumper = _activeDumper;
            var info = _info;

            var result = await Task.Run(() =>
                dumper.Dump(link, info, options, progress, _cts.Token));

            Log($"CRC32: {result.Crc32:X8}");

            if (result.ChecksumOk is bool ok)
                Log($"チェックサム検証: {(ok ? "一致" : "不一致")} — {result.ChecksumDetail}");

            // GBA はセーブ装置の種類を ROM の中の目印から判定するので、
            // 読めたものを覚えておく。もう一度読ませずに済む。
            if (info.Kind == CartridgeKind.GameBoyAdvance) _lastGbaRom = result.Rom;

            // 照合してからファイル名を決める。
            var identified = ReportNoIntroMatch(result.Rom, result.Crc32);

            // 照合できなかったときは、取りこぼさないよう控えを残す。
            // 吸い出しには時間がかかるうえ、原因を調べるには現物が要る。
            // 保存ダイアログでどう選ばれても、これは手元に残る。
            if (identified is null && result.ChecksumOk == false)
                SaveRecoveryCopy(result.Rom, info.RomExtension);

            string suggested = identified is not null
                ? FileNaming.MakeRomFileName(identified.GameName, info.RomExtension)
                : MakeFileName(info);

            // ここが空になると保存ダイアログのファイル名欄が空で開く。
            // 経路が増えたので、値をログに残して追えるようにしておく。
            if (string.IsNullOrWhiteSpace(suggested))
            {
                Log($"警告: ファイル名を組み立てられませんでした" +
                    $"（タイトル「{info.Title}」拡張子「{info.RomExtension}」）。既定の名前を使います。");

                suggested = "cartridge" + (info.RomExtension is { Length: > 0 } ext ? ext : ".bin");
            }

            Log($"保存ダイアログの既定名: {suggested}");

            var save = new SaveFileDialog
            {
                Title = "ROM の保存先",
                FileName = suggested,
                Filter = $"ROM ファイル (*{info.RomExtension})|*{info.RomExtension}|すべてのファイル (*.*)|*.*",
                AddExtension = true,

                // DefaultExt はピリオドを含めない形式。
                // ".nes" のように渡すと拡張子の補完が正しく働かない。
                DefaultExt = info.RomExtension.TrimStart('.'),
            };

            // 保存をやめると吸い出した内容は失われる。
            // 時間をかけて読んだものなので、取り違えでないことを一度確かめる。
            while (save.ShowDialog(this) != true)
            {
                var discard = MessageBox.Show(this,
                    "保存をやめると、吸い出した内容は破棄されます。\n" +
                    "もう一度吸い出すには読み直しが必要です。\n\n" +
                    "破棄してよろしいですか？",
                    "保存の確認", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (discard == MessageBoxResult.Yes)
                {
                    Log("保存を中止しました。吸い出した内容は破棄されます。");
                    ProgressText.Text = "保存せず終了";
                    return;
                }
            }

            await File.WriteAllBytesAsync(save.FileName, result.Rom);
            Log($"ROM を保存しました: {save.FileName} ({FormatBytes(result.Rom.LongLength)})");

            if (result.Save is { Length: > 0 })
            {
                string savePath = Path.ChangeExtension(save.FileName, ".srm");
                await File.WriteAllBytesAsync(savePath, result.Save);
                Log($"セーブデータを保存しました: {savePath} ({FormatBytes(result.Save.LongLength)})");
            }

            ShowCartridgeInfo(result.Info);

            var elapsed = DateTime.Now - started;
            ProgressText.Text = $"完了 ({elapsed.TotalSeconds:0.0} 秒)";
            Log($"完了。所要時間 {elapsed.TotalSeconds:0.0} 秒 / " +
                $"平均 {result.Rom.LongLength / Math.Max(1.0, elapsed.TotalSeconds) / 1024.0:0.0} KB/s");

            if (result.ChecksumOk == false)
            {
                MessageBox.Show(this,
                    $"吸い出しは完了しましたが、チェックサムが一致しません。\n\n{result.ChecksumDetail}",
                    "チェックサム不一致", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            Log("吸い出しを中止しました。");
            ProgressText.Text = "中止";
        }
        catch (RfcaWriteBlockedException)
        {
            Log("書き込み保護により吸い出しを中止しました。");
            ProgressText.Text = "中止（書き込み保護）";
            MessageBox.Show(this,
                "この機種の吸い出しにはバンク切り替えのための書き込みが必要です。\n\n" +
                "カートリッジには何も書き込まずに中止しました。\n" +
                "続けるには上部の「カートリッジへの書き込みを禁止」を外してください。\n\n" +
                "※ GBA・SFC・メガドライブの吸い出しに書き込みは不要です。",
                "書き込み保護", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"吸い出しに失敗しました: {ex.Message}");
            ProgressText.Text = "失敗";
            MessageBox.Show(this, ex.Message, "吸い出し失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            CancelButton.IsEnabled = false;
            SetBusy(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    /// <summary>先頭が "NES" なら iNES ヘッダ付き。</summary>
    private static bool IsINesFile(byte[] rom)
        => rom.Length > 16
           && rom[0] == 'N' && rom[1] == 'E' && rom[2] == 'S' && rom[3] == 0x1A;

    /// <summary>
    /// 吸い出した ROM を No-Intro の DAT と照合する。
    ///
    /// DAT は同梱していない。利用者が DataBase フォルダに置いたものを読む。
    /// 無ければ何も言わずに黙っている（機能の必須要件ではない）。
    ///
    /// 一致すれば「正規のダンプと同一」と言い切れる。
    /// 一致しないこと自体は失敗を意味しない（未収録・リビジョン違い・
    /// 容量判定のずれなど理由はいろいろある）。
    /// </summary>
    private NoIntroEntry? ReportNoIntroMatch(byte[] rom, uint crc32)
    {
        var db = NoIntroDatabase.Load(log: null);

        if (db.IsEmpty)
        {
            Log("No-Intro DAT が読み込めませんでした。照合を省略します。");
            return null;
        }

        var match = db.Match(rom, rom.Length);

        if (match is not null)
        {
            Log($"No-Intro 一致: {match.GameName}");
            Log($"  {match.RomName} / {FormatBytes(match.Size)} / CRC32 {match.Crc32}");
            return match;
        }

        // ファミコンは iNES ヘッダ (16 バイト) を付けて出力している。
        // No-Intro には Headered と Headerless の 2 種類があり、
        // ヘッダのミラーリング指定などはカセットから読めず推測で埋めているため、
        // Headered とは PRG/CHR が正しくても一致しないことがある。
        // ROM 本体だけを Headerless の DAT と照合する。
        if (IsINesFile(rom))
        {
            var body = db.Match(rom.AsSpan(16), rom.Length - 16);

            if (body is not null)
            {
                Log($"No-Intro 一致 (Headerless): {body.GameName}");
                Log($"  {body.RomName} / {FormatBytes(body.Size)} / CRC32 {body.Crc32}");
                Log("  ROM 本体は正しく吸い出せています（iNES ヘッダは当アプリが付けたものです）。");
                return body;
            }
        }

        Log($"No-Intro 照合: 一致なし（CRC32 {crc32:X8} / {db.EntryCount} 件と比較）。");

        // 同じ CRC32 が無くても、容量だけ合う候補を挙げると原因の見当がつく。
        var sameCrc = db.FindByCrc(crc32);
        if (sameCrc.Count > 0)
            Log($"  CRC32 は一致しますが MD5/SHA-1 が違います: {sameCrc[0].GameName}");

        return null;
    }


    private DumpOptions? BuildOptions()
    {
        if (!int.TryParse(ChunkSizeBox.Text.Trim(), out int chunk) || chunk <= 0)
        {
            MessageBox.Show(this, "転送サイズには正の整数を入れてください。", "設定",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        if (!int.TryParse(RetryBox.Text.Trim(), out int retry) || retry < 0)
        {
            MessageBox.Show(this, "リトライ回数には 0 以上の整数を入れてください。", "設定",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        long? romSizeOverride = null;

        if (OverrideSizeCheck.IsChecked == true)
        {
            if (!double.TryParse(RomSizeBox.Text.Trim(), out double amount) || amount <= 0)
            {
                MessageBox.Show(this, "ROM サイズには正の数を入れてください。", "設定",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            romSizeOverride = RomSizeUnitCombo.SelectedIndex switch
            {
                1 => (long)(amount * 1024),                 // KB
                2 => (long)(amount * 1024 * 1024 / 8),      // Mbit
                _ => (long)(amount * 1024 * 1024),          // MB
            };
        }

        return new DumpOptions
        {
            ChunkSize = chunk,
            RetryCount = retry,
            RomSizeOverride = romSizeOverride,
            SnesMapperOverride = MapperChoices[Math.Max(0, SnesMapperCombo.SelectedIndex)].Value,
            ForceMmcInit = ForceMmcCheck.IsChecked == true,
            IncludeSaveRam = IncludeSaveCheck.IsChecked == true,
            VerifyChecksum = VerifyChecksumCheck.IsChecked == true,
            GbaRomBase = _gbaRomBase,
            NesMapperOverride = NesMapperChoices[Math.Max(0, NesMapperCombo.SelectedIndex)].Number,
            NesPrgSize = ParseKilobytes(NesPrgSizeBox.Text),
            NesChrSize = ParseKilobytes(NesChrSizeBox.Text),
        };
    }

    /// <summary>KB 表記の入力をバイト数に直す。空欄や不正なら null。</summary>
    private static long? ParseKilobytes(string text)
        => long.TryParse((text ?? "").Trim(), out long kb) && kb >= 0 ? kb * 1024 : null;

    private void OverrideSize_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;

        bool on = OverrideSizeCheck.IsChecked == true;
        RomSizeBox.IsEnabled = on;
        RomSizeUnitCombo.IsEnabled = on;
    }

    // ==================================================================
    // 診断
    // ==================================================================

    private async void Diagnose_Click(object sender, RoutedEventArgs e)
    {
        if (_link is null) return;

        SetBusy(true);

        try
        {
            Log("SFC 診断ダンプを開始します（読み出しのみ）…");

            var link = _link;
            string contents = await Task.Run(() =>
                SnesDiagnostics.Run(link, line => Dispatcher.BeginInvoke(() => Log(line))));

            string path = Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath) ?? ".",
                $"sfc-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            await File.WriteAllTextAsync(path, contents);
            Log($"診断結果を保存しました: {path}");

            MessageBox.Show(this, $"診断結果を保存しました。\n\n{path}",
                "SFC 診断ダンプ", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"診断に失敗しました: {ex.Message}");
            MessageBox.Show(this, ex.Message, "診断失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// 状態要求と判明済みリードを記録する。
    /// カートリッジを差し替えながら繰り返し押して、1 つのファイルで見比べる。
    /// </summary>
    /// <summary>
    /// マッパーへの書き込みが実際に届いているかを測る。
    ///
    /// ワルキューレの冒険で、PRG は正しく読めているのに、どの手順で
    /// バンクを切り替えても内容が変わらないことが分かった。アダプタは
    /// 受理応答を返すので、送れていないことがログからは見えない。
    /// 読み戻して比べるしか確かめようがない。
    /// </summary>
    private async void NesWriteProbe_Click(object sender, RoutedEventArgs e)
    {
        if (_link is null) return;

        SetBusy(true);

        using var journal = ProbeJournal.CreateNextTo(
            "nes-writeprobe",
            line => Dispatcher.BeginInvoke(() => Log(line)));

        try
        {
            _link.InvalidateWake();

            var link = _link;
            await Task.Run(() => NesWriteProbe.Run(link, journal));

            Log($"書き込み検査を記録しました: {journal.Path}");
        }
        catch (RfcaDisconnectedException ex)
        {
            journal.Write($"！！ {ex.Message}");
            Log(ex.Message);
            MessageBox.Show(this, ex.Message, "アダプタが切断されました",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Disconnect();
        }
        catch (Exception ex)
        {
            journal.Write($"！！ {ex.GetType().Name}: {ex.Message}");
            Log($"検査に失敗しました: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SlotComparison_Click(object sender, RoutedEventArgs e)
    {
        if (_link is null) return;

        string label = string.IsNullOrWhiteSpace(SlotLabelBox.Text)
            ? "（名称未設定）"
            : SlotLabelBox.Text.Trim();

        SetBusy(true);

        using var journal = ProbeJournal.AppendNextTo(
            "slot-comparison",
            line => Dispatcher.BeginInvoke(() => Log(line)));

        try
        {
            _link.InvalidateWake();

            var link = _link;
            await Task.Run(() => SlotComparison.Run(link, label, journal));

            Log($"「{label}」を記録しました: {journal.Path}");
        }
        catch (RfcaDisconnectedException ex)
        {
            journal.Write($"！！ {ex.Message}");
            Log(ex.Message);
            MessageBox.Show(this, ex.Message, "アダプタが切断されました",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Disconnect();
        }
        catch (Exception ex)
        {
            journal.Write($"！！ {ex.GetType().Name}: {ex.Message}");
            Log($"記録に失敗しました: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// GBA スロットの先頭 4KB を採取し、任天堂ロゴの位置から
    /// ROM 先頭がどのアドレスに対応するのかを確かめる。
    /// </summary>
    private async void GbaHeadSample_Click(object sender, RoutedEventArgs e)
    {
        if (_link is null) return;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        ProbeCancelButton.IsEnabled = true;

        using var journal = ProbeJournal.CreateNextTo(
            "gba-head",
            line => Dispatcher.BeginInvoke(() => Log(line)));

        try
        {
            Log($"GBA 先頭の採取を開始します。記録: {journal.Path}");
            _link.InvalidateWake();

            var link = _link;
            var token = _cts.Token;
            var result = await Task.Run(() => GbaHeadSampler.Run(link, journal, 0x1000, token));

            string binPath = Path.ChangeExtension(journal.Path, ".bin");
            await File.WriteAllBytesAsync(binPath, result.Data);

            journal.Write($"採取した内容を保存しました: {binPath}");
            Log($"採取データ: {binPath}");

            if (result.LogoAt >= 0)
            {
                _gbaRomBase = result.RomBase;

                AssignedText.Text =
                    $"任天堂ロゴ 0x{result.LogoAt:X4} / ROM 先頭 0x{result.RomBase:X4}";

                MessageBox.Show(this,
                    "任天堂ロゴを検出しました。\n\n" +
                    $"ロゴの位置 : 0x{result.LogoAt:X4}\n" +
                    $"ROM 先頭   : アドレス 0x{result.RomBase:X4}\n\n{journal.Path}",
                    "GBA 先頭の採取", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                AssignedText.Text = "任天堂ロゴを検出できませんでした。";

                MessageBox.Show(this,
                    "任天堂ロゴが見つかりませんでした。\n\n" +
                    "カートリッジを挿し直してから、もう一度試してください。\n\n" +
                    journal.Path,
                    "GBA 先頭の採取", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            journal.Write("（利用者が中止しました）");
            Log("採取を中止しました。");
        }
        catch (RfcaDisconnectedException ex)
        {
            journal.Write($"！！ {ex.Message}");
            Log(ex.Message);
            MessageBox.Show(this, ex.Message, "アダプタが切断されました",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Disconnect();
        }
        catch (Exception ex)
        {
            journal.Write($"！！ {ex.GetType().Name}: {ex.Message}");
            Log($"採取に失敗しました: {ex.Message}");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            ProbeCancelButton.IsEnabled = false;
            SetBusy(false);
        }
    }

    // ==================================================================
    // 共通
    // ==================================================================

    /// <summary>吸い出し済みの GBA の ROM。セーブ装置の判定に使う。</summary>
    private byte[]? _lastGbaRom;

    /// <summary>
    /// EEPROM の容量を根拠をもって決められたか。
    ///
    /// 中身が空のカセットでは読んでも分からない。その場合に限り、
    /// 書き込むファイルの大きさを手掛かりとして採用してよい。
    /// </summary>
    private bool _eepromSizeCertain = true;

    /// <summary>「自動」に続けて、種類を手で選べるようにしておく。</summary>
    private static readonly GbaSaveType[] GbaSaveTypeOrder =
    [
        GbaSaveType.None,           // 0 番は「自動」
        GbaSaveType.Sram, GbaSaveType.Fram,
        GbaSaveType.Eeprom4k, GbaSaveType.Eeprom64k,
        GbaSaveType.Flash512k, GbaSaveType.Flash1M,
    ];

    private void InitializeGbaSaveTypes()
    {
        GbaSaveTypeCombo.Items.Add("自動（ROM の目印から判定）");

        foreach (var type in GbaSaveTypeOrder[1..])
            GbaSaveTypeCombo.Items.Add(GbaSave.DisplayName(type));

        GbaSaveTypeCombo.SelectedIndex = 0;
    }

    private void GbaSaveWriteAllow_Changed(object sender, RoutedEventArgs e)
    {
        bool allow = GbaSaveWriteAllowCheck.IsChecked == true;

        if (_link is not null) _link.AllowSaveWrites = allow;

        GbaSaveWriteButton.IsEnabled = allow && _link is not null;

        Log(allow
            ? "セーブデータの書き込みを許可しました。ROM 領域には書き込めません。"
            : "セーブデータの書き込みを禁止しました。");
    }

    /// <summary>
    /// セーブ装置の種類を決める。
    ///
    /// 「自動」のときは ROM の中の目印を探す。GBA は種類をヘッダで申告しないため、
    /// これ以外に知る方法がない。吸い出し済みの ROM があればそれを使い、
    /// 無ければ読んでから判定する。
    /// </summary>
    private async Task<GbaSaveType> ResolveGbaSaveTypeAsync()
    {
        int index = GbaSaveTypeCombo.SelectedIndex;

        if (index > 0) return GbaSaveTypeOrder[index];

        if (_lastGbaRom is null)
        {
            if (_link is null || _info is null || _activeDumper is null)
            {
                Log("先に「カセットを識別」を押してください。");
                return GbaSaveType.None;
            }

            Log("セーブ装置を判定するため、先に ROM を読みます。");

            var link = _link;
            var info = _info;
            var dumper = _activeDumper;
            var options = BuildOptions() ?? new DumpOptions();
            var token = _cts?.Token ?? CancellationToken.None;

            var romProgress = new Progress<DumpProgress>(p =>
            {
                DumpProgressBar.Value = p.Ratio;
                ProgressText.Text = $"{p.Stage}  {p.Ratio:P1}";
            });

            var result = await Task.Run(() => dumper.Dump(link, info, options, romProgress, token));

            _lastGbaRom = result.Rom;
        }

        var detected = GbaSave.Detect(_lastGbaRom);

        if (detected == GbaSaveType.None)
        {
            Log("ROM に目印が見つかりませんでした。セーブしないソフトか、独自の方式です。手で指定してください。");
            return detected;
        }

        // EEPROM の目印は 4kbit も 64kbit も同じで、容量が書かれていない。
        // 読んだ内容から判定する。読むだけで書き込みはしない。
        if (GbaSave.AlternateEeprom(detected) is not null && _link is not null)
        {
            var link = _link;
            var token = _cts?.Token ?? CancellationToken.None;

            var probe = await Task.Run(() => GbaSave.ProbeEepromSize(link, detected, token));

            _eepromSizeCertain = probe.Determined;
            detected = probe.Type;

            if (probe.Reason.Length > 0)
                Log(probe.Determined
                    ? $"EEPROM の容量を判定しました: {probe.Reason}"
                    : $"EEPROM の容量を決められませんでした: {probe.Reason}");
        }
        else
        {
            _eepromSizeCertain = true;
        }

        Log($"セーブ装置を判定しました: {GbaSave.DisplayName(detected)}");

        return detected;
    }

    /// <summary>読み書きする対象。機種ごとの違いをここで吸収する。</summary>
    private sealed record SaveTarget(string Label, long Size, GbaSaveType GbaType);

    /// <summary>
    /// 何をどれだけ読み書きするかを決める。
    ///
    /// GBA だけはセーブ装置の種類をヘッダで申告しないため、
    /// ROM の中の目印から判定する。他の機種はヘッダから分かる。
    /// </summary>
    private async Task<SaveTarget?> ResolveSaveTargetAsync()
    {
        if (_info is null)
        {
            Log("先に「カセットを識別」を押してください。");
            return null;
        }

        switch (_info.Kind)
        {
            case CartridgeKind.GameBoyAdvance:
            {
                var type = await ResolveGbaSaveTypeAsync();
                if (type == GbaSaveType.None) return null;

                return new SaveTarget(GbaSave.DisplayName(type), GbaSave.SizeOf(type), type);
            }

            case CartridgeKind.SuperFamicom:
            {
                if (_info.SnesMapping is null)
                {
                    Log("SFC のマッパーが分かりません。セーブを読み書きできません。");
                    return null;
                }

                if (_info.SaveMemorySize <= 0)
                {
                    Log("このカートリッジにはセーブ RAM がありません。");
                    return null;
                }

                return new SaveTarget(
                    $"SFC セーブ RAM {_info.SaveMemorySize / 1024}KB",
                    _info.SaveMemorySize, GbaSaveType.None);
            }

            case CartridgeKind.GameBoy:
            {
                if (_info.GbCartridgeType is null)
                {
                    Log("GB のカートリッジ種別が分かりません。セーブを読み書きできません。");
                    return null;
                }

                if (_info.SaveMemorySize <= 0)
                {
                    Log("このカートリッジにはセーブ用の外部 RAM がありません。");
                    return null;
                }

                return new SaveTarget(
                    $"GB セーブ {_info.SaveMemorySize / 1024}KB ({_info.Mapper})",
                    _info.SaveMemorySize, GbaSaveType.None);
            }

            default:
                Log($"{_info.Kind.ToDisplayName()} のセーブ読み書きには対応していません。");
                return null;
        }
    }

    private byte[] ReadSaveCore(
        IRfcaLink link, CartridgeInfo info, SaveTarget target,
        IProgress<DumpProgress>? progress, CancellationToken token)
        => info.Kind switch
        {
            CartridgeKind.GameBoyAdvance =>
                GbaSave.Read(link, target.GbaType, progress, token),

            CartridgeKind.SuperFamicom =>
                SnesSave.Read(link, info.SnesMapping!.Value, target.Size, progress, token),

            CartridgeKind.GameBoy =>
                GbSave.Read(link, info.GbCartridgeType!.Value, target.Size, progress, token),

            _ => throw new RfcaException("この機種のセーブ読み出しには対応していません。"),
        };

    private void WriteSaveCore(
        IRfcaLink link, CartridgeInfo info, SaveTarget target, byte[] data,
        IProgress<DumpProgress>? progress, CancellationToken token)
    {
        switch (info.Kind)
        {
            case CartridgeKind.GameBoyAdvance:
                GbaSave.Write(link, target.GbaType, data, progress, token);
                break;

            case CartridgeKind.SuperFamicom:
                SnesSave.Write(link, info.SnesMapping!.Value, data, progress, token);
                break;

            case CartridgeKind.GameBoy:
                GbSave.Write(link, info.GbCartridgeType!.Value, data, progress, token);
                break;

            default:
                throw new RfcaException("この機種のセーブ書き込みには対応していません。");
        }
    }

    private async void GbaSaveRead_Click(object sender, RoutedEventArgs e)
    {
        if (_link is null) return;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        CancelButton.IsEnabled = true;

        try
        {
            var target = await ResolveSaveTargetAsync();
            if (target is null || _info is null) return;

            var progress = new Progress<DumpProgress>(p =>
            {
                DumpProgressBar.Value = p.Ratio;
                ProgressText.Text = $"{p.Stage}  {FormatBytes(p.BytesDone)} / {FormatBytes(p.BytesTotal)}";
            });

            var link = _link;
            var info = _info;
            var token = _cts.Token;

            Log($"セーブを吸い出します: {target.Label}");

            byte[] save = await Task.Run(() => ReadSaveCore(link, info, target, progress, token));

            var dialog = new SaveFileDialog
            {
                Title = "セーブデータの保存先",
                FileName = FileNaming.MakeRomFileName(_info?.Title, ".sav"),
                Filter = "セーブデータ (*.sav)|*.sav|すべてのファイル (*.*)|*.*",
                AddExtension = true,
                DefaultExt = "sav",
            };

            if (dialog.ShowDialog(this) != true)
            {
                Log("保存を中止しました。");
                return;
            }

            await File.WriteAllBytesAsync(dialog.FileName, save);

            Log($"セーブを保存しました: {dialog.FileName} ({FormatBytes(save.Length)})");
            ProgressText.Text = "セーブの吸い出し完了";
        }
        catch (OperationCanceledException)
        {
            Log("セーブの吸い出しを中止しました。");
        }
        catch (Exception ex)
        {
            Log($"セーブの吸い出しに失敗しました: {ex.Message}");
            MessageBox.Show(this, ex.Message, "セーブの吸い出し",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            CancelButton.IsEnabled = false;
            SetBusy(false);
        }
    }

    private async void GbaSaveWrite_Click(object sender, RoutedEventArgs e)
    {
        if (_link is null) return;

        if (GbaSaveWriteAllowCheck.IsChecked != true)
        {
            Log("セーブデータの書き込みが許可されていません。");
            return;
        }

        _cts = new CancellationTokenSource();
        SetBusy(true);
        CancelButton.IsEnabled = true;

        try
        {
            var target = await ResolveSaveTargetAsync();
            if (target is null || _info is null) return;

            long size = target.Size;


            var open = new OpenFileDialog
            {
                Title = "書き込むセーブデータ",
                Filter = "セーブデータ (*.sav)|*.sav|すべてのファイル (*.*)|*.*",
            };

            if (open.ShowDialog(this) != true)
            {
                Log("書き込みを中止しました。");
                return;
            }

            byte[] data = await File.ReadAllBytesAsync(open.FileName);

            // EEPROM は目印で容量が決まらない。選ばれたファイルが
            // もう一方の容量に一致するなら、取り違えではなく容量の判定違いの
            // 可能性が高い。捨てずに、そちらとして書くかを尋ねる。
            if (data.Length != size
                && _info.Kind == CartridgeKind.GameBoyAdvance
                && GbaSave.AlternateEeprom(target.GbaType) is GbaSaveType alternate
                && data.Length == GbaSave.SizeOf(alternate))
            {
                // 容量を決められていないなら、ファイルの大きさが唯一の手掛かり。
                // 根拠が無いほうを保って尋ねるより、そのまま採用するほうが素直。
                bool accept = !_eepromSizeCertain;

                if (accept)
                {
                    Log($"EEPROM の容量を読み分けられなかったため、" +
                        $"選ばれたファイルに合わせて {GbaSave.DisplayName(alternate)} として扱います。");
                }
                else
                {
                    var switchTo = MessageBox.Show(this,
                        $"選んだファイルは {data.Length} バイトで、" +
                        $"{GbaSave.DisplayName(alternate)} の大きさに一致します。\n\n" +
                        $"ただし読み出した内容からは {target.Label} と判断しています。\n" +
                        $"別のカセットのセーブを選んでいないか確かめてください。\n\n" +
                        $"{GbaSave.DisplayName(alternate)} として書き込みますか？",
                        "セーブの書き込み", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                    accept = switchTo == MessageBoxResult.Yes;
                }

                if (accept)
                {
                    target = target with
                    {
                        Label = GbaSave.DisplayName(alternate),
                        Size = GbaSave.SizeOf(alternate),
                        GbaType = alternate,
                    };

                    size = target.Size;

                    Log($"セーブ装置を {target.Label} として扱います。");
                }
            }

            if (data.Length != size)
            {
                string message =
                    $"ファイルの大きさが {target.Label} と合いません。\n\n" +
                    $"必要: {size} バイト\n選んだファイル: {data.Length} バイト";

                Log(message.Replace("\n", " "));
                MessageBox.Show(this, message, "セーブの書き込み",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var link = _link;
            var info = _info;
            var token = _cts.Token;

            // 上書きする前に、今カートリッジに入っているものを控えに残す。
            // 取り違えて書いたとき、元に戻せる手立てがこれしかない。
            Log("上書きする前に、現在のセーブを控えに残します。");

            byte[] backup = await Task.Run(() => ReadSaveCore(link, info, target, null, token));

            string backupPath = Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory(),
                $"save-backup-{DateTime.Now:yyyyMMdd-HHmmss}.sav");

            await File.WriteAllBytesAsync(backupPath, backup);
            Log($"控えを保存しました: {backupPath}");

            var confirm = MessageBox.Show(this,
                "カートリッジのセーブデータを上書きします。\n\n" +
                $"対象: {target.Label}\n" +
                $"書き込むファイル: {Path.GetFileName(open.FileName)}\n" +
                $"大きさ: {size} バイト\n\n" +
                $"現在のセーブは次の場所に控えてあります。\n{backupPath}\n\n" +
                "書き込みを実行しますか？",
                "セーブの書き込み", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                Log("書き込みを中止しました。");
                return;
            }

            var progress = new Progress<DumpProgress>(p =>
            {
                DumpProgressBar.Value = p.Ratio;
                ProgressText.Text = $"{p.Stage}  {FormatBytes(p.BytesDone)} / {FormatBytes(p.BytesTotal)}";
            });

            await Task.Run(() => WriteSaveCore(link, info, target, data, progress, token));

            Log("セーブを書き込み、読み戻して一致を確認しました。");
            ProgressText.Text = "セーブの書き込み完了";
        }
        catch (OperationCanceledException)
        {
            Log("セーブの書き込みを中止しました。");
        }
        catch (Exception ex)
        {
            Log($"セーブの書き込みに失敗しました: {ex.Message}");
            MessageBox.Show(this, ex.Message, "セーブの書き込み",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            CancelButton.IsEnabled = false;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        bool live = !busy && _link is not null;

        IdentifyButton.IsEnabled = live;
        DumpButton.IsEnabled = live && _info is not null;
        ConnectButton.IsEnabled = !busy;
        DetectButton.IsEnabled = live;
        DiagnoseButton.IsEnabled = live;
        SlotComparisonButton.IsEnabled = live;
        NesWriteProbeButton.IsEnabled = live;
        GbaHeadSampleButton.IsEnabled = live;
        GbaSaveReadButton.IsEnabled = live;
        GbaSaveWriteAllowCheck.IsEnabled = live;
        GbaSaveWriteButton.IsEnabled = live && GbaSaveWriteAllowCheck.IsChecked == true;
        AutoDetectPortButton.IsEnabled = !busy && _link is null;

        Cursor = busy ? Cursors.Wait : null;
    }

    /// <summary>
    /// 照合できなかった吸い出しの控えを、EXE と同じ場所に残す。
    ///
    /// 原因が読み違いなのか未収録なのかは、現物を見ないと分からない。
    /// 保存ダイアログを閉じてしまうと調べる手立てが無くなるので、
    /// 利用者の選択とは別に、解析用の 1 本を確保しておく。
    /// </summary>
    private void SaveRecoveryCopy(byte[] rom, string extension)
    {
        try
        {
            string dir = Path.GetDirectoryName(Environment.ProcessPath)
                ?? Directory.GetCurrentDirectory();

            string path = Path.Combine(
                dir,
                $"unmatched-{DateTime.Now:yyyyMMdd-HHmmss}" +
                (extension is { Length: > 0 } ? extension : ".bin"));

            File.WriteAllBytes(path, rom);

            Log($"照合できなかったため、解析用の控えを保存しました: {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"解析用の控えを保存できませんでした: {ex.Message}");
        }
    }

    /// <summary>
    /// 組み込まれた版。ウィンドウのタイトルに出す。
    ///
    /// 実機で試しては直す往復が続くため、
    /// 今動かしているのがどのビルドかを取り違えないようにする。
    /// </summary>
    private static string AppVersion
    {
        get
        {
            var version = System.Reflection.Assembly
                .GetExecutingAssembly()
                .GetName()
                .Version;

            return version is null
                ? ""
                : $"v{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    private static string MakeFileName(CartridgeInfo info)
        => FileNaming.MakeRomFileName(info.Title, info.RomExtension);

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / 1024.0 / 1024.0:0.##} MB";
    }

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        _link?.Dispose();
        base.OnClosed(e);
    }
}
