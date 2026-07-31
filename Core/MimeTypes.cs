namespace WebDavShare.Core;

/// <summary>MIME 映射。视频类型必须准确，否则部分播放器拒绝流式播放。</summary>
public static class MimeTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mkv"] = "video/x-matroska",
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/x-m4v",
        [".mov"] = "video/quicktime",
        [".avi"] = "video/x-msvideo",
        [".wmv"] = "video/x-ms-wmv",
        [".flv"] = "video/x-flv",
        [".ts"] = "video/mp2t",
        [".m2ts"] = "video/mp2t",
        [".webm"] = "video/webm",
        [".mpg"] = "video/mpeg",
        [".mpeg"] = "video/mpeg",
        [".rmvb"] = "application/vnd.rn-realmedia-vbr",
        [".3gp"] = "video/3gpp",

        [".mp3"] = "audio/mpeg",
        [".flac"] = "audio/flac",
        [".aac"] = "audio/aac",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".m4a"] = "audio/mp4",

        [".srt"] = "application/x-subrip",
        [".ass"] = "text/x-ssa",
        [".ssa"] = "text/x-ssa",
        [".vtt"] = "text/vtt",
        [".sub"] = "text/plain",

        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",

        [".txt"] = "text/plain; charset=utf-8",
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".css"] = "text/css",
        [".js"] = "text/javascript",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".7z"] = "application/x-7z-compressed",
        [".rar"] = "application/vnd.rar",
        [".iso"] = "application/x-iso9660-image",
    };

    public static string ForPath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Length > 0 && Map.TryGetValue(extension, out var mime)
            ? mime
            : "application/octet-stream";
    }
}
