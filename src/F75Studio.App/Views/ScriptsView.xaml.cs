using System.Windows.Controls;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace F75Studio.App.Views;

public partial class ScriptsView : UserControl
{
    private static readonly IHighlightingDefinition AhkHighlighting = LoadHighlighting();

    public ScriptsView()
    {
        InitializeComponent();
        Editor.SyntaxHighlighting = AhkHighlighting;
    }

    private static IHighlightingDefinition LoadHighlighting()
    {
        using var stream = typeof(ScriptsView).Assembly.GetManifestResourceStream("AutoHotkey.xshd")!;
        using var reader = XmlReader.Create(stream);
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
