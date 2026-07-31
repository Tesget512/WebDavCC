using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace WebDavShare.Core;

/// <summary>
/// 开机自启。两种方式：
///   Logon —— HKCU Run 项，无需管理员，用户可在任务管理器"启动"页自行禁用。
///   Boot  —— 计划任务，系统启动即运行（无需登录），注册时需要一次管理员提权。
/// </summary>
public static class AutoStartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WebDavCC";
    private const string TaskName = "WebDavCC_Boot";

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WebDavCC.exe");

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    public static bool IsEnabled(AutoStartMode mode) =>
        mode == AutoStartMode.Logon ? IsLogonEnabled() : IsBootEnabled();

    /// <summary>应用自启设置。返回 (成功, 提示信息)。</summary>
    public static (bool Success, string Message) Apply(bool enabled, AutoStartMode mode)
    {
        try
        {
            // 切换模式时先清掉另一种，避免两个入口同时存在导致启动两次
            if (enabled)
            {
                var other = mode == AutoStartMode.Logon ? AutoStartMode.Boot : AutoStartMode.Logon;
                Disable(other);
            }
            else
            {
                Disable(AutoStartMode.Logon);
                Disable(AutoStartMode.Boot);
                return (true, Strings.AutoStartDisabled);
            }

            return mode == AutoStartMode.Logon ? EnableLogon() : EnableBoot();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException
                                  or System.ComponentModel.Win32Exception)
        {
            return (false, Strings.AutoStartGenericFailed(e.Message));
        }
    }

    private static void Disable(AutoStartMode mode)
    {
        if (mode == AutoStartMode.Logon)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        else if (IsBootEnabled())
        {
            RunSchTasks($"/Delete /TN \"{TaskName}\" /F", elevate: !IsElevated);
        }
    }

    // ---------------- 登录后启动 ----------------

    private static bool IsLogonEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Contains(ExecutablePath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static (bool, string) EnableLogon()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (key is null)
        {
            return (false, Strings.AutoStartRegistryFailed);
        }
        key.SetValue(ValueName, $"\"{ExecutablePath}\" --autostart");
        return (true, Strings.AutoStartLogonSet);
    }

    // ---------------- 系统启动即运行 ----------------

    private static bool IsBootEnabled()
    {
        var result = RunSchTasks($"/Query /TN \"{TaskName}\"", elevate: false);
        return result.ExitCode == 0;
    }

    private static (bool, string) EnableBoot()
    {
        // /RL HIGHEST + /SC ONSTART 需要管理员，未提权时用 runas 拉起
        var user = WindowsIdentity.GetCurrent().Name;
        var arguments = $"/Create /TN \"{TaskName}\" /TR \"\\\"{ExecutablePath}\\\" --autostart\" "
                        + $"/SC ONSTART /RU \"{user}\" /RL HIGHEST /F";

        var result = RunSchTasks(arguments, elevate: !IsElevated);
        if (result.ExitCode == 0)
        {
            return (true, Strings.AutoStartBootSet);
        }
        if (result.Cancelled)
        {
            return (false, Strings.AutoStartCancelled);
        }
        return (false, Strings.AutoStartTaskFailed(result.Output));
    }

    private sealed record SchTasksResult(int ExitCode, string Output, bool Cancelled);

    private static SchTasksResult RunSchTasks(string arguments, bool elevate)
    {
        var info = new ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = elevate,
        };
        if (elevate)
        {
            info.Verb = "runas";       // 触发 UAC
            info.WindowStyle = ProcessWindowStyle.Hidden;
        }
        else
        {
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return new SchTasksResult(-1, "schtasks could not be started", false);
            }
            var output = "";
            if (!elevate)
            {
                output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            }
            process.WaitForExit(15000);
            return new SchTasksResult(process.ExitCode, output.Trim(), false);
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return new SchTasksResult(-1, "", Cancelled: true);   // 用户点了"否"
        }
    }
}
