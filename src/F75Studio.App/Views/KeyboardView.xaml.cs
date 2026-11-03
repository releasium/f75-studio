using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using F75Studio.App.ViewModels;

namespace F75Studio.App.Views;

public partial class KeyboardView : UserControl
{
    public KeyboardView() => InitializeComponent();

    private KeyboardViewModel? Vm => DataContext as KeyboardViewModel;

    private void OnKeyMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not KeyItemViewModel key || Vm is null) return;
        Vm.ClickKey(key, additive: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        e.Handled = true;
    }

    private void OnKeyMouseEnter(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if ((sender as FrameworkElement)?.DataContext is KeyItemViewModel key) Vm?.DragOverKey(key);
    }
}
