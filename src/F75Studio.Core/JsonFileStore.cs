using System.Text.Json;
using System.Text.Json.Serialization;

namespace F75Studio.Core;

public sealed class JsonFileStore<T> where T : class
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Func<T> _createDefault;

    public JsonFileStore(string filePath, Func<T> createDefault)
    {
        FilePath = filePath;
        _createDefault = createDefault;
    }

    public string FilePath { get; }

    public T Load()
    {
        if (!File.Exists(FilePath)) return _createDefault();
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(FilePath), Options) ?? _createDefault();
        }
        catch (JsonException)
        {
            File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
            return _createDefault();
        }
    }

    public void Save(T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, FilePath, overwrite: true);
    }
}
