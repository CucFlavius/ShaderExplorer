using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using ShaderExplorer.App.Helpers;
using ShaderExplorer.App.Services;
using ShaderExplorer.App.ViewModels;
using ShaderExplorer.App.Views;
using ShaderExplorer.Core.Models;
using ShaderExplorer.Renderer;

namespace ShaderExplorer.App;

public partial class MainWindow : Window
{
    private MonacoEditorService? _editorService;

    private PermutationSidebarBuilder? _sidebarBuilder;
    private bool _viewportInitialized;

    public MainWindow()
    {
        InitializeComponent();
        ShaderBadgeBorder.Visibility = Visibility.Collapsed;
        Loaded += MainWindow_Loaded;
        Closing += (_, _) => Viewport.Shutdown();
        StateChanged += (_, _) => UpdateMaximizeIcon();
        Deactivated += (_, _) => EditorDropOverlay.Visibility = Visibility.Collapsed;
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private ThemeResources? _theme;
    private PropertyPanelBuilder? _panelBuilder;

    private void UpdateMaximizeIcon()
    {
        MaximizeIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _theme = new ThemeResources(
            NameBrush: (SolidColorBrush)FindResource("SyntaxNameBrush"),
            RenamedBrush: (SolidColorBrush)FindResource("SyntaxRenamedBrush"),
            TypeBrush: (SolidColorBrush)FindResource("SyntaxTypeBrush"),
            DetailBrush: (SolidColorBrush)FindResource("SyntaxDetailBrush"),
            HeaderBrush: (SolidColorBrush)FindResource("SyntaxHeaderBrush"),
            Bg2Brush: (SolidColorBrush)FindResource("Bg2Brush"),
            HoverOverlayBrush: (SolidColorBrush)FindResource("HoverOverlayBrush"),
            FgTertiaryBrush: (SolidColorBrush)FindResource("FgTertiaryBrush"),
            FgPrimaryBrush: (SolidColorBrush)FindResource("FgPrimaryBrush"),
            FgSecondaryBrush: (SolidColorBrush)FindResource("FgSecondaryBrush"),
            BorderSubtleBrush: (SolidColorBrush)FindResource("BorderSubtleBrush"),
            AccentBrush: (SolidColorBrush)FindResource("AccentBrush"),
            MonoFont: (FontFamily)FindResource("MonoFont"),
            IconFont: (FontFamily)FindResource("IconFont"));

        _panelBuilder = new PropertyPanelBuilder(
            _theme,
            (Style)FindResource("ToolbarButton"),
            (renameKey, originalName, displayName) =>
            {
                var dialog = new RenameDialog(originalName, displayName) { Owner = this };
                if (dialog.ShowDialog() == true) ViewModel.RenameVariable(renameKey, dialog.NewName);
            },
            BrowseTextureForSlot,
            ClearTextureForSlot);

        _sidebarBuilder = new PermutationSidebarBuilder(_theme, ViewModel.SelectPermutation);

        _editorService = new MonacoEditorService(MonacoWebView);
        _editorService.EditorReady += () =>
        {
            MonacoWebView.Visibility = Visibility.Visible;
            EditorLoadingOverlay.Visibility = Visibility.Collapsed;
        };

        // Wire event handlers before awaiting — SetContentAsync buffers pre-ready content
        ViewModel.HlslContentChanged += async hlsl =>
        {
            if (_editorService != null)
                await _editorService.SetContentAsync(hlsl);

            UpdatePropertiesPanels();
            ApplyShaderToPreview();
            UpdateBadgeVisibility();
        };

        ViewModel.LanguageChanged += async lang =>
        {
            if (_editorService != null)
                await _editorService.SetLanguageAsync(lang);
        };

        ViewModel.BlsLoaded += UpdatePermutationSidebar;

        // Start editor init and viewport init concurrently
        var editorTask = _editorService.InitializeAsync();

        try
        {
            Viewport.Initialize();
            _viewportInitialized = true;
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"3D Preview unavailable: {ex.Message}";
        }

        await editorTask;

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1])) ViewModel.LoadShader(args[1]);
    }

    private void UpdateBadgeVisibility()
    {
        ShaderBadgeBorder.Visibility = string.IsNullOrEmpty(ViewModel.ShaderInfoBadge)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ApplyShaderToPreview()
    {
        if (!_viewportInitialized || Viewport.Renderer == null) return;

        var shader = ViewModel.CurrentShader;
        if (shader?.RawBytecode != null && shader.RawBytecode.Length > 0 && shader.Type == ShaderType.Pixel)
        {
            Viewport.Renderer.SetUserPixelShader(shader.RawBytecode, shader.InputSignature);
            Viewport.Renderer.SetupTexturesFromReflection(shader.ResourceBindings);

            // Restore persisted texture assignments
            foreach (var (slot, path) in ViewModel.RenameMapping.TextureAssignments)
                try
                {
                    if (File.Exists(path))
                    {
                        var data = TextureLoader.Load(File.ReadAllBytes(path));
                        Viewport.Renderer.SetSlotTexture(slot, data, path);
                    }
                }
                catch
                {
                    // Silently skip broken assignments
                }
        }
        else
        {
            Viewport.Renderer.ClearUserShaders();
        }

        Viewport.RenderFrame();
    }

    // ═══ BLS Permutation Sidebar ═══

    private void UpdatePermutationSidebar()
    {
        if (ViewModel.IsBlsFile && ViewModel.BlsContainer != null)
        {
            SidebarColumn.Width = new GridLength(200);
            PermutationSidebar.Visibility = Visibility.Visible;
            _sidebarBuilder?.Populate(PermutationList, ViewModel.BlsContainer);
        }
        else
        {
            SidebarColumn.Width = new GridLength(0);
            PermutationSidebar.Visibility = Visibility.Collapsed;
            _sidebarBuilder?.Clear(PermutationList);
        }
    }

    // ═══ Recent Files ═══

    private void RecentFiles_Click(object sender, RoutedEventArgs e)
    {
        var recent = ViewModel.RecentFiles;
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("DarkContextMenu"),
            PlacementTarget = RecentFilesButton,
            Placement = PlacementMode.Bottom,
            MinWidth = 300
        };

        var menuItemStyle = (Style)FindResource("DarkMenuItem");

        if (recent.Count == 0)
            menu.Items.Add(new MenuItem
            {
                Header = "No recent files",
                IsEnabled = false,
                Style = menuItemStyle
            });
        else
            foreach (var filePath in recent)
            {
                var fileName = Path.GetFileName(filePath);
                var dirName = Path.GetDirectoryName(filePath) ?? "";
                var parentDir = Path.GetFileName(dirName);

                var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
                headerPanel.Children.Add(new TextBlock
                {
                    Text = fileName,
                    Foreground = _theme!.FgPrimaryBrush,
                    VerticalAlignment = VerticalAlignment.Center
                });
                headerPanel.Children.Add(new TextBlock
                {
                    Text = $"  {parentDir}",
                    Foreground = _theme!.FgTertiaryBrush,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var item = new MenuItem
                {
                    Header = headerPanel,
                    ToolTip = filePath,
                    Style = menuItemStyle,
                    Tag = filePath
                };
                item.Click += (_, _) => ViewModel.LoadShader((string)item.Tag);
                menu.Items.Add(item);
            }

        menu.IsOpen = true;
    }

    // ═══ Properties Panels ═══

    private void UpdatePropertiesPanels()
    {
        var shader = ViewModel.CurrentShader;
        if (shader == null || _panelBuilder == null) return;

        _panelBuilder.UpdateSignaturePanel(SignaturePanel, shader);
        _panelBuilder.UpdateBuffersPanel(BuffersPanel, shader, ViewModel.RenameMapping);
        _panelBuilder.UpdateResourcesPanel(ResourcesPanel, shader);
        _panelBuilder.UpdateTexturesPanel(TexturesPanel, shader, ViewModel.RenameMapping);
    }

    private void BrowseTextureForSlot(int slot)
    {
        if (!_viewportInitialized || Viewport.Renderer == null) return;

        var dialog = new OpenFileDialog
        {
            Filter =
                "Texture Files|*.dds;*.png;*.jpg;*.jpeg;*.bmp|DDS Files|*.dds|Image Files|*.png;*.jpg;*.jpeg;*.bmp|All Files|*.*",
            Title = $"Select Texture for slot t{slot}"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var fileBytes = File.ReadAllBytes(dialog.FileName);
            var data = TextureLoader.Load(fileBytes);
            Viewport.Renderer.SetSlotTexture(slot, data, dialog.FileName);
            ViewModel.SetTextureAssignment(slot, dialog.FileName);
            Viewport.RenderFrame();

            // Refresh the textures panel
            if (ViewModel.CurrentShader != null)
                _panelBuilder?.UpdateTexturesPanel(TexturesPanel, ViewModel.CurrentShader, ViewModel.RenameMapping);
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Failed to load texture: {ex.Message}";
        }
    }

    private void ClearTextureForSlot(int slot)
    {
        if (!_viewportInitialized || Viewport.Renderer == null) return;

        Viewport.Renderer.ClearSlotTexture(slot);
        ViewModel.SetTextureAssignment(slot, null);
        Viewport.RenderFrame();

        // Refresh the textures panel
        if (ViewModel.CurrentShader != null)
            _panelBuilder?.UpdateTexturesPanel(TexturesPanel, ViewModel.CurrentShader, ViewModel.RenameMapping);
    }

    // Chrome handlers
    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // Drag and drop
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
        else
            e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            EditorDropOverlay.Visibility = Visibility.Visible;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        EditorDropOverlay.Visibility = Visibility.Collapsed;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        EditorDropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            if (files.Length > 0)
                ViewModel.LoadShader(files[0]);
        }
    }

    // Editor drop overlay handlers (captures drops over WebView2 HWND)
    private void DropOverlay_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
        else
            e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void DropOverlay_Drop(object sender, DragEventArgs e)
    {
        EditorDropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            if (files.Length > 0)
                ViewModel.LoadShader(files[0]);
        }

        e.Handled = true;
    }

    private void DropOverlay_DragLeave(object sender, DragEventArgs e)
    {
        EditorDropOverlay.Visibility = Visibility.Collapsed;
    }

}