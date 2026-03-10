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

using System.Runtime.CompilerServices;

namespace Em68030.Core.Jit;

/// <summary>
/// Block cache and execution count tracking for the JIT compiler.
/// Uses direct-mapped arrays for O(1) lookup with minimal overhead.
/// </summary>
public class JitCache
{
    // Direct-mapped block cache: 8192 slots, indexed by (physAddr >> 1) & mask
    // Instructions are word-aligned, so >> 1 gives unique indices per instruction address
    private const int BlockCacheShift = 13;
    private const int BlockCacheSize = 1 << BlockCacheShift;
    private const int BlockCacheMask = BlockCacheSize - 1;

    // Execution count array: 65536 slots, byte counters
    // Aliasing is harmless (slightly earlier compilation)
    private const int CountCacheSize = 65536;
    private const int CountCacheMask = CountCacheSize - 1;

    private readonly CompiledBlock?[] _blockCache = new CompiledBlock?[BlockCacheSize];
    private readonly byte[] _counts = new byte[CountCacheSize];
    private readonly bool[] _uncompilable = new bool[CountCacheSize];

    public int BlockCount { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CompiledBlock? TryGetBlock(uint physAddr)
    {
        var block = _blockCache[(physAddr >> 1) & BlockCacheMask];
        if (block != null && block.PhysicalAddress == physAddr)
            return block;
        return null;
    }

    public void AddBlock(uint physAddr, CompiledBlock block)
    {
        _blockCache[(physAddr >> 1) & BlockCacheMask] = block;
        BlockCount++;
    }

    public void RemoveBlock(uint physAddr)
    {
        int idx = (int)((physAddr >> 1) & BlockCacheMask);
        if (_blockCache[idx] != null && _blockCache[idx]!.PhysicalAddress == physAddr)
            _blockCache[idx] = null;
    }

    /// <summary>
    /// Invalidate all compiled blocks (MMU flush, privilege change, reset).
    /// </summary>
    public void InvalidateAll()
    {
        Array.Clear(_blockCache);
        Array.Clear(_uncompilable);
        BlockCount = 0;
        // Keep _counts — they represent hotness regardless of translation
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte IncrementAndGetCount(uint physAddr)
    {
        ref byte c = ref _counts[(physAddr >> 1) & CountCacheMask];
        return ++c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsUncompilable(uint physAddr)
        => _uncompilable[(physAddr >> 1) & CountCacheMask];

    public void MarkUncompilable(uint physAddr)
        => _uncompilable[(physAddr >> 1) & CountCacheMask] = true;
}
