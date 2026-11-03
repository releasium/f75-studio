using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace F75Studio.App.ViewModels;

public sealed record NavItem(string Title, SymbolRegular Icon, object Page);

public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private NavItem _selected;

    public MainViewModel(IEnumerable<NavItem> items)
    {
        Items = items.ToList();
        _selected = Items[0];
    }

    public IReadOnlyList<NavItem> Items { get; }

    public void Navigate(object page) => Selected = Items.First(i => ReferenceEquals(i.Page, page));
}
