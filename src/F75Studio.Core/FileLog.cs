namespace F75Studio.Core;

public sealed class FileLog
{
    private readonly string _folder;
    private readonly object _gate = new();

    public FileLog(string folder) => _folder = folder;

    public string Write(string message)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_folder);
            var path = Path.Combine(_folder, $"{DateTime.Now:yyyy-MM-dd}.log");
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            return path;
        }
    }

    public string Write(Exception ex, string context) => Write($"{context}: {ex}");
}
