using F75Studio.App.Services;

namespace F75Studio.Tests.TestSupport;

public sealed class FakeStartupRegistration : IStartupRegistration
{
    public bool IsEnabled { get; private set; }
    public void SetEnabled(bool enabled) => IsEnabled = enabled;
}
