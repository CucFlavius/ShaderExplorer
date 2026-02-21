namespace ShaderExplorer.Decompiler.Chunks;

public class StatChunk
{
    public uint InstructionCount { get; set; }
    public uint TempRegisterCount { get; set; }
    public uint DefCount { get; set; }
    public uint DclCount { get; set; }
    public uint FloatInstructionCount { get; set; }
    public uint IntInstructionCount { get; set; }
    public uint UIntInstructionCount { get; set; }
    public uint StaticFlowControlCount { get; set; }
    public uint DynamicFlowControlCount { get; set; }
    public uint EmitInstructionCount { get; set; }
    public uint TempArrayCount { get; set; }
    public uint ArrayInstructionCount { get; set; }
    public uint CutInstructionCount { get; set; }
}