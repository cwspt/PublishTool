using System.Configuration;
using System.Data;
using System.Windows;
using PublishTool.App.Services;

namespace PublishTool.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DiagnosticLogService.Write("Application", $"Started; version={Environment.Version}; pid={Environment.ProcessId}");

        DispatcherUnhandledException += (_, args) =>
            DiagnosticLogService.Write("UnhandledException", args.Exception.ToString());
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            DiagnosticLogService.Write("UnhandledException", args.ExceptionObject?.ToString() ?? "Unknown exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
            DiagnosticLogService.Write("UnobservedTaskException", args.Exception.ToString());
    }
}

