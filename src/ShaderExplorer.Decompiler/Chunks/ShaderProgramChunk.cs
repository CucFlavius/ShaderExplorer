using ShaderExplorer.Core.Models;

namespace ShaderExplorer.Decompiler.Chunks;

public class ShaderProgramChunk
{
    public ShaderType ShaderType { get; set; }
    public int MajorVersion { get; set; }
    public int MinorVersion { get; set; }
    public uint Length { get; set; } // in DWORDs
    public List<Instruction> Instructions { get; set; } = [];
}

public class Instruction
{
    public OpcodeType Opcode { get; set; }
    public uint Length { get; set; }
    public bool IsExtended { get; set; }
    public bool IsSaturated { get; set; }
    public uint ControlBits { get; set; }
    public List<ExtendedToken> ExtendedTokens { get; set; } = [];
    public List<Operand> Operands { get; set; } = [];

    // For resource dimension declarations
    public ResourceDimToken? ResourceDim { get; set; }

    // For specific declaration opcodes
    public uint GlobalFlags { get; set; }

    // For indexable temp declarations
    public uint TempRegIndex { get; set; }
    public uint TempRegCount { get; set; }
    public uint TempRegComponents { get; set; }
}

public class ExtendedToken
{
    public ExtendedOpcodeType Type { get; set; }
    public uint RawToken { get; set; }

    // For sample offset
    public int OffsetU { get; set; }
    public int OffsetV { get; set; }
    public int OffsetW { get; set; }

    // For resource dim
    public byte ResourceDim { get; set; }

    // For resource return type
    public byte ReturnTypeX { get; set; }
    public byte ReturnTypeY { get; set; }
    public byte ReturnTypeZ { get; set; }
    public byte ReturnTypeW { get; set; }
}

public class ResourceDimToken
{
    public RdefResourceDimension Dimension { get; set; }
    public uint SampleCount { get; set; }
}

public enum ExtendedOpcodeType : uint
{
    Empty = 0,
    SampleControls = 1,
    ResourceDim = 2,
    ResourceReturnType = 3
}

public class Operand
{
    public OperandType Type { get; set; }
    public int NumComponents { get; set; } // 0=0, 1=1, 2=4, 3=N(used for OperandIndex)
    public SelectionMode SelectionMode { get; set; }
    public byte SwizzleX { get; set; }
    public byte SwizzleY { get; set; }
    public byte SwizzleZ { get; set; }
    public byte SwizzleW { get; set; }
    public byte WriteMask { get; set; }       // 4-bit mask for XYZW
    public byte SelectComponent { get; set; } // for select_1 mode
    public IndexDimension IndexDimension { get; set; }
    public IndexRepresentation[] IndexRepresentations { get; set; } = new IndexRepresentation[3];
    public OperandIndex[] Indices { get; set; } = new OperandIndex[3];
    public OperandModifier Modifier { get; set; }
    public float[] ImmediateValues { get; set; } = new float[4];
    public int[] ImmediateValuesInt { get; set; } = new int[4];
    public bool IsExtended { get; set; }
}

public class OperandIndex
{
    public ulong Value { get; set; }
    public Operand? RelativeOperand { get; set; }
}

public enum OperandType : uint
{
    Temp = 0,
    Input = 1,
    Output = 2,
    IndexableTemp = 3,
    Immediate32 = 4,
    Immediate64 = 5,
    Sampler = 6,
    Resource = 7,
    ConstantBuffer = 8,
    ImmediateConstantBuffer = 9,
    Label = 10,
    InputPrimitiveID = 11,
    OutputDepth = 12,
    Null = 13,
    Rasterizer = 14,
    OutputCoverageMask = 15,
    Stream = 16,
    FunctionBody = 17,
    FunctionTable = 18,
    Interface = 19,
    FunctionInput = 20,
    FunctionOutput = 21,
    OutputControlPointID = 22,
    InputForkInstanceID = 23,
    InputJoinInstanceID = 24,
    InputControlPoint = 25,
    OutputControlPoint = 26,
    InputPatchConstant = 27,
    InputDomainPoint = 28,
    ThisPointer = 29,
    UnorderedAccessView = 30,
    ThreadGroupSharedMemory = 31,
    InputThreadID = 32,
    InputThreadGroupID = 33,
    InputThreadIDInGroup = 34,
    InputCoverageMask = 35,
    InputThreadIDInGroupFlattened = 36,
    InputGSInstanceID = 37,
    OutputDepthGreaterEqual = 38,
    OutputDepthLessEqual = 39,
    CycleCounter = 40,
    OutputStencilRef = 41,
    InnerCoverage = 42
}

public enum SelectionMode : uint
{
    Mask = 0,
    Swizzle = 1,
    Select1 = 2
}

public enum IndexDimension : uint
{
    D0 = 0,
    D1 = 1,
    D2 = 2,
    D3 = 3
}

public enum IndexRepresentation : uint
{
    Immediate32 = 0,
    Immediate64 = 1,
    Relative = 2,
    Immediate32PlusRelative = 3
}

public enum OperandModifier
{
    None = 0,
    Negate = 1,
    Abs = 2,
    AbsNegate = 3
}

public enum OpcodeType : uint
{
    Add = 0,
    And = 1,
    Break = 2,
    BreakC = 3,
    Call = 4,
    CallC = 5,
    Case = 6,
    Continue = 7,
    ContinueC = 8,
    Cut = 9,
    Default = 10,
    DerivRtx = 11,
    DerivRty = 12,
    Discard = 13,
    Div = 14,
    Dp2 = 15,
    Dp3 = 16,
    Dp4 = 17,
    Else = 18,
    Emit = 19,
    EmitThenCut = 20,
    EndIf = 21,
    EndLoop = 22,
    EndSwitch = 23,
    Eq = 24,
    Exp = 25,
    Frc = 26,
    FtoI = 27,
    FtoU = 28,
    Ge = 29,
    IAdd = 30,
    If = 31,
    IEq = 32,
    IGe = 33,
    ILt = 34,
    IMad = 35,
    IMax = 36,
    IMin = 37,
    IMul = 38,
    INe = 39,
    INeg = 40,
    IShl = 41,
    IShr = 42,
    ItoF = 43,
    Label = 44,
    Ld = 45,
    LdMs = 46,
    Log = 47,
    Loop = 48,
    Lt = 49,
    Mad = 50,
    Min = 51,
    Max = 52,
    CustomData = 53,
    Mov = 54,
    Movc = 55,
    Mul = 56,
    Ne = 57,
    Nop = 58,
    Not = 59,
    Or = 60,
    ResInfo = 61,
    Ret = 62,
    RetC = 63,
    RoundNe = 64,
    RoundNi = 65,
    RoundPi = 66,
    RoundZ = 67,
    Rsq = 68,
    Sample = 69,
    SampleC = 70,
    SampleCLz = 71,
    SampleL = 72,
    SampleD = 73,
    SampleB = 74,
    Sqrt = 75,
    Switch = 76,
    SinCos = 77,
    UDiv = 78,
    ULt = 79,
    UGe = 80,
    UMul = 81,
    UMad = 82,
    UMax = 83,
    UMin = 84,
    UShr = 85,
    UtoF = 86,
    Xor = 87,

    // Declaration opcodes
    DclResource = 88,
    DclConstantBuffer = 89,
    DclSampler = 90,
    DclIndexRange = 91,
    DclGsOutputPrimitiveTopology = 92,
    DclGsInputPrimitive = 93,
    DclMaxOutputVertexCount = 94,
    DclInput = 95,
    DclInputSgv = 96,
    DclInputSiv = 97,
    DclInputPs = 98,
    DclInputPsSgv = 99,
    DclInputPsSiv = 100,
    DclOutput = 101,
    DclOutputSgv = 102,
    DclOutputSiv = 103,
    DclTemps = 104,
    DclIndexableTemp = 105,
    DclGlobalFlags = 106,

    // End of D3D10.0 opcodes
    Reserved0 = 107,

    // D3D10.1 opcodes
    Lod = 108,
    Gather4 = 109,
    SamplePos = 110,
    SampleInfo = 111,

    // End of D3D10.1 opcodes
    Reserved1 = 112,

    // D3D11 (SM5.0) opcodes
    HsDecls = 113,
    HsControlPointPhase = 114,
    HsForkPhase = 115,
    HsJoinPhase = 116,
    EmitStream = 117,
    CutStream = 118,
    EmitThenCutStream = 119,
    InterfaceCall = 120,
    BufInfo = 121,
    DerivRtxCoarse = 122,
    DerivRtxFine = 123,
    DerivRtyCoarse = 124,
    DerivRtyFine = 125,
    Gather4C = 126,
    Gather4Po = 127,
    Gather4PoC = 128,
    Rcp = 129,
    F32toF16 = 130,
    F16toF32 = 131,
    UAddc = 132,
    USubb = 133,
    CountBits = 134,
    FirstBitHi = 135,
    FirstBitLo = 136,
    FirstBitShi = 137,
    UBfe = 138,
    IBfe = 139,
    Bfi = 140,
    BfRev = 141,
    Swapc = 142,

    // SM5.0 declaration opcodes
    DclStream = 143,
    DclFunctionBody = 144,
    DclFunctionTable = 145,
    DclInterface = 146,
    DclInputControlPointCount = 147,
    DclOutputControlPointCount = 148,
    DclTessDomain = 149,
    DclTessPartitioning = 150,
    DclTessOutputPrimitive = 151,
    DclHsMaxTessFactor = 152,
    DclHsForkPhaseInstanceCount = 153,
    DclHsJoinPhaseInstanceCount = 154,
    DclThreadGroup = 155,
    DclUnorderedAccessViewTyped = 156,
    DclUnorderedAccessViewRaw = 157,
    DclUnorderedAccessViewStructured = 158,
    DclThreadGroupSharedMemoryRaw = 159,
    DclThreadGroupSharedMemoryStructured = 160,
    DclResourceRaw = 161,
    DclResourceStructured = 162,

    // SM5.0 memory/UAV opcodes
    LdUavTyped = 163,
    StoreUavTyped = 164,
    LdRaw = 165,
    StoreRaw = 166,
    LdStructured = 167,
    StoreStructured = 168,

    // SM5.0 atomic opcodes
    AtomicAnd = 169,
    AtomicOr = 170,
    AtomicXor = 171,
    AtomicCmpStore = 172,
    AtomicIAdd = 173,
    AtomicIMax = 174,
    AtomicIMin = 175,
    AtomicUMax = 176,
    AtomicUMin = 177,
    ImmAtomicAlloc = 178,
    ImmAtomicConsume = 179,
    ImmAtomicIAdd = 180,
    ImmAtomicAnd = 181,
    ImmAtomicOr = 182,
    ImmAtomicXor = 183,
    ImmAtomicExch = 184,
    ImmAtomicCmpExch = 185,
    ImmAtomicIMax = 186,
    ImmAtomicIMin = 187,
    ImmAtomicUMax = 188,
    ImmAtomicUMin = 189,

    // SM5.0 sync/double/eval opcodes
    Sync = 190,
    DAdd = 191,
    DMax = 192,
    DMin = 193,
    DMul = 194,
    DEq = 195,
    DGe = 196,
    DLt = 197,
    DNe = 198,
    DMov = 199,
    DMovc = 200,
    DtoF = 201,
    FtoD = 202,
    EvalSnapped = 203,
    EvalSampleIndex = 204,
    EvalCentroid = 205,
    DclGsInstanceCount = 206,

    Abort = 207,
    DebugBreak = 208
}