using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;

namespace F75Studio.App.Services;

public sealed class TrayService : IDisposable
{
    private readonly TaskbarIcon _icon;

    public TrayService(AppServices services, Action show, Action exit)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Open F75 Studio", show));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", exit));

        _icon = new TaskbarIcon
        {
            ToolTipText = "F75 Studio",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            ContextMenu = menu,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(show),
        };
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void Notify(string title, string message) => _icon.ShowNotification(title, message);

    public void Dispose() => _icon.Dispose();

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
