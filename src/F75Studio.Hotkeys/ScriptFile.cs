using System.Text;

namespace F75Studio.Hotkeys;

public static class ScriptFile
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, content, Utf8NoBom);
    }

    public static void SaveWithBackup(string path, string content)
    {
        if (File.Exists(path))
        {
            // The very first save keeps the untouched original forever; .bak is the rolling previous version.
            if (!File.Exists(path + ".orig")) File.Copy(path, path + ".orig");
            File.Copy(path, path + ".bak", overwrite: true);
        }
        Write(path, content);
    }
}
