// Copyright 2026 hha0x617
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

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

    /// <summary>True if block has no memory access instructions (no bailout possible).</summary>
    public bool RegisterOnly { get; }

    /// <summary>Compiled delegate. Takes CPU, returns next PC.</summary>
    public Func<MC68030, uint> Execute { get; }

    /// <summary>Tracks bailout frequency for blacklisting.</summary>
    public ushort BailoutCount;

    public CompiledBlock(uint physicalAddress, int instructionCount, int totalCycles, int byteLength,
        bool registerOnly, Func<MC68030, uint> execute)
    {
        PhysicalAddress = physicalAddress;
        InstructionCount = instructionCount;
        TotalCycles = totalCycles;
        ByteLength = byteLength;
        RegisterOnly = registerOnly;
        Execute = execute;
    }
}
