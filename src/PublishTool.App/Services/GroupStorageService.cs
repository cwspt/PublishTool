using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using PublishTool.App.Models;

namespace PublishTool.App.Services;

public class GroupStorageService
{
    private readonly string _filePath;

    public GroupStorageService()
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        _filePath = Path.Combine(appDir, "project-groups.json");
    }

    public List<ProjectGroup> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new List<ProjectGroup>();
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<ProjectGroup>>(json) ?? new List<ProjectGroup>();
        }
        catch { return new List<ProjectGroup>(); }
    }

    public void Save(List<ProjectGroup> groups)
    {
        var json = JsonSerializer.Serialize(groups, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }
}