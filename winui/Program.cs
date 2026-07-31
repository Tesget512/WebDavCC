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
            // 单实例：已在运行时唤起已有窗口，避免开机自启和手动点击各起一个
            using var singleton = new Mutex(true, @"Local\WebDavShare.SingleInstance",
                out var isFirst);
            if (!isFirst)
            {
                SingleInstance.ActivateExisting();
                return 0;
            }

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
    private const int SW_RESTORE = 9;

    public static void ActivateExisting()
    {
        var current = System.Diagnostics.Process.GetCurrentProcess();
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(current.ProcessName))
        {
            if (process.Id == current.Id || process.MainWindowHandle == IntPtr.Zero)
            {
                continue;
            }
            ShowWindow(process.MainWindowHandle, SW_RESTORE);
            SetForegroundWindow(process.MainWindowHandle);
            return;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
