using System.Diagnostics;

namespace OpenUtauMcp.Server;

/// <summary>
/// openutau-mcp install-bridge [--openutau-dir &lt;folder&gt;]: builds the bridge from this checkout against the installed
/// OpenUtau and copies it to Documents\OpenUtau\Plugins\McpBridge (the Install target). Needs the .NET SDK and
/// OpenUtau closed (it locks the plugin DLL). Run it after every OpenUtau update.
/// </summary>
public static class InstallBridge {
    public static async Task<int> RunAsync(IReadOnlyList<string> args) {
        string? root = FindRepoRoot(AppContext.BaseDirectory) ?? FindRepoRoot(Environment.CurrentDirectory);
        if (root is null) {
            Console.Error.WriteLine("Cannot find the openutau-mcp checkout (OpenUtauMcp.slnx) above this program or the current folder.");
            return 2;
        }
        if (Process.GetProcessesByName("OpenUtau").Length > 0) {
            Console.Error.WriteLine("OpenUtau is running and locks the plugin. Save your work, close OpenUtau, and run this again.");
            return 1;
        }
        var arguments = new List<string> { "build", Path.Combine(root, "src", "OpenUtauMcp.Bridge"), "-c", "Release", "-t:Install", "-v", "q", "-nologo" };
        int dir = args.ToList().IndexOf("--openutau-dir");
        if (dir >= 0 && dir + 1 < args.Count) {
            arguments.Add($"-p:OpenUtauDir={args[dir + 1]}");
        }
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        foreach (var a in arguments) {
            start.ArgumentList.Add(a);
        }
        Console.WriteLine($"Building and installing the bridge from {root} ...");
        Process process;
        try {
            process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        } catch (Exception e) {
            Console.Error.WriteLine($"Cannot run dotnet ({e.Message}). Install the .NET 10 SDK.");
            return 2;
        }
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0) {
            Console.Error.WriteLine("The build failed (see above). Is OpenUtau installed where Directory.Build.props expects it? Use --openutau-dir.");
            return process.ExitCode;
        }
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OpenUtau", "Plugins", "McpBridge",
            "OpenUtauMcp.Bridge.dll");
        Console.WriteLine(File.Exists(installed)
            ? $"Installed {installed}. Start OpenUtau, then check with: openutau-mcp doctor"
            : "The build succeeded but the plugin was not found where expected; run openutau-mcp doctor.");
        return 0;
    }

    static string? FindRepoRoot(string start) {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent) {
            if (File.Exists(Path.Combine(dir.FullName, "OpenUtauMcp.slnx"))) {
                return dir.FullName;
            }
        }
        return null;
    }
}
