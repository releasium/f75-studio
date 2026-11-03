using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace F75Studio.Core;

public sealed class SingleInstance : IDisposable
{
    private readonly string _name;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    public SingleInstance(string name)
    {
        _name = name;
        _mutex = new Mutex(initiallyOwned: true, $@"Local\{name}.Mutex", out var createdNew);
        IsFirstInstance = createdNew;
        if (IsFirstInstance) _ = ListenAsync(_cts.Token);
    }

    public bool IsFirstInstance { get; }

    /// <summary>Raised on a background thread with the arguments another instance was started with.</summary>
    public event EventHandler<string[]>? ArgumentsReceived;

    public static async Task<bool> SendToFirstInstanceAsync(string name, string[] args, TimeSpan timeout)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", PipeName(name), PipeDirection.Out);
            await client.ConnectAsync((int)timeout.TotalMilliseconds);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false));
            await writer.WriteAsync(JsonSerializer.Serialize(args));
            await writer.FlushAsync();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return false;
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName(_name), PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var args = JsonSerializer.Deserialize<string[]>(await reader.ReadToEndAsync(ct)) ?? [];
                ArgumentsReceived?.Invoke(this, args);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // A client sent garbage or disconnected early; keep listening.
            }
        }
    }

    private static string PipeName(string name) => $"{name}.Pipe.{Environment.UserName}";

    public void Dispose()
    {
        _cts.Cancel();
        _mutex.Dispose();
    }
}
