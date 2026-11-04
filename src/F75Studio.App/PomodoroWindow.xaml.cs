using System.Windows.Threading;
using F75Studio.App.ViewModels;
using Wpf.Ui.Controls;

namespace F75Studio.App;

public partial class PomodoroWindow : FluentWindow
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public PomodoroWindow(PomodoroViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _tick.Tick += (_, _) => viewModel.Refresh();
        Loaded += (_, _) => _tick.Start();
        Closed += (_, _) => _tick.Stop();
    }
}
