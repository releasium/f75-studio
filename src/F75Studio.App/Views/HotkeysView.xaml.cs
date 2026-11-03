using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using F75Studio.App.ViewModels;
using F75Studio.Hotkeys;
using Microsoft.Win32;

namespace F75Studio.App.Views;

public partial class HotkeysView : UserControl
{
    public HotkeysView() => InitializeComponent();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not HotkeysViewModel { IsRecording: true } vm) return;
        if (ToAhkKey(e) is { } key) vm.RecordKeyDown(key);
        e.Handled = true;
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (DataContext is not HotkeysViewModel { IsRecording: true } vm) return;
        if (ToAhkKey(e) is { } key) vm.RecordKeyUp(key);
        e.Handled = true;
    }

    private static string? ToAhkKey(KeyEventArgs e) =>
        AhkKeyNames.FromWpfKeyName((e.Key == Key.System ? e.SystemKey : e.Key).ToString());

    private void OnBrowseRunTarget(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HotkeyItemViewModel item) return;
        var dialog = new OpenFileDialog { Title = "Choose a program or file", Filter = "Programs|*.exe;*.bat;*.cmd;*.lnk|All files|*.*" };
        if (dialog.ShowDialog() == true) item.RunTarget = dialog.FileName;
    }
}
