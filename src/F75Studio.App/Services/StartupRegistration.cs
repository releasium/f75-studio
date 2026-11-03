using Microsoft.Win32;

namespace F75Studio.App.Services;

public interface IStartupRegistration
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

public sealed class RegistryStartupRegistration : IStartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "F75Studio";
    private readonly string _command;

    public RegistryStartupRegistration(string exePath) => _command = $"\"{exePath}\" --tray";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, _command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
