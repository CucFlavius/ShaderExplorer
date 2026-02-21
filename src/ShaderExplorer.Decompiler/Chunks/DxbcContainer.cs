namespace ShaderExplorer.Decompiler.Chunks;

public class DxbcContainer
{
    public byte[] Hash { get; set; } = new byte[16];
    public uint Version { get; set; }
    public uint TotalSize { get; set; }
    public List<DxbcChunk> Chunks { get; set; } = [];
    public RdefChunk? ResourceDefinitions { get; set; }
    public SignatureChunk? InputSignature { get; set; }
    public SignatureChunk? OutputSignature { get; set; }
    public SignatureChunk? PatchConstantSignature { get; set; }
    public ShaderProgramChunk? ShaderProgram { get; set; }
    public StatChunk? Statistics { get; set; }
    public byte[]? DxilChunkData { get; set; }
    public string? DxilChunkType { get; set; }
    public byte[]? SpdbData { get; set; }
}

public class DxbcChunk
{
    public string FourCC { get; set; } = string.Empty;
    public uint Size { get; set; }
    public int DataOffset { get; set; }
}