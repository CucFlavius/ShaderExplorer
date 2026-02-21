namespace ShaderExplorer.Decompiler.Dxil;

/// <summary>
///     Top-level container for parsed DXIL assembly.
/// </summary>
public class DxilModule
{
    public string TargetTriple { get; set; } = string.Empty;
    public string TargetDatalayout { get; set; } = string.Empty;
    public List<DxilFunction> Functions { get; set; } = [];
    public List<DxilGlobalVariable> Globals { get; set; } = [];
    public List<DxilMetadata> NamedMetadata { get; set; } = [];
    public List<DxilResourceBinding> ResourceBindings { get; set; } = [];
    public List<DxilCBufferVariable> CBufferVariables { get; set; } = [];
    public List<DxilStructType> StructTypes { get; set; } = [];

    /// <summary>The entry-point function (typically the "main" or first define).</summary>
    public DxilFunction? EntryPoint => Functions.FirstOrDefault(f => !f.IsDeclaration);
}

public class DxilFunction
{
    public string Name { get; set; } = string.Empty;
    public DxilType ReturnType { get; set; } = DxilType.Void;
    public List<DxilParameter> Parameters { get; set; } = [];
    public List<DxilBasicBlock> BasicBlocks { get; set; } = [];
    public bool IsDeclaration { get; set; }
    public List<string> Attributes { get; set; } = [];
}

public class DxilParameter
{
    public DxilType Type { get; set; } = DxilType.Void;
    public string Name { get; set; } = string.Empty;
    public int AddressSpace { get; set; } = -1; // -1=none, 0=thread, 1=device, 2=constant, 3=threadgroup
    public string RawTypeText { get; set; } = ""; // Full original type string for Metal analysis
}

public class DxilBasicBlock
{
    public string Label { get; set; } = string.Empty;
    public List<DxilInstruction> Instructions { get; set; } = [];
    public DxilTerminator? Terminator { get; set; }
    public List<string> Predecessors { get; set; } = [];
    public List<string> Successors { get; set; } = [];
}

/// <summary>
///     A single DXIL instruction (SSA form: %result = opcode operands).
/// </summary>
public class DxilInstruction
{
    public string? ResultName { get; set; }
    public DxilInstructionKind Kind { get; set; }
    public string RawText { get; set; } = string.Empty;

    // For Call instructions
    public DxilType? CallReturnType { get; set; }
    public string CalledFunction { get; set; } = string.Empty;
    public List<DxilOperand> Arguments { get; set; } = [];
    public int DxilOpCode { get; set; } = -1;

    // For binary/compare ops (fadd, fmul, fcmp, icmp, etc.)
    public string Operator { get; set; } = string.Empty;
    public string? Predicate { get; set; }
    public DxilOperand? Operand1 { get; set; }
    public DxilOperand? Operand2 { get; set; }

    // For phi nodes
    public DxilType? PhiType { get; set; }
    public List<(DxilOperand Value, string Block)> PhiIncoming { get; set; } = [];

    // For select
    public DxilOperand? SelectCondition { get; set; }
    public DxilOperand? SelectTrue { get; set; }
    public DxilOperand? SelectFalse { get; set; }

    // For extractvalue / insertvalue
    public DxilOperand? AggregateOperand { get; set; }
    public List<int> Indices { get; set; } = [];
    public DxilOperand? InsertedValue { get; set; }

    // For GEP (getelementptr)
    public DxilOperand? GepBase { get; set; }
    public List<DxilOperand> GepIndices { get; set; } = [];
    public DxilType? GepBaseType { get; set; } // Element type for GEP struct resolution

    // For casts (bitcast, fptoui, etc.)
    public DxilOperand? CastSource { get; set; }
    public DxilType? CastDestType { get; set; }

    // For load/store
    public DxilOperand? LoadStorePointer { get; set; }
    public DxilOperand? StoreValue { get; set; }

    // For alloca
    public DxilType? AllocaType { get; set; }

    // For extractelement / insertelement
    public DxilOperand? VectorOperand { get; set; }
    public DxilOperand? VectorIndex { get; set; }
    public DxilOperand? InsertScalar { get; set; } // insertelement scalar value

    // For shufflevector
    public DxilOperand? ShuffleVector2 { get; set; }
    public List<int> ShuffleMask { get; set; } = [];
}

public enum DxilInstructionKind
{
    Unknown,
    Call,
    BinaryOp,
    CompareOp,
    Phi,
    Select,
    ExtractValue,
    InsertValue,
    GetElementPtr,
    Cast,
    Load,
    Store,
    Alloca,
    ExtractElement,
    InsertElement,
    ShuffleVector
}

/// <summary>
///     A terminator instruction that ends a basic block.
/// </summary>
public class DxilTerminator
{
    public DxilTerminatorKind Kind { get; set; }
    public string RawText { get; set; } = string.Empty;

    // For ret
    public DxilOperand? ReturnValue { get; set; }

    // For br (unconditional)
    public string? TargetLabel { get; set; }

    // For br (conditional)
    public DxilOperand? Condition { get; set; }
    public string? TrueLabel { get; set; }
    public string? FalseLabel { get; set; }

    // For switch
    public DxilOperand? SwitchValue { get; set; }
    public string? DefaultLabel { get; set; }
    public List<(DxilOperand Value, string Label)> SwitchCases { get; set; } = [];
}

public enum DxilTerminatorKind
{
    Return,
    Branch,
    ConditionalBranch,
    Switch,
    Unreachable
}

/// <summary>
///     Represents a value used as an operand in an instruction.
/// </summary>
public class DxilOperand
{
    public DxilOperandKind Kind { get; set; }
    public string RawText { get; set; } = string.Empty;

    // For SSA references
    public string? Name { get; set; }

    // For constants
    public double? FloatValue { get; set; }
    public long? IntValue { get; set; }
    public bool? BoolValue { get; set; }

    public DxilType? Type { get; set; }

    public override string ToString()
    {
        return RawText;
    }
}

public enum DxilOperandKind
{
    SsaRef, // %name or %N
    IntConstant,
    FloatConstant,
    BoolConstant,
    Undef,
    ZeroInit,
    Null,
    Global, // @name
    Label   // label %name
}

/// <summary>
///     Simplified type representation for DXIL.
/// </summary>
public class DxilType
{
    public static readonly DxilType Void = new() { Name = "void" };
    public static readonly DxilType Float = new() { Name = "float" };
    public static readonly DxilType Double = new() { Name = "double" };
    public static readonly DxilType Half = new() { Name = "half" };
    public static readonly DxilType I1 = new() { Name = "i1" };
    public static readonly DxilType I8 = new() { Name = "i8" };
    public static readonly DxilType I16 = new() { Name = "i16" };
    public static readonly DxilType I32 = new() { Name = "i32" };
    public static readonly DxilType I64 = new() { Name = "i64" };
    public string Name { get; set; } = string.Empty;

    public override string ToString()
    {
        return Name;
    }
}

public class DxilGlobalVariable
{
    public string Name { get; set; } = string.Empty;
    public DxilType Type { get; set; } = DxilType.Void;
    public string RawText { get; set; } = string.Empty;
}

public class DxilMetadata
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>
///     Resource binding info parsed from DXIL disassembly comments.
///     Used when RDEF chunk is not available (SM6.0+ compiled with DXC).
/// </summary>
public class DxilResourceBinding
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // cbuffer, sampler, texture, UAV
    public string Format { get; set; } = string.Empty;
    public string Dim { get; set; } = string.Empty;
    public string HlslBind { get; set; } = string.Empty;
    public int BindPoint { get; set; }
    public char BindClass { get; set; } // 't', 's', 'b', 'u'
}

/// <summary>
///     Constant buffer variable parsed from DXIL disassembly comments.
/// </summary>
public class DxilCBufferVariable
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int Offset { get; set; }
    public string CBufferName { get; set; } = string.Empty;
}

public class DxilStructType
{
    public string Name { get; set; } = ""; // e.g. "struct.FragmentIn"
    public List<DxilStructField> Fields { get; set; } = [];
}

public class DxilStructField
{
    public DxilType Type { get; set; } = DxilType.Void;
    public string Name { get; set; } = ""; // default "field0", may be renamed
    public int Offset { get; set; }
}