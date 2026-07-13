using System.Collections.Generic;
using PublishTool.App.Models;

namespace PublishTool.App;

public class PublishToolConfig
{
    public List<ProjectEntry> Projects { get; set; } = new();
    public List<ProjectGroup> Groups { get; set; } = new();
}