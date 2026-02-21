namespace ShaderExplorer.Decompiler.Dxil;

/// <summary>
///     DXIL intrinsic opcodes (passed as first i32 argument to dx.op.* calls).
/// </summary>
public enum DxilOpCode
{
    TempRegLoad = 0,
    TempRegStore = 1,
    MinPrecXRegLoad = 2,
    MinPrecXRegStore = 3,
    LoadInput = 4,
    StoreOutput = 5,
    FAbs = 6,
    Saturate = 7,
    IsNaN = 8,
    IsInf = 9,
    IsFinite = 10,
    IsNormal = 11,
    Cos = 12,
    Sin = 13,
    Tan = 14,
    Acos = 15,
    Asin = 16,
    Atan = 17,
    Hcos = 18,
    Hsin = 19,
    Htan = 20,
    Exp = 21,
    Frc = 22,
    Log = 23,
    Sqrt = 24,
    Rsqrt = 25,
    Round_ne = 26,
    Round_ni = 27,
    Round_pi = 28,
    Round_z = 29,
    Bfrev = 30,
    Countbits = 31,
    FirstbitLo = 32,
    FirstbitHi = 33,
    FirstbitSHi = 34,
    FMax = 35,
    FMin = 36,
    IMax = 37,
    IMin = 38,
    UMax = 39,
    UMin = 40,
    IMul = 41,
    UMul = 42,
    UDiv = 43,
    UAddc = 44,
    USubb = 45,
    FMad = 46,
    Fma = 47,
    IMad = 48,
    UMad = 49,
    Msad = 50,
    Ibfe = 51,
    Ubfe = 52,
    Bfi = 53,
    Dot2 = 54,
    Dot3 = 55,
    Dot4 = 56,
    CreateHandle = 57,
    CBufferLoad = 58,
    CBufferLoadLegacy = 59,
    Sample = 60,
    SampleBias = 61,
    SampleLevel = 62,
    SampleGrad = 63,
    SampleCmp = 64,
    SampleCmpLevelZero = 65,
    TextureLoad = 66,
    TextureStore = 67,
    BufferLoad = 68,
    BufferStore = 69,
    BufferUpdateCounter = 70,
    CheckAccessFullyMapped = 71,
    GetDimensions = 72,
    TextureGather = 73,
    TextureGatherCmp = 74,
    Texture2DMSGetSamplePosition = 75,
    RenderTargetGetSamplePosition = 76,
    RenderTargetGetSampleCount = 77,
    AtomicBinOp = 78,
    AtomicCompareExchange = 79,
    Barrier = 80,
    CalculateLOD = 81,
    Discard = 82,
    DerivCoarseX = 83,
    DerivCoarseY = 84,
    DerivFineX = 85,
    DerivFineY = 86,
    EvalSnapped = 87,
    EvalSampleIndex = 88,
    EvalCentroid = 89,
    SampleIndex = 90,
    Coverage = 91,
    InnerCoverage = 92,
    ThreadId = 93,
    GroupId = 94,
    ThreadIdInGroup = 95,
    FlattenedThreadIdInGroup = 96,
    EmitStream = 97,
    CutStream = 98,
    EmitThenCutStream = 99,
    GSInstanceID = 100,
    MakeDouble = 101,
    SplitDouble = 102,
    LoadOutputControlPoint = 103,
    LoadPatchConstant = 104,
    DomainLocation = 105,
    StorePatchConstant = 106,
    OutputControlPointID = 107,
    PrimitiveID = 108,
    CycleCounterLegacy = 109,
    WaveIsFirstLane = 110,
    WaveGetLaneIndex = 111,
    WaveGetLaneCount = 112,
    WaveAnyTrue = 113,
    WaveAllTrue = 114,
    WaveActiveAllEqual = 115,
    WaveActiveBallot = 116,
    WaveReadLaneAt = 117,
    WaveReadLaneFirst = 118,
    WaveActiveOp = 119,
    WaveActiveBit = 120,
    WavePrefixOp = 121,
    QuadReadLaneAt = 122,
    QuadOp = 123,
    BitcastI16toF16 = 124,
    BitcastF16toI16 = 125,
    BitcastI32toF32 = 126,
    BitcastF32toI32 = 127,
    BitcastI64toF64 = 128,
    BitcastF64toI64 = 129,
    LegacyF32ToF16 = 130,
    LegacyF16ToF32 = 131,
    LegacyDoubleToFloat = 132,
    LegacyDoubleToSInt32 = 133,
    LegacyDoubleToUInt32 = 134,
    WaveAllBitCount = 135,
    WavePrefixBitCount = 136,
    AttributeAtVertex = 137,
    ViewID = 138,
    RawBufferLoad = 139,
    RawBufferStore = 140,
    InstanceID = 141,
    InstanceIndex = 142,
    HitKind = 143,
    RayFlags = 144,
    DispatchRaysIndex = 145,
    DispatchRaysDimensions = 146,
    WorldRayOrigin = 147,
    WorldRayDirection = 148,
    ObjectRayOrigin = 149,
    ObjectRayDirection = 150,
    ObjectToWorld = 151,
    WorldToObject = 152,
    RayTMin = 153,
    RayTCurrent = 154,
    IgnoreHit = 155,
    AcceptHitAndEndSearch = 156,
    TraceRay = 157,
    ReportHit = 158,
    CallShader = 159,
    CreateHandleForLib = 160,
    Dot2AddHalf = 162,
    Dot4AddI8Packed = 163,
    Dot4AddU8Packed = 164,
    AnnotateHandle = 216,
    CreateHandleFromBinding = 217,
    CreateHandleFromHeap = 218
}

/// <summary>
///     Maps DXIL opcodes to HLSL equivalents.
/// </summary>
public static class DxilOpMapping
{
    private static readonly Dictionary<int, string> HlslNames = new()
    {
        [(int)DxilOpCode.LoadInput] = "LoadInput",
        [(int)DxilOpCode.StoreOutput] = "StoreOutput",
        [(int)DxilOpCode.FAbs] = "abs",
        [(int)DxilOpCode.Saturate] = "saturate",
        [(int)DxilOpCode.IsNaN] = "isnan",
        [(int)DxilOpCode.IsInf] = "isinf",
        [(int)DxilOpCode.IsFinite] = "isfinite",
        [(int)DxilOpCode.Cos] = "cos",
        [(int)DxilOpCode.Sin] = "sin",
        [(int)DxilOpCode.Tan] = "tan",
        [(int)DxilOpCode.Acos] = "acos",
        [(int)DxilOpCode.Asin] = "asin",
        [(int)DxilOpCode.Atan] = "atan",
        [(int)DxilOpCode.Hcos] = "cosh",
        [(int)DxilOpCode.Hsin] = "sinh",
        [(int)DxilOpCode.Htan] = "tanh",
        [(int)DxilOpCode.Exp] = "exp2",
        [(int)DxilOpCode.Frc] = "frac",
        [(int)DxilOpCode.Log] = "log2",
        [(int)DxilOpCode.Sqrt] = "sqrt",
        [(int)DxilOpCode.Rsqrt] = "rsqrt",
        [(int)DxilOpCode.Round_ne] = "round",
        [(int)DxilOpCode.Round_ni] = "floor",
        [(int)DxilOpCode.Round_pi] = "ceil",
        [(int)DxilOpCode.Round_z] = "trunc",
        [(int)DxilOpCode.Bfrev] = "reversebits",
        [(int)DxilOpCode.Countbits] = "countbits",
        [(int)DxilOpCode.FirstbitLo] = "firstbitlow",
        [(int)DxilOpCode.FirstbitHi] = "firstbithigh",
        [(int)DxilOpCode.FirstbitSHi] = "firstbithigh",
        [(int)DxilOpCode.FMax] = "max",
        [(int)DxilOpCode.FMin] = "min",
        [(int)DxilOpCode.IMax] = "max",
        [(int)DxilOpCode.IMin] = "min",
        [(int)DxilOpCode.UMax] = "max",
        [(int)DxilOpCode.UMin] = "min",
        [(int)DxilOpCode.FMad] = "mad",
        [(int)DxilOpCode.Fma] = "fma",
        [(int)DxilOpCode.IMad] = "mad",
        [(int)DxilOpCode.UMad] = "mad",
        [(int)DxilOpCode.Dot2] = "dot",
        [(int)DxilOpCode.Dot3] = "dot",
        [(int)DxilOpCode.Dot4] = "dot",
        [(int)DxilOpCode.Sample] = "Sample",
        [(int)DxilOpCode.SampleBias] = "SampleBias",
        [(int)DxilOpCode.SampleLevel] = "SampleLevel",
        [(int)DxilOpCode.SampleGrad] = "SampleGrad",
        [(int)DxilOpCode.SampleCmp] = "SampleCmp",
        [(int)DxilOpCode.SampleCmpLevelZero] = "SampleCmpLevelZero",
        [(int)DxilOpCode.TextureLoad] = "Load",
        [(int)DxilOpCode.TextureStore] = "Store",
        [(int)DxilOpCode.BufferLoad] = "Load",
        [(int)DxilOpCode.BufferStore] = "Store",
        [(int)DxilOpCode.GetDimensions] = "GetDimensions",
        [(int)DxilOpCode.TextureGather] = "Gather",
        [(int)DxilOpCode.TextureGatherCmp] = "GatherCmp",
        [(int)DxilOpCode.CalculateLOD] = "CalculateLevelOfDetail",
        [(int)DxilOpCode.Discard] = "discard",
        [(int)DxilOpCode.DerivCoarseX] = "ddx_coarse",
        [(int)DxilOpCode.DerivCoarseY] = "ddy_coarse",
        [(int)DxilOpCode.DerivFineX] = "ddx_fine",
        [(int)DxilOpCode.DerivFineY] = "ddy_fine",
        [(int)DxilOpCode.EvalSnapped] = "EvaluateAttributeSnapped",
        [(int)DxilOpCode.EvalSampleIndex] = "EvaluateAttributeAtSample",
        [(int)DxilOpCode.EvalCentroid] = "EvaluateAttributeAtCentroid",
        [(int)DxilOpCode.Barrier] = "Barrier",
        [(int)DxilOpCode.EmitStream] = "EmitStream",
        [(int)DxilOpCode.CutStream] = "CutStream",
        [(int)DxilOpCode.EmitThenCutStream] = "EmitThenCutStream",
        [(int)DxilOpCode.LegacyF32ToF16] = "f32tof16",
        [(int)DxilOpCode.LegacyF16ToF32] = "f16tof32",
        [(int)DxilOpCode.RawBufferLoad] = "Load",
        [(int)DxilOpCode.RawBufferStore] = "Store"
    };

    public static string? GetHlslEquivalent(int opcode)
    {
        return HlslNames.GetValueOrDefault(opcode);
    }

    /// <summary>
    ///     Returns true if this opcode is a unary math function (one input, one output).
    /// </summary>
    public static bool IsUnaryMath(int opcode)
    {
        return opcode is
            (int)DxilOpCode.FAbs or
            (int)DxilOpCode.Saturate or
            (int)DxilOpCode.IsNaN or
            (int)DxilOpCode.IsInf or
            (int)DxilOpCode.IsFinite or
            (int)DxilOpCode.IsNormal or
            (int)DxilOpCode.Cos or
            (int)DxilOpCode.Sin or
            (int)DxilOpCode.Tan or
            (int)DxilOpCode.Acos or
            (int)DxilOpCode.Asin or
            (int)DxilOpCode.Atan or
            (int)DxilOpCode.Hcos or
            (int)DxilOpCode.Hsin or
            (int)DxilOpCode.Htan or
            (int)DxilOpCode.Exp or
            (int)DxilOpCode.Frc or
            (int)DxilOpCode.Log or
            (int)DxilOpCode.Sqrt or
            (int)DxilOpCode.Rsqrt or
            (int)DxilOpCode.Round_ne or
            (int)DxilOpCode.Round_ni or
            (int)DxilOpCode.Round_pi or
            (int)DxilOpCode.Round_z or
            (int)DxilOpCode.Bfrev or
            (int)DxilOpCode.Countbits or
            (int)DxilOpCode.FirstbitLo or
            (int)DxilOpCode.FirstbitHi or
            (int)DxilOpCode.FirstbitSHi or
            (int)DxilOpCode.LegacyF32ToF16 or
            (int)DxilOpCode.LegacyF16ToF32;
    }

    /// <summary>
    ///     Returns true if this opcode is a binary math function (two inputs, one output).
    /// </summary>
    public static bool IsBinaryMath(int opcode)
    {
        return opcode is
            (int)DxilOpCode.FMax or
            (int)DxilOpCode.FMin or
            (int)DxilOpCode.IMax or
            (int)DxilOpCode.IMin or
            (int)DxilOpCode.UMax or
            (int)DxilOpCode.UMin;
    }

    /// <summary>
    ///     Returns true if this is a ternary math function (mad/fma).
    /// </summary>
    public static bool IsTernaryMath(int opcode)
    {
        return opcode is
            (int)DxilOpCode.FMad or
            (int)DxilOpCode.Fma or
            (int)DxilOpCode.IMad or
            (int)DxilOpCode.UMad;
    }

    /// <summary>
    ///     Returns true if this is a dot product op.
    /// </summary>
    public static bool IsDot(int opcode)
    {
        return opcode is
            (int)DxilOpCode.Dot2 or
            (int)DxilOpCode.Dot3 or
            (int)DxilOpCode.Dot4;
    }

    /// <summary>
    ///     Returns the component count for a dot product opcode.
    /// </summary>
    public static int DotComponentCount(int opcode)
    {
        return opcode switch
        {
            (int)DxilOpCode.Dot2 => 2,
            (int)DxilOpCode.Dot3 => 3,
            (int)DxilOpCode.Dot4 => 4,
            _ => 0
        };
    }

    /// <summary>
    ///     Returns true if this is a texture sampling op.
    /// </summary>
    public static bool IsSample(int opcode)
    {
        return opcode is
            (int)DxilOpCode.Sample or
            (int)DxilOpCode.SampleBias or
            (int)DxilOpCode.SampleLevel or
            (int)DxilOpCode.SampleGrad or
            (int)DxilOpCode.SampleCmp or
            (int)DxilOpCode.SampleCmpLevelZero;
    }
}