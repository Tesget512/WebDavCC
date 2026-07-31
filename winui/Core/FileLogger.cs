namespace WebDavShare.Core;

/// <summary>
/// 文件日志。托盘后台运行时界面可能没打开，出问题需要有地方可查。
/// 单文件滚动，超过上限就截断前半部分。
/// </summary>
public static class FileLogger
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly Lock Gate = new();
    private static string? _path;

    public static string Path
    {
        get
        {
            if (_path is null)
            {
                Directory.CreateDirectory(ConfigStore.DataDir);
                _path = System.IO.Path.Combine(ConfigStore.DataDir, "webdav.log");
            }
            return _path;
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
                File.AppendAllText(Path, line);
                Trim();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 日志写不进去不该拖垮主流程
        }
    }

    private static void Trim()
    {
        var info = new FileInfo(Path);
        if (!info.Exists || info.Length <= MaxBytes)
        {
            return;
        }
        var lines = File.ReadAllLines(Path);
        File.WriteAllLines(Path, lines.Skip(lines.Length / 2));
    }
}
