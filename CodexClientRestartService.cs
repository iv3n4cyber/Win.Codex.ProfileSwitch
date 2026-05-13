using System.Diagnostics;
using Microsoft.Win32;

namespace Win.Codex.ProfileSwitch;

internal sealed class CodexClientRestartService
{
    private static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExitAfterKillTimeout = TimeSpan.FromSeconds(3);

    public async Task<RestartResult> RestartAsync(CancellationToken cancellationToken = default)
    {
        var processes = FindCodexClientProcesses();
        var launchTarget = ResolveLaunchTarget(processes);

        await StopProcessesAsync(processes, cancellationToken);

        if (launchTarget is null)
        {
            launchTarget = ResolveInstalledCodexTarget();
        }

        if (launchTarget is null)
        {
            return new RestartResult(
                false,
                "已关闭正在运行的 Codex 客户端，但没有找到可启动的 Codex 安装路径。请从开始菜单手动打开一次 Codex。"
            );
        }

        StartCodex(launchTarget.Value);
        return new RestartResult(true, "已关闭并重新启动 Codex 客户端。");
    }

    private static List<Process> FindCodexClientProcesses()
    {
        return Process.GetProcesses()
            .Where(IsCodexClientProcess)
            .ToList();
    }

    private static bool IsCodexClientProcess(Process process)
    {
        try
        {
            var name = process.ProcessName;
            if (!name.Equals("Codex", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("codex", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var path = SafeMainModulePath(process);
            if (path is null)
            {
                return name.Equals("Codex", StringComparison.OrdinalIgnoreCase);
            }

            if (path.Contains(@"\.codex\.sandbox-bin\", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return path.Contains(@"\WindowsApps\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) ||
                   Path.GetFileName(path).Equals("Codex.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static LaunchTarget? ResolveLaunchTarget(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            var path = SafeMainModulePath(process);
            if (path is null)
            {
                continue;
            }

            if (TryCreatePackagedAppTarget(path, out var packagedTarget))
            {
                return packagedTarget;
            }

            if (File.Exists(path))
            {
                return LaunchTarget.Executable(path);
            }
        }

        return null;
    }

    private static LaunchTarget? ResolveInstalledCodexTarget()
    {
        foreach (var target in ResolveAppPathTargets())
        {
            return target;
        }

        foreach (var path in ResolveCommonInstallPaths())
        {
            if (TryCreatePackagedAppTarget(path, out var packagedTarget))
            {
                return packagedTarget;
            }

            if (File.Exists(path))
            {
                return LaunchTarget.Executable(path);
            }
        }

        return null;
    }

    private static IEnumerable<LaunchTarget> ResolveAppPathTargets()
    {
        string[] registryPaths =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Codex.exe",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\codex.exe"
        ];

        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var keyPath in registryPaths)
            {
                using var key = root.OpenSubKey(keyPath);
                var value = key?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
                {
                    yield return LaunchTarget.Executable(value);
                }
            }
        }
    }

    private static IEnumerable<string> ResolveCommonInstallPaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        string[] candidates =
        [
            Path.Combine(localAppData, "Programs", "Codex", "Codex.exe"),
            Path.Combine(localAppData, "Programs", "OpenAI Codex", "Codex.exe"),
            Path.Combine(programFiles, "Codex", "Codex.exe"),
            Path.Combine(programFiles, "OpenAI Codex", "Codex.exe"),
            Path.Combine(programFilesX86, "Codex", "Codex.exe"),
            Path.Combine(programFilesX86, "OpenAI Codex", "Codex.exe")
        ];

        foreach (var candidate in candidates.Where(File.Exists))
        {
            yield return candidate;
        }

        var windowsAppsRoot = Path.Combine(programFiles, "WindowsApps");
        if (!Directory.Exists(windowsAppsRoot))
        {
            yield break;
        }

        IEnumerable<string> packageDirectories;
        try
        {
            packageDirectories = Directory.EnumerateDirectories(windowsAppsRoot, "OpenAI.Codex_*");
        }
        catch
        {
            yield break;
        }

        foreach (var directory in packageDirectories)
        {
            var candidate = Path.Combine(directory, "Codex.exe");
            if (File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static bool TryCreatePackagedAppTarget(string executablePath, out LaunchTarget target)
    {
        target = default;

        var directory = Path.GetDirectoryName(executablePath);
        var packageDirectoryName = directory is null ? null : Path.GetFileName(directory);
        if (packageDirectoryName is null ||
            !packageDirectoryName.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var doubleUnderscoreIndex = packageDirectoryName.LastIndexOf("__", StringComparison.Ordinal);
        if (doubleUnderscoreIndex < 0)
        {
            return false;
        }

        var publisherId = packageDirectoryName[(doubleUnderscoreIndex + 2)..];
        if (string.IsNullOrWhiteSpace(publisherId))
        {
            return false;
        }

        target = LaunchTarget.ShellApp($"OpenAI.Codex_{publisherId}!App");
        return true;
    }

    private static async Task StopProcessesAsync(IEnumerable<Process> processes, CancellationToken cancellationToken)
    {
        var processList = processes.ToList();
        foreach (var process in processList)
        {
            TryCloseMainWindow(process);
        }

        await WaitForExitAsync(processList, GracefulCloseTimeout, cancellationToken);

        foreach (var process in processList.Where(IsStillRunning))
        {
            TryKill(process);
        }

        await WaitForExitAsync(processList, ExitAfterKillTimeout, cancellationToken);

        foreach (var process in processList)
        {
            process.Dispose();
        }
    }

    private static void StartCodex(LaunchTarget target)
    {
        var startInfo = target.Kind switch
        {
            LaunchTargetKind.Executable => new ProcessStartInfo
            {
                FileName = target.Value,
                UseShellExecute = true
            },
            LaunchTargetKind.ShellApp => new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"shell:AppsFolder\\{target.Value}",
                UseShellExecute = true
            },
            _ => throw new InvalidOperationException("不支持的 Codex 启动方式")
        };

        Process.Start(startInfo);
    }

    private static async Task WaitForExitAsync(
        IReadOnlyCollection<Process> processes,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && processes.Any(IsStillRunning))
        {
            await Task.Delay(200, cancellationToken);
        }
    }

    private static bool IsStillRunning(Process process)
    {
        try
        {
            process.Refresh();
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void TryCloseMainWindow(Process process)
    {
        try
        {
            if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero)
            {
                process.CloseMainWindow();
            }
        }
        catch
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static string? SafeMainModulePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}

internal readonly record struct RestartResult(bool Started, string Message);

internal enum LaunchTargetKind
{
    Executable,
    ShellApp
}

internal readonly record struct LaunchTarget(LaunchTargetKind Kind, string Value)
{
    public static LaunchTarget Executable(string path) =>
        new(LaunchTargetKind.Executable, path);

    public static LaunchTarget ShellApp(string appUserModelId) =>
        new(LaunchTargetKind.ShellApp, appUserModelId);
}
