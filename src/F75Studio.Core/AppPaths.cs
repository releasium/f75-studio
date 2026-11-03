namespace F75Studio.Core;

public sealed class AppPaths
{
    public AppPaths(string dataRoot, string documentsRoot)
    {
        DataRoot = dataRoot;
        DocumentsRoot = documentsRoot;
    }

    public static AppPaths Default() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "F75Studio"),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

    public string DataRoot { get; }
    public string DocumentsRoot { get; }
    public string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public string HotkeysFile => Path.Combine(DataRoot, "hotkeys.json");
    public string LogsFolder => Path.Combine(DataRoot, "logs");
    public string GeneratedScriptsFolder => Path.Combine(DocumentsRoot, "F75Studio");
    public string GeneratedHotkeysScript => Path.Combine(GeneratedScriptsFolder, "hotkeys.ahk");

    public IReadOnlyList<string> ScriptSearchFolders =>
        [DocumentsRoot, Path.Combine(DocumentsRoot, "AutoHotkey"), GeneratedScriptsFolder];
}
