namespace ShaderExplorer.Decompiler.Chunks;

public class RdefChunk
{
    public List<RdefConstantBuffer> ConstantBuffers { get; set; } = [];
    public List<RdefResourceBinding> ResourceBindings { get; set; } = [];
    public uint TargetVersion { get; set; }
    public uint ProgramType { get; set; }
    public uint Flags { get; set; }
    public string Creator { get; set; } = string.Empty;
}

public class RdefConstantBuffer
{
    public string Name { get; set; } = string.Empty;
    public List<RdefVariable> Variables { get; set; } = [];
    public uint Size { get; set; }
    public uint Flags { get; set; }
    public uint Type { get; set; }
}

public class RdefVariable
{
    public string Name { get; set; } = string.Empty;
    public uint StartOffset { get; set; }
    public uint Size { get; set; }
    public uint Flags { get; set; }
    public RdefType Type { get; set; } = new();
    public byte[]? DefaultValue { get; set; }
    public uint StartTexture { get; set; }
    public uint TextureSize { get; set; }
    public uint StartSampler { get; set; }
    public uint SamplerSize { get; set; }
}

public class RdefType
{
    public RdefVariableClass Class { get; set; }
    public RdefVariableType Type { get; set; }
    public ushort Rows { get; set; }
    public ushort Columns { get; set; }
    public ushort Elements { get; set; }
    public ushort MemberCount { get; set; }
    public List<RdefStructMember> Members { get; set; } = [];
    public string? Name { get; set; }
}

public class RdefStructMember
{
    public string Name { get; set; } = string.Empty;
    public uint Offset { get; set; }
    public RdefType Type { get; set; } = new();
}

public enum RdefVariableClass : ushort
{
    Scalar = 0,
    Vector = 1,
    MatrixRows = 2,
    MatrixColumns = 3,
    Object = 4,
    Struct = 5,
    InterfaceClass = 6,
    InterfacePointer = 7
}

public enum RdefVariableType : ushort
{
    Void = 0,
    Bool = 1,
    Int = 2,
    Float = 3,
    String = 4,
    Texture = 5,
    Texture1D = 6,
    Texture2D = 7,
    Texture3D = 8,
    TextureCube = 9,
    Sampler = 10,
    Sampler1D = 11,
    Sampler2D = 12,
    Sampler3D = 13,
    SamplerCube = 14,
    PixelShader = 15,
    VertexShader = 16,
    PixelFragment = 17,
    VertexFragment = 18,
    UInt = 19,
    UInt8 = 20,
    GeometryShader = 21,
    Rasterizer = 22,
    DepthStencil = 23,
    Blend = 24,
    Buffer = 25,
    CBuffer = 26,
    TBuffer = 27,
    Texture1DArray = 28,
    Texture2DArray = 29,
    RenderTargetView = 30,
    DepthStencilView = 31,
    Texture2DMS = 32,
    Texture2DMSArray = 33,
    TextureCubeArray = 34,
    HullShader = 35,
    DomainShader = 36,
    InterfacePointer = 37,
    ComputeShader = 38,
    Double = 39,
    RWTexture1D = 40,
    RWTexture1DArray = 41,
    RWTexture2D = 42,
    RWTexture2DArray = 43,
    RWTexture3D = 44,
    RWBuffer = 45,
    ByteAddressBuffer = 46,
    RWByteAddressBuffer = 47,
    StructuredBuffer = 48,
    RWStructuredBuffer = 49,
    AppendStructuredBuffer = 50,
    ConsumeStructuredBuffer = 51,
    Min8Float = 52,
    Min10Float = 53,
    Min16Float = 54,
    Min12Int = 55,
    Min16Int = 56,
    Min16UInt = 57
}

public class RdefResourceBinding
{
    public string Name { get; set; } = string.Empty;
    public RdefShaderInputType Type { get; set; }
    public RdefResourceReturnType ReturnType { get; set; }
    public RdefResourceDimension Dimension { get; set; }
    public uint NumSamples { get; set; }
    public uint BindPoint { get; set; }
    public uint BindCount { get; set; }
    public uint Flags { get; set; }
}

public enum RdefShaderInputType : uint
{
    CBuffer = 0,
    TBuffer = 1,
    Texture = 2,
    Sampler = 3,
    UAVRWTyped = 4,
    Structured = 5,
    UAVRWStructured = 6,
    ByteAddress = 7,
    UAVRWByteAddress = 8,
    UAVAppendStructured = 9,
    UAVConsumeStructured = 10,
    UAVRWStructuredWithCounter = 11
}

public enum RdefResourceReturnType : uint
{
    None = 0,
    UNorm = 1,
    SNorm = 2,
    SInt = 3,
    UInt = 4,
    Float = 5,
    Mixed = 6,
    Double = 7,
    Continued = 8
}

public enum RdefResourceDimension : uint
{
    Unknown = 0,
    Buffer = 1,
    Texture1D = 2,
    Texture1DArray = 3,
    Texture2D = 4,
    Texture2DArray = 5,
    Texture2DMS = 6,
    Texture2DMSArray = 7,
    Texture3D = 8,
    TextureCube = 9,
    TextureCubeArray = 10,
    RawBuffer = 11,
    StructuredBuffer = 12
}