using System.IO;
using System.Text.Json;
using ShaderExplorer.Core.Models;

namespace ShaderExplorer.App.Services;

public static class SidecarService
{
    public static string GetSidecarPath(string shaderPath)
    {
        return shaderPath + ".shaderexplorer.json";
    }

    public static RenameMapping Load(string shaderPath)
    {
        var sidecarPath = GetSidecarPath(shaderPath);
        if (!File.Exists(sidecarPath)) return new RenameMapping();

        try
        {
            var json = File.ReadAllText(sidecarPath);
            return JsonSerializer.Deserialize<RenameMapping>(json) ?? new RenameMapping();
        }
        catch
        {
            return new RenameMapping();
        }
    }

    public static void Save(string shaderPath, RenameMapping mapping)
    {
        // Only save if there's actually something to persist
        if (mapping.VariableRenames.Count == 0 && mapping.BufferDefinitions.Count == 0
                                                 && mapping.TextureAssignments.Count == 0)
        {
            var path = GetSidecarPath(shaderPath);
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(mapping, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetSidecarPath(shaderPath), json);
        }
        catch
        {
            // Silently ignore save failures
        }
    }
}
