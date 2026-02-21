namespace ShaderExplorer.Decompiler.Chunks;

public class SignatureChunk
{
    public string ChunkType { get; set; } = string.Empty; // ISGN, OSGN, ISG1, OSG1, PSG1
    public List<SignatureElement> Elements { get; set; } = [];
}

public class SignatureElement
{
    public string SemanticName { get; set; } = string.Empty;
    public uint SemanticIndex { get; set; }
    public uint SystemValueType { get; set; }
    public ComponentType ComponentType { get; set; }
    public uint Register { get; set; }
    public byte Mask { get; set; }
    public byte ReadWriteMask { get; set; }
    public uint Stream { get; set; }
    public uint MinPrecision { get; set; }
}

public enum ComponentType : uint
{
    Unknown = 0,
    UInt32 = 1,
    Int32 = 2,
    Float32 = 3
}