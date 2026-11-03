using System.ComponentModel;
using Wpf.Ui.Controls;

namespace F75Studio.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow() => InitializeComponent();

    /// <summary>When false, closing the window only hides it to the tray.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
