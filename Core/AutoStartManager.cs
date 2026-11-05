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
                                  or System.ComponentModel.Win32Exception
                                  or InvalidOperationException)
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
        var result = RunSchTasks($"/Query /TN \"{TaskName}\" /XML", elevate: false);
        if (result.ExitCode != 0)
        {
            return false;
        }
        // 旧版本用 "/SC ONSTART /RU <user>"（不带 /RP）注册的任务落成
        // LogonType=InteractiveToken：开机时没人登录，这种任务永远不会被触发，
        // 却一直显示"已启用"误导用户。只认 S4U（免密、登录前后都能跑）的任务。
        return result.Output.Contains("<LogonType>S4U</LogonType>",
            StringComparison.OrdinalIgnoreCase);
    }

    private static (bool, string) EnableBoot()
    {
        // 不能用 "/SC ONSTART /RU <user>" 直接建任务：不给 /RP 时 schtasks 会把
        // 任务落成 LogonType=InteractiveToken，而 InteractiveToken 只能在用户
        // 已登录的交互会话里运行——开机时没人登录，Boot 触发器永远不触发
        // （实测 LastTaskResult=0x41303 "任务尚未运行"）。改用 XML 导入精确控制：
        // S4U 免密登录类型（登录前后都能跑）、不受电池策略限制、去掉默认的
        // 72 小时执行时限（不然服务跑三天就被任务计划程序杀掉）。
        var xmlPath = Path.Combine(Path.GetTempPath(), "WebDavCC_Boot_task.xml");
        var sid = WindowsIdentity.GetCurrent().User?.Value
                  ?? throw new InvalidOperationException("cannot resolve current user SID");
        File.WriteAllText(xmlPath, BuildBootTaskXml(sid, ExecutablePath),
            System.Text.Encoding.Unicode);

        var result = RunSchTasks(
            $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F", elevate: !IsElevated);
        try
        {
            File.Delete(xmlPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 临时文件删不掉无妨
        }

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

    /// <summary>开机任务定义。S4U = 以当前用户身份免密非交互运行，登录前后都有效。</summary>
    private static string BuildBootTaskXml(string sid, string exePath)
    {
        var command = System.Security.SecurityElement.Escape(exePath);
        return $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>WebDavCC start at system boot (background, no tray icon)</Description>
            <URI>\{TaskName}</URI>
          </RegistrationInfo>
          <Principals>
            <Principal id="Author">
              <UserId>{sid}</UserId>
              <LogonType>S4U</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>true</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Triggers>
            <BootTrigger>
              <Enabled>true</Enabled>
            </BootTrigger>
          </Triggers>
          <Actions Context="Author">
            <Exec>
              <Command>{command}</Command>
              <Arguments>--autostart</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;
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
            if (!process.WaitForExit(15000))
            {
                // 超时多半是 schtasks 在等密码输入；不杀会残留，且此时读
                // ExitCode 会抛 InvalidOperationException
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception
                                          or InvalidOperationException)
                {
                    // 进程恰好自己退出了
                }
                return new SchTasksResult(-1, "schtasks timed out", false);
            }
            return new SchTasksResult(process.ExitCode, output.Trim(), false);
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return new SchTasksResult(-1, "", Cancelled: true);   // 用户点了"否"
        }
    }
}
