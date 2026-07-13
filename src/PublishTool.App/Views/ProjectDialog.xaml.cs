using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Windows.Controls;
using PublishTool.App.Models;

namespace PublishTool.App.Views;

public partial class ProjectDialog : Window
{
    public ProjectEntry Result { get; private set; } = null!;
    private ObservableCollection<ScriptEntry> _customScripts = new();
    private readonly ProjectEntry? _existing;

    public ProjectDialog(ProjectEntry? existing = null)
    {
        InitializeComponent();
        _existing = existing;
        IcsCustomScripts.ItemsSource = _customScripts;

        if (existing != null)
        {
            Title = "编辑项目";
            TxtName.Text = existing.Name;
            TxtPath.Text = existing.ProjectPath;
            TxtPublishDir.Text = existing.PublishDir;
            CmbType.SelectedIndex = (int)existing.Type;
            TxtDebugScript.Text = existing.DebugScript ?? string.Empty;
            TxtReleaseScript.Text = existing.ReleaseScript ?? string.Empty;
            TxtPublishScript.Text = existing.PublishScript ?? string.Empty;
            _customScripts = new ObservableCollection<ScriptEntry>(
                existing.CustomScripts.Select(s => new ScriptEntry { Name = s.Name, Path = s.Path }));
            IcsCustomScripts.ItemsSource = _customScripts;
        }
    }

    private void BrowsePath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
        var dlg = new OpenFileDialog
        {
            Title = "选择项目文件",
            Filter = "项目文件|*.csproj;*.sln;build.gradle;package.json|所有文件|*.*",
            InitialDirectory = GetProjectDir()
        };

        if (dlg.ShowDialog() == true)
        {
            TxtPath.Text = dlg.FileName;

            if (dlg.FileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                AutoDetectCsprojType(dlg.FileName);
            else if (dlg.FileName.EndsWith("build.gradle", StringComparison.OrdinalIgnoreCase))
                CmbType.SelectedIndex = 3;
            else if (dlg.FileName.EndsWith("package.json", StringComparison.OrdinalIgnoreCase))
                CmbType.SelectedIndex = 4;
            else if (dlg.FileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                AutoDetectBlazorType(dlg.FileName);

            if (string.IsNullOrWhiteSpace(TxtName.Text))
                TxtName.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开文件对话框失败: " + ex.ToString(), "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AutoDetectCsprojType(string csprojPath)
    {
        try
        {
            var content = File.ReadAllText(csprojPath);
            if (content.Contains("<UseWPF>true</UseWPF>"))
                CmbType.SelectedIndex = 2;
            else if (content.Contains("<UseWindowsForms>true</UseWindowsForms>"))
                CmbType.SelectedIndex = 1;
            else
                CmbType.SelectedIndex = 0;
        }
        catch
        {
            CmbType.SelectedIndex = 0;
        }
    }

    
    private string GetProjectDir()
    {
        var path = TxtPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(path)) return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return File.Exists(path) ? Path.GetDirectoryName(path)! : path;
    }

    private void BrowsePublishDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择发布目标目录", InitialDirectory = GetProjectDir() };
        if (dlg.ShowDialog() == true)
            TxtPublishDir.Text = dlg.FolderName;
    }
    private void BrowseDebugScript_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 Debug 编译脚本",
            Filter = "脚本文件|*.bat;*.cmd;*.ps1;*.sh|所有文件|*.*",
            InitialDirectory = GetProjectDir()
        };
        if (dlg.ShowDialog() == true)
            TxtDebugScript.Text = dlg.FileName;
    }

    private void BrowseReleaseScript_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 Release 编译脚本",
            Filter = "脚本文件|*.bat;*.cmd;*.ps1;*.sh|所有文件|*.*",
            InitialDirectory = GetProjectDir()
        };
        if (dlg.ShowDialog() == true)
            TxtReleaseScript.Text = dlg.FileName;
    }

    private void BrowsePublishScript_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择发布脚本",
            Filter = "脚本文件|*.bat;*.cmd;*.ps1;*.sh|所有文件|*.*",
            InitialDirectory = GetProjectDir()
        };
        if (dlg.ShowDialog() == true)
            TxtPublishScript.Text = dlg.FileName;
    }

    
    
    private void AddCustomScript_Click(object sender, RoutedEventArgs e)
    {
        _customScripts.Add(new ScriptEntry { Name = "新脚本", Path = "" });
    }

    private void BrowseCustomScript_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ScriptEntry entry)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择脚本文件",
                Filter = "脚本文件|*.bat;*.cmd;*.ps1;*.sh|所有文件|*.*",
                InitialDirectory = GetProjectDir()
            };
            if (dlg.ShowDialog() == true)
                entry.Path = dlg.FileName;
        }
    }

    private void RemoveCustomScript_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ScriptEntry entry)
            _customScripts.Remove(entry);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = TxtName.Text.Trim();
        var path = TxtPath.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("请输入项目名称", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) && !Directory.Exists(path))
        {
            MessageBox.Show("请输入有效的项目文件路径", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        Result = new ProjectEntry
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N")[..12],
            Name = name,
            ProjectPath = path,
            Type = (ProjectType)CmbType.SelectedIndex,
            PublishDir = TxtPublishDir.Text.Trim(),
            DebugScript = NullIfEmpty(TxtDebugScript.Text),
            ReleaseScript = NullIfEmpty(TxtReleaseScript.Text),
            PublishScript = NullIfEmpty(TxtPublishScript.Text),
            CustomScripts = _customScripts.Where(s => !string.IsNullOrWhiteSpace(s.Path)).Select(s => new ScriptEntry { Name = s.Name.Trim(), Path = s.Path.Trim() }).ToList(),
            AddedAt = _existing?.AddedAt ?? DateTime.Now
        };

        DialogResult = true;
        Close();
    }

    private void AutoDetectBlazorType(string csprojPath)
    {
        try
        {
            var content = File.ReadAllText(csprojPath);
            if (content.Contains("Microsoft.NET.Sdk.BlazorWebAssembly", StringComparison.OrdinalIgnoreCase))
                CmbType.SelectedIndex = 5;
            else if (content.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
                CmbType.SelectedIndex = 6;
        }
        catch { }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}