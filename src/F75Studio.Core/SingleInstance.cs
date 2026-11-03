using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace F75Studio.Core;

public sealed class SingleInstance : IDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

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

                // A client that connects and never finishes writing must not block the listener forever.
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(ReadTimeout);
                var args = JsonSerializer.Deserialize<string[]>(await reader.ReadToEndAsync(readCts.Token)) ?? [];

                try
                {
                    ArgumentsReceived?.Invoke(this, args);
                }
                catch (Exception)
                {
                    // A subscriber threw. Swallow it so forwarding keeps working for later messages.
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Per-connection read timeout: drop this client and keep listening.
            }
            catch (ObjectDisposedException)
            {
                // Dispose ran while this loop was between checks; treat as shutdown.
                return;
            }
            catch (IOException)
            {
                // Typically "All pipe instances are busy". Back off so the loop does not spin.
                try
                {
                    await Task.Delay(RetryDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            catch (JsonException)
            {
                // A client sent garbage; keep listening.
            }
        }
    }

    private static string PipeName(string name) =>
        $"{name}.Pipe.{Environment.UserName}.{Process.GetCurrentProcess().SessionId}";

    public void Dispose()
    {
        _cts.Cancel();
        _mutex.Dispose();
        _cts.Dispose();
    }
}
