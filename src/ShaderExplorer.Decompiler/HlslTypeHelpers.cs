using ShaderExplorer.Core.Models;

namespace ShaderExplorer.Decompiler;

public static class HlslTypeHelpers
{
    public static string HlslTypeName(ShaderVariableType type)
    {
        if (type.Class == ShaderVariableClass.Struct)
            return "struct";

        var baseType = type.Type switch
        {
            ShaderBaseType.Float => "float",
            ShaderBaseType.Int => "int",
            ShaderBaseType.UInt => "uint",
            ShaderBaseType.Bool => "bool",
            ShaderBaseType.Double => "double",
            ShaderBaseType.Min16Float => "min16float",
            ShaderBaseType.Min16Int => "min16int",
            ShaderBaseType.Min16UInt => "min16uint",
            _ => "float"
        };

        if (type.Class is ShaderVariableClass.MatrixRows or ShaderVariableClass.MatrixColumns)
            return $"{baseType}{type.Rows}x{type.Columns}";
        if (type.Class == ShaderVariableClass.Vector)
            return type.Columns > 1 ? $"{baseType}{type.Columns}" : baseType;
        return baseType;
    }

    public static string ComponentTypeToHlsl(ComponentType ct, byte mask)
    {
        var count = 0;
        for (var i = 0; i < 4; i++)
            if ((mask & (1 << i)) != 0)
                count++;

        var baseType = ct switch
        {
            ComponentType.Float32 => "float",
            ComponentType.Int32 => "int",
            ComponentType.UInt32 => "uint",
            _ => "float"
        };

        return count > 1 ? $"{baseType}{count}" : baseType;
    }

    public static string TextureDimensionType(ResourceDimension dim)
    {
        return dim switch
        {
            ResourceDimension.Texture1D => "Texture1D",
            ResourceDimension.Texture1DArray => "Texture1DArray",
            ResourceDimension.Texture2D => "Texture2D",
            ResourceDimension.Texture2DArray => "Texture2DArray",
            ResourceDimension.Texture2DMultisampled => "Texture2DMS<float4>",
            ResourceDimension.Texture2DMultisampledArray => "Texture2DMSArray<float4>",
            ResourceDimension.Texture3D => "Texture3D",
            ResourceDimension.TextureCube => "TextureCube",
            ResourceDimension.TextureCubeArray => "TextureCubeArray",
            ResourceDimension.Buffer => "Buffer<float4>",
            _ => "Texture2D"
        };
    }
}
