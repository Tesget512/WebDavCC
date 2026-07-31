# WebDavCC

A Windows tool that shares a local folder over WebDAV with HTTPS.

English | [简体中文](README.zh-CN.md)

## Features

- WebDAV over HTTPS (TLS 1.2/1.3), no administrator rights required
- Byte-range requests, so seeking in a video works without downloading it first
- Self-signed certificate generated on first run, including the machine's LAN IPs in the SAN
- Read-only mode, HTTP Basic authentication, or open access on a trusted network
- Multiple folders, each mounted under its own name
- System tray operation with optional start on sign-in or at system boot
- English and Chinese interface, following the system language
- Browser-friendly directory listing in addition to the WebDAV endpoints

Implemented methods: `OPTIONS`, `GET`, `HEAD`, `POST`, `PUT`, `DELETE`, `PROPFIND`,
`PROPPATCH`, `MKCOL`, `COPY`, `MOVE`, `LOCK`, `UNLOCK`. `LOCK` and `PROPPATCH` are included
because Windows Explorer needs them to copy files onto a mapped drive.

## Requirements

Windows 10 1809 (build 17763) or later, plus:

1. **.NET Desktop Runtime 10.0 (x64)** — https://dotnet.microsoft.com/download/dotnet/10.0
2. **Windows App Runtime 1.7 (x64)** —
   https://aka.ms/windowsappsdk/1.7/latest/windowsappruntimeinstall-x64.exe

Run the official `WindowsAppRuntimeInstall-x64.exe`. Installing only the Framework MSIX is
not enough: the Main and DDLM companion packages are what let an unpackaged app locate the
runtime, and without them the app cannot start.

If either runtime is missing, WebDavCC shows a dialog naming the missing component with a
link to the download page instead of failing silently.

## Usage

1. Download the release archive and extract it anywhere.
2. Run `WebDavCC.exe`.
3. Click **Add** and choose a folder. Double-click an entry to rename its mount point —
   this is the name clients will see, and ASCII names are the most compatible.
4. Set a port, and a username and password if the network is not fully trusted.
5. Click **Start**. The address bar shows the URLs to use, and the copy button puts the LAN
   address on the clipboard.

Leaving the username empty means anyone on the same network can read the shares.

### Command line

```
WebDavCC.exe --autostart     Start minimized to tray and launch the server immediately
WebDavCC.exe --lang=en       Force English
WebDavCC.exe --lang=zh       Force Chinese
```

### Files created next to the executable

```
config.json     Settings, written when you change them
certs/          server.crt and server.pfx, generated on first start
webdav.log      Errors and start/stop events, capped at 2 MB
```

If the program directory is not writable (for example under `Program Files`), these move to
`%LOCALAPPDATA%\WebDavShare` automatically.

## Certificates

The certificate is self-signed, so clients will warn that it is not trusted. Either accept
it in the client, or export it with the toolbar button and install the `.crt` into the
client's trusted root store (on Windows: double-click, Install Certificate, Local Machine,
Trusted Root Certification Authorities).

**Caveat:** some applications bundle their own certificate roots instead of reading the
system store. Anything built on Rust's `rustls` with the default `webpki-roots` feature,
for example, only trusts the Mozilla CA list compiled into the binary, so importing the
certificate into Windows will not help those clients. If a client still refuses to connect
after a system-wide install, this is likely why.

## Client setup

| Client | Notes |
| --- | --- |
| Infuse, nPlayer, VLC | Add a WebDAV source, accept the untrusted certificate |
| Kodi | Source type "Web server directory (HTTPS)" |
| Windows Explorer | Import `server.crt` into Trusted Root first, otherwise the mapping fails |
| Browser | Open the address directly for a plain directory listing |

## Building

Requires Visual Studio 2022 or newer with the .NET desktop workload, and the .NET 10 SDK.

```
build.bat      Build into bin\
publish.bat    Redistributable output into dist\
```

`dotnet build` may fail on some machines with a missing
`Microsoft.Build.Packaging.Pri.Tasks.dll`. The scripts call the MSBuild that ships with
Visual Studio, which has it. If Visual Studio is installed somewhere other than the default
path, edit the `MSBUILD` variable at the top of both scripts.

## Project layout

```
Core/
  WebDavServer.cs           TLS listener, auth, path mapping, response writing
  WebDavServer.Files.cs     GET with byte ranges, PUT, DELETE, MKCOL, COPY, MOVE
  WebDavServer.Propfind.cs  PROPFIND, PROPPATCH, LOCK, UNLOCK
  HttpParser.cs             Request parsing, chunked bodies
  CertificateManager.cs     Self-signed certificate generation
  AutoStartManager.cs       Registry Run entry and scheduled task
  TrayIcon.cs               Shell_NotifyIcon wrapper
  Strings.cs                English and Chinese interface text
  ConfigStore.cs            Settings persistence
MainWindow.xaml(.cs)        Interface
Program.cs                  Entry point, runtime check, single instance
```

The server uses `TcpListener` with `SslStream` rather than `HttpListener`. Binding HTTPS
through `HttpListener` requires `netsh http add sslcert` and an elevated prompt, which
would defeat the point of a copy-and-run tool.

## Notes

Most of this was written with Claude. It has been tested — protocol behaviour, byte ranges,
locking, path handling and a clean-directory run of the published build — but it has not
had a security audit or long-term use. Read the code before pointing it at anything
sensitive.

Issues and pull requests are welcome.

## License

MIT
