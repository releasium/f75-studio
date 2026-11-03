namespace F75Studio.App.Services;

/// <summary>Resolves the path of F75Studio.exe even when the app is hosted by `dotnet F75Studio.dll`.</summary>
public static class AppExecutable
{
    public static string Path { get; } = Resolve(AppContext.BaseDirectory, Environment.ProcessPath, File.Exists);

    public static string Resolve(string baseDirectory, string? processPath, Func<string, bool> fileExists)
    {
        var exe = System.IO.Path.Combine(baseDirectory, "F75Studio.exe");
        return fileExists(exe) ? exe : processPath ?? exe;
    }
}
