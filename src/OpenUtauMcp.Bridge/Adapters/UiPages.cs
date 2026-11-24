using System.Reflection;
using Serilog;

namespace OpenUtauMcp.Bridge.Adapters;

/// <summary>
/// OpenUtau's main window shows the start page (recent files, recovery banner) until its own open actions set
/// MainWindowViewModel.Page = 1. When the bridge loads a project, the window must be switched explicitly, or the
/// user keeps seeing the start page although the project is loaded. The view model lives in OpenUtau.exe (not
/// OpenUtau.Core), so it is reached by reflection through Avalonia's Application.Current. Best effort: failures
/// are logged, never thrown. UI thread only.
/// </summary>
public static class UiPages {
    const int ProjectPage = 1;

    public static void ShowProject() {
        try {
            var appType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Avalonia.Application", throwOnError: false))
                .FirstOrDefault(t => t is not null);
            var app = appType?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var lifetime = app?.GetType().GetProperty("ApplicationLifetime")?.GetValue(app);
            var window = lifetime?.GetType().GetProperty("MainWindow")?.GetValue(lifetime);
            var viewModel = window?.GetType().GetProperty("DataContext")?.GetValue(window);
            var page = viewModel?.GetType().GetProperty("Page");
            if (page is null || !page.CanWrite) {
                Log.Warning($"{BridgeHost.LogPrefix} could not find the main window page to show the project");
                return;
            }
            page.SetValue(viewModel, ProjectPage);
        } catch (Exception e) {
            Log.Warning(e, $"{BridgeHost.LogPrefix} could not switch the main window to the project page");
        }
    }
}
