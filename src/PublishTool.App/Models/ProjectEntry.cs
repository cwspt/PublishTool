using System;
using System.Text.Json.Serialization;

using System.IO;
using System.Linq;
using System.Xml.Linq;

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
    public string PublishDir { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; } = DateTime.Now;

    public string? DebugScript { get; set; }
    public string? ReleaseScript { get; set; }
    
        private string? _iconPath;
    private bool _iconResolved;

    [JsonIgnore]
    public string? IconPath
    {
        get
        {
            if (!_iconResolved)
            {
                _iconPath = DetectIcon();
                _iconResolved = true;
            }
            return _iconPath;
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
                return FindDotNetIcon(projDir);
            case ProjectType.Android:
                return FindAndroidIcon(projDir);
            case ProjectType.Vue:
              case ProjectType.BlazorWasm:
              case ProjectType.BlazorServer:
                  return FindDotNetIcon(projDir) ?? FindWebIcon(projDir);
            default:
                return null;
        }
    }

    private static string? FindDotNetIcon(string projDir)
    {
        try
        {
            var csprojFiles = Directory.GetFiles(projDir, "*.csproj", SearchOption.AllDirectories);
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
            var candidates = Directory.GetFiles(projDir, "ic_launcher.png", SearchOption.AllDirectories);
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
                if (name is "bin" or "obj" or ".git" or ".vs" or "node_modules" or "dist")
                    continue;
                CollectFiles(d, pattern, results);
            }
        }
        catch { }
    }


    
    
    /// <summary>Clear cached icon so it gets re-detected on next access.</summary>
    public void RefreshIcon()
    {
        _iconPath = null;
        _iconResolved = false;
    }

    /// <summary>自定义脚本列表（名称 + 路径）。</summary>
    public List<ScriptEntry> CustomScripts { get; set; } = new();

    public string? PublishScript { get; set; }

    /// <summary>运行时状态，不持久化到 JSON。</summary>
    [JsonIgnore]
    public BuildStatus Status { get; set; } = BuildStatus.Idle;

    [JsonIgnore]
    public string StatusText => Status switch
    {
        BuildStatus.Idle => "",
        BuildStatus.BuildingDebug => "编译中(Debug)...",
        BuildStatus.BuildingRelease => "编译中(Release)...",
        BuildStatus.Publishing => "发布中...",
        BuildStatus.Waiting => "等待中...",
        _ => ""
    };
}