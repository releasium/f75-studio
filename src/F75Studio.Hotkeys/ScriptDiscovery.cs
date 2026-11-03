namespace F75Studio.Hotkeys;

public sealed record ScriptInfo(string Path, string Name, DateTime LastModified);

public static class ScriptDiscovery
{
    public static IReadOnlyList<ScriptInfo> Discover(IEnumerable<string> folders) =>
        folders
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.ahk", SearchOption.TopDirectoryOnly))
            // Windows' "*.ahk" pattern can also match longer extensions (8.3 quirk), so filter explicitly.
            .Where(p => p.EndsWith(".ahk", StringComparison.OrdinalIgnoreCase)
                        && !p.EndsWith(".pending.ahk", StringComparison.OrdinalIgnoreCase))
            .Select(System.IO.Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new ScriptInfo(path, System.IO.Path.GetFileName(path), File.GetLastWriteTime(path)))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
