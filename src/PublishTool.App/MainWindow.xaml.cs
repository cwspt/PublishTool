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
using System.Threading;
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
    private readonly Stopwatch _uiHeartbeatStopwatch = Stopwatch.StartNew();
    private readonly Stopwatch _logScrollStopwatch = Stopwatch.StartNew();
    private TimeSpan _lastUiHeartbeat;
    private DateTime _lastIconRefreshUtc = DateTime.UtcNow;
    private int _pendingOutputCount;
    private long _pendingOutputCharacters;
    private int _droppedOutputLines;
    private int _storedLogCharacters;
    private Stopwatch? _totalRunStopwatch;
    private Stopwatch? _currentProjectStopwatch;
    private ProjectEntry? _timedProject;
    private string? _runSummary;
    private long _lastElapsedSecond = -1;

    private const int MaxOutputBatchCharacters = 16 * 1024;
    private const int MaxOutputLineCharacters = 32 * 1024;
    private const int MaxPendingOutputCharacters = 4 * 1024 * 1024;
    private const int MaxStoredLogCharacters = 600_000;
    private const int RetainedLogCharacters = 450_000;
    private const int OutputFlushBudgetMilliseconds = 12;
    private const int LogScrollIntervalMilliseconds = 250;
    private const int DispatcherDelayWarningMilliseconds = 1_000;
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
        _outputFlushTimer.Tick += (_, _) =>
        {
            RecordUiHeartbeat();
            RefreshRunningDuration();
            FlushPendingOutput();
        };
        _outputFlushTimer.Start();
        LoadProjects();
        DiagnosticLogService.Write("UI", "Main window initialized");
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

        ClearOutput();
        TxtStatus.Text = "执行脚本: " + script.Name;
        LogLine("========== 执行脚本: " + script.Name + " ==========");
        LogLine("脚本: " + script.Path);

        var sw = Stopwatch.StartNew();
        selected.Status = BuildStatus.Publishing;
        BeginRunTiming("执行脚本: " + script.Name);
        BeginProjectTiming(selected);
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
            TxtStatus.Text = ok ? "脚本执行成功 (总用时 " + ts + ")" : "脚本执行失败 (总用时 " + ts + ")";
        }
        catch (Exception ex)
        {
            LogLine("执行异常: " + ex.Message);
            TxtStatus.Text = "执行异常";
        }
        finally
        {
            EndRunTiming();
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
        BeginRunTiming($"编译: {selected.Name} ({CmbConfig.Text})");
        BeginProjectTiming(selected);
        UpdateUI();

        ClearOutput();
        RefreshRunningDuration(force: true);
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
            TxtStatus.Text = success ? $"编译成功 (总用时 {ts})" : $"编译失败 (总用时 {ts})";
        }
        catch (Exception ex)
        {
            LogLine($"编译异常: {ex.Message}");
            TxtStatus.Text = "编译异常";
        }
        finally
        {
            EndRunTiming();
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
        BeginRunTiming($"发布: {selected.Name}");
        BeginProjectTiming(selected);
        UpdateUI();

        ClearOutput();
        RefreshRunningDuration(force: true);
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
            TxtStatus.Text = success ? $"发布成功 (总用时 {ts})" : $"发布失败 (总用时 {ts})";
        }
        catch (Exception ex)
        {
            LogLine($"发布异常: {ex.Message}");
            TxtStatus.Text = "发布异常";
        }
        finally
        {
            EndRunTiming();
            selected.Status = BuildStatus.Idle;
            _activeProject = null;
            UpdateUI();
        }
    }

    private void OnBuildOutput(string text)
    {
        var boundedText = text.Length <= MaxOutputLineCharacters
            ? text
            : text[..MaxOutputLineCharacters] + " ... [该行过长，已截断]";
        QueueLogLine(AnsiEscapeSequence.Replace(boundedText, string.Empty), allowDrop: true);
    }

    private void LogLine(string text)
    {
        QueueLogLine(text, allowDrop: false);
    }

    private void QueueLogLine(string text, bool allowDrop)
    {
        if (allowDrop && Interlocked.Read(ref _pendingOutputCharacters) >= MaxPendingOutputCharacters)
        {
            Interlocked.Increment(ref _droppedOutputLines);
            return;
        }

        Interlocked.Add(ref _pendingOutputCharacters, text.Length + Environment.NewLine.Length);
        Interlocked.Increment(ref _pendingOutputCount);
        _pendingOutput.Enqueue(text);
    }

    private void FlushPendingOutput()
    {
        var flushStopwatch = Stopwatch.StartNew();
        var batch = new StringBuilder();
        while (batch.Length < MaxOutputBatchCharacters &&
               flushStopwatch.ElapsedMilliseconds < OutputFlushBudgetMilliseconds &&
               _pendingOutput.TryDequeue(out var line))
        {
            Interlocked.Decrement(ref _pendingOutputCount);
            Interlocked.Add(ref _pendingOutputCharacters, -(line.Length + Environment.NewLine.Length));
            batch.AppendLine(line);
        }

        var droppedLines = Interlocked.Exchange(ref _droppedOutputLines, 0);
        if (droppedLines > 0)
        {
            batch.AppendLine($"[日志产生速度过快，已丢弃 {droppedLines} 行；详情已写入 diagnostic 日志]");
            DiagnosticLogService.Write("UI",
                $"Dropped {droppedLines} output lines; pendingChars={Math.Max(0, Interlocked.Read(ref _pendingOutputCharacters))}");
        }

        if (batch.Length == 0) return;

        AppendLogText(batch.ToString());
        flushStopwatch.Stop();
        if (flushStopwatch.ElapsedMilliseconds >= 100)
        {
            DiagnosticLogService.Write("UI",
                $"Log flush took {flushStopwatch.ElapsedMilliseconds}ms; batchChars={batch.Length}; " +
                $"pendingLines={Math.Max(0, Volatile.Read(ref _pendingOutputCount))}; storedChars={_storedLogCharacters}");
        }
    }

    private void AppendLogText(string text)
    {
        if (_storedLogCharacters + text.Length > MaxStoredLogCharacters)
        {
            var existingText = TxtOutput.Text;
            var retainStart = Math.Max(0, existingText.Length - RetainedLogCharacters);
            if (retainStart > 0)
            {
                var nextLineBreak = existingText.IndexOf('\n', retainStart);
                if (nextLineBreak >= 0)
                    retainStart = nextLineBreak + 1;
            }

            const string truncationNotice = "[较早的运行日志已截断，完整卡顿诊断请查看本地 diagnostic 日志]\r\n";
            var retainedText = existingText[retainStart..];
            TxtOutput.Text = truncationNotice + retainedText;
            TxtOutput.CaretIndex = TxtOutput.Text.Length;
            _storedLogCharacters = truncationNotice.Length + retainedText.Length;
            DiagnosticLogService.Write("UI", $"Visible log truncated; retainedChars={_storedLogCharacters}");
        }

        TxtOutput.AppendText(text);
        _storedLogCharacters += text.Length;

        if (_logScrollStopwatch.ElapsedMilliseconds >= LogScrollIntervalMilliseconds ||
            Volatile.Read(ref _pendingOutputCount) <= 0)
        {
            TxtOutput.ScrollToEnd();
            _logScrollStopwatch.Restart();
        }
    }

    private void ClearOutput()
    {
        while (_pendingOutput.TryDequeue(out var clearedLine))
        {
            Interlocked.Decrement(ref _pendingOutputCount);
            Interlocked.Add(ref _pendingOutputCharacters, -(clearedLine.Length + Environment.NewLine.Length));
        }
        Interlocked.Exchange(ref _droppedOutputLines, 0);
        TxtOutput.Clear();
        _storedLogCharacters = 0;
        _logScrollStopwatch.Restart();
    }

    private void RecordUiHeartbeat()
    {
        var now = _uiHeartbeatStopwatch.Elapsed;
        if (_lastUiHeartbeat != TimeSpan.Zero)
        {
            var delay = now - _lastUiHeartbeat;
            if (delay.TotalMilliseconds >= DispatcherDelayWarningMilliseconds)
            {
                var managedMemoryMb = GC.GetTotalMemory(forceFullCollection: false) / 1024d / 1024d;
                var workingSetMb = 0d;
                try
                {
                    using var currentProcess = Process.GetCurrentProcess();
                    workingSetMb = currentProcess.WorkingSet64 / 1024d / 1024d;
                }
                catch { }

                DiagnosticLogService.Write("UI",
                    $"Dispatcher delayed {delay.TotalMilliseconds:F0}ms; " +
                    $"pendingLines={Math.Max(0, Volatile.Read(ref _pendingOutputCount))}; " +
                    $"pendingChars={Math.Max(0, Interlocked.Read(ref _pendingOutputCharacters))}; " +
                    $"storedChars={_storedLogCharacters}; managedMemoryMb={managedMemoryMb:F1}; " +
                    $"workingSetMb={workingSetMb:F1}; gen2Collections={GC.CollectionCount(2)}; " +
                    $"task={_runSummary ?? "none"}");
            }
        }

        _lastUiHeartbeat = now;
    }

    private void BeginRunTiming(string summary)
    {
        _totalRunStopwatch = Stopwatch.StartNew();
        _runSummary = summary;
        _lastElapsedSecond = -1;
        DiagnosticLogService.Write("Task", $"Started: {summary}");
        RefreshRunningDuration(force: true);
    }

    private void UpdateRunSummary(string summary)
    {
        _runSummary = summary;
        RefreshRunningDuration(force: true);
    }

    private void BeginProjectTiming(ProjectEntry project)
    {
        _timedProject = project;
        _currentProjectStopwatch = Stopwatch.StartNew();
        project.CurrentTaskElapsed = TimeSpan.Zero;
        LstProjects.Items.Refresh();
    }

    private void EndProjectTiming()
    {
        if (_timedProject != null && _currentProjectStopwatch != null)
        {
            _currentProjectStopwatch.Stop();
            _timedProject.CurrentTaskElapsed = _currentProjectStopwatch.Elapsed;
        }

        _currentProjectStopwatch = null;
        _timedProject = null;
    }

    private void EndRunTiming()
    {
        EndProjectTiming();
        _totalRunStopwatch?.Stop();
        if (_totalRunStopwatch != null)
        {
            DiagnosticLogService.Write("Task",
                $"Finished: {_runSummary ?? "unknown"}; elapsed={_totalRunStopwatch.Elapsed.TotalMilliseconds:F0}ms");
        }
        _totalRunStopwatch = null;
        _runSummary = null;
        _lastElapsedSecond = -1;
        TxtTotalElapsed.Text = string.Empty;
    }

    private void RefreshRunningDuration(bool force = false)
    {
        if (_totalRunStopwatch == null || _runSummary == null) return;

        var elapsed = _totalRunStopwatch.Elapsed;
        var elapsedSecond = (long)elapsed.TotalSeconds;
        if (!force && elapsedSecond == _lastElapsedSecond) return;

        _lastElapsedSecond = elapsedSecond;
        TxtStatus.Text = _runSummary;
        TxtTotalElapsed.Text = $"总用时 {FormatElapsed(elapsed)}";

        if (_timedProject != null && _currentProjectStopwatch != null && _timedProject.Status != BuildStatus.Idle)
        {
            _timedProject.CurrentTaskElapsed = _currentProjectStopwatch.Elapsed;
            LstProjects.Items.Refresh();
        }
    }

    private static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalHours >= 1
        ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
        : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";

    
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


    private void BuildGroup_Click(object sender, RoutedEventArgs e)
    {

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
        BeginRunTiming("执行组脚本: " + group.Name);

        try
        {
            // Use visible window so the PowerShell/Cmd window shows
            // a important for start scripts that launch servers, etc.
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = workDir,
                UseShellExecute = true
            };
            if (group.BuildScript.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = "powershell.exe";
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(group.BuildScript);
            }
            else
            {
                psi.FileName = "cmd.exe";
                psi.ArgumentList.Add("/k");
                psi.ArgumentList.Add(group.BuildScript);
            }
            Process.Start(psi);

            sw.Stop();
            var ts = sw.Elapsed.TotalSeconds >= 60
                ? sw.Elapsed.Minutes + "分" + sw.Elapsed.Seconds + "秒"
                : sw.Elapsed.TotalSeconds.ToString("F1") + "秒";

            LogLine("========== 已在独立窗口启动组脚本 (耗时 " + ts + ") ==========");
            TxtStatus.Text = "组脚本已在独立窗口启动: " + group.Name;
        }
        catch (Exception ex)
        {
            LogLine("执行异常: " + ex.Message);
            TxtStatus.Text = "执行异常";
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

        ClearOutput();
        TxtStatus.Text = "发布组: " + group.Name + " | 共 " + ordered.Count + " 个，已完成 0";
        LogLine("========== 开始发布项目组: " + group.Name + " (" + ordered.Count + " 个项目) ==========");

        var groupSw = Stopwatch.StartNew();
        var successCount = 0;
        var failCount = 0;
        BeginRunTiming("发布组: " + group.Name + " | 共 " + ordered.Count + " 个，已完成 0");

        try
        {
            for (int i = 0; i < ordered.Count; i++)
            {
                var proj = ordered[i];

                UpdateRunSummary("发布组: " + group.Name + " | 共 " + ordered.Count + " 个，已完成 " + i + "，还剩 " + (ordered.Count - i) + " 个");

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
                BeginProjectTiming(proj);
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
                    EndProjectTiming();
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
            TxtStatus.Text = "项目组 " + group.Name + " 发布完成 (" + successCount + "/" + ordered.Count + ") | 总用时 " + gts;
        }
        finally
        {
            foreach (var project in ordered)
                project.Status = BuildStatus.Idle;
            EndRunTiming();
            UpdateUI();
        }
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

        ClearOutput();
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
        if (_build.IsRunning || DateTime.UtcNow - _lastIconRefreshUtc < TimeSpan.FromSeconds(30))
            return;

        var refreshStopwatch = Stopwatch.StartNew();
        foreach (var p in _projects)
            p.RefreshIcon();
        LstProjects.Items.Refresh();
        _lastIconRefreshUtc = DateTime.UtcNow;

        if (refreshStopwatch.ElapsedMilliseconds >= 250)
        {
            DiagnosticLogService.Write("UI",
                $"Icon refresh took {refreshStopwatch.ElapsedMilliseconds}ms; projects={_projects.Count}");
        }
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
