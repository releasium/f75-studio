using CommunityToolkit.Mvvm.ComponentModel;
using F75Studio.Lighting;

namespace F75Studio.App.ViewModels;

public sealed partial class KeyItemViewModel(KeyDef key) : ObservableObject
{
    [ObservableProperty] private Rgb _color = Rgb.Black;
    [ObservableProperty] private bool _isSelected;

    public KeyDef Key { get; } = key;
    public string Name => Key.Name;
    public int Led => Key.Led;
    public int X => Key.X;
    public int Y => Key.Y;
    public int Width => Key.Width;
    public int Height => Key.Height;
}
