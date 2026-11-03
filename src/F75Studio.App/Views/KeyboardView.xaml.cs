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

    /// <summary>Only a user drag or key press may change the hue; load-time echoes and coercion are ignored.</summary>
    private void OnHueSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not Slider slider || Vm is null) return;
        if (!slider.IsMouseCaptureWithin && !slider.IsKeyboardFocused) return;
        Vm.Hue = e.NewValue;
    }

    private void OnKeyMouseEnter(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if ((sender as FrameworkElement)?.DataContext is KeyItemViewModel key) Vm?.DragOverKey(key);
    }
}
