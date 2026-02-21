using ShaderExplorer.Core.Models;
using ShaderExplorer.Decompiler.Chunks;
using ShaderExplorer.Decompiler.Dxil;

namespace ShaderExplorer.App.Services;

public record ShaderLoadResult(
    ShaderInfo Shader,
    DxbcContainer? Container,
    DxilModule? DxilModule,
    string Hlsl,
    string EditorLanguage = "hlsl",
    string? ErrorMessage = null);
