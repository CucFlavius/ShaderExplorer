using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShaderExplorer.Core.Models;

namespace ShaderExplorer.App.Helpers;

public class PropertyPanelBuilder
{
    private readonly ThemeResources _theme;
    private readonly Style _toolbarButtonStyle;
    private readonly Action<string, string, string> _onRename;
    private readonly Action<int> _onBrowseTexture;
    private readonly Action<int> _onClearTexture;

    public PropertyPanelBuilder(
        ThemeResources theme,
        Style toolbarButtonStyle,
        Action<string, string, string> onRename,
        Action<int> onBrowseTexture,
        Action<int> onClearTexture)
    {
        _theme = theme;
        _toolbarButtonStyle = toolbarButtonStyle;
        _onRename = onRename;
        _onBrowseTexture = onBrowseTexture;
        _onClearTexture = onClearTexture;
    }

    public void UpdateSignaturePanel(StackPanel panel, ShaderInfo shader)
    {
        panel.Children.Clear();

        if (shader.InputSignature.Count > 0)
            panel.Children.Add(CreateSectionExpander("Inputs", "\uE8AB", () =>
            {
                var p = new StackPanel();
                var i = 0;
                foreach (var sig in shader.InputSignature)
                {
                    var semantic = sig.SemanticIndex > 0 ? $"{sig.SemanticName}{sig.SemanticIndex}" : sig.SemanticName;
                    var mask = FormatHelper.FormatMask(sig.Mask);
                    p.Children.Add(CreatePropertyRow(
                        $"v{sig.Register}", $"{sig.ComponentType} {mask}", semantic, i++));
                }

                return p;
            }));
        if (shader.OutputSignature.Count > 0)
            panel.Children.Add(CreateSectionExpander("Outputs", "\uE8AB", () =>
            {
                var p = new StackPanel();
                var i = 0;
                foreach (var sig in shader.OutputSignature)
                {
                    var semantic = sig.SemanticIndex > 0 ? $"{sig.SemanticName}{sig.SemanticIndex}" : sig.SemanticName;
                    var mask = FormatHelper.FormatMask(sig.Mask);
                    p.Children.Add(CreatePropertyRow(
                        $"o{sig.Register}", $"{sig.ComponentType} {mask}", semantic, i++));
                }

                return p;
            }));
    }

    public void UpdateBuffersPanel(StackPanel panel, ShaderInfo shader, RenameMapping renameMapping)
    {
        panel.Children.Clear();
        foreach (var cb in shader.ConstantBuffers)
        {
            var cbLocal = cb;
            panel.Children.Add(CreateSectionExpander(
                $"cbuffer {cb.Name} : register(b{cb.RegisterSlot})  [{cb.Size} bytes]",
                "\uE8F1",
                () =>
                {
                    var p = new StackPanel();
                    var i = 0;
                    foreach (var v in cbLocal.Variables)
                    {
                        var typeName = FormatHelper.FormatVariableType(v.VariableType);
                        var renameKey = $"ConstantBuffer:{cbLocal.RegisterSlot}:{v.Offset}";
                        var isRenamed = renameMapping.VariableRenames.ContainsKey(renameKey);
                        var displayName = isRenamed ? renameMapping.VariableRenames[renameKey] : v.Name;

                        p.Children.Add(CreateRenameablePropertyRow(
                            displayName, typeName, $"offset: {v.Offset}, size: {v.Size}",
                            renameKey, v.Name, isRenamed, i++));
                    }

                    return p;
                }));
        }
    }

    public void UpdateResourcesPanel(StackPanel panel, ShaderInfo shader)
    {
        panel.Children.Clear();

        if (shader.ResourceBindings.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No resource bindings",
                Foreground = _theme.FgTertiaryBrush,
                Margin = new Thickness(12, 8, 0, 0),
                FontSize = 11
            });
            return;
        }

        var i = 0;
        foreach (var rb in shader.ResourceBindings)
        {
            var regType = rb.Type switch
            {
                ResourceType.Texture => "t",
                ResourceType.Sampler => "s",
                ResourceType.UAVRWTyped or
                    ResourceType.UAVRWStructured or
                    ResourceType.UAVRWByteAddress => "u",
                _ => "t"
            };
            panel.Children.Add(CreatePropertyRow(
                rb.Name, rb.Type.ToString(), $"register({regType}{rb.BindPoint})", i++));
        }
    }

    public void UpdateTexturesPanel(StackPanel panel, ShaderInfo shader, RenameMapping renameMapping)
    {
        panel.Children.Clear();

        var textureBindings = shader.ResourceBindings
            .Where(rb => rb.Type == ResourceType.Texture)
            .ToList();

        if (textureBindings.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No texture bindings",
                Foreground = _theme.FgTertiaryBrush,
                Margin = new Thickness(12, 8, 12, 8),
                FontSize = 11
            });
            return;
        }

        var rowIndex = 0;
        foreach (var binding in textureBindings)
        {
            var slot = binding.BindPoint;
            var assignedPath = renameMapping.TextureAssignments.TryGetValue(slot, out var p) ? p : null;

            panel.Children.Add(CreateTextureSlotRow(binding, assignedPath, rowIndex++));
        }
    }

    private Border CreateTextureSlotRow(ResourceBindingInfo binding, string? assignedPath, int rowIndex)
    {
        var slot = binding.BindPoint;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var regBlock = new TextBlock
        {
            Text = $"t{slot}",
            Foreground = _theme.DetailBrush,
            FontSize = 11,
            FontFamily = _theme.MonoFont,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(regBlock, 0);

        var nameBlock = new TextBlock
        {
            Text = binding.Name,
            Foreground = _theme.NameBrush,
            FontSize = 11,
            FontFamily = _theme.MonoFont,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(nameBlock, 1);

        var displayPath = assignedPath != null ? Path.GetFileName(assignedPath) : "Default";
        var pathBlock = new TextBlock
        {
            Text = displayPath,
            Foreground = assignedPath != null ? _theme.FgPrimaryBrush : _theme.FgTertiaryBrush,
            FontSize = 11,
            FontFamily = _theme.MonoFont,
            FontStyle = assignedPath != null ? FontStyles.Normal : FontStyles.Italic,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = assignedPath ?? "Using placeholder texture"
        };
        Grid.SetColumn(pathBlock, 2);

        // Browse button
        var browseBtn = new Button
        {
            Content = new TextBlock
            {
                Text = "\uE8E5",
                FontFamily = _theme.IconFont,
                FontSize = 11
            },
            ToolTip = "Browse for texture file",
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand,
            Style = _toolbarButtonStyle
        };
        browseBtn.Click += (_, _) => _onBrowseTexture(slot);
        Grid.SetColumn(browseBtn, 3);

        // Clear button
        var clearBtn = new Button
        {
            Content = new TextBlock
            {
                Text = "\uE711",
                FontFamily = _theme.IconFont,
                FontSize = 10
            },
            ToolTip = "Clear texture assignment",
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(2, 0, 0, 0),
            Cursor = Cursors.Hand,
            Visibility = assignedPath != null ? Visibility.Visible : Visibility.Collapsed,
            Style = _toolbarButtonStyle
        };
        clearBtn.Click += (_, _) => _onClearTexture(slot);
        Grid.SetColumn(clearBtn, 4);

        grid.Children.Add(regBlock);
        grid.Children.Add(nameBlock);
        grid.Children.Add(pathBlock);
        grid.Children.Add(browseBtn);
        grid.Children.Add(clearBtn);

        var border = new Border
        {
            Child = grid,
            Background = rowIndex % 2 == 0 ? Brushes.Transparent : _theme.Bg2Brush,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(12, 4, 8, 4),
            Margin = new Thickness(4, 0, 4, 0)
        };

        border.MouseEnter += (_, _) => border.Background = _theme.HoverOverlayBrush;
        border.MouseLeave += (_, _) => border.Background = rowIndex % 2 == 0 ? Brushes.Transparent : _theme.Bg2Brush;

        return border;
    }

    public Expander CreateSectionExpander(string headerText, string iconGlyph, Func<StackPanel> contentFactory)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            Text = iconGlyph,
            FontFamily = _theme.IconFont,
            FontSize = 11,
            Foreground = _theme.HeaderBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        header.Children.Add(new TextBlock
        {
            Text = headerText,
            FontWeight = FontWeights.SemiBold,
            Foreground = _theme.HeaderBrush,
            FontSize = 11,
            FontFamily = _theme.MonoFont,
            VerticalAlignment = VerticalAlignment.Center
        });

        var expander = new Expander
        {
            Header = header,
            IsExpanded = true,
            Content = contentFactory()
        };
        return expander;
    }

    public Border CreateRenameablePropertyRow(string displayName, string type, string detail,
        string renameKey, string originalName, bool isRenamed, int rowIndex)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });

        var nameBlock = new TextBlock
        {
            Text = displayName,
            Foreground = isRenamed ? _theme.RenamedBrush : _theme.NameBrush,
            FontSize = 11,
            FontFamily = _theme.MonoFont,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = isRenamed ? $"Renamed from: {originalName} (click to rename)" : "Click to rename"
        };
        Grid.SetColumn(nameBlock, 0);

        var typeBlock = new TextBlock
        {
            Text = type,
            Foreground = _theme.TypeBrush,
            FontSize = 11,
            FontFamily = _theme.MonoFont,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(typeBlock, 1);

        var detailBlock = new TextBlock
        {
            Text = detail,
            Foreground = _theme.DetailBrush,
            FontSize = 11,
            FontFamily = _theme.MonoFont,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(detailBlock, 2);

        // Edit icon (visible on hover)
        var editIcon = new TextBlock
        {
            Text = "\uE70F",
            FontFamily = _theme.IconFont,
            FontSize = 10,
            Foreground = _theme.FgTertiaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        Grid.SetColumn(editIcon, 3);

        grid.Children.Add(nameBlock);
        grid.Children.Add(typeBlock);
        grid.Children.Add(detailBlock);
        grid.Children.Add(editIcon);

        var border = new Border
        {
            Child = grid,
            Background = rowIndex % 2 == 0 ? Brushes.Transparent : _theme.Bg2Brush,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(12, 3, 8, 3),
            Margin = new Thickness(4, 0, 4, 0)
        };

        border.MouseEnter += (_, _) =>
        {
            border.Background = _theme.HoverOverlayBrush;
            editIcon.Visibility = Visibility.Visible;
        };
        border.MouseLeave += (_, _) =>
        {
            border.Background = rowIndex % 2 == 0 ? Brushes.Transparent : _theme.Bg2Brush;
            editIcon.Visibility = Visibility.Collapsed;
        };
        border.MouseLeftButtonUp += (_, _) => _onRename(renameKey, originalName, displayName);
        border.Cursor = Cursors.Hand;

        return border;
    }

    public Border CreatePropertyRow(string name, string type, string detail, int rowIndex)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var nameBlock = new TextBlock
        {
            Text = name,
            Foreground = _theme.NameBrush,
            FontSize = 11,
            FontFamily = _theme.MonoFont,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(nameBlock, 0);

        var typeBlock = new TextBlock
        {
            Text = type,
            Foreground = _theme.TypeBrush,
            FontSize = 11,
            FontFamily = _theme.MonoFont,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(typeBlock, 1);

        var detailBlock = new TextBlock
        {
            Text = detail,
            Foreground = _theme.DetailBrush,
            FontSize = 11,
            FontFamily = _theme.MonoFont,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(detailBlock, 2);

        grid.Children.Add(nameBlock);
        grid.Children.Add(typeBlock);
        grid.Children.Add(detailBlock);

        var border = new Border
        {
            Child = grid,
            Background = rowIndex % 2 == 0 ? Brushes.Transparent : _theme.Bg2Brush,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(12, 3, 8, 3),
            Margin = new Thickness(4, 0, 4, 0)
        };

        border.MouseEnter += (_, _) => border.Background = _theme.HoverOverlayBrush;
        border.MouseLeave += (_, _) => border.Background = rowIndex % 2 == 0 ? Brushes.Transparent : _theme.Bg2Brush;

        return border;
    }
}
