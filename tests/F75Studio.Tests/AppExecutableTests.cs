using F75Studio.App.Services;

namespace F75Studio.Tests;

public class AppExecutableTests
{
    [Fact]
    public void Prefers_exe_next_to_the_assembly_over_dotnet_host()
    {
        var path = AppExecutable.Resolve(@"C:\app\", @"C:\dotnet\dotnet.exe", _ => true);

        Assert.Equal(@"C:\app\F75Studio.exe", path);
    }

    [Fact]
    public void Falls_back_to_process_path_when_exe_is_missing()
    {
        var path = AppExecutable.Resolve(@"C:\app\", @"C:\x\F75Studio.exe", _ => false);

        Assert.Equal(@"C:\x\F75Studio.exe", path);
    }
}
