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
