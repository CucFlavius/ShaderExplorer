using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShaderExplorer.Core.Models;

namespace ShaderExplorer.App.Helpers;

public class PermutationSidebarBuilder
{
    private readonly ThemeResources _theme;
    private readonly Action<BlsPlatform, BlsPermutation> _onPermutationSelected;
    private Border? _selectedItem;

    public PermutationSidebarBuilder(ThemeResources theme, Action<BlsPlatform, BlsPermutation> onPermutationSelected)
    {
        _theme = theme;
        _onPermutationSelected = onPermutationSelected;
    }

    public void Populate(StackPanel panel, BlsContainer bls)
    {
        panel.Children.Clear();
        _selectedItem = null;
        var multiPlatform = bls.Platforms.Count > 1;

        foreach (var platform in bls.Platforms)
        {
            var permsPanel = new StackPanel();
            foreach (var perm in platform.Permutations)
            {
                var item = CreatePermutationItem(platform, perm);
                permsPanel.Children.Add(item);
            }

            if (multiPlatform)
            {
                var smLabel = platform.Tag switch
                {
                    "DX40" => "SM4.0",
                    "DX50" => "SM5.0",
                    "DX60" => "SM6.0",
                    "MT11" => "Metal",
                    _ => ""
                };
                var nonEmpty = platform.Permutations.Count(p => !p.IsEmpty);
                var headerText = $"{platform.Tag}";
                if (!string.IsNullOrEmpty(smLabel)) headerText += $" ({smLabel})";
                headerText += $"  [{nonEmpty}/{platform.Permutations.Count}]";

                var chevron = new TextBlock
                {
                    Text = "\uE972",
                    FontFamily = _theme.IconFont,
                    FontSize = 8,
                    Foreground = _theme.FgTertiaryBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(0)
                };

                var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
                headerPanel.Children.Add(chevron);
                headerPanel.Children.Add(new TextBlock
                {
                    Text = headerText,
                    FontFamily = _theme.MonoFont,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = _theme.HeaderBrush
                });

                var header = new Border
                {
                    Padding = new Thickness(8, 6, 8, 4),
                    Background = _theme.Bg2Brush,
                    BorderBrush = _theme.BorderSubtleBrush,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = headerPanel,
                    Cursor = Cursors.Hand
                };

                header.MouseLeftButtonUp += (_, _) =>
                {
                    var collapsed = permsPanel.Visibility == Visibility.Visible;
                    permsPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
                    ((RotateTransform)chevron.RenderTransform).Angle = collapsed ? -90 : 0;
                };

                panel.Children.Add(header);
            }

            panel.Children.Add(permsPanel);
        }
    }

    public void Clear(StackPanel panel)
    {
        panel.Children.Clear();
        _selectedItem = null;
    }

    private Border CreatePermutationItem(BlsPlatform platform, BlsPermutation perm)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var namePanel = new StackPanel
            { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var bcFormat = perm.Info?.BytecodeFormat;
        if (!perm.IsEmpty && bcFormat is BlsBytecodeFormat.MetalSource or BlsBytecodeFormat.MetalAIR)
        {
            var isSource = bcFormat == BlsBytecodeFormat.MetalSource;
            namePanel.Children.Add(new TextBlock
            {
                Text = isSource ? "\uE943" : "\uE74C",
                FontFamily = _theme.IconFont,
                FontSize = 9,
                Foreground = isSource ? _theme.AccentBrush : _theme.FgTertiaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0),
                ToolTip = isSource ? "Metal source" : "Compiled Metal library"
            });
        }

        var label = $"Perm {perm.Index}";
        namePanel.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = _theme.MonoFont,
            FontSize = 11,
            Foreground = perm.IsEmpty ? _theme.FgTertiaryBrush : _theme.FgPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(namePanel, 0);
        grid.Children.Add(namePanel);

        if (!perm.IsEmpty)
        {
            var sizeStr = FormatHelper.FormatSize(perm.DecompressedSize);
            var sizeBlock = new TextBlock
            {
                Text = sizeStr,
                FontFamily = _theme.MonoFont,
                FontSize = 10,
                Foreground = _theme.FgTertiaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            Grid.SetColumn(sizeBlock, 1);
            grid.Children.Add(sizeBlock);
        }
        else
        {
            var emptyBlock = new TextBlock
            {
                Text = "empty",
                FontFamily = _theme.MonoFont,
                FontSize = 10,
                FontStyle = FontStyles.Italic,
                Foreground = _theme.FgTertiaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            Grid.SetColumn(emptyBlock, 1);
            grid.Children.Add(emptyBlock);
        }

        var border = new Border
        {
            Child = grid,
            Padding = new Thickness(10, 3, 8, 3),
            Background = Brushes.Transparent,
            Cursor = perm.IsEmpty ? Cursors.Arrow : Cursors.Hand
        };

        if (!perm.IsEmpty)
        {
            border.MouseEnter += (_, _) =>
            {
                if (border != _selectedItem)
                    border.Background = _theme.HoverOverlayBrush;
            };
            border.MouseLeave += (_, _) =>
            {
                if (border != _selectedItem)
                    border.Background = Brushes.Transparent;
            };
            border.MouseLeftButtonUp += (_, _) =>
            {
                if (_selectedItem != null)
                    _selectedItem.Background = Brushes.Transparent;
                _selectedItem = border;
                border.Background = _theme.AccentBrush;

                _onPermutationSelected(platform, perm);
            };
        }

        return border;
    }
}
