namespace Em68030.IO;

using Em68030.Core;

/// <summary>
/// Catch-all device for unmapped MVME147 I/O space.
/// Prevents bus errors when the kernel probes addresses that don't
/// correspond to specific emulated devices.
/// Returns 0 for reads, ignores writes.
/// </summary>
public class Mvme147IoSpaceDevice : IMemoryMappedDevice
{
    public byte ReadByte(uint address) => 0;
    public ushort ReadWord(uint address) => 0;
    public uint ReadLong(uint address) => 0;
    public void WriteByte(uint address, byte value) { }
    public void WriteWord(uint address, ushort value) { }
    public void WriteLong(uint address, uint value) { }
}
