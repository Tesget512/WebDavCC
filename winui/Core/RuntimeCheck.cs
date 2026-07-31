using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WebDavShare.Core;

/// <summary>
/// Windows App Runtime 的引导与依赖检查。
///
/// 非打包应用必须先调用 Bootstrap 才能加载 WinUI；失败时进程会在界面出现前
/// 就崩溃，用户只看到闪退。这里用 Win32 MessageBox 给出中文提示和下载入口
/// （SDK 自带的引导对话框只有英文）。
/// </summary>
public static class RuntimeCheck
{
    /// <summary>Windows App SDK 1.7 = 0x00010007。</summary>
    private const uint MajorMinor = 0x00010007;

    private const string DownloadPage =
        "https://aka.ms/windowsappsdk/1.7/latest/windowsappruntimeinstall-x64.exe";

    private static bool _initialized;

    /// <summary>返回 false 表示依赖缺失、已提示用户，调用方应立即退出。</summary>
    public static bool Initialize()
    {
        int hr;
        try
        {
            hr = MddBootstrapInitialize2(MajorMinor, "", default, 0);
        }
        catch (DllNotFoundException)
        {
            ShowMissingDialog(Strings.RuntimeBootstrapMissing);
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            ShowMissingDialog(Strings.RuntimeBootstrapIncompatible);
            return false;
        }

        if (hr == 0)
        {
            _initialized = true;
            return true;
        }

        ShowMissingDialog(Strings.RuntimeMissingVersion(hr));
        return false;
    }

    public static void Shutdown()
    {
        if (_initialized)
        {
            _initialized = false;
            try
            {
                MddBootstrapShutdown();
            }
            catch (DllNotFoundException)
            {
                // 进程即将退出，忽略
            }
        }
    }

    private static void ShowMissingDialog(string reason)
    {
        var text = Strings.RuntimeMissingBody(reason);

        var choice = MessageBox(IntPtr.Zero, text, Strings.RuntimeMissingCaption,
            MB_YESNO | MB_ICONWARNING | MB_SETFOREGROUND);
        if (choice == IDYES)
        {
            OpenUrl(DownloadPage);
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception
                                  or InvalidOperationException)
        {
            // 没有默认浏览器时无能为力，提示文本里已含说明
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PackageVersion
    {
        public ulong Version;
    }

    [DllImport("Microsoft.WindowsAppRuntime.Bootstrap.dll", CharSet = CharSet.Unicode)]
    private static extern int MddBootstrapInitialize2(
        uint majorMinorVersion, string versionTag, PackageVersion minVersion, int options);

    [DllImport("Microsoft.WindowsAppRuntime.Bootstrap.dll")]
    private static extern void MddBootstrapShutdown();

    private const uint MB_YESNO = 0x00000004;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_SETFOREGROUND = 0x00010000;
    private const int IDYES = 6;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
