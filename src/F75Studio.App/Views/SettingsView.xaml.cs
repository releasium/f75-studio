using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using F75Studio.App.ViewModels;
using Microsoft.Win32;

namespace F75Studio.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void OnBrowseAhk(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Locate AutoHotkey v2", Filter = "AutoHotkey v2|AutoHotkey*.exe|Programs|*.exe" };
        if (dialog.ShowDialog() == true && DataContext is SettingsViewModel vm) vm.AhkPath = dialog.FileName;
    }

    private void OnOpenScriptsFolder(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        Directory.CreateDirectory(vm.ScriptsFolder);
        Process.Start("explorer.exe", vm.ScriptsFolder);
    }
}
