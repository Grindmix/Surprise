using System;
using System.IO;
using System.Reflection;

namespace Surprise.Setup;

internal static class ExecutorBundle
{
    private const string ResourceName = "Surprise.bundled.executor.exe";
    private static string? ownedPath;

    /// <summary>Published executor extracted from Setup, or a sibling/dev copy when this build has no embedded file.</summary>
    public static string Prepare()
    {
        if (ownedPath is not null && File.Exists(ownedPath)) return ownedPath;
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (resource is not null)
        {
            string path = Path.Combine(Path.GetTempPath(), "Surprise-bundled-executor-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) resource.CopyTo(file);
                ownedPath = path;
                return path;
            }
            catch
            {
                if (File.Exists(path)) File.Delete(path);
                throw;
            }
        }
        string sibling = Path.Combine(AppContext.BaseDirectory, "Surprise.Executor.exe");
        if (File.Exists(sibling)) return Path.GetFullPath(sibling);
        string? dev = FindDevExecutor();
        if (dev is not null) return dev;
        throw new FileNotFoundException("The published executor is not bundled with Setup and was not found beside it.");
    }

    public static void Cleanup()
    {
        if (ownedPath is null) return;
        try { if (File.Exists(ownedPath)) File.Delete(ownedPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        ownedPath = null;
    }

    private static string? FindDevExecutor()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string config = dir.FullName.Contains("Debug", StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";
            string[] candidates =
            [
                Path.Combine(dir.FullName, "artifacts", "publish", "win-x64", "Executor", "Surprise.Executor.exe"),
                Path.Combine(dir.FullName, "artifacts", "publish", "win-arm64", "Executor", "Surprise.Executor.exe"),
                Path.Combine(dir.FullName, "src", "Surprise.Executor", "bin", config, "net10.0-windows10.0.19041.0", "win-x64", "Surprise.Executor.exe"),
                Path.Combine(dir.FullName, "src", "Surprise.Executor", "bin", config, "net10.0-windows10.0.19041.0", "Surprise.Executor.exe"),
                Path.Combine(dir.FullName, "src", "Surprise.Executor", "bin", "Release", "net10.0-windows10.0.19041.0", "Surprise.Executor.exe"),
                Path.Combine(dir.FullName, "src", "Surprise.Executor", "bin", "Debug", "net10.0-windows10.0.19041.0", "Surprise.Executor.exe")
            ];
            foreach (string candidate in candidates)
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }
}
