using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebDavShare.Core;

/// <summary>服务配置。可序列化为 config.json 持久化。</summary>
public sealed class ServerConfig
{
    public int Port { get; set; } = 8443;
    public bool ReadOnly { get; set; } = true;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>挂载名 -> 本地绝对路径。</summary>
    public Dictionary<string, string> Shares { get; set; } = new();

    public bool AutoStartEnabled { get; set; }
    public AutoStartMode AutoStartMode { get; set; } = AutoStartMode.Logon;
    public bool StartServiceOnLaunch { get; set; } = true;
    public bool StartMinimizedToTray { get; set; } = true;

    [JsonIgnore]
    public bool RequiresAuth => !string.IsNullOrEmpty(User);

    public ServerConfig Clone() => new()
    {
        Port = Port,
        ReadOnly = ReadOnly,
        User = User,
        Password = Password,
        Shares = new Dictionary<string, string>(Shares),
        AutoStartEnabled = AutoStartEnabled,
        AutoStartMode = AutoStartMode,
        StartServiceOnLaunch = StartServiceOnLaunch,
        StartMinimizedToTray = StartMinimizedToTray,
    };
}

public enum AutoStartMode
{
    /// <summary>HKCU Run 项，登录后启动，无需管理员。</summary>
    Logon,
    /// <summary>计划任务，系统启动即运行，需管理员注册一次。</summary>
    Boot,
}

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DataDir
    {
        get
        {
            var exe = Environment.ProcessPath;
            var dir = exe is null ? AppContext.BaseDirectory : Path.GetDirectoryName(exe)!;
            // 程序目录不可写时（比如装在 Program Files）退回 AppData
            return IsWritable(dir)
                ? dir
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WebDavShare");
        }
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".w{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string ConfigPath => Path.Combine(DataDir, "config.json");
    public static string CertDir => Path.Combine(DataDir, "certs");

    public static ServerConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<ServerConfig>(json, Options);
                if (cfg is not null)
                {
                    // 目录可能已被删除或移动
                    foreach (var key in cfg.Shares.Where(p => !Directory.Exists(p.Value))
                                                 .Select(p => p.Key).ToList())
                    {
                        cfg.Shares.Remove(key);
                    }
                    return cfg;
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // 配置损坏时用默认值，不阻断启动
        }
        return new ServerConfig();
    }

    public static void Save(ServerConfig config)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, Options));
    }
}
