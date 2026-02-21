using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ShaderExplorer.App.Helpers;
using ShaderExplorer.App.Services;
using ShaderExplorer.Core.Models;
using ShaderExplorer.Decompiler;
using ShaderExplorer.Decompiler.Chunks;
using ShaderExplorer.Decompiler.Dxil;

namespace ShaderExplorer.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ShaderLoadService _loadService = new();
    private readonly RecentFilesService _recentFilesService = new();

    [ObservableProperty] private BlsContainer? _blsContainer;

    private byte[]? _blsFileBytes;

    private DxbcContainer? _currentContainer;
    private DxilModule? _currentDxilModule;

    [ObservableProperty] private ShaderInfo? _currentShader;

    [ObservableProperty] private string _decompiledHlsl = string.Empty;

    [ObservableProperty] private string _editorLanguage = "hlsl";

    [ObservableProperty] private bool _isBlsFile;

    [ObservableProperty] private BlsPermutation? _selectedPermutation;

    [ObservableProperty] private BlsPlatform? _selectedPlatform;

    [ObservableProperty] private string _shaderFileName = "";

    [ObservableProperty] private string _shaderFormatText = "";

    [ObservableProperty] private string _shaderInfoBadge = "";

    [ObservableProperty] private string _statusText = "Ready";

    [ObservableProperty] private string _windowTitle = "ShaderExplorer";

    public RenameMapping RenameMapping { get; private set; } = new();

    public IReadOnlyList<string> RecentFiles => _recentFilesService.Files;

    public event Action<string>? HlslContentChanged;
    public event Action<string>? LanguageChanged;
    public event Action? BlsLoaded;

    [RelayCommand]
    private void OpenFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Compiled Shaders|*.cso;*.dxbc;*.fxo;*.dxil;*.bls;*.bin;*.mtl;*.mtllib|All Files|*.*",
            Title = "Open Compiled Shader"
        };

        if (dialog.ShowDialog() == true) LoadShader(dialog.FileName);
    }

    public void LoadShader(string filePath)
    {
        try
        {
            StatusText = $"Loading {Path.GetFileName(filePath)}...";
            var bytes = File.ReadAllBytes(filePath);

            // Check header to determine shader format
            var format = ShaderFormatDetector.Detect(bytes);

            if (format != ShaderFormat.Unknown)
                _recentFilesService.Add(filePath);

            if (format == ShaderFormat.Unknown)
            {
                ClearBlsState();
                StatusText = $"Error: Unrecognized shader format in {Path.GetFileName(filePath)}";
                DecompiledHlsl =
                    $"// Unrecognized shader format\n// File: {filePath}\n// First 4 bytes: {(bytes.Length >= 4 ? $"0x{BitConverter.ToUInt32(bytes, 0):X8}" : "too short")}";
                HlslContentChanged?.Invoke(DecompiledHlsl);
                return;
            }

            if (format == ShaderFormat.BLS)
            {
                LoadBlsFile(filePath, bytes);
                return;
            }

            ClearBlsState();

            if (format == ShaderFormat.MetalSource)
            {
                LoadMetalSource(bytes, filePath);
                return;
            }

            if (format == ShaderFormat.MetalLib)
            {
                LoadMetalLib(bytes, filePath);
                return;
            }

            if (format == ShaderFormat.DXIL)
            {
                LoadDxilBytes(bytes, filePath);
                return;
            }

            LoadDxbcBytes(bytes, filePath);
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            DecompiledHlsl = $"// Error loading shader:\n// {ex.Message}\n// {ex.StackTrace}";
            HlslContentChanged?.Invoke(DecompiledHlsl);
        }
    }

    private void LoadBlsFile(string filePath, byte[] bytes)
    {
        var blsParser = new BlsParser();
        BlsContainer = blsParser.Parse(bytes);
        BlsContainer.FilePath = filePath;
        IsBlsFile = true;
        _blsFileBytes = bytes;

        ShaderFileName = Path.GetFileName(filePath);
        WindowTitle = $"ShaderExplorer - {ShaderFileName}";
        ShaderFormatText = "BLS";

        // Probe Metal platform permutation content types (source vs compiled)
        foreach (var platform in BlsContainer.Platforms)
            if (platform.BytecodeFormat is BlsBytecodeFormat.MetalAIR or BlsBytecodeFormat.MetalSource
                || platform.Tag.StartsWith("MT", StringComparison.OrdinalIgnoreCase))
                blsParser.ProbePermutationFormats(bytes, platform);

        // Count total perms and non-empty
        var totalPerms = BlsContainer.Platforms.Sum(p => p.Permutations.Count);
        var nonEmpty = BlsContainer.Platforms.Sum(p => p.Permutations.Count(pp => !pp.IsEmpty));
        var platformList = string.Join(", ", BlsContainer.Platforms.Select(p => p.Tag));

        StatusText =
            $"Loaded {ShaderFileName} - {BlsContainer.Platforms.Count} platform(s) [{platformList}], {nonEmpty}/{totalPerms} permutations";
        ShaderInfoBadge = $"BLS {platformList}";

        BlsLoaded?.Invoke();

        // Auto-select first non-empty DXBC permutation on a DX platform
        var dxPlatform = BlsContainer.Platforms
                             .FirstOrDefault(p => p.Tag.StartsWith("DX", StringComparison.OrdinalIgnoreCase))
                         ?? BlsContainer.Platforms.FirstOrDefault();
        if (dxPlatform != null)
        {
            var firstPerm = dxPlatform.Permutations.FirstOrDefault(p => !p.IsEmpty);
            if (firstPerm != null)
                SelectPermutation(dxPlatform, firstPerm);
        }
    }

    public void SelectPermutation(BlsPlatform platform, BlsPermutation perm)
    {
        if (perm.IsEmpty)
        {
            StatusText = $"Permutation {perm.Index} is empty";
            DecompiledHlsl = $"// Empty permutation (index {perm.Index})";
            HlslContentChanged?.Invoke(DecompiledHlsl);
            return;
        }

        try
        {
            SelectedPlatform = platform;
            SelectedPermutation = perm;

            // Extract bytecode if not cached
            if (perm.Bytecode == null && _blsFileBytes != null)
            {
                var blsParser = new BlsParser();
                blsParser.ExtractPermutationBytecode(_blsFileBytes, platform, perm);
            }

            if (perm.Bytecode == null || perm.Bytecode.Length == 0)
            {
                ShowPermutationInfoOnly(platform, perm);
                return;
            }

            // Detect inner format — check permutation info bytecodeFormat first (for Metal),
            // then fall back to magic-based detection
            var bcFormat = perm.Info?.BytecodeFormat ?? BlsBytecodeFormat.Unknown;

            if (bcFormat == BlsBytecodeFormat.MetalSource)
            {
                LoadMetalSource(perm.Bytecode, BlsContainer?.FilePath ?? "");
                var totalPerms = platform.Permutations.Count;
                var sizeStr = FormatHelper.FormatSize(perm.Info?.BytecodeSize ?? perm.Bytecode.Length);
                ShaderInfoBadge = $"{CurrentShader?.Type} Metal  [Perm {perm.Index}/{totalPerms}]";
                StatusText = $"BLS - {platform.Tag} - Perm {perm.Index}/{totalPerms} ({sizeStr} Metal Source)";
                ShaderFormatText = $"BLS/{platform.Tag}";
            }
            else if (bcFormat == BlsBytecodeFormat.MetalAIR)
            {
                LoadMetalLib(perm.Bytecode, BlsContainer?.FilePath ?? "");
                var totalPerms = platform.Permutations.Count;
                var sizeStr = FormatHelper.FormatSize(perm.Info?.BytecodeSize ?? perm.Bytecode.Length);
                ShaderInfoBadge = $"MetalLib  [Perm {perm.Index}/{totalPerms}]";
                StatusText = $"BLS - {platform.Tag} - Perm {perm.Index}/{totalPerms} ({sizeStr} MetalLib)";
                ShaderFormatText = $"BLS/{platform.Tag}";
            }
            else
            {
                var innerFormat = ShaderFormatDetector.Detect(perm.Bytecode);
                if (innerFormat == ShaderFormat.DXBC)
                {
                    LoadDxbcBytes(perm.Bytecode, BlsContainer?.FilePath ?? "");

                    // Update badge/status with permutation context
                    var totalPerms = platform.Permutations.Count;
                    ShaderInfoBadge =
                        $"{CurrentShader?.Type} SM{CurrentShader?.MajorVersion}.{CurrentShader?.MinorVersion}  [Perm {perm.Index}/{totalPerms}]";
                    var sizeStr = FormatHelper.FormatSize(perm.Info?.BytecodeSize ?? perm.Bytecode.Length);
                    StatusText = $"BLS - {platform.Tag} - Perm {perm.Index}/{totalPerms} ({sizeStr} DXBC)";
                    ShaderFormatText = $"BLS/{platform.Tag}";
                }
                else if (innerFormat == ShaderFormat.DXIL)
                {
                    LoadDxilBytes(perm.Bytecode, BlsContainer?.FilePath ?? "");

                    var totalPerms = platform.Permutations.Count;
                    var sizeStr = FormatHelper.FormatSize(perm.Info?.BytecodeSize ?? perm.Bytecode.Length);
                    ShaderInfoBadge =
                        $"{CurrentShader?.Type} SM{CurrentShader?.MajorVersion}.{CurrentShader?.MinorVersion}  [Perm {perm.Index}/{totalPerms}]";
                    StatusText = $"BLS - {platform.Tag} - Perm {perm.Index}/{totalPerms} ({sizeStr} DXIL)";
                    ShaderFormatText = $"BLS/{platform.Tag}";
                }
                else
                {
                    ShowPermutationInfoOnly(platform, perm);
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Error extracting permutation: {ex.Message}";
            DecompiledHlsl = $"// Error extracting permutation {perm.Index}:\n// {ex.Message}";
            HlslContentChanged?.Invoke(DecompiledHlsl);
        }
    }

    private void ShowPermutationInfoOnly(BlsPlatform platform, BlsPermutation perm)
    {
        var totalPerms = platform.Permutations.Count;
        var format = perm.Info?.BytecodeFormat ?? BlsBytecodeFormat.Unknown;
        var sizeStr = FormatHelper.FormatSize(perm.DecompressedSize);

        DecompiledHlsl = $"// Permutation {perm.Index} ({platform.Tag})\n" +
                         $"// Bytecode format: {format}\n" +
                         $"// Blob size: {sizeStr}\n" +
                         $"// Decompilation not available for this format.";
        ShaderInfoBadge = $"{platform.Tag}  [Perm {perm.Index}/{totalPerms}]";
        StatusText = $"BLS - {platform.Tag} - Perm {perm.Index}/{totalPerms} ({sizeStr} {format})";
        ShaderFormatText = $"BLS/{platform.Tag}";
        CurrentShader = null;
        _currentContainer = null;
        HlslContentChanged?.Invoke(DecompiledHlsl);
    }

    private void LoadDxbcBytes(byte[] bytecode, string filePath)
    {
        RenameMapping = SidecarService.Load(filePath);
        var result = _loadService.LoadDxbc(bytecode, filePath, RenameMapping);
        ApplyLoadResult(result, filePath, "DXBC");
    }

    private void LoadDxilBytes(byte[] bytecode, string filePath)
    {
        RenameMapping = SidecarService.Load(filePath);
        var result = _loadService.LoadDxil(bytecode, filePath, RenameMapping);
        ApplyLoadResult(result, filePath, "DXIL");
    }

    private void LoadMetalSource(byte[] data, string filePath)
    {
        RenameMapping = new RenameMapping();
        var result = _loadService.LoadMetalSource(data, filePath);
        ApplyLoadResult(result, filePath, "Metal");
    }

    private void LoadMetalLib(byte[] data, string filePath)
    {
        RenameMapping = new RenameMapping();
        var result = _loadService.LoadMetalLib(data, filePath);
        ApplyLoadResult(result, filePath, "MetalLib");
    }

    private void ApplyLoadResult(ShaderLoadResult result, string filePath, string formatName)
    {
        SetEditorLanguage(result.EditorLanguage);

        CurrentShader = result.Shader;
        _currentContainer = result.Container;
        _currentDxilModule = result.DxilModule;
        DecompiledHlsl = result.Hlsl;

        if (!IsBlsFile)
        {
            var shader = result.Shader;
            ShaderFileName = Path.GetFileName(filePath);
            WindowTitle = $"ShaderExplorer - {ShaderFileName}";
            ShaderFormatText = formatName;

            if (formatName == "Metal")
            {
                ShaderInfoBadge = $"{shader.Type} Metal";
                StatusText = $"Loaded {ShaderFileName} ({shader.Type} Metal Shader)";
            }
            else if (formatName == "MetalLib")
            {
                ShaderInfoBadge = "MetalLib";
                StatusText = $"Loaded {ShaderFileName} (Metal Library, {FormatHelper.FormatSize(shader.RawBytecode?.Length ?? 0)})";
            }
            else if (result.ErrorMessage != null)
            {
                ShaderInfoBadge = $"{shader.Type} SM{shader.MajorVersion}.{shader.MinorVersion} {formatName}";
                StatusText = $"{result.ErrorMessage} for {ShaderFileName}";
            }
            else
            {
                ShaderInfoBadge = $"{shader.Type} SM{shader.MajorVersion}.{shader.MinorVersion}";
                StatusText =
                    $"Loaded {ShaderFileName} ({shader.Type} Shader SM{shader.MajorVersion}.{shader.MinorVersion}{(formatName == "DXIL" ? " DXIL" : "")})";
            }
        }

        HlslContentChanged?.Invoke(DecompiledHlsl);
    }

    private void SetEditorLanguage(string language)
    {
        if (EditorLanguage != language)
        {
            EditorLanguage = language;
            LanguageChanged?.Invoke(language);
        }
    }

    private void ClearBlsState()
    {
        IsBlsFile = false;
        BlsContainer = null;
        SelectedPlatform = null;
        SelectedPermutation = null;
        _blsFileBytes = null;
        BlsLoaded?.Invoke();
    }

    public void RenameVariable(string key, string newName)
    {
        if (string.IsNullOrEmpty(key)) return;

        if (newName == key)
            RenameMapping.VariableRenames.Remove(key);
        else
            RenameMapping.VariableRenames[key] = newName;

        SidecarService.Save(CurrentShader?.FilePath ?? "", RenameMapping);
        RegenerateHlsl();
    }

    public void SetTextureAssignment(int slot, string? path)
    {
        if (path == null)
            RenameMapping.TextureAssignments.Remove(slot);
        else
            RenameMapping.TextureAssignments[slot] = path;
        SidecarService.Save(CurrentShader?.FilePath ?? "", RenameMapping);
    }

    public void SetBufferDefinition(string bufferName, string hlslDefinition)
    {
        if (string.IsNullOrEmpty(hlslDefinition))
            RenameMapping.BufferDefinitions.Remove(bufferName);
        else
            RenameMapping.BufferDefinitions[bufferName] = hlslDefinition;

        SidecarService.Save(CurrentShader?.FilePath ?? "", RenameMapping);
        RegenerateHlsl();
    }

    public void RegenerateHlsl()
    {
        if (CurrentShader == null) return;

        DecompiledHlsl = _loadService.RegenerateHlsl(
            CurrentShader, _currentContainer, _currentDxilModule, RenameMapping);

        HlslContentChanged?.Invoke(DecompiledHlsl);
    }
}
