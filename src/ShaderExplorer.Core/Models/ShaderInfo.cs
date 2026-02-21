namespace ShaderExplorer.Core.Models;

public enum ShaderType
{
    Pixel,
    Vertex,
    Geometry,
    Hull,
    Domain,
    Compute,
    Unknown
}

public class ShaderInfo
{
    public ShaderType Type { get; set; }
    public int MajorVersion { get; set; }
    public int MinorVersion { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string DecompiledHlsl { get; set; } = string.Empty;
    public string? OriginalHlsl { get; set; }
    public string? OriginalFilePath { get; set; }
    public bool IsDxil { get; set; }
    public bool IsMetal { get; set; }
    public byte[] RawBytecode { get; set; } = [];
    public List<SignatureElement> InputSignature { get; set; } = [];
    public List<SignatureElement> OutputSignature { get; set; } = [];
    public List<ConstantBufferInfo> ConstantBuffers { get; set; } = [];
    public List<ResourceBindingInfo> ResourceBindings { get; set; } = [];
}

public class SignatureElement
{
    public string SemanticName { get; set; } = string.Empty;
    public int SemanticIndex { get; set; }
    public int Register { get; set; }
    public ComponentType ComponentType { get; set; }
    public byte Mask { get; set; }
    public byte ReadWriteMask { get; set; }
    public SystemValueType SystemValue { get; set; }
    public int Stream { get; set; }
}

public enum ComponentType
{
    Unknown = 0,
    UInt32 = 1,
    Int32 = 2,
    Float32 = 3
}

public enum SystemValueType
{
    Undefined = 0,
    Position = 1,
    ClipDistance = 2,
    CullDistance = 3,
    RenderTargetArrayIndex = 4,
    ViewportArrayIndex = 5,
    VertexID = 6,
    PrimitiveID = 7,
    InstanceID = 8,
    IsFrontFace = 9,
    SampleIndex = 10,
    FinalQuadEdgeTessFactor = 11,
    FinalQuadInsideTessFactor = 12,
    FinalTriEdgeTessFactor = 13,
    FinalTriInsideTessFactor = 14,
    FinalLineDetailTessFactor = 15,
    FinalLineDensityTessFactor = 16,
    Target = 64,
    Depth = 65,
    Coverage = 66,
    DepthGreaterEqual = 67,
    DepthLessEqual = 68,
    StencilRef = 69
}

public class ConstantBufferInfo
{
    public string Name { get; set; } = string.Empty;
    public int RegisterSlot { get; set; }
    public int Size { get; set; }
    public List<ConstantVariableInfo> Variables { get; set; } = [];
}

public class ConstantVariableInfo
{
    public string Name { get; set; } = string.Empty;
    public int Offset { get; set; }
    public int Size { get; set; }
    public ShaderVariableType VariableType { get; set; } = new();
}

public class ShaderVariableType
{
    public ShaderVariableClass Class { get; set; }
    public ShaderBaseType Type { get; set; }
    public int Rows { get; set; }
    public int Columns { get; set; }
    public int Elements { get; set; }
    public List<StructMemberInfo> Members { get; set; } = [];
}

public class StructMemberInfo
{
    public string Name { get; set; } = string.Empty;
    public int Offset { get; set; }
    public ShaderVariableType Type { get; set; } = new();
}

public enum ShaderVariableClass
{
    Scalar,
    Vector,
    MatrixRows,
    MatrixColumns,
    Object,
    Struct,
    InterfaceClass,
    InterfacePointer
}

public enum ShaderBaseType
{
    Void,
    Bool,
    Int,
    Float,
    String,
    Texture,
    Texture1D,
    Texture2D,
    Texture3D,
    TextureCube,
    Sampler,
    Sampler1D,
    Sampler2D,
    Sampler3D,
    SamplerCube,
    PixelShader,
    VertexShader,
    PixelFragment,
    VertexFragment,
    UInt,
    UInt8,
    GeometryShader,
    Rasterizer,
    DepthStencil,
    Blend,
    Buffer,
    CBuffer,
    TBuffer,
    Texture1DArray,
    Texture2DArray,
    RenderTargetView,
    DepthStencilView,
    Texture2DMultisampled,
    Texture2DMultisampledArray,
    TextureCubeArray,
    HullShader,
    DomainShader,
    InterfacePointer,
    ComputeShader,
    Double,
    RWTexture1D,
    RWTexture1DArray,
    RWTexture2D,
    RWTexture2DArray,
    RWTexture3D,
    RWBuffer,
    ByteAddressBuffer,
    RWByteAddressBuffer,
    StructuredBuffer,
    RWStructuredBuffer,
    AppendStructuredBuffer,
    ConsumeStructuredBuffer,
    Min8Float,
    Min10Float,
    Min16Float,
    Min12Int,
    Min16Int,
    Min16UInt
}

public class ResourceBindingInfo
{
    public string Name { get; set; } = string.Empty;
    public ResourceType Type { get; set; }
    public ResourceDimension Dimension { get; set; }
    public int BindPoint { get; set; }
    public int BindCount { get; set; }
    public ResourceReturnType ReturnType { get; set; }
}

public enum ResourceType
{
    CBuffer,
    TBuffer,
    Texture,
    Sampler,
    UAVRWTyped,
    Structured,
    UAVRWStructured,
    ByteAddress,
    UAVRWByteAddress,
    UAVAppendStructured,
    UAVConsumeStructured,
    UAVRWStructuredWithCounter
}

public enum ResourceDimension
{
    Unknown,
    Buffer,
    Texture1D,
    Texture1DArray,
    Texture2D,
    Texture2DArray,
    Texture2DMultisampled,
    Texture2DMultisampledArray,
    Texture3D,
    TextureCube,
    TextureCubeArray,
    RawBuffer,
    StructuredBuffer
}

public enum ResourceReturnType
{
    None,
    UNorm,
    SNorm,
    SInt,
    UInt,
    Float,
    Mixed,
    Double,
    Continued
}