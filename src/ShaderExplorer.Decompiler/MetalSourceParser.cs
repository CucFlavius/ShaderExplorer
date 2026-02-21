using ShaderExplorer.Core.Models;

namespace ShaderExplorer.Decompiler;

/// <summary>
///     Parses Metal Shading Language source code to extract shader metadata:
///     entry point type, resource bindings (buffers, textures, samplers),
///     and input/output signatures from struct member attributes.
/// </summary>
public static partial class MetalSourceParser
{
    // Entry point patterns: "fragment Outputs _main(...)", "vertex Outputs _main(...)", "kernel void _main(...)"
    [GeneratedRegex(@"\b(fragment|vertex|kernel)\s+\w+\s+_main\s*\(", RegexOptions.Compiled)]
    private static partial Regex EntryPointRegex();

    // Buffer bindings: "constant TypeName& name [[buffer(N)]]"
    [GeneratedRegex(@"constant\s+(\w[\w:]*)\s*&\s*(\w+)\s*\[\[buffer\((\d+)\)\]\]", RegexOptions.Compiled)]
    private static partial Regex BufferBindingRegex();

    // Texture bindings: "texture2d<float> name [[texture(N)]]"
    [GeneratedRegex(@"(texture\w*<[^>]+>)\s+(\w+)\s*\[\[texture\((\d+)\)\]\]", RegexOptions.Compiled)]
    private static partial Regex TextureBindingRegex();

    // Sampler bindings: "sampler name [[sampler(N)]]"
    [GeneratedRegex(@"sampler\s+(\w+)\s*\[\[sampler\((\d+)\)\]\]", RegexOptions.Compiled)]
    private static partial Regex SamplerBindingRegex();

    // Input struct members: "type name [[user(SEMANTICN)]]" or "[[stage_in]]" etc.
    [GeneratedRegex(@"(\w[\w:]*(?:\d+)?)\s+(\w+)\s*\[\[user\((\w+)\)\]\]", RegexOptions.Compiled)]
    private static partial Regex UserAttributeRegex();

    // Output color: "type name [[color(N)]]"
    [GeneratedRegex(@"(\w[\w:]*(?:\d+)?)\s+(\w+)\s*\[\[color\((\d+)\)\]\]", RegexOptions.Compiled)]
    private static partial Regex ColorAttributeRegex();

    // Output depth: "float name [[depth(any)]]"
    [GeneratedRegex(@"(\w+)\s+(\w+)\s*\[\[depth\(\w+\)\]\]", RegexOptions.Compiled)]
    private static partial Regex DepthAttributeRegex();

    // Output sample_mask: "uint name [[sample_mask]]"
    [GeneratedRegex(@"(\w+)\s+(\w+)\s*\[\[sample_mask\]\]", RegexOptions.Compiled)]
    private static partial Regex SampleMaskRegex();

    // Position output: "float4 name [[position]]"
    [GeneratedRegex(@"(\w+)\s+(\w+)\s*\[\[position\]\]", RegexOptions.Compiled)]
    private static partial Regex PositionAttributeRegex();

    public static ShaderInfo Parse(string source)
    {
        var shader = new ShaderInfo();

        // Detect shader type from entry point
        var entryMatch = EntryPointRegex().Match(source);
        if (entryMatch.Success)
            shader.Type = entryMatch.Groups[1].Value switch
            {
                "fragment" => ShaderType.Pixel,
                "vertex" => ShaderType.Vertex,
                "kernel" => ShaderType.Compute,
                _ => ShaderType.Unknown
            };

        // Extract buffer bindings → ConstantBuffers + ResourceBindings
        foreach (Match m in BufferBindingRegex().Matches(source))
        {
            var typeName = m.Groups[1].Value;
            var varName = m.Groups[2].Value;
            var slot = int.Parse(m.Groups[3].Value);

            shader.ConstantBuffers.Add(new ConstantBufferInfo
            {
                Name = varName,
                RegisterSlot = slot
            });

            shader.ResourceBindings.Add(new ResourceBindingInfo
            {
                Name = varName,
                Type = ResourceType.CBuffer,
                BindPoint = slot,
                BindCount = 1
            });
        }

        // Extract texture bindings
        foreach (Match m in TextureBindingRegex().Matches(source))
        {
            var texType = m.Groups[1].Value;
            var varName = m.Groups[2].Value;
            var slot = int.Parse(m.Groups[3].Value);

            var dim = texType switch
            {
                _ when texType.StartsWith("texture1d") => ResourceDimension.Texture1D,
                _ when texType.StartsWith("texture2d_array") => ResourceDimension.Texture2DArray,
                _ when texType.StartsWith("texture2d_ms") => ResourceDimension.Texture2DMultisampled,
                _ when texType.StartsWith("texture2d") => ResourceDimension.Texture2D,
                _ when texType.StartsWith("texture3d") => ResourceDimension.Texture3D,
                _ when texType.StartsWith("texturecube") => ResourceDimension.TextureCube,
                _ => ResourceDimension.Texture2D
            };

            shader.ResourceBindings.Add(new ResourceBindingInfo
            {
                Name = varName,
                Type = ResourceType.Texture,
                Dimension = dim,
                BindPoint = slot,
                BindCount = 1
            });
        }

        // Extract sampler bindings
        foreach (Match m in SamplerBindingRegex().Matches(source))
        {
            var varName = m.Groups[1].Value;
            var slot = int.Parse(m.Groups[2].Value);

            shader.ResourceBindings.Add(new ResourceBindingInfo
            {
                Name = varName,
                Type = ResourceType.Sampler,
                BindPoint = slot,
                BindCount = 1
            });
        }

        // Extract input signature from [[user(...)]] attributes
        var reg = 0;
        foreach (Match m in UserAttributeRegex().Matches(source))
        {
            var semantic = m.Groups[3].Value;
            // Split semantic name and index (e.g., "TEXCOORD1" → "TEXCOORD", 1)
            var (semName, semIndex) = SplitSemantic(semantic);

            shader.InputSignature.Add(new SignatureElement
            {
                SemanticName = semName,
                SemanticIndex = semIndex,
                Register = reg++,
                ComponentType = ComponentType.Float32,
                Mask = GuessComponentMask(m.Groups[1].Value)
            });
        }

        // Extract output signature from [[color(N)]], [[depth(...)]], [[position]], [[sample_mask]]
        foreach (Match m in ColorAttributeRegex().Matches(source))
        {
            var slot = int.Parse(m.Groups[3].Value);
            shader.OutputSignature.Add(new SignatureElement
            {
                SemanticName = "SV_Target",
                SemanticIndex = slot,
                Register = slot,
                ComponentType = ComponentType.Float32,
                Mask = GuessComponentMask(m.Groups[1].Value)
            });
        }

        foreach (Match m in DepthAttributeRegex().Matches(source))
            shader.OutputSignature.Add(new SignatureElement
            {
                SemanticName = "SV_Depth",
                Register = 0,
                ComponentType = ComponentType.Float32,
                Mask = 0x1,
                SystemValue = SystemValueType.Depth
            });

        foreach (Match m in SampleMaskRegex().Matches(source))
            shader.OutputSignature.Add(new SignatureElement
            {
                SemanticName = "SV_Coverage",
                Register = 0,
                ComponentType = ComponentType.UInt32,
                Mask = 0x1,
                SystemValue = SystemValueType.Coverage
            });

        foreach (Match m in PositionAttributeRegex().Matches(source))
            shader.OutputSignature.Add(new SignatureElement
            {
                SemanticName = "SV_Position",
                Register = 0,
                ComponentType = ComponentType.Float32,
                Mask = 0xF,
                SystemValue = SystemValueType.Position
            });

        return shader;
    }

    private static (string name, int index) SplitSemantic(string semantic)
    {
        var i = semantic.Length - 1;
        while (i >= 0 && char.IsDigit(semantic[i]))
            i--;

        if (i < semantic.Length - 1 && i >= 0)
        {
            var name = semantic[..(i + 1)];
            var index = int.Parse(semantic[(i + 1)..]);
            return (name, index);
        }

        return (semantic, 0);
    }

    private static byte GuessComponentMask(string metalType)
    {
        // float4/half4/int4 → xyzw, float3 → xyz, float2 → xy, float → x
        if (metalType.EndsWith("4") || (metalType == "uint" && false)) return 0xF;
        if (metalType.EndsWith("3")) return 0x7;
        if (metalType.EndsWith("2")) return 0x3;

        // Types like float4, half4 etc
        return metalType switch
        {
            _ when metalType.Contains("4") => 0xF,
            _ when metalType.Contains("3") => 0x7,
            _ when metalType.Contains("2") => 0x3,
            _ => 0xF // default to xyzw for unknown
        };
    }
}