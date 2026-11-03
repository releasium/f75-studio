namespace F75Studio.Hotkeys;

public static class AhkLocator
{
    public static string? Find(string? configuredPath, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        return Candidates(configuredPath).FirstOrDefault(fileExists);
    }

    public static IEnumerable<string> Candidates(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath)) yield return configuredPath;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programFiles, "AutoHotkey", "v2", "AutoHotkey64.exe");
        yield return Path.Combine(programFiles, "AutoHotkey", "AutoHotkey64.exe");
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localAppData, "Programs", "AutoHotkey", "v2", "AutoHotkey64.exe");
    }
}
