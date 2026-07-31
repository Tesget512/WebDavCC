using Microsoft.UI.Xaml;
using WebDavShare.Core;

namespace WebDavShare;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }

    /// <summary>由 --autostart 触发时静默进托盘，不弹主窗口。</summary>
    public static bool LaunchedByAutoStart { get; private set; }

    public App()
    {
        InitializeComponent();

        // 未捕获异常直接崩溃对后台服务很不友好，记下来并继续
        UnhandledException += (_, e) =>
        {
            FileLogger.Write($"Unhandled exception: {e.Exception}");
            e.Handled = true;
            Window?.AppendLog(Strings.UnhandledError(e.Message), LogLevel.Error);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        LaunchedByAutoStart = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

        Window = new MainWindow();
        Window.Initialize(LaunchedByAutoStart);
    }
}
