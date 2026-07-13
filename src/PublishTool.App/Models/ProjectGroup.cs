using System.Collections.Generic;

namespace PublishTool.App.Models;

public class ProjectGroup
{
    public string Id { get; set; } = System.Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = string.Empty;
    public List<string> ProjectIds { get; set; } = new();
    public string? BuildScript { get; set; }
}