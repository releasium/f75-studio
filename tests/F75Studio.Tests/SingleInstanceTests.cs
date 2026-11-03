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
        var received = new TaskCompletionSource<string[]>();
        first.ArgumentsReceived += (_, args) => received.TrySetResult(args);

        using var second = new SingleInstance(name);
        var sent = await SingleInstance.SendToFirstInstanceAsync(name, ["--profile", "Gaming"], TimeSpan.FromSeconds(3));

        Assert.True(first.IsFirstInstance);
        Assert.False(second.IsFirstInstance);
        Assert.True(sent);
        Assert.Equal(new[] { "--profile", "Gaming" }, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
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
}
