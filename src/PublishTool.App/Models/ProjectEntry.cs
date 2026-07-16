using System;
using System.Text.Json.Serialization;

using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PublishTool.App.Models;

public enum ProjectType
{
    Console,
    WinForms,
    Wpf,
    Android,
    Vue,
    BlazorWasm,
    BlazorServer
}

public enum BuildStatus
{
    Idle,
    BuildingDebug,
    BuildingRelease,
    Publishing,
    Waiting
}

public class ProjectEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = string.Empty;
    public string ProjectPath { get; set; } = string.Empty;
    public ProjectType Type { get; set; }
    [JsonIgnore]
    public ImageSource? TypeIcon =>
        Type switch
        {
            ProjectType.Console => LoadAssetImage("Assets/console.png"),
            ProjectType.WinForms => LoadAssetImage("Assets/winform.png"),
            ProjectType.Wpf => LoadAssetImage("Assets/wpf.png"),
            ProjectType.Android => LoadAssetImage("Assets/android.png"),
            ProjectType.Vue => LoadAssetImage("Assets/vue.png"),
            ProjectType.BlazorWasm or ProjectType.BlazorServer => LoadAssetImage("Assets/blazor.png"),
            _ => null
        };

    private static BitmapImage? LoadAssetImage(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Relative);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }
    public string PublishDir { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; } = DateTime.Now;

    public string? DebugScript { get; set; }
    public string? ReleaseScript { get; set; }
    
    private ImageSource? _iconSource;
    private bool _iconResolved;

    [JsonIgnore]
    public ImageSource? IconSource
    {
        get
        {
            if (!_iconResolved)
            {
                _iconSource = LoadIcon(DetectIcon());
                _iconResolved = true;
            }
            return _iconSource;
        }
    }

    private static ImageSource? LoadIcon(string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath)) return null;

        try
        {
            // OnLoad copies the decoded image into memory before the stream is disposed.
            using var stream = new FileStream(iconPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private string? DetectIcon()
    {
        var projDir = File.Exists(ProjectPath) ? Path.GetDirectoryName(ProjectPath)! : ProjectPath;

        switch (Type)
        {
            case ProjectType.Console:
            case ProjectType.WinForms:
            case ProjectType.Wpf:
                return FindDotNetIcon(ProjectPath, projDir);
            case ProjectType.Android:
                return FindAndroidIcon(projDir);
            case ProjectType.Vue:
              case ProjectType.BlazorWasm:
              case ProjectType.BlazorServer:
                  return FindDotNetIcon(ProjectPath, projDir) ?? FindWebIcon(projDir);
            default:
                return null;
        }
    }

    private static string? FindDotNetIcon(string projectPath, string projDir)
    {
        try
        {
            var csprojFiles = File.Exists(projectPath) &&
                              projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                ? new[] { projectPath }
                : SafeSearch(projDir, "*.csproj");
            foreach (var csproj in csprojFiles)
            {
                var doc = XDocument.Load(csproj);
                XNamespace ns = doc.Root?.GetDefaultNamespace() ?? "";
                var iconEl = doc.Descendants(ns + "ApplicationIcon").FirstOrDefault();
                if (iconEl != null)
                {
                    var iconRelPath = iconEl.Value.Trim();
                    var iconFullPath = Path.Combine(Path.GetDirectoryName(csproj)!, iconRelPath);
                    if (File.Exists(iconFullPath)) return iconFullPath;
                }
            }
        }
        catch { }
        return null;
    }

    private static string? FindAndroidIcon(string projDir)
    {
        try
        {
            var candidates = SafeSearch(projDir, "ic_launcher.png").ToArray();
            var foreground = candidates.FirstOrDefault(f => f.Contains("mipmap"));
            return foreground ?? candidates.FirstOrDefault();
        }
        catch { }
        return null;
    }

    private static string? FindWebIcon(string projDir)
    {
        try
        {
            // 1. public/favicon.ico (Vue convention)
            var favicon = Path.Combine(projDir, "public", "favicon.ico");
            if (File.Exists(favicon)) return favicon;

            // 2. Parse index.html for <link rel="icon"> (highest priority - explicit config)
            var htmlIcon = ParseIndexHtmlIcon(projDir);
            if (htmlIcon != null) return htmlIcon;

            // 3. wwwroot/favicon.ico (Blazor convention)
            var wwwFavicon = Path.Combine(projDir, "wwwroot", "favicon.ico");
            if (File.Exists(wwwFavicon)) return wwwFavicon;

            // 4. Safe recursive search for logo.png / favicon*.png / favicon*.ico
            var logo = SafeSearch(projDir, "logo.png").FirstOrDefault();
            if (logo != null) return logo;
            var faviconPng = SafeSearch(projDir, "favicon*.png").FirstOrDefault();
            if (faviconPng != null) return faviconPng;
            var faviconIco = SafeSearch(projDir, "favicon*.ico").FirstOrDefault();
            return faviconIco;
        }
        catch { }
        return null;
    }

    private static string? ParseIndexHtmlIcon(string projDir)
    {
        try
        {
            var indexPath = Path.Combine(projDir, "wwwroot", "index.html");
            var isWwwroot = true;
            if (!File.Exists(indexPath))
            {
                indexPath = Path.Combine(projDir, "index.html");
                isWwwroot = false;
                if (!File.Exists(indexPath)) return null;
            }
            var html = File.ReadAllText(indexPath);
            var match = System.Text.RegularExpressions.Regex.Match(html,
                @"<link\s+[^>]*rel\s*=\s*""(?:shortcut\s+)?icon""[^>]*href\s*=\s*""([^""]+)""",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            var href = match.Groups[1].Value.Trim();

            // Determine base directory for resolving relative paths
            var baseDir = isWwwroot ? Path.Combine(projDir, "wwwroot") : projDir;

            // If href starts with "/", it's a web-root-relative path → try public/ (Vue convention) then baseDir
            if (href.StartsWith("/"))
            {
                href = href.TrimStart('/');
                var result = Path.GetFullPath(Path.Combine(projDir, "public", href));
                if (File.Exists(result)) return result;
                result = Path.GetFullPath(Path.Combine(baseDir, href));
                if (File.Exists(result)) return result;
                return null;
            }

            var iconPath = Path.GetFullPath(Path.Combine(baseDir, href));
            return File.Exists(iconPath) ? iconPath : null;
        }
        catch { return null; }
    }

    private static IEnumerable<string> SafeSearch(string dir, string pattern)
    {
        var results = new List<string>();
        CollectFiles(dir, pattern, results);
        return results;
    }

    private static void CollectFiles(string dir, string pattern, List<string> results)
    {
        try
        {
            results.AddRange(Directory.GetFiles(dir, pattern));
            foreach (var d in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(d);
                if (name is "bin" or "obj" or "build" or ".git" or ".vs" or "node_modules" or "dist")
                    continue;
                CollectFiles(d, pattern, results);
            }
        }
        catch { }
    }


    
    
    /// <summary>Clear the in-memory icon so it gets re-detected on next access.</summary>
    public void RefreshIcon()
    {
        _iconSource = null;
        _iconResolved = false;
    }

    /// <summary>自定义脚本列表（名称 + 路径）。</summary>
    public List<ScriptEntry> CustomScripts { get; set; } = new();

    public string? PublishScript { get; set; }

    /// <summary>运行时状态，不持久化到 JSON。</summary>
    [JsonIgnore]
    public TimeSpan CurrentTaskElapsed { get; set; }

    [JsonIgnore]
    public BuildStatus Status { get; set; } = BuildStatus.Idle;

    [JsonIgnore]
    public string StatusText => Status switch
    {
        BuildStatus.Idle => "",
        BuildStatus.BuildingDebug => $"编译中(Debug) {FormatElapsed(CurrentTaskElapsed)}",
        BuildStatus.BuildingRelease => $"编译中(Release) {FormatElapsed(CurrentTaskElapsed)}",
        BuildStatus.Publishing => $"发布中 {FormatElapsed(CurrentTaskElapsed)}",
        BuildStatus.Waiting => "等待中...",
        _ => ""
    };

    private static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalHours >= 1
        ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
        : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
}
