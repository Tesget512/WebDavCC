using System.Globalization;

namespace WebDavShare.Core;

/// <summary>
/// 界面文案。跟随系统语言，中文以外一律用英文。
///
/// 没用 .resw/PRI：非打包 WinUI 应用加载 PRI 资源需要额外的清单与部署配置，
/// 对一个双语工具来说不值得，静态字典足够且启动更快。
/// </summary>
public static class Strings
{
    private static readonly bool UseChinese = DetectChinese();

    private static bool DetectChinese()
    {
        // 命令行可强制语言：--lang=en / --lang=zh，方便用户自选与自动化测试
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (arg.StartsWith("--lang=", StringComparison.OrdinalIgnoreCase))
            {
                return arg[7..].StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            }
        }
        try
        {
            return CultureInfo.CurrentUICulture.Name
                .StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    /// <summary>zh 时取第一个参数，否则取第二个。</summary>
    private static string Pick(string zh, string en) => UseChinese ? zh : en;

    // ---------------- 窗口与标题 ----------------
    public static string AppTitle => Pick("WebDavCC 共享", "WebDavCC Share");
    public static string WindowTitle => "WebDavCC";

    // ---------------- 状态区 ----------------
    public static string StatusStopped => Pick("未运行", "Stopped");
    public static string StatusRunning => Pick("运行中", "Running");
    public static string StatusStarting => Pick("正在启动…", "Starting...");
    public static string StatusStartFailed => Pick("启动失败", "Failed to start");
    public static string StatusHintIdle =>
        Pick("添加共享文件夹后点击启动", "Add a folder to share, then click Start");
    public static string StatusHintStarting =>
        Pick("正在生成证书并绑定端口", "Generating certificate and binding port");

    public static string ButtonStart => Pick("启动服务", "Start");
    public static string ButtonStop => Pick("停止服务", "Stop");

    public static string TipCopyUrl => Pick("复制访问地址", "Copy address");
    public static string TipOpenBrowser => Pick("浏览器打开", "Open in browser");
    public static string TipExportCert => Pick("导出证书", "Export certificate");

    // ---------------- 共享文件夹 ----------------
    public static string SharedFolders => Pick("共享文件夹", "Shared folders");
    public static string SharedFoldersHint =>
        Pick("双击可重命名挂载点", "Double-click an entry to rename its mount point");
    public static string ButtonAdd => Pick("添加", "Add");
    public static string ButtonRemove => Pick("移除", "Remove");

    public static string PickFolderTitle => Pick("选择要共享的文件夹", "Choose a folder to share");
    public static string DuplicateTitle => Pick("已存在", "Already added");
    public static string DuplicateBody =>
        Pick("该文件夹已在共享列表中。", "That folder is already in the list.");

    public static string RenameTitle => Pick("重命名挂载点", "Rename mount point");
    public static string RenamePrompt => Pick(
        "客户端看到的名称（英文更兼容）：",
        "Name shown to clients (ASCII names are the most compatible):");
    public static string InvalidNameTitle => Pick("名称无效", "Invalid name");
    public static string InvalidNameBody => Pick(
        "挂载名不能包含 \\ / : * ? \" < > | 等字符，也不能是 . 或 ..",
        "Mount names cannot contain \\ / : * ? \" < > | or be \".\" or \"..\"");
    public static string NameTakenTitle => Pick("名称重复", "Name already used");
    public static string NameTakenBody(string name) => Pick(
        $"「{name}」已被占用。", $"\"{name}\" is already in use.");

    // ---------------- 设置 ----------------
    public static string ConnectionSettings => Pick("连接设置", "Connection");
    public static string LabelPort => Pick("端口", "Port");
    public static string LabelUser => Pick("用户名", "Username");
    public static string LabelPassword => Pick("密码", "Password");
    public static string PlaceholderUser => Pick("留空 = 免登录", "Empty = no login");
    public static string PlaceholderPassword =>
        Pick("填了用户名就必须设密码", "Required when a username is set");

    public static string ReadOnlyMode => Pick("只读模式", "Read-only");
    public static string ReadOnlyOn =>
        Pick("客户端仅可读取，无法上传或删改",
             "Clients can only read; uploads and deletions are blocked");
    public static string ReadOnlyOff =>
        Pick("客户端可以上传、修改、删除文件",
             "Clients can upload, modify and delete files");

    public static string AutoStart => Pick("开机自动启动", "Start with Windows");
    public static string AutoStartMethod => Pick("自启方式", "Startup method");
    public static string AutoStartLogon =>
        Pick("登录后启动（无需管理员）", "After sign-in (no admin needed)");
    public static string AutoStartBoot =>
        Pick("系统启动即运行（需管理员）", "At system boot (requires admin)");
    public static string MinimizeToTray =>
        Pick("关闭窗口时最小化到托盘", "Minimize to tray instead of closing");

    // ---------------- 日志 ----------------
    public static string RunLog => Pick("运行日志", "Log");
    public static string ButtonClear => Pick("清空", "Clear");
    public static string NoPasswordWarning => Pick(
        "未设置密码，同网络任何人都能访问",
        "No password set - anyone on this network can connect");

    public static string WelcomeLine1 => Pick(
        "欢迎使用。添加共享文件夹后点击「启动服务」即可。",
        "Welcome. Add a folder to share, then click Start.");
    public static string WelcomeLine2 => Pick(
        "首次启动会自动生成 HTTPS 证书，约需一秒。",
        "An HTTPS certificate is generated on first start; this takes about a second.");
    public static string ConfigLoaded(int count) => Pick(
        $"已载入上次的配置（{count} 个共享）",
        $"Loaded previous configuration ({count} share(s))");

    public static string ShareAdded(string name, string path) => Pick(
        $"已添加共享: {name} -> {path}", $"Added share: {name} -> {path}");
    public static string ServerStarted(string mode, string account) => Pick(
        $"服务已启动 [{mode}]  账号: {account}",
        $"Server started [{mode}]  account: {account}");
    public static string ServerStopped => Pick("服务已停止", "Server stopped");
    public static string StartFailed(string reason) => Pick(
        "启动失败: " + reason, "Failed to start: " + reason);
    public static string CopiedToClipboard(string url) => Pick(
        "已复制到剪贴板: " + url, "Copied to clipboard: " + url);
    public static string CertExported(string path) => Pick(
        "证书已导出: " + path, "Certificate exported: " + path);
    public static string ConfigSaveFailed(string reason) => Pick(
        "配置保存失败: " + reason, "Could not save configuration: " + reason);
    public static string UnhandledError(string reason) => Pick(
        "发生未处理异常: " + reason, "Unhandled error: " + reason);

    public static string ModeReadOnly => Pick("只读", "read-only");
    public static string ModeReadWrite => Pick("可读写", "read-write");
    public static string AccountAnonymous => Pick("匿名（无需登录）", "anonymous (no login)");

    // ---------------- 对话框 ----------------
    public static string DialogOk => Pick("确定", "OK");
    public static string DialogCancel => Pick("取消", "Cancel");
    public static string DialogClose => Pick("知道了", "Got it");

    public static string NoSharesTitle => Pick("没有共享", "No shares");
    public static string NoSharesBody => Pick(
        "请先添加至少一个要共享的文件夹。", "Add at least one folder to share first.");
    public static string MissingDirTitle => Pick("目录不存在", "Folder missing");
    public static string MissingDirBody(string list) => Pick(
        "以下目录已不可用：\n" + list,
        "These folders are no longer available:\n" + list);
    public static string MissingPasswordTitle => Pick("缺少密码", "Password required");
    public static string MissingPasswordBody => Pick(
        "填写了用户名就必须设置密码。",
        "A password is required when a username is set.");
    public static string NotRunningTitle => Pick("未运行", "Not running");
    public static string NotRunningBody => Pick("请先启动服务。", "Start the server first.");

    public static string PortInUse(int port) => Pick(
        $"端口 {port} 已被其他程序占用。\n\n换一个端口（比如 8444、9443）再试。",
        $"Port {port} is already in use.\n\nTry a different port, for example 8444 or 9443.");
    public static string PortInUseByInstance(int port) => Pick(
        $"端口 {port} 已被占用，可能有另一个 WebDavCC 实例正在运行。\n\n"
        + "比如「系统启动即运行」的后台实例：它没有托盘图标，"
        + "请在任务管理器里结束 WebDavCC 后再试。",
        $"Port {port} is in use; another WebDavCC instance may be running.\n\n"
        + "For example a background instance started with Windows - it has no tray icon. "
        + "End WebDavCC in Task Manager first.");
    public static string PortDenied(int port) => Pick(
        $"端口 {port} 被系统拒绝。\n\n1024 以下的端口需要管理员权限，建议改用 8443 这类高位端口。",
        $"Port {port} was refused by the system.\n\n"
        + "Ports below 1024 require administrator rights; use a high port such as 8443.");

    public static string NoCertTitle => Pick("还没有证书", "No certificate yet");
    public static string NoCertBody => Pick(
        "先启动一次服务，程序会自动生成证书。",
        "Start the server once and a certificate will be generated automatically.");
    public static string ExportOkTitle => Pick("导出成功", "Exported");
    public static string ExportOkBody(string path) => Pick(
        $"证书已保存到：\n{path}\n\n"
        + "把它拷到客户端设备安装为「受信任的根证书」，就不会再提示证书不安全。\n\n"
        + "Windows：双击 → 安装证书 → 本地计算机 → 受信任的根证书颁发机构",
        $"Certificate saved to:\n{path}\n\n"
        + "Install it on the client device as a trusted root certificate to stop "
        + "the security warning.\n\n"
        + "Windows: double-click -> Install Certificate -> Local Machine -> "
        + "Trusted Root Certification Authorities");
    public static string ExportDialogTitle => Pick("导出证书", "Export certificate");
    public static string CertFileType => Pick("证书文件", "Certificate");

    public static string AutoStartFailedTitle => Pick("设置自启失败", "Could not change startup");

    public static string ExitConfirmTitle => Pick("退出", "Quit");
    public static string ExitConfirmBody => Pick(
        "服务正在运行，退出会断开所有客户端连接。\n\n确定退出吗？",
        "The server is running. Quitting will disconnect all clients.\n\nQuit anyway?");

    // ---------------- 托盘 ----------------
    public static string TrayTooltipIdle => Pick("WebDavCC — 未运行", "WebDavCC - stopped");
    public static string TrayTooltipRunning(string url) => Pick(
        $"WebDavCC — 运行中\n{url}", $"WebDavCC - running\n{url}");
    public static string TrayOpen => Pick("打开管理界面", "Open window");
    public static string TrayCopyUrl => Pick("复制访问地址", "Copy address");
    public static string TrayStart => Pick("启动服务", "Start server");
    public static string TrayStop => Pick("停止服务", "Stop server");
    public static string TrayExit => Pick("退出", "Quit");
    public static string TrayStillRunningTitle =>
        Pick("WebDavCC 仍在运行", "WebDavCC is still running");
    public static string TrayStillRunningBody => Pick(
        "服务继续在后台提供访问。右键托盘图标可停止或退出。",
        "The server keeps running in the background. "
        + "Right-click the tray icon to stop or quit.");
    public static string TrayStartFailedTitle =>
        Pick("WebDavCC 启动失败", "WebDavCC failed to start");
    public static string TrayStartedTitle =>
        Pick("WebDavCC 已在后台运行", "WebDavCC is running in the background");
    public static string TrayStartedBody(string url) => Pick(
        $"开机自启动成功，服务正在提供访问。\n{url}",
        $"Started automatically with Windows.\n{url}");

    // ---------------- 自启动 ----------------
    public static string AutoStartDisabled => Pick("已关闭开机自启", "Startup entry removed");
    public static string AutoStartLogonSet =>
        Pick("已设置为登录后自动启动", "Will start automatically after sign-in");
    public static string AutoStartBootSet => Pick(
        "已设置为系统启动即运行（无需登录）",
        "Will start at system boot (no sign-in needed)");
    public static string AutoStartCancelled =>
        Pick("已取消管理员授权，未能设置", "Administrator approval was cancelled");
    public static string AutoStartTaskFailed(string detail) => Pick(
        "创建计划任务失败: " + detail, "Could not create scheduled task: " + detail);
    public static string AutoStartRegistryFailed =>
        Pick("无法写入注册表", "Could not write to the registry");
    public static string AutoStartGenericFailed(string detail) => Pick(
        "设置失败: " + detail, "Failed: " + detail);
    public static string AutoStartNoShares => Pick(
        "开机自启：没有可用的共享文件夹，服务未启动",
        "Autostart: no usable shared folders, server not started");
    public static string AutoStartTriggered => Pick(
        "由开机自启触发，正在后台启动服务…",
        "Triggered by autostart, starting server in the background...");

    // ---------------- 证书 ----------------
    public static string CertExpiring =>
        Pick("证书即将过期，正在重新生成…", "Certificate is expiring, regenerating...");
    public static string CertCorrupt =>
        Pick("证书已损坏，正在重新生成…", "Certificate is unreadable, regenerating...");
    public static string CertGenerating =>
        Pick("正在生成自签名证书…", "Generating self-signed certificate...");
    public static string CertGenerated(DateTime until) => Pick(
        $"证书已生成，有效期至 {until:yyyy-MM-dd}",
        $"Certificate generated, valid until {until:yyyy-MM-dd}");

    // ---------------- 运行时检查 ----------------
    public static string RuntimeMissingCaption =>
        Pick("WebDavCC — 缺少运行时组件", "WebDavCC - missing runtime");
    public static string RuntimeBootstrapMissing => Pick(
        "找不到 Windows App Runtime 引导程序。",
        "The Windows App Runtime bootstrapper could not be found.");
    public static string RuntimeBootstrapIncompatible => Pick(
        "Windows App Runtime 引导程序版本不兼容。",
        "The Windows App Runtime bootstrapper is incompatible.");
    public static string RuntimeMissingVersion(int hr) => Pick(
        $"系统缺少 Windows App Runtime 1.7 运行时组件（错误码 0x{hr:X8}）。",
        $"Windows App Runtime 1.7 is missing (error 0x{hr:X8}).");
    public static string RuntimeMissingBody(string reason) => Pick(
        $"""
        无法启动 WebDavCC。

        {reason}

        本程序需要以下运行时组件：
          - Windows App Runtime 1.7（含 Main / DDLM 配套包）
          - .NET Desktop Runtime 10.0

        注意：只安装 Framework 包不够，必须运行官方安装器
        WindowsAppRuntimeInstall-x64.exe 才会一并注册配套包。

        是否现在打开下载页面？
        """,
        $"""
        WebDavCC cannot start.

        {reason}

        This application requires:
          - Windows App Runtime 1.7 (including the Main / DDLM packages)
          - .NET Desktop Runtime 10.0

        Note: installing only the Framework package is not enough. Run the
        official WindowsAppRuntimeInstall-x64.exe so the companion packages
        are registered as well.

        Open the download page now?
        """);

    // ---------------- 浏览器目录页 ----------------
    public static string WebSharedFolders => Pick("共享目录", "Shared folders");
    public static string WebParentDir => Pick("上一级", "Parent directory");
}
