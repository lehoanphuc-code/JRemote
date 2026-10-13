namespace JRemote.Android.Adb;

public static class AdbLocator
{
    private static readonly string[] SearchPaths =
    [
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin", "adb", "adb.exe"),
        Path.Combine(Directory.GetCurrentDirectory(), "bin", "adb", "adb.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"),
        Environment.GetEnvironmentVariable("ANDROID_HOME") is { } home
            ? Path.Combine(home, "platform-tools", "adb.exe")
            : string.Empty,
        Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") is { } root
            ? Path.Combine(root, "platform-tools", "adb.exe")
            : string.Empty
    ];

    public static string? FindAdbPath()
    {
        foreach (var path in SearchPaths)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }

        // Try checking PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var folder in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim(), "adb.exe");
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {
                // Ignore invalid PATH entries
            }
        }

        return null;
    }
}
