using System.Windows.Media;

namespace ShaderExplorer.App.Helpers;

public record ThemeResources(
    SolidColorBrush NameBrush,
    SolidColorBrush RenamedBrush,
    SolidColorBrush TypeBrush,
    SolidColorBrush DetailBrush,
    SolidColorBrush HeaderBrush,
    SolidColorBrush Bg2Brush,
    SolidColorBrush HoverOverlayBrush,
    SolidColorBrush FgTertiaryBrush,
    SolidColorBrush FgPrimaryBrush,
    SolidColorBrush FgSecondaryBrush,
    SolidColorBrush BorderSubtleBrush,
    SolidColorBrush AccentBrush,
    FontFamily MonoFont,
    FontFamily IconFont);
