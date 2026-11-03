using System.Windows;

namespace F75Studio.App.Services;

public static class Ui
{
    /// <summary>Runs on the UI thread; runs inline when there is no WPF Application (unit tests).</summary>
    public static void Run(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
