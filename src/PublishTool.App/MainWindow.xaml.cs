using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using PublishTool.App.Models;
using PublishTool.App.Services;
using PublishTool.App.Views;

namespace PublishTool.App;

public partial class MainWindow : Window
{
    private readonly ProjectStorageService _storage = new();
    private readonly BuildService _build = new();
    private readonly GroupStorageService _groupStorage = new();
    private ObservableCollection<ScriptEntry> _customScripts = new();
    private List<ProjectGroup> _groups = new();
    private ObservableCollection<ProjectEntry> _projects = new();
    private ProjectEntry? _activeProject;
    private readonly ConcurrentQueue<string> _pendingOutput = new();
    private DispatcherTimer? _outputFlushTimer;

    private const int MaxOutputBatchCharacters = 64 * 1024;
    private const int MaxStoredLogCharacters = 1_500_000;
    private const int RetainedLogCharacters = 1_200_000;
    private static readonly Regex AnsiEscapeSequence = new(
        @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\a]*(?:\a|\x1B\\))",
        RegexOptions.Compiled);

    public MainWindow()
    {
        InitializeComponent();
        _build.OutputReceived += OnBuildOutput;
        _outputFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _outputFlushTimer.Tick += (_, _) => FlushPendingOutput();
        _outputFlushTimer.Start();
        LoadProjects();
    }

    private void LoadProjects()
    {
        _groups = _groupStorage.Load();
        RefreshGroupCombo();
        _projects = new ObservableCollection<ProjectEntry>(_storage.Load());
        LstProjects.ItemsSource = _projects;
        UpdateUI();
        RefreshGroupCombo();
    }

    private void SaveProjects()
    {
        _storage.Save(_projects.ToList());
        UpdateUI();
    }

    private ProjectEntry? SelectedProject =>
        LstProjects.SelectedItem as ProjectEntry;

    private void UpdateUI()
    {
        var hasSelection = SelectedProject != null;
        TxtEmptyHint.Visibility = _projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtProjectCount.Text = $"共 {_projects.Count} 个项目";
        BtnStop.IsEnabled = _build.IsRunning;

        // Refresh list to show status text changes
        LstProjects.Items.Refresh();
        BtnGroupPublish.IsEnabled = CmbGroup.SelectedItem != null && !_build.IsRunning;
                RefreshCustomScriptCombo();
        BtnGroupBuild.IsEnabled = CmbGroup.SelectedItem != null && !_build.IsRunning && GetSelectedGroup()?.BuildScript != null;
    }

    private void RefreshCustomScriptCombo()
    {
        var selected = SelectedProject;
        CmbCustomScript.Items.Clear();
        if (selected == null || selected.CustomScripts.Count == 0) return;
        foreach (var s in selected.CustomScripts)
            CmbCustomScript.Items.Add(s.Name + " (" + s.Path + ")");
        if (CmbCustomScript.Items.Count > 0)
            CmbCustomScript.SelectedIndex = 0;
    }

    private async void RunCustomScript_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProject;
        if (selected == null) return;
        var idx = CmbCustomScript.SelectedIndex;
        if (idx < 0 || idx >= selected.CustomScripts.Count) return;

        var script = selected.CustomScripts[idx];

        if (_build.IsRunning)
        {
            MessageBox.Show("当前有任务正在执行中，请等待完成或点击停止", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var workDir = System.IO.File.Exists(selected.ProjectPath)
            ? System.IO.Path.GetDirectoryName(selected.ProjectPath)!
            : selected.ProjectPath;

        TxtOutput.Clear();
        TxtStatus.Text = "执行脚本: " + script.Name;
        LogLine("========== 执行脚本: " + script.Name + " ==========");
        LogLine("脚本: " + script.Path);

        var sw = Stopwatch.StartNew();
        selected.Status = BuildStatus.Publishing;
        UpdateUI();

        try
        {
            var ok = false;
            if (script.Path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                ok = await _build.RunProcessForGroup("powershell", "-NoProfile -ExecutionPolicy Bypass -File \"" + script.Path + "\"", workDir);
            else
                ok = await _build.RunProcessForGroup("cmd", "/c \"" + script.Path + "\"", workDir);

            sw.Stop();
            var ts = sw.Elapsed.TotalSeconds >= 60
                ? sw.Elapsed.Minutes + "分" + sw.Elapsed.Seconds + "秒"
                : sw.Elapsed.TotalSeconds.ToString("F1") + "秒";

            LogLine(ok
                ? "========== 脚本执行成功 (耗时 " + ts + ") =========="
                : "========== 脚本执行失败 (耗时 " + ts + ") ==========");
            TxtStatus.Text = ok ? "脚本执行成功 (" + ts + ")" : "脚本执行失败 (" + ts + ")";
        }
        catch (Exception ex)
        {
            LogLine("执行异常: " + ex.Message);
            TxtStatus.Text = "执行异常";
        }
        finally
        {
            selected.Status = BuildStatus.Idle;
            UpdateUI();
        }
    }


    private void LstProjects_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateUI();

    private void AddProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ProjectDialog();
        dlg.Owner = this;
        if (dlg.ShowDialog() == true)
        {
            _projects.Add(dlg.Result);
            SaveProjects();
        }
    }

    private void EditProject_Click(object sender, RoutedEventArgs e)
    {
        // Clear IME composition to avoid TextStore crash
        MoveFocusToSafeElement();
        var selected = SelectedProject;
        if (selected == null)
        {
            MessageBox.Show("请先选中一个项目", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new ProjectDialog(selected);
        dlg.Owner = this;
        if (dlg.ShowDialog() == true)
        {
            var idx = _projects.IndexOf(selected);
            _projects[idx] = dlg.Result;
            SaveProjects();
            dlg.Result.RefreshIcon();
        }
    }

    private void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProject;
        if (selected == null) return;

        if (MessageBox.Show($"确定要删除项目「{selected.Name}」吗？", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _projects.Remove(selected);
            SaveProjects();
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProject;
        if (selected == null) return;

        var dir = File.Exists(selected.ProjectPath)
            ? Path.GetDirectoryName(selected.ProjectPath)
            : selected.ProjectPath;

        if (dir != null && Directory.Exists(dir))
            Process.Start("explorer.exe", dir);
    }

    private void OpenBuildOutput_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProject;
        if (selected == null) return;

        var isRelease = CmbConfig.SelectedIndex == 1;
        var path = _build.GetBuildOutputPath(selected, isRelease);

        if (Directory.Exists(path))
            Process.Start("explorer.exe", path);
        else
        {
            var parent = Path.GetDirectoryName(path);
            if (parent != null && Directory.Exists(parent))
                Process.Start("explorer.exe", parent);
            else
                MessageBox.Show($"编译产物目录不存在，请先编译该项目。\n预期路径: {path}",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OpenPublishDir_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProject;
        if (selected == null) return;

        if (string.IsNullOrWhiteSpace(selected.PublishDir))
        {
            MessageBox.Show("该项目未设置发布目录", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (Directory.Exists(selected.PublishDir))
            Process.Start("explorer.exe", selected.PublishDir);
        else
            MessageBox.Show($"发布目录尚不存在，请先发布该项目。\n路径: {selected.PublishDir}",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

        private void ImportConfig_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "导入配置（含项目组）",
            Filter = "JSON 文件|*.json|所有文件|*.*",
            InitialDirectory = AppDomain.CurrentDomain.BaseDirectory
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = File.ReadAllText(dlg.FileName);
            int projectCount = 0;
            int groupCount = 0;

            // Try new format first (PublishToolConfig with Projects + Groups)
            var config = JsonSerializer.Deserialize<PublishToolConfig>(json);
            if (config is { Projects.Count: > 0 })
            {
                var result = MessageBox.Show(
                    "发现 " + config.Projects.Count + " 个项目" +
                    (config.Groups.Count > 0 ? "，" + config.Groups.Count + " 个项目组" : "") + "。\n\n" +
                    "「是」= 替换当前列表\n「否」= 追加到当前列表\n「取消」= 不导入",
                    "导入配置", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                if (result == MessageBoxResult.Cancel) return;

                if (result == MessageBoxResult.Yes)
                    _projects.Clear();

                foreach (var p in config.Projects)
                    _projects.Add(p);

                if (config.Groups.Count > 0)
                {
                    if (result == MessageBoxResult.Yes)
                        _groups.Clear();
                    _groups.AddRange(config.Groups);
                    _groupStorage.Save(_groups);
                    RefreshGroupCombo();
                }

                projectCount = config.Projects.Count;
                groupCount = config.Groups.Count;
            }
            else
            {
                // Fallback: old format (project list only)
                var imported = JsonSerializer.Deserialize<List<ProjectEntry>>(json);
                if (imported == null || imported.Count == 0)
                {
                    MessageBox.Show("文件中没有有效的配置数据", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var result = MessageBox.Show(
                    "发现 " + imported.Count + " 个项目。\n\n" +
                    "「是」= 替换当前列表\n「否」= 追加到当前列表\n「取消」= 不导入",
                    "导入配置", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                if (result == MessageBoxResult.Cancel) return;

                if (result == MessageBoxResult.Yes)
                    _projects.Clear();

                foreach (var p in imported)
                    _projects.Add(p);

                projectCount = imported.Count;
            }

            SaveProjects();
            var msg = "成功导入 " + projectCount + " 个项目";
            if (groupCount > 0)
                msg += "，" + groupCount + " 个项目组";
            MessageBox.Show(msg, "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("导入失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportConfig_Click(object sender, RoutedEventArgs e)
    {
        if (_projects.Count == 0 && _groups.Count == 0)
        {
            MessageBox.Show("当前没有项目和项目组可导出", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "导出配置（含项目组）",
            Filter = "JSON 文件|*.json|所有文件|*.*",
            FileName = "PublishTool_Config_" + DateTime.Now.ToString("yyyyMMdd") + ".json",
            InitialDirectory = AppDomain.CurrentDomain.BaseDirectory
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            var config = new PublishToolConfig
            {
                Projects = _projects.ToList(),
                Groups = _groups
            };
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dlg.FileName, json);
            var msg = "成功导出 " + _projects.Count + " 个项目";
            if (_groups.Count > 0)
                msg += "，" + _groups.Count + " 个项目组";
            msg += " 到:\n" + dlg.FileName;
            MessageBox.Show(msg, "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_build.IsRunning)
        {
            _build.Cancel();
            UpdateUI();
        }
    }

    private async void Build_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProject;
        if (selected == null) return;

        if (_build.IsRunning)
        {
            MessageBox.Show("当前有任务正在执行中，请等待完成或点击停止", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (selected.Status != BuildStatus.Idle)
        {
            MessageBox.Show($"项目「{selected.Name}」正在 {selected.StatusText}，请等待完成", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var isRelease = CmbConfig.SelectedIndex == 1;
        var sw = Stopwatch.StartNew();
        var status = isRelease ? BuildStatus.BuildingRelease : BuildStatus.BuildingDebug;

        _activeProject = selected;
        selected.Status = status;
        UpdateUI();

        TxtOutput.Clear();
        TxtStatus.Text = selected.StatusText;
        LogLine($"========== 开始编译: {selected.Name} ({CmbConfig.Text}) ==========");

        try
        {
            var success = await _build.BuildAsync(selected, isRelease);
            sw.Stop();
            var ts = sw.Elapsed.TotalSeconds >= 60
                ? $"{sw.Elapsed.Minutes}分{sw.Elapsed.Seconds}秒"
                : $"{sw.Elapsed.TotalSeconds:F1}秒";
            LogLine(success
                ? $"========== 编译成功 (耗时 {ts}) =========="
                : $"========== 编译失败 (耗时 {ts}) ==========");
            TxtStatus.Text = success ? $"编译成功 ({ts})" : $"编译失败 ({ts})";
        }
        catch (Exception ex)
        {
            LogLine($"编译异常: {ex.Message}");
            TxtStatus.Text = "编译异常";
        }
        finally
        {
            selected.Status = BuildStatus.Idle;
            _activeProject = null;
            UpdateUI();
        }
    }

    private async void Publish_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProject;
        if (selected == null) return;

        if (_build.IsRunning)
        {
            MessageBox.Show("当前有任务正在执行中，请等待完成或点击停止", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (selected.Status != BuildStatus.Idle)
        {
            MessageBox.Show($"项目「{selected.Name}」正在 {selected.StatusText}，请等待完成", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(selected.PublishDir))
        {
            MessageBox.Show("该项目未设置发布目录，请先编辑项目设置发布目录", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _activeProject = selected;
        selected.Status = BuildStatus.Publishing;
        UpdateUI();

        TxtOutput.Clear();
        TxtStatus.Text = selected.StatusText;
        LogLine($"========== 开始发布: {selected.Name} ==========");

        try
        {
            var success = await _build.PublishAsync(selected);
            sw.Stop();
            var ts = sw.Elapsed.TotalSeconds >= 60
                ? $"{sw.Elapsed.Minutes}分{sw.Elapsed.Seconds}秒"
                : $"{sw.Elapsed.TotalSeconds:F1}秒";
            LogLine(success
                ? $"========== 发布成功 (耗时 {ts}) =========="
                : $"========== 发布失败 (耗时 {ts}) ==========");
            TxtStatus.Text = success ? $"发布成功 ({ts})" : $"发布失败 ({ts})";
        }
        catch (Exception ex)
        {
            LogLine($"发布异常: {ex.Message}");
            TxtStatus.Text = "发布异常";
        }
        finally
        {
            selected.Status = BuildStatus.Idle;
            _activeProject = null;
            UpdateUI();
        }
    }

    private void OnBuildOutput(string text)
    {
        _pendingOutput.Enqueue(AnsiEscapeSequence.Replace(text, string.Empty));
    }

    private void LogLine(string text)
    {
        FlushPendingOutput(flushAll: true);
        AppendLogText(text + Environment.NewLine);
    }

    private void FlushPendingOutput(bool flushAll = false)
    {
        do
        {
            if (_pendingOutput.IsEmpty) return;

            var batch = new StringBuilder();
            while (batch.Length < MaxOutputBatchCharacters && _pendingOutput.TryDequeue(out var line))
                batch.AppendLine(line);

            if (batch.Length > 0)
                AppendLogText(batch.ToString());
        }
        while (flushAll && !_pendingOutput.IsEmpty);
    }

    private void AppendLogText(string text)
    {
        var excess = TxtOutput.Text.Length + text.Length - MaxStoredLogCharacters;
        if (excess > 0)
        {
            var removeLength = Math.Min(excess + 100_000, TxtOutput.Text.Length);
            TxtOutput.Select(0, removeLength);
            TxtOutput.SelectedText = string.Empty;
        }

        TxtOutput.AppendText(text);
        TxtOutput.ScrollToEnd();
    }

    private void ClearOutput()
    {
        while (_pendingOutput.TryDequeue(out _)) { }
        TxtOutput.Clear();
    }

    
    // ========== 项目组 ==========

    private void RefreshGroupCombo()
    {
        CmbGroup.Items.Clear();
        foreach (var g in _groups)
            CmbGroup.Items.Add(g.Name);
        if (_groups.Count > 0)
            CmbGroup.SelectedIndex = 0;
        UpdateUI();
    }

    private void CmbGroup_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateUI();

    private void ManageGroups_Click(object sender, RoutedEventArgs e)
    {
        var cloned = new List<ProjectGroup>(_groups.Count);
        foreach (var g in _groups)
            cloned.Add(new ProjectGroup { Id = g.Id, Name = g.Name, ProjectIds = new List<string>(g.ProjectIds), BuildScript = g.BuildScript });
        var dlg = new GroupDialog(cloned, _projects.ToList());
        dlg.Owner = this;
        if (dlg.ShowDialog() == true)
        {
            _groups = cloned;
            _groupStorage.Save(_groups);
            RefreshGroupCombo();
        }
    }

    
    
        
    // ========== 项目组编译 ==========

    private ProjectGroup? GetSelectedGroup()
    {
        var idx = CmbGroup.SelectedIndex;
        return idx >= 0 && idx < _groups.Count ? _groups[idx] : null;
    }

    private async void BuildGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_build.IsRunning)
        {
            MessageBox.Show("当前有任务正在执行中，请等待完成或点击停止", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var group = GetSelectedGroup();
        if (group == null || string.IsNullOrWhiteSpace(group.BuildScript) || group.ProjectIds.Count == 0)
        {
            MessageBox.Show("当前项目组未设置编译脚本", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Resolve first project for working directory
        var projectMap = _projects.ToDictionary(p => p.Id);
        ProjectEntry? firstProj = null;
        foreach (var pid in group.ProjectIds)
        {
            if (projectMap.TryGetValue(pid, out firstProj))
                break;
        }

        if (firstProj == null)
        {
            MessageBox.Show("项目组中没有找到有效的项目", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var workDir = System.IO.File.Exists(firstProj.ProjectPath)
            ? System.IO.Path.GetDirectoryName(firstProj.ProjectPath)!
            : firstProj.ProjectPath;

        ClearOutput();
        TxtStatus.Text = "执行组脚本: " + group.Name;
        LogLine("========== 执行组脚本: " + group.Name + " ==========");
        LogLine("脚本: " + group.BuildScript);

        var sw = Stopwatch.StartNew();

        try
        {
            Task<bool> execution;
            if (group.BuildScript.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                execution = _build.RunProcessForGroup("powershell", "-NoProfile -ExecutionPolicy Bypass -File \"" + group.BuildScript + "\"", workDir);
            else
                execution = _build.RunProcessForGroup("cmd", "/c \"" + group.BuildScript + "\"", workDir);

            UpdateUI();
            var ok = await execution;

            sw.Stop();
            var ts = sw.Elapsed.TotalSeconds >= 60
                ? sw.Elapsed.Minutes + "分" + sw.Elapsed.Seconds + "秒"
                : sw.Elapsed.TotalSeconds.ToString("F1") + "秒";

            LogLine(ok
                ? "========== 组脚本执行成功 (耗时 " + ts + ") =========="
                : "========== 组脚本执行失败 (耗时 " + ts + ") ==========");
            TxtStatus.Text = ok ? "组脚本执行成功 (" + ts + ")" : "组脚本执行失败 (" + ts + ")";
        }
        catch (Exception ex)
        {
            LogLine("执行异常: " + ex.Message);
            TxtStatus.Text = "执行异常";
        }
        finally
        {
            UpdateUI();
        }
    }

    private async void PublishGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_build.IsRunning)
        {
            MessageBox.Show("当前有任务正在执行中，请等待完成或点击停止", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var idx = CmbGroup.SelectedIndex;
        if (idx < 0 || idx >= _groups.Count) return;

        var group = _groups[idx];
        if (group.ProjectIds.Count == 0)
        {
            MessageBox.Show("该项目组没有成员", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var projectMap = _projects.ToDictionary(p => p.Id);
        var ordered = new List<ProjectEntry>();
        foreach (var pid in group.ProjectIds)
        {
            if (projectMap.TryGetValue(pid, out var proj))
                ordered.Add(proj);
        }

        if (ordered.Count == 0)
        {
            MessageBox.Show("项目组中没有找到有效的项目", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Mark all as waiting
        foreach (var p in ordered)
            p.Status = BuildStatus.Waiting;
        UpdateUI();

        TxtOutput.Clear();
        TxtStatus.Text = "发布组: " + group.Name + " | 共 " + ordered.Count + " 个，已完成 0";
        LogLine("========== 开始发布项目组: " + group.Name + " (" + ordered.Count + " 个项目) ==========");

        var groupSw = Stopwatch.StartNew();
        var successCount = 0;
        var failCount = 0;

        for (int i = 0; i < ordered.Count; i++)
        {
            var proj = ordered[i];

            TxtStatus.Text = "发布组: " + group.Name + " | 共 " + ordered.Count + " 个，已完成 " + i + "，还剩 " + (ordered.Count - i) + " 个";

            LogLine("");
            LogLine(">>> [" + (i + 1) + "/" + ordered.Count + "] 发布: " + proj.Name);

            if (string.IsNullOrWhiteSpace(proj.PublishDir))
            {
                LogLine("[跳过] " + proj.Name + ": 未设置发布目录");
                proj.Status = BuildStatus.Idle;
                failCount++;
                UpdateUI();
                continue;
            }

            proj.Status = BuildStatus.Publishing;
            UpdateUI();

            var sw = Stopwatch.StartNew();
            try
            {
                var ok = await _build.PublishAsync(proj);
                sw.Stop();
                var ts = sw.Elapsed.TotalSeconds >= 60
                    ? sw.Elapsed.Minutes + "分" + sw.Elapsed.Seconds + "秒"
                    : sw.Elapsed.TotalSeconds.ToString("F1") + "秒";

                if (ok)
                {
                    successCount++;
                    LogLine(">>> " + proj.Name + ": 发布成功 (" + ts + ")");
                }
                else
                {
                    failCount++;
                    LogLine(">>> " + proj.Name + ": 发布失败 (" + ts + ")");
                }
            }
            finally
            {
                proj.Status = BuildStatus.Idle;
                UpdateUI();
            }
        }

        groupSw.Stop();
        var gts = groupSw.Elapsed.TotalSeconds >= 60
            ? groupSw.Elapsed.Minutes + "分" + groupSw.Elapsed.Seconds + "秒"
            : groupSw.Elapsed.TotalSeconds.ToString("F1") + "秒";

        LogLine("");
        LogLine("========== 项目组发布完成: 成功 " + successCount + ", 失败 " + failCount + ", 总耗时 " + gts + " ==========");
        TxtStatus.Text = "项目组 " + group.Name + " 发布完成 (" + successCount + "/" + ordered.Count + ")";
    }    
    
    private void Run_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProject;
        if (selected == null) return;

        if (selected.Type is ProjectType.Android)
        {
            MessageBox.Show("Android 项目暂不支持直接启动", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (selected.Type == ProjectType.Vue)
        {
            var workDir = System.IO.File.Exists(selected.ProjectPath)
                ? System.IO.Path.GetDirectoryName(selected.ProjectPath)!
                : selected.ProjectPath;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    WorkingDirectory = workDir,
                    UseShellExecute = true
                };
                psi.ArgumentList.Add("/k");
                psi.ArgumentList.Add("npm.cmd run dev");
                Process.Start(psi);

                ClearOutput();
                LogLine("========== 已在独立命令行窗口启动 Vue 开发服务器 ==========");
                LogLine("工作目录: " + workDir);
                TxtStatus.Text = "Vue 已在独立命令行窗口启动: " + selected.Name;
            }
            catch (Exception ex)
            {
                LogLine("Vue 启动失败: " + ex.Message);
                TxtStatus.Text = "Vue 启动失败";
            }
            return;
        }

        if (_build.IsRunning)
        {
            MessageBox.Show("当前有任务正在执行中，请等待完成或点击停止", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var isRelease = CmbConfig.SelectedIndex == 1;

        if (selected.Type is ProjectType.BlazorWasm or ProjectType.BlazorServer)
        {
            _build.RunProject(selected, isRelease);
            return;
        }

        TxtOutput.Clear();
        TxtStatus.Text = "查找可执行文件: " + selected.Name;
        LogLine("========== 启动: " + selected.Name + " (" + (isRelease ? "Release" : "Debug") + ") ==========");

        _build.RunProject(selected, isRelease);
    }

    
    /// <summary>Move focus to toolbar to clear IME composition state, preventing TSF crash on ShowDialog.</summary>
    private void MoveFocusToSafeElement()
    {
        try
        {
            System.Windows.Input.FocusManager.SetFocusedElement(this, BtnStop);
        }
        catch { }
    }

    
    private void Window_Activated(object sender, EventArgs e)
    {
        foreach (var p in _projects)
            p.RefreshIcon();
        LstProjects.Items.Refresh();
    }

    
    // ---- Context Menu Handlers ----

    
    private void OpenOutputDir_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProject;
        if (selected == null) return;
        var isRelease = CmbConfig.SelectedIndex == 1;
        var dir = _build.GetBuildOutputPath(selected, isRelease);
        if (Directory.Exists(dir)) System.Diagnostics.Process.Start("explorer.exe", dir);
        else MessageBox.Show("编译产物目录不存在，请先编译该项目", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenProjectDir_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProject;
        if (selected == null) return;
        var dir = File.Exists(selected.ProjectPath) ? Path.GetDirectoryName(selected.ProjectPath)! : selected.ProjectPath;
        if (Directory.Exists(dir)) System.Diagnostics.Process.Start("explorer.exe", dir);
    }

    private void ContextBuildDebug_Click(object sender, RoutedEventArgs e)
    {
        CmbConfig.SelectedIndex = 0;
        Build_Click(sender, e);
    }

    private void ContextBuildRelease_Click(object sender, RoutedEventArgs e)
    {
        CmbConfig.SelectedIndex = 1;
        Build_Click(sender, e);
    }

    private void ContextPublish_Click(object sender, RoutedEventArgs e)
    {
        Publish_Click(sender, e);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        ClearOutput();
    }
}
