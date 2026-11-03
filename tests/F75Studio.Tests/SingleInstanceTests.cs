using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using F75Studio.Core;
using F75Studio.Tests.TestSupport;

namespace F75Studio.Tests;

public class SingleInstanceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Second_instance_forwards_arguments_to_the_first()
    {
        var name = "F75StudioTest" + Guid.NewGuid().ToString("N");
        using var first = new SingleInstance(name);
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ArgumentsReceived += (_, args) => received.TrySetResult(args);

        using var second = new SingleInstance(name);
        var sent = await SingleInstance.SendToFirstInstanceAsync(name, ["--profile", "Gaming"], TimeSpan.FromSeconds(3));

        Assert.True(first.IsFirstInstance);
        Assert.False(second.IsFirstInstance);
        Assert.True(sent);
        Assert.Equal(new[] { "--profile", "Gaming" }, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Listener_survives_a_subscriber_that_throws()
    {
        var name = "F75StudioTest" + Guid.NewGuid().ToString("N");
        using var first = new SingleInstance(name);
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        first.ArgumentsReceived += (_, args) =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("subscriber failed");
            received.TrySetResult(args);
        };

        Assert.True(await SingleInstance.SendToFirstInstanceAsync(name, ["--first"], TimeSpan.FromSeconds(3)));
        Assert.True(await SingleInstance.SendToFirstInstanceAsync(name, ["--second"], TimeSpan.FromSeconds(3)));

        Assert.Equal(new[] { "--second" }, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Listener_survives_a_client_that_sends_malformed_json()
    {
        var name = "F75StudioTest" + Guid.NewGuid().ToString("N");
        using var first = new SingleInstance(name);
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ArgumentsReceived += (_, args) => received.TrySetResult(args);

        await SendRawAsync(name, "{not json");
        Assert.True(await SingleInstance.SendToFirstInstanceAsync(name, ["--after-garbage"], TimeSpan.FromSeconds(3)));

        Assert.Equal(new[] { "--after-garbage" }, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Sending_without_a_first_instance_returns_false()
    {
        Assert.False(await SingleInstance.SendToFirstInstanceAsync("F75StudioNobody" + Guid.NewGuid().ToString("N"), ["--show"], TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void FileLog_appends_to_a_daily_file()
    {
        var log = new FileLog(_temp.Combine("logs"));

        var path = log.Write("first");
        log.Write(new InvalidOperationException("boom"), "Context");

        Assert.Equal($"{DateTime.Now:yyyy-MM-dd}.log", Path.GetFileName(path));
        var text = File.ReadAllText(path);
        Assert.Contains("first", text);
        Assert.Contains("Context: System.InvalidOperationException: boom", text);
    }

    // Writes a raw payload to the listener's pipe. The pipe name mirrors SingleInstance's private naming
    // so the test can send something that SendToFirstInstanceAsync would never produce.
    private static async Task SendRawAsync(string name, string payload)
    {
        var pipe = $"{name}.Pipe.{Environment.UserName}.{Process.GetCurrentProcess().SessionId}";
        await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out);
        await client.ConnectAsync(3000);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false));
        await writer.WriteAsync(payload);
        await writer.FlushAsync();
    }
}
