namespace Em68030.Core.Jit;

/// <summary>
/// Represents a JIT-compiled basic block of MC68030 instructions.
/// </summary>
public class CompiledBlock
{
    /// <summary>Physical address where the block starts.</summary>
    public uint PhysicalAddress { get; }

    /// <summary>Number of instructions in this block.</summary>
    public int InstructionCount { get; }

    /// <summary>Total approximate cycle count for the block.</summary>
    public int TotalCycles { get; }

    /// <summary>Total byte length of the block (for cache invalidation range).</summary>
    public int ByteLength { get; }

    /// <summary>Compiled delegate. Takes CPU, returns next PC.</summary>
    public Func<MC68030, uint> Execute { get; }

    public CompiledBlock(uint physicalAddress, int instructionCount, int totalCycles, int byteLength, Func<MC68030, uint> execute)
    {
        PhysicalAddress = physicalAddress;
        InstructionCount = instructionCount;
        TotalCycles = totalCycles;
        ByteLength = byteLength;
        Execute = execute;
    }
}
