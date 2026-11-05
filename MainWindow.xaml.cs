using System.Collections.ObjectModel;
using System.Net.Sockets;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WebDavShare.Core;
using Windows.Graphics;
using WinRT.Interop;

namespace WebDavShare;

public enum LogLevel { Info, Success, Error, Muted }

public sealed class ShareItem
{
    public required string Name { get; init; }
    public required string Path { get; init; }
}

public sealed class LogEntry
{
    public required string Text { get; init; }
    public required LogLevel Level { get; init; }

    public SolidColorBrush Brush => Level switch
    {
        LogLevel.Success => new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen),
        LogLevel.Error => new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
        LogLevel.Muted => new SolidColorBrush(Microsoft.UI.Colors.Gray),
        _ => new SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue),
    };
}

public sealed partial class MainWindow : Window
{
    private const int MaxLogEntries = 400;

    private readonly ObservableCollection<ShareItem> _shares = [];
    private readonly ObservableCollection<LogEntry> _logs = [];
    private readonly DispatcherQueue _dispatcher;

    private ServerConfig _config = new();
    private WebDavServer? _server;
    private TrayIcon? _tray;
    private IReadOnlyList<string> _urls = [];
    private bool _suppressEvents;
    private bool _uiReady;
    private bool _reallyClosing;

    public MainWindow()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        ShareList.ItemsSource = _shares;
        LogList.ItemsSource = _logs;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        Title = Strings.WindowTitle;

        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        Closed += OnWindowClosed;
    }

    /// <summary>AppWindow.Resize 收物理像素，高 DPI 下直接传逻辑尺寸会明显偏小。</summary>
    private void ResizeForDpi(int logicalWidth, int logicalHeight)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi <= 0 ? 1.0 : dpi / 96.0;
        AppWindow.Resize(new SizeInt32(
            (int)(logicalWidth * scale), (int)(logicalHeight * scale)));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public void Initialize(bool autoStart)
    {
        try
        {
            InitializeCore(autoStart);
        }
        catch (Exception e)
        {
            FileLogger.Write($"Initialization failed: {e}");
            throw;
        }
    }

    private void InitializeCore(bool autoStart)
    {
        ApplyLocalizedText();
        _config = ConfigStore.Load();
        ApplyConfigToUi();
        _uiReady = true;
        SetupTray();

        if (_config.Shares.Count == 0)
        {
            AppendLog(Strings.WelcomeLine1, LogLevel.Muted);
            AppendLog(Strings.WelcomeLine2, LogLevel.Muted);
        }
        else
        {
            AppendLog(Strings.ConfigLoaded(_config.Shares.Count), LogLevel.Muted);
        }

        if (autoStart)
        {
            // 成功路径默认不落盘，出问题（比如用户说"自启动没生效"）时无据可查
            FileLogger.Write("Launched by autostart (--autostart)");
            AppendLog(Strings.AutoStartTriggered, LogLevel.Muted);
            if (_config.StartMinimizedToTray)
            {
                AppWindow.Hide();
            }
            if (_config.Shares.Count > 0)
            {
                _ = StartServerAsync(silent: true);
            }
            else
            {
                FileLogger.Write(Strings.AutoStartNoShares);
            }
        }
        else
        {
            Activate();
            // 在 Activate 之后再改尺寸，构造期 Resize 会让内容首帧渲染不出来
            ResizeForDpi(940, 620);
        }
    }

    /// <summary>把界面文案填成当前语言。XAML 里不写死文本，便于中英切换。</summary>
    private void ApplyLocalizedText()
    {
        Title = Strings.WindowTitle;
        TitleBarText.Text = Strings.AppTitle;

        StatusTitle.Text = Strings.StatusStopped;
        StatusDetail.Text = Strings.StatusHintIdle;
        ToggleButton.Content = Strings.ButtonStart;

        ToolTipService.SetToolTip(CopyUrlButton, Strings.TipCopyUrl);
        ToolTipService.SetToolTip(OpenBrowserButton, Strings.TipOpenBrowser);
        ToolTipService.SetToolTip(ExportCertButton, Strings.TipExportCert);

        SharedFoldersLabel.Text = Strings.SharedFolders;
        SharedFoldersHint.Text = Strings.SharedFoldersHint;
        AddButtonText.Text = Strings.ButtonAdd;
        RemoveShareButton.Content = Strings.ButtonRemove;

        ConnectionLabel.Text = Strings.ConnectionSettings;
        PortBox.Header = Strings.LabelPort;
        UserBox.Header = Strings.LabelUser;
        UserBox.PlaceholderText = Strings.PlaceholderUser;
        PasswordBox.Header = Strings.LabelPassword;
        PasswordBox.PlaceholderText = Strings.PlaceholderPassword;

        ReadOnlyLabel.Text = Strings.ReadOnlyMode;
        AutoStartLabel.Text = Strings.AutoStart;
        AutoStartModeBox.Header = Strings.AutoStartMethod;
        AutoStartModeBox.Items.Clear();
        AutoStartModeBox.Items.Add(Strings.AutoStartLogon);
        AutoStartModeBox.Items.Add(Strings.AutoStartBoot);
        AutoStartModeBox.SelectedIndex = 0;
        MinimizeToTrayCheck.Content = Strings.MinimizeToTray;

        LogLabel.Text = Strings.RunLog;
        ClearLogButton.Content = Strings.ButtonClear;
        SecurityHintText.Text = Strings.NoPasswordWarning;
    }

    // ---------------- 配置与界面同步 ----------------

    private void ApplyConfigToUi()
    {
        _suppressEvents = true;

        PortBox.Value = _config.Port;
        UserBox.Text = _config.User;
        PasswordBox.Password = _config.Password;
        ReadOnlySwitch.IsOn = _config.ReadOnly;
        MinimizeToTrayCheck.IsChecked = _config.StartMinimizedToTray;

        _shares.Clear();
        foreach (var (name, path) in _config.Shares.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            _shares.Add(new ShareItem { Name = name, Path = path });
        }

        // 以系统实际状态为准，配置文件可能与注册表脱节
        var actualMode = AutoStartManager.IsEnabled(AutoStartMode.Boot)
            ? AutoStartMode.Boot
            : AutoStartMode.Logon;
        var actuallyEnabled = AutoStartManager.IsEnabled(actualMode);
        AutoStartSwitch.IsOn = actuallyEnabled;
        AutoStartModeBox.SelectedIndex = actualMode == AutoStartMode.Boot ? 1 : 0;
        AutoStartModeBox.IsEnabled = actuallyEnabled;
        _config.AutoStartEnabled = actuallyEnabled;
        _config.AutoStartMode = actualMode;

        _suppressEvents = false;
        UpdateSecurityHint();
        UpdateReadOnlyHint();
    }

    private void CollectUiIntoConfig()
    {
        _config.Port = (int)(double.IsNaN(PortBox.Value) ? 8443 : PortBox.Value);
        _config.User = UserBox.Text.Trim();
        _config.Password = PasswordBox.Password;
        _config.ReadOnly = ReadOnlySwitch.IsOn;
        _config.StartMinimizedToTray = MinimizeToTrayCheck.IsChecked == true;
        _config.Shares = _shares.ToDictionary(s => s.Name, s => s.Path);
    }

    private void UpdateSecurityHint() =>
        SecurityHintText.Visibility = string.IsNullOrWhiteSpace(UserBox.Text) && _shares.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;

    private void SaveConfig()
    {
        // 界面还没完成初始化时不写盘，否则会用空白状态覆盖用户已有配置
        if (!_uiReady)
        {
            return;
        }
        CollectUiIntoConfig();
        try
        {
            ConfigStore.Save(_config);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppendLog(Strings.ConfigSaveFailed(e.Message), LogLevel.Error);
        }
    }

    // ---------------- 共享管理 ----------------

    private async void OnAddShare(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        var path = folder.Path;
        if (_shares.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            await ShowMessageAsync(Strings.DuplicateTitle, Strings.DuplicateBody);
            return;
        }

        var baseName = System.IO.Path.GetFileName(path.TrimEnd(
            System.IO.Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(baseName))
        {
            baseName = "share";
        }
        var name = baseName;
        var suffix = 2;
        while (_shares.Any(s => s.Name == name))
        {
            name = baseName + suffix++;
        }

        _shares.Add(new ShareItem { Name = name, Path = path });
        AppendLog(Strings.ShareAdded(name, path), LogLevel.Muted);
        UpdateSecurityHint();
        SaveConfig();
    }

    private void OnRemoveShare(object sender, RoutedEventArgs e)
    {
        if (ShareList.SelectedItem is ShareItem item)
        {
            _shares.Remove(item);
            UpdateSecurityHint();
            SaveConfig();
        }
    }

    private async void OnShareDoubleTapped(object sender,
        Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (ShareList.SelectedItem is not ShareItem item)
        {
            return;
        }

        var input = new TextBox { Text = item.Name, SelectionStart = 0,
            SelectionLength = item.Name.Length };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = Strings.RenameTitle,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = Strings.RenamePrompt, TextWrapping = TextWrapping.Wrap },
                    input,
                },
            },
            PrimaryButtonText = Strings.DialogOk,
            CloseButtonText = Strings.DialogCancel,
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var newName = input.Text.Trim();
        if (newName.Length == 0 || newName == item.Name)
        {
            return;
        }
        if (!IsValidMountName(newName))
        {
            await ShowMessageAsync(Strings.InvalidNameTitle, Strings.InvalidNameBody);
            return;
        }
        if (_shares.Any(s => s.Name == newName))
        {
            await ShowMessageAsync(Strings.NameTakenTitle, Strings.NameTakenBody(newName));
            return;
        }

        var index = _shares.IndexOf(item);
        _shares[index] = new ShareItem { Name = newName, Path = item.Path };
        SaveConfig();
    }

    /// <summary>
    /// 挂载名会拼进 URL 和本地路径：含 : * ? 等字符会让 Path.GetFullPath 抛异常，
    /// "." / ".." 则在路径解析时会被吞掉，一律拒绝。
    /// </summary>
    private static bool IsValidMountName(string name) =>
        name.Length > 0 && name != "." && name != ".." &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    // ---------------- 启停 ----------------

    private async void OnToggleServer(object sender, RoutedEventArgs e)
    {
        if (_server is not null)
        {
            await StopServerAsync();
        }
        else
        {
            await StartServerAsync(silent: false);
        }
    }

    /// <summary>启动服务。返回是否成功，供自启动流程判断是否弹气泡。</summary>
    private async Task<bool> StartServerAsync(bool silent)
    {
        CollectUiIntoConfig();

        if (_config.Shares.Count == 0)
        {
            if (!silent)
            {
                await ShowMessageAsync(Strings.NoSharesTitle, Strings.NoSharesBody);
            }
            return false;
        }

        var missing = _config.Shares.Where(p => !Directory.Exists(p.Value)).ToList();
        if (missing.Count > 0)
        {
            var text = string.Join("\n", missing.Select(p => p.Value));
            AppendLog(Strings.MissingDirBody(text), LogLevel.Error);
            if (!silent)
            {
                await ShowMessageAsync(Strings.MissingDirTitle, Strings.MissingDirBody(text));
            }
            return false;
        }

        if (_config.RequiresAuth && string.IsNullOrEmpty(_config.Password))
        {
            if (!silent)
            {
                await ShowMessageAsync(Strings.MissingPasswordTitle, Strings.MissingPasswordBody);
            }
            return false;
        }

        ToggleButton.IsEnabled = false;
        SetStatus(Strings.StatusStarting, Microsoft.UI.Colors.Gray, Strings.StatusHintStarting);

        try
        {
            var certificate = await Task.Run(() =>
                CertificateManager.GetOrCreate(m => Post(() => AppendLog(m, LogLevel.Muted))));

            var server = new WebDavServer(_config.Clone(), certificate);
            server.Log += line => Post(() => AppendLog(line, LogLevel.Info));
            await Task.Run(server.Start);

            _server = server;
            _urls = server.AccessUrls();

            var mode = _config.ReadOnly ? Strings.ModeReadOnly : Strings.ModeReadWrite;
            var who = _config.RequiresAuth ? _config.User : Strings.AccountAnonymous;
            AppendLog(Strings.ServerStarted(mode, who), LogLevel.Success);
            foreach (var url in _urls)
            {
                AppendLog("  " + url, LogLevel.Success);
            }

            SetStatus(Strings.StatusRunning, Microsoft.UI.Colors.MediumSeaGreen,
                string.Join("    ", _urls));
            ToggleButton.Content = Strings.ButtonStop;
            ToggleButton.Style = (Style)Application.Current.Resources["DefaultButtonStyle"];
            SetInputsEnabled(false);
            UpdateTray(true);
            SaveConfig();

            // Win11 默认把托盘图标收进"隐藏图标"折叠区，自启动成功时给个气泡
            // 提示，否则用户会误以为自启动没有生效
            if (silent)
            {
                var url = _urls.FirstOrDefault(u => !u.Contains("127.0.0.1")) ?? _urls[0];
                _tray?.ShowBalloon(Strings.TrayStartedTitle,
                    Strings.TrayStartedBody(url));
            }
            return true;
        }
        catch (SocketException ex)
        {
            var message = ex.SocketErrorCode switch
            {
                SocketError.AddressAlreadyInUse when System.Diagnostics.Process
                        .GetProcessesByName(Environment.ProcessPath is null
                            ? "WebDavCC"
                            : System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath))
                    .Length > 1
                    => Strings.PortInUseByInstance(_config.Port),
                SocketError.AddressAlreadyInUse => Strings.PortInUse(_config.Port),
                SocketError.AccessDenied => Strings.PortDenied(_config.Port),
                _ => ex.Message,
            };
            await FailStartAsync(message, silent);
        }
        catch (Exception ex)
        {
            await FailStartAsync($"{ex.GetType().Name}: {ex.Message}", silent);
        }
        finally
        {
            ToggleButton.IsEnabled = true;
        }
        return false;
    }

    private async Task FailStartAsync(string message, bool silent)
    {
        _server = null;
        SetStatus(Strings.StatusStartFailed, Microsoft.UI.Colors.IndianRed,
            message.Replace("\n\n", " "));
        AppendLog(Strings.StartFailed(message.Replace("\n\n", " ")), LogLevel.Error);
        UpdateTray(false);
        if (!silent)
        {
            await ShowMessageAsync(Strings.StatusStartFailed, message);
        }
        else
        {
            _tray?.ShowBalloon(Strings.TrayStartFailedTitle, message.Replace("\n\n", " "));
        }
    }

    private async Task StopServerAsync()
    {
        if (_server is null)
        {
            return;
        }
        ToggleButton.IsEnabled = false;
        var server = _server;
        _server = null;

        await server.DisposeAsync();

        _urls = [];
        SetStatus(Strings.StatusStopped, Microsoft.UI.Colors.Gray, Strings.StatusHintIdle);
        ToggleButton.Content = Strings.ButtonStart;
        ToggleButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        ToggleButton.IsEnabled = true;
        AppendLog(Strings.ServerStopped, LogLevel.Muted);
        SetInputsEnabled(true);
        UpdateTray(false);
    }

    /// <summary>运行时锁定配置项，避免改了却不生效造成误解。</summary>
    private void SetInputsEnabled(bool enabled)
    {
        PortBox.IsEnabled = enabled;
        UserBox.IsEnabled = enabled;
        PasswordBox.IsEnabled = enabled;
        ReadOnlySwitch.IsEnabled = enabled;
        AddShareButton.IsEnabled = enabled;
        RemoveShareButton.IsEnabled = enabled;
        ShareList.IsEnabled = enabled;
    }

    private void SetStatus(string title, Windows.UI.Color color, string detail)
    {
        StatusTitle.Text = title;
        StatusDot.Fill = new SolidColorBrush(color);
        StatusDetail.Text = detail;
    }

    // ---------------- 辅助操作 ----------------

    private async void OnCopyUrl(object sender, RoutedEventArgs e)
    {
        if (_urls.Count == 0)
        {
            await ShowMessageAsync(Strings.NotRunningTitle, Strings.NotRunningBody);
            return;
        }
        var url = _urls.FirstOrDefault(u => !u.Contains("127.0.0.1")) ?? _urls[0];
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(url);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        AppendLog(Strings.CopiedToClipboard(url), LogLevel.Muted);
    }

    private async void OnOpenBrowser(object sender, RoutedEventArgs e)
    {
        if (_urls.Count == 0)
        {
            await ShowMessageAsync(Strings.NotRunningTitle, Strings.NotRunningBody);
            return;
        }
        await Windows.System.Launcher.LaunchUriAsync(new Uri(_urls[0]));
    }

    private async void OnExportCert(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(CertificateManager.CertPemPath))
        {
            await ShowMessageAsync(Strings.NoCertTitle, Strings.NoCertBody);
            return;
        }

        var picker = new Windows.Storage.Pickers.FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.SuggestedFileName = "webdav-server";
        picker.FileTypeChoices.Add(Strings.CertFileType, [".crt"]);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        File.Copy(CertificateManager.CertPemPath, file.Path, overwrite: true);
        AppendLog(Strings.CertExported(file.Path), LogLevel.Muted);
        await ShowMessageAsync(Strings.ExportOkTitle, Strings.ExportOkBody(file.Path));
    }

    private void OnClearLog(object sender, RoutedEventArgs e) => _logs.Clear();

    private void OnTraySettingChanged(object sender, RoutedEventArgs e)
    {
        if (!_suppressEvents)
        {
            SaveConfig();
        }
    }

    private void OnReadOnlyToggled(object sender, RoutedEventArgs e)
    {
        UpdateReadOnlyHint();
        if (!_suppressEvents)
        {
            SaveConfig();
        }
    }

    private void UpdateReadOnlyHint()
    {
        // XAML 解析期就可能触发 Toggled，此时后面的控件尚未创建
        if (ReadOnlyHint is null)
        {
            return;
        }
        ReadOnlyHint.Text = ReadOnlySwitch.IsOn ? Strings.ReadOnlyOn : Strings.ReadOnlyOff;
    }

    // ---------------- 自启动 ----------------

    private async void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }
        AutoStartModeBox.IsEnabled = AutoStartSwitch.IsOn;
        await ApplyAutoStartAsync();
    }

    private async void OnAutoStartModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || !AutoStartSwitch.IsOn)
        {
            return;
        }
        await ApplyAutoStartAsync();
    }

    private async Task ApplyAutoStartAsync()
    {
        var enabled = AutoStartSwitch.IsOn;
        var mode = AutoStartModeBox.SelectedIndex == 1
            ? AutoStartMode.Boot
            : AutoStartMode.Logon;

        // 失败回滚要用：切换方式前先把原状态记下来（配置在启动时已和系统实际状态对齐）
        var previousEnabled = _config.AutoStartEnabled;
        var previousMode = _config.AutoStartMode;

        var (success, message) = await Task.Run(() => AutoStartManager.Apply(enabled, mode));
        AppendLog(message, success ? LogLevel.Success : LogLevel.Error);

        if (!success)
        {
            // 失败时注册表/计划任务处于半成品状态：Apply(true, mode) 可能已把
            // 另一种入口删掉。恢复到切换前的配置，避免自启动彻底失效。
            _suppressEvents = true;
            if (previousEnabled)
            {
                var (restored, _) = await Task.Run(
                    () => AutoStartManager.Apply(true, previousMode));
                FileLogger.Write(restored
                    ? $"Autostart restored to {previousMode}"
                    : "Autostart restore failed");
            }
            var actual = previousEnabled ? previousMode : AutoStartMode.Logon;
            var on = previousEnabled && AutoStartManager.IsEnabled(actual);
            AutoStartSwitch.IsOn = on;
            AutoStartModeBox.SelectedIndex = previousMode == AutoStartMode.Boot ? 1 : 0;
            AutoStartModeBox.IsEnabled = on;
            _suppressEvents = false;
            await ShowMessageAsync(Strings.AutoStartFailedTitle, message);
            return;
        }

        _config.AutoStartEnabled = enabled;
        _config.AutoStartMode = mode;
        SaveConfig();
    }

    // ---------------- 托盘 ----------------

    private void SetupTray()
    {
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        _tray = new TrayIcon(iconPath, Strings.TrayTooltipIdle);
        _tray.DoubleClicked += ShowMainWindow;
        RebuildTrayMenu(false);
        _tray.Show();
    }

    private void UpdateTray(bool running)
    {
        var address = _urls.FirstOrDefault(u => !u.Contains("127.0.0.1")) ?? "";
        _tray?.UpdateTip(running
            ? Strings.TrayTooltipRunning(address)
            : Strings.TrayTooltipIdle);
        RebuildTrayMenu(running);
    }

    private void RebuildTrayMenu(bool running)
    {
        if (_tray is null)
        {
            return;
        }
        _tray.ClearMenu();
        _tray.AddMenuItem(Strings.TrayOpen, ShowMainWindow);
        _tray.AddMenuItem(Strings.TrayCopyUrl, () => Post(() => OnCopyUrl(this, new RoutedEventArgs())),
            enabled: running);
        _tray.AddSeparator();
        _tray.AddMenuItem(running ? Strings.TrayStop : Strings.TrayStart,
            () => Post(async () =>
            {
                if (running)
                {
                    await StopServerAsync();
                }
                else
                {
                    await StartServerAsync(silent: false);
                    ShowMainWindow();
                }
            }));
        _tray.AddSeparator();
        _tray.AddMenuItem(Strings.TrayExit, () => Post(async () =>
        {
            // 服务在跑时退出会掐断所有客户端连接，先确认一下
            if (_server is not null)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = Strings.ExitConfirmTitle,
                    Content = new TextBlock
                    {
                        Text = Strings.ExitConfirmBody,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    PrimaryButtonText = Strings.DialogOk,
                    CloseButtonText = Strings.DialogCancel,
                    DefaultButton = ContentDialogButton.Primary,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }
            }
            QuitApplication();
        }));
    }

    private async void QuitApplication()
    {
        _reallyClosing = true;
        await StopServerAsync();
        SaveConfig();
        _tray?.Dispose();
        _tray = null;
        Application.Current.Exit();
    }

    /// <summary>第二个进程实例请求弹出主窗口（自启动的实例窗口可能是隐藏的）。</summary>
    public void RequestShow() => Post(ShowMainWindow);

    private void ShowMainWindow() => Post(() =>
    {
        AppWindow.Show();
        AppWindow.MoveInZOrderAtTop();
        Activate();
    });

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // 勾选了最小化到托盘时，关闭按钮只隐藏窗口，服务继续跑
        if (!_reallyClosing && _config.StartMinimizedToTray && _tray is not null)
        {
            args.Handled = true;
            AppWindow.Hide();
            if (_server is not null)
            {
                _tray.ShowBalloon(Strings.TrayStillRunningTitle,
                    Strings.TrayStillRunningBody);
            }
            return;
        }

        SaveConfig();
        _server?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        _tray?.Dispose();
    }

    // ---------------- 日志 ----------------

    public void AppendLog(string text, LogLevel level = LogLevel.Info)
    {
        if (level is LogLevel.Error or LogLevel.Success)
        {
            FileLogger.Write(text);
        }
        Post(() =>
        {
            _logs.Add(new LogEntry { Text = text, Level = level });
            while (_logs.Count > MaxLogEntries)
            {
                _logs.RemoveAt(0);
            }
            LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null,
                disableAnimation: true);
        });
    }

    /// <summary>后台线程回到 UI 线程；tkinter 那套线程限制在 WinUI 同样存在。</summary>
    private void Post(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }

    private void Post(Func<Task> action) => Post(() => _ = action());

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = Strings.DialogClose,
        };
        await dialog.ShowAsync();
    }
}
