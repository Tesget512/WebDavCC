# WebDavCC

一个通过 HTTPS 把本地文件夹共享为 WebDAV 的 Windows 工具。

[English](README.md) | 简体中文

## 功能

- HTTPS 的 WebDAV（TLS 1.2/1.3），不需要管理员权限
- 支持 Range 请求，拖动进度条不用先下载整个文件
- 首次运行自动生成自签名证书，SAN 里包含本机的局域网 IP
- 只读模式、HTTP Basic 认证，或者在可信网络里直接开放
- 可共享多个文件夹，各自挂在不同名称下
- 托盘常驻，可选登录后启动或系统启动即运行
- 中英文界面，跟随系统语言
- 除 WebDAV 外还提供网页版目录列表，浏览器可直接打开

已实现的方法：`OPTIONS`、`GET`、`HEAD`、`POST`、`PUT`、`DELETE`、`PROPFIND`、
`PROPPATCH`、`MKCOL`、`COPY`、`MOVE`、`LOCK`、`UNLOCK`。其中 `LOCK` 和 `PROPPATCH` 是必需的，
Windows 资源管理器往映射的网络驱动器里拷文件时会用到，缺了会报错。

## 运行环境

Windows 10 1809（build 17763）或更高版本，另需：

1. **.NET Desktop Runtime 10.0 (x64)** — https://dotnet.microsoft.com/download/dotnet/10.0
2. **Windows App Runtime 1.7 (x64)** —
   https://aka.ms/windowsappsdk/1.7/latest/windowsappruntimeinstall-x64.exe

请运行官方的 `WindowsAppRuntimeInstall-x64.exe`。只装 Framework MSIX 包是不够的：非打包应用
要靠 Main 和 DDLM 配套包才能定位到运行时，缺了程序起不来。

如果缺少任一运行时，WebDavCC 会弹窗说明缺什么并给出下载链接，而不是直接闪退。

## 使用

1. 下载发布包，解压到任意位置。
2. 运行 `WebDavCC.exe`。
3. 点「添加」选择文件夹。双击列表项可以改挂载名，这是客户端看到的名称，用英文兼容性最好。
4. 设置端口；如果网络不完全可信，再设置用户名和密码。
5. 点「启动服务」。地址栏会显示可用的访问地址，复制按钮会把局域网地址放进剪贴板。

用户名留空意味着同网络任何人都能读取共享内容。

### 命令行参数

```
WebDavCC.exe --autostart     静默启动到托盘并立即开启服务
WebDavCC.exe --lang=en       强制英文界面
WebDavCC.exe --lang=zh       强制中文界面
```

### 程序在 exe 同级目录生成的文件

```
config.json     配置，改动时自动保存
certs/          server.crt 和 server.pfx，首次启动时生成
webdav.log      错误和启停记录，上限 2 MB
```

如果程序目录不可写（比如装在 `Program Files` 下），这些文件会自动改到
`%LOCALAPPDATA%\WebDavShare`。

## 关于证书

证书是自签名的，客户端会提示不受信任。要么在客户端里选择信任，要么用工具栏的导出按钮导出
`.crt`，安装到客户端的受信任根证书区（Windows 上：双击 → 安装证书 → 本地计算机 →
受信任的根证书颁发机构）。

**需要注意**：有些程序不读系统证书存储，而是自带一份根证书列表。比如用 Rust 的 `rustls`
且启用默认 `webpki-roots` 特性的客户端，只信任编译进二进制的 Mozilla CA 列表，这种情况下把
证书导入 Windows 是没用的。如果全局安装证书后客户端仍然连不上，多半就是这个原因。

## 客户端配置

| 客户端 | 说明 |
| --- | --- |
| Infuse、nPlayer、VLC | 添加 WebDAV 源，勾选接受不受信任的证书 |
| Kodi | 源类型选「Web 服务器目录 (HTTPS)」 |
| Windows 资源管理器 | 需要先把 `server.crt` 导入受信任的根证书，否则映射会失败 |
| 浏览器 | 直接打开地址即可看到目录列表 |

## 编译

需要 Visual Studio 2022 或更新版本（含 .NET 桌面开发工作负载）以及 .NET 10 SDK。

```
build.bat      编译到 bin\
publish.bat    生成可分发产物到 dist\
```

在某些机器上 `dotnet build` 会因缺少 `Microsoft.Build.Packaging.Pri.Tasks.dll` 而失败。
脚本调用的是 Visual Studio 自带的 MSBuild，它有这个文件。如果 VS 装在非默认路径，改一下两个
脚本开头的 `MSBUILD` 变量即可。

## 项目结构

```
Core/
  WebDavServer.cs           TLS 监听、认证、路径映射、响应输出
  WebDavServer.Files.cs     GET（含 Range）、PUT、DELETE、MKCOL、COPY、MOVE
  WebDavServer.Propfind.cs  PROPFIND、PROPPATCH、LOCK、UNLOCK
  HttpParser.cs             请求解析、分块传输
  CertificateManager.cs     自签名证书生成
  AutoStartManager.cs       注册表 Run 项与计划任务
  TrayIcon.cs               Shell_NotifyIcon 封装
  Strings.cs                中英文界面文案
  ConfigStore.cs            配置持久化
MainWindow.xaml(.cs)        界面
Program.cs                  入口、运行时检查、单实例
```

服务端用的是 `TcpListener` + `SslStream`，而不是 `HttpListener`。后者绑定 HTTPS 需要
`netsh http add sslcert` 和管理员授权，那样就违背了「拷过去就能跑」的初衷。

## 说明

代码主要由 Claude 编写。协议行为、Range、锁、路径处理以及发布产物在干净目录下的运行都测过，
但没有做过安全审计，也没有长期使用验证。用在敏感场景前请先自己读一遍代码。

欢迎提 issue 和 PR。

## 许可证

MIT
