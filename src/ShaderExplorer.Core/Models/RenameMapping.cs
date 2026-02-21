namespace ShaderExplorer.Core.Models;

public class RenameMapping
{
    public Dictionary<string, string> VariableRenames { get; set; } = new();
    public Dictionary<string, string> BufferDefinitions { get; set; } = new();
    public Dictionary<int, string> TextureAssignments { get; set; } = new();
}