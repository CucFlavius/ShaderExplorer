using ShaderExplorer.Core.Models;

namespace ShaderExplorer.App.Helpers;

public static class FormatHelper
{
    public static string FormatSize(int bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    public static string FormatMask(byte mask)
    {
        var s = "";
        if ((mask & 1) != 0) s += "x";
        if ((mask & 2) != 0) s += "y";
        if ((mask & 4) != 0) s += "z";
        if ((mask & 8) != 0) s += "w";
        return s;
    }

    public static string FormatVariableType(ShaderVariableType type)
    {
        var baseType = type.Type switch
        {
            ShaderBaseType.Float => "float",
            ShaderBaseType.Int => "int",
            ShaderBaseType.UInt => "uint",
            ShaderBaseType.Bool => "bool",
            ShaderBaseType.Double => "double",
            _ => "float"
        };

        if (type.Class is ShaderVariableClass.MatrixRows or ShaderVariableClass.MatrixColumns)
            return $"{baseType}{type.Rows}x{type.Columns}";

        if (type.Class == ShaderVariableClass.Vector && type.Columns > 1)
            return $"{baseType}{type.Columns}";

        if (type.Class == ShaderVariableClass.Struct)
            return "struct";

        return baseType;
    }
}
