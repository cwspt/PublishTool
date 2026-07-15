using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PublishTool.App.Models;

namespace PublishTool.App.Services;

public class BuildService
{
    public event Action<string>? OutputReceived;
    public bool IsRunning => _cts != null;

    private CancellationTokenSource? _cts;
    private Process? _currentProcess;

    public void Cancel()
    {
        var processId = "none";
        try { processId = _currentProcess?.Id.ToString() ?? "none"; } catch { }
        DiagnosticLogService.Write("Process", $"Cancellation requested; pid={processId}");
        _cts?.Cancel();
        try { _currentProcess?.Kill(true); } catch { }
        OnOutput("========== 用户取消了当前任务 ==========");
    }

    public string GetBuildOutputPath(ProjectEntry project, bool isRelease)
    {
        var config = isRelease ? "Release" : "Debug";
        var projDir = File.Exists(project.ProjectPath)
            ? Path.GetDirectoryName(project.ProjectPath)!
            : project.ProjectPath;

        if (project.Type == ProjectType.Vue)
            return Path.Combine(projDir, "dist");

        if (project.Type == ProjectType.Android)
        {
            var variant = isRelease ? "release" : "debug";
            return Path.Combine(projDir, "app", "build", "outputs", "apk", variant);
        }

        // Check for custom output path in .csproj
        var csprojPath = project.ProjectPath;
        if (File.Exists(csprojPath) && csprojPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var customBin = ParseCsprojOutputPath(csprojPath, config);
            if (customBin != null) return customBin;
        }

        var bin = Path.Combine(projDir, "bin", config);
        if (Directory.Exists(bin))
        {
            var netDirs2 = Directory.GetDirectories(bin, "net*");
            if (netDirs2.Length > 0) return netDirs2[0];
            return bin;
        }

        return SearchBinRecursive(projDir, config, 0) ?? bin;
    }

    public async Task<bool> BuildAsync(ProjectEntry project, bool isRelease)
    {
        _cts = new CancellationTokenSource();
        _currentProcess = null;
        try
        {
            if (isRelease && !string.IsNullOrWhiteSpace(project.ReleaseScript))
                return await RunCustomScriptAsync(project.ReleaseScript, ResolveWorkDir(project));

            if (!isRelease && !string.IsNullOrWhiteSpace(project.DebugScript))
                return await RunCustomScriptAsync(project.DebugScript, ResolveWorkDir(project));

            var config = isRelease ? "Release" : "Debug";

            if (project.Type == ProjectType.Android)
                return await RunAndroidBuildAsync(project, isRelease);

            if (project.Type == ProjectType.Vue)
                return await RunVueBuildAsync(project);

            return await RunProcessAsync("dotnet", $@"build ""{project.ProjectPath}"" -c {config}", Path.GetDirectoryName(project.ProjectPath)!);
        }
        catch (OperationCanceledException) { return false; }
        finally { _cts = null; _currentProcess = null; }
    }

    public async Task<bool> PublishAsync(ProjectEntry project)
    {
        _cts = new CancellationTokenSource();
        _currentProcess = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(project.PublishScript))
                return await RunCustomScriptAsync(project.PublishScript, ResolveWorkDir(project));

            if (string.IsNullOrWhiteSpace(project.PublishDir))
            {
                OnOutput("错误: 未设置发布目录");
                return false;
            }

            if (project.Type == ProjectType.Android)
                return await RunAndroidPublishAsync(project);

            if (project.Type == ProjectType.Vue)
                return await RunVuePublishAsync(project);

            var projDir = Path.GetDirectoryName(project.ProjectPath)!;
            return await RunProcessAsync("dotnet", $@"publish ""{project.ProjectPath}"" -c Release -o ""{project.PublishDir}""", projDir);
        }
        catch (OperationCanceledException) { return false; }
        finally { _cts = null; _currentProcess = null; }
    }

    
    /// <summary>公开的自定义脚本执行（供项目组编译使用）。</summary>
    public async Task<bool> RunProcessForGroup(string fileName, string arguments, string workingDir)
    {
        _cts = new CancellationTokenSource();
        _currentProcess = null;
        try
        {
            return await RunProcessAsync(fileName, arguments, workingDir);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _cts = null;
            _currentProcess = null;
        }
    }

    
    /// <summary>启动编译产物的可执行文件（不等待，独立窗口运行）。</summary>
    public void RunProject(ProjectEntry project, bool isRelease)
    {
        var exePath = GetRunExePath(project, isRelease);

        if (exePath == null && project.Type is ProjectType.Console or ProjectType.WinForms or ProjectType.Wpf or ProjectType.BlazorWasm or ProjectType.BlazorServer)
        {
            // No .exe found, fall back to dotnet run (works for Blazor, WebAPI, etc.)
            var config = isRelease ? "Release" : "Debug";
            OnOutput($"未找到 .exe，尝试 dotnet run -c {config} --no-build...");
            
            // Read launchSettings.json for the application URL
            var launchUrl = GetLaunchUrl(project.ProjectPath);
            
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"run -c {config} --no-build --project \"{project.ProjectPath}\"",
                    WorkingDirectory = Path.GetDirectoryName(project.ProjectPath)!,
                    UseShellExecute = true
                };
                Process.Start(psi);
                OnOutput($"启动成功: dotnet run --project {project.ProjectPath}");
                
                // Auto-open browser if URL found
                if (launchUrl != null)
                {
                    OnOutput($"正在打开浏览器: {launchUrl}");
                    Process.Start(new ProcessStartInfo { FileName = launchUrl, UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                OnOutput($"启动失败: {ex.Message}");
            }
            return;
        }

        if (exePath == null)
        {
            OnOutput("错误: 找不到可执行文件");
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
                UseShellExecute = true
            };
            Process.Start(psi);
            OnOutput($"启动成功: {exePath}");
        }
        catch (Exception ex)
        {
            OnOutput($"启动失败: {ex.Message}");
        }
    }

    /// <summary>查找项目的可执行文件路径。</summary>
    public string? GetRunExePath(ProjectEntry project, bool isRelease)
    {
        var outputDir = GetBuildOutputPath(project, isRelease);
        if (!Directory.Exists(outputDir)) return null;

        if (project.Type == ProjectType.Vue)
        {
            // Vue: just return the dist dir, caller handles npm
            return null;
        }

        // .NET: find .exe matching project name
        var projName = Path.GetFileNameWithoutExtension(project.ProjectPath);
        if (projName == null) projName = project.Name;

        var candidates = Directory.GetFiles(outputDir, "*.exe", SearchOption.TopDirectoryOnly);
        var exact = candidates.FirstOrDefault(f =>
            Path.GetFileNameWithoutExtension(f).Equals(projName, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        return candidates.FirstOrDefault();
    }

    private Task<bool> RunCustomScriptAsync(string script, string workDir)
    {
        if (script.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            var escapedScript = script.Replace("'", "''");
            var command = "$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); " +
                $"& '{escapedScript}'; " +
                "if ($null -eq $LASTEXITCODE) { exit 0 } else { exit $LASTEXITCODE }";
            return RunProcessAsync("powershell", $@"-NoProfile -ExecutionPolicy Bypass -Command ""{command}""", workDir);
        }

        return RunProcessAsync("cmd", $@"/c ""{script}""", workDir);
    }

    private static string ResolveWorkDir(ProjectEntry project)
    {
        return File.Exists(project.ProjectPath)
            ? Path.GetDirectoryName(project.ProjectPath)!
            : project.ProjectPath;
    }

    private async Task<bool> RunAndroidBuildAsync(ProjectEntry project, bool isRelease)
    {
        var projDir = project.ProjectPath;
        var variant = isRelease ? "assembleRelease" : "assembleDebug";
        return await RunProcessAsync("cmd", $"/c gradlew.bat {variant}", projDir);
    }

    private Task<bool> RunAndroidPublishAsync(ProjectEntry project)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        return Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            var copiedFiles = 0;
            DiagnosticLogService.Write("FilePublish", $"Android publish started; project={project.Name}");

            var projDir = project.ProjectPath;
            Directory.CreateDirectory(project.PublishDir);

            var apkDirs = new[] { @"app\build\outputs\apk" };
            foreach (var dir in apkDirs)
            {
                var fullDir = Path.Combine(projDir, dir);
                if (!Directory.Exists(fullDir)) continue;

                foreach (var apk in Directory.EnumerateFiles(fullDir, "*.apk", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var dest = Path.Combine(project.PublishDir, Path.GetFileName(apk));
                    File.Copy(apk, dest, true);
                    copiedFiles++;
                    OnOutput($"已复制: {Path.GetFileName(apk)} -> {project.PublishDir}");
                }
            }

            OnOutput("Android 发布完成");
            DiagnosticLogService.Write("FilePublish",
                $"Android publish finished; project={project.Name}; files={copiedFiles}; elapsed={stopwatch.Elapsed.TotalMilliseconds:F0}ms");
            return true;
        }, ct);
    }

    private async Task<bool> RunVueBuildAsync(ProjectEntry project)
    {
        var projDir = ResolveWorkDir(project);
        return await RunProcessAsync("cmd", "/c npm run build", projDir);
    }

    private Task<bool> RunVuePublishAsync(ProjectEntry project)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        return Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            var copiedFiles = 0;
            DiagnosticLogService.Write("FilePublish", $"Vue publish started; project={project.Name}");

            var projDir = ResolveWorkDir(project);
            var distPath = Path.Combine(projDir, "dist");
            if (!Directory.Exists(distPath))
            {
                OnOutput("错误: dist 目录不存在，请先编译该项目");
                return false;
            }

            Directory.CreateDirectory(project.PublishDir);

            foreach (var file in Directory.EnumerateFiles(distPath, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(distPath, file);
                var dest = Path.Combine(project.PublishDir, relative);
                var destDir = Path.GetDirectoryName(dest);
                if (destDir != null)
                    Directory.CreateDirectory(destDir);
                File.Copy(file, dest, true);
                copiedFiles++;
                OnOutput($"已复制: {relative} -> {project.PublishDir}");
            }

            OnOutput("Vue 发布完成");
            DiagnosticLogService.Write("FilePublish",
                $"Vue publish finished; project={project.Name}; files={copiedFiles}; elapsed={stopwatch.Elapsed.TotalMilliseconds:F0}ms");
            return true;
        }, ct);
    }

    private async Task<bool> RunProcessAsync(string fileName, string arguments, string workingDir)
    {
        OnOutput($"执行: {fileName} {arguments}");
        DiagnosticLogService.Write("Process", $"Starting: {fileName} {arguments}; workDir={workingDir}");
        var processStopwatch = Stopwatch.StartNew();
        long standardOutputLines = 0;
        long standardErrorLines = 0;

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        _currentProcess = process;

        var processExited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var standardOutputClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var standardErrorClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ct = _cts?.Token ?? CancellationToken.None;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                Interlocked.Increment(ref standardOutputLines);
                OnOutput(e.Data);
            }
            else standardOutputClosed.TrySetResult(true);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                Interlocked.Increment(ref standardErrorLines);
                OnOutput($"错误: {e.Data}");
            }
            else standardErrorClosed.TrySetResult(true);
        };
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => processExited.TrySetResult(process.ExitCode == 0);

        using var cancellationRegistration = ct.Register(() => processExited.TrySetResult(false));

        async Task WaitForRedirectedStreamsAsync(string reason)
        {
            var streamsClosed = Task.WhenAll(standardOutputClosed.Task, standardErrorClosed.Task);
            if (await Task.WhenAny(streamsClosed, Task.Delay(TimeSpan.FromSeconds(5))) == streamsClosed)
            {
                await streamsClosed;
                return;
            }

            DiagnosticLogService.Write("Process",
                $"Redirected streams did not close within 5s after {reason}; pid={process.Id}; " +
                $"stdoutLines={Interlocked.Read(ref standardOutputLines)}; stderrLines={Interlocked.Read(ref standardErrorLines)}");
            try { process.CancelOutputRead(); } catch { }
            try { process.CancelErrorRead(); } catch { }
            standardOutputClosed.TrySetResult(true);
            standardErrorClosed.TrySetResult(true);
        }

        try
        {
            process.Start();
            DiagnosticLogService.Write("Process", $"Started; pid={process.Id}; file={fileName}");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var completedTask = await Task.WhenAny(
                processExited.Task,
                Task.Delay(TimeSpan.FromMinutes(10), ct));

            if (ct.IsCancellationRequested)
            {
                try { process.Kill(true); } catch { }
                await WaitForRedirectedStreamsAsync("cancellation");
                DiagnosticLogService.Write("Process",
                    $"Cancelled; pid={process.Id}; elapsed={processStopwatch.Elapsed.TotalMilliseconds:F0}ms");
                throw new OperationCanceledException(ct);
            }

            if (completedTask != processExited.Task || !process.HasExited)
            {
                try { process.Kill(true); } catch { }
                await WaitForRedirectedStreamsAsync("timeout");
                OnOutput("编译超时（10分钟），已终止");
                DiagnosticLogService.Write("Process",
                    $"Timed out; pid={process.Id}; elapsed={processStopwatch.Elapsed.TotalMilliseconds:F0}ms");
                return false;
            }

            // The process exit event can precede the final async output callbacks.
            await WaitForRedirectedStreamsAsync("process exit");
            var success = process.ExitCode == 0;
            DiagnosticLogService.Write("Process",
                $"Exited; pid={process.Id}; exitCode={process.ExitCode}; elapsed={processStopwatch.Elapsed.TotalMilliseconds:F0}ms; " +
                $"stdoutLines={Interlocked.Read(ref standardOutputLines)}; stderrLines={Interlocked.Read(ref standardErrorLines)}");
            return success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            OnOutput($"执行失败: {ex.Message}");
            DiagnosticLogService.Write("Process", $"Failed after {processStopwatch.Elapsed.TotalMilliseconds:F0}ms: {ex}");
            return false;
        }
    }

    private void OnOutput(string text) => OutputReceived?.Invoke(text);

    private static string? FindBinNetDir(string baseDir, string config)
    {
        var bin = Path.Combine(baseDir, "bin", config);
        if (!Directory.Exists(bin)) return null;
        var netDirs = Directory.GetDirectories(bin, "net*");
        return netDirs.Length > 0 ? netDirs[0] : bin;
    }

    
    private static string? ParseCsprojOutputPath(string csprojPath, string config)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(csprojPath);
            System.Xml.Linq.XNamespace ns = doc.Root?.GetDefaultNamespace() ?? "";
            var props = doc.Descendants(ns + "PropertyGroup")
                .Where(pg => pg.Attribute("Condition") == null || pg.Attribute("Condition")!.Value.Contains(config, StringComparison.OrdinalIgnoreCase) == false)
                .SelectMany(pg => pg.Elements());

            string? outputPath = null, baseOutputPath = null;
            foreach (var el in props)
            {
                if (el.Name.LocalName == "OutputPath") outputPath = el.Value.Trim();
                if (el.Name.LocalName == "BaseOutputPath") baseOutputPath = el.Value.Trim();
            }

            var resolved = outputPath ?? baseOutputPath;
            if (string.IsNullOrWhiteSpace(resolved)) return null;

            var projDir = Path.GetDirectoryName(csprojPath)!;
            var full = Path.GetFullPath(Path.Combine(projDir, resolved));

            // Append config and framework if needed
            if (!full.EndsWith(config, StringComparison.OrdinalIgnoreCase))
            {
                var framework = doc.Descendants(ns + "TargetFramework").FirstOrDefault()?.Value.Trim();
                if (!string.IsNullOrWhiteSpace(framework))
                    return Path.Combine(full, config, framework);
                return Path.Combine(full, config);
            }

            return full;
        }
        catch { return null; }
    }

    
    private static string? GetLaunchUrl(string csprojPath)
    {
        try
        {
            var projDir = Path.GetDirectoryName(csprojPath)!;
            var launchSettingsPath = Path.Combine(projDir, "Properties", "launchSettings.json");
            if (!File.Exists(launchSettingsPath)) return null;

            var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(launchSettingsPath));
            if (!json.RootElement.TryGetProperty("profiles", out var profiles)) return null;

            // Iterate profiles and find first with applicationUrl
            foreach (var profile in profiles.EnumerateObject())
            {
                if (profile.Value.TryGetProperty("applicationUrl", out var url))
                    return url.GetString()?.Split(';')[0]; // Return first URL
            }
        }
        catch { }
        return null;
    }

    private static string? SearchBinRecursive(string dir, string config, int depth)
    {
        if (depth > 3) return null;
        try
        {
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                if (subDir.EndsWith(Path.DirectorySeparatorChar + ".git") ||
                    subDir.EndsWith(Path.DirectorySeparatorChar + ".vs")) continue;
                var result = FindBinNetDir(subDir, config);
                if (result != null) return result;
                result = SearchBinRecursive(subDir, config, depth + 1);
                if (result != null) return result;
            }
        }
        catch { }
        return null;
    }
}
