using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WebDavShare.Core;

namespace WebDavShare;

/// <summary>
/// 自定义入口点。默认由 XAML 生成的 Main 会直接触碰 WinUI 类型，
/// 那样在缺少运行时的机器上会静默闪退；这里先做依赖检查再启动。
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 必须在任何 WinUI 类型被触碰之前完成，否则会静默闪退
        if (!RuntimeCheck.Initialize())
        {
            return 1;
        }

        try
        {
            // 激活事件必须先于互斥体创建：已在运行的实例要能立刻收到第二次启动的信号，
            // 否则存在"对方查不到事件、白白退出"的窗口期。
            using var showUi = new EventWaitHandle(false, EventResetMode.AutoReset,
                SingleInstance.ShowUiEventName, out _);

            // 单实例：已在运行时唤起已有窗口，避免开机自启和手动点击各起一个
            using var singleton = new Mutex(true, SingleInstance.MutexName, out var isFirst);
            if (!isFirst)
            {
                showUi.Set();
                return 0;
            }

            // 自启动的实例把窗口藏进了托盘，此时 MainWindowHandle 为 0，
            // 靠枚举窗口找不到它，用户会误以为"自启动没生效"。
            // 改用命名事件：再次启动 exe 时置位事件，常驻线程负责弹出主窗口。
            var watcher = new Thread(() => SingleInstance.WatchShowUiRequests(showUi))
            {
                IsBackground = true,
                Name = "WebDavShare.ShowUiWatcher",
            };
            watcher.Start();

            XamlCheckProcessRequirements();
            WinRT.ComWrappersSupport.InitializeComWrappers();

            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
            return 0;
        }
        finally
        {
            RuntimeCheck.Shutdown();
        }
    }

    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();
}

internal static class SingleInstance
{
    public const string MutexName = @"Local\WebDavShare.SingleInstance";
    public const string ShowUiEventName = @"Local\WebDavShare.ShowUi";

    /// <summary>阻塞等待激活请求；事件被释放（进程退出）时线程结束。</summary>
    public static void WatchShowUiRequests(EventWaitHandle showUi)
    {
        try
        {
            while (showUi.WaitOne())
            {
                // MainWindow 尚未创建时（启动中）信号只能丢弃；Reference read 竞态无害
                App.Window?.RequestShow();
            }
        }
        catch (ObjectDisposedException)
        {
            // 主线程退出时释放了句柄
        }
        catch (AbandonedMutexException)
        {
            // 其他实例异常退出留下的废弃互斥体，不影响事件等待
        }
    }
}
