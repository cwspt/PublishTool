using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using PublishTool.App.Models;

namespace PublishTool.App.Services;

public class ProjectStorageService
{
    private readonly string _filePath;

    public ProjectStorageService()
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        _filePath = Path.Combine(appDir, "projects.json");
    }

    public List<ProjectEntry> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new List<ProjectEntry>();

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<ProjectEntry>>(json) ?? new List<ProjectEntry>();
        }
        catch
        {
            return new List<ProjectEntry>();
        }
    }

    public void Save(List<ProjectEntry> projects)
    {
        var json = JsonSerializer.Serialize(projects, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }
}
