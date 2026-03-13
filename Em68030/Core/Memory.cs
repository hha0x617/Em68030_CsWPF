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

using System.Buffers.Binary;

namespace Em68030.Core;

public enum RegionType { Ram, Rom }

public class MemoryRegion
{
    public uint BaseAddress { get; }
    public int Size { get; }
    public RegionType Type { get; }
    public byte[] Data { get; }

    public MemoryRegion(uint baseAddress, int size, RegionType type)
    {
        BaseAddress = baseAddress;
        Size = size;
        Type = type;
        Data = new byte[size];
    }

    public bool Contains(uint address) => address >= BaseAddress && address < BaseAddress + (uint)Size;
}

public class Memory
{
    private readonly List<MemoryRegion> _regions = new();
    private readonly Dictionary<uint, IMemoryMappedDevice> _deviceMap = new();

    // Fast path: direct reference to base-0 RAM region (avoids FindRegion/FindDevice per access)
    private byte[]? _fastRam;
    private uint _fastRamSize;
    private uint _deviceMinAddr = uint.MaxValue; // Lowest device-mapped address
    private uint _fastRamLimit; // Min(_fastRamSize, _deviceMinAddr) — single comparison fast path

    // Last-hit region cache for non-fastRAM accesses (e.g., ROM at $FF800000)
    private MemoryRegion? _lastRegion;

    /// <summary>
    /// Direct access to the base-0 RAM array for the framebuffer renderer.
    /// The renderer reads VRAM data from this array on the UI thread.
    /// No locking needed — worst case is a single torn frame.
    /// </summary>
    public byte[]? FastRam => _fastRam;
    public uint FastRamSize => _fastRamSize;

    public int Size
    {
        get
        {
            if (_regions.Count == 0) return 0;
            uint max = 0;
            foreach (var r in _regions)
            {
                uint end = r.BaseAddress + (uint)r.Size;
                if (end > max) max = end;
            }
            return (int)max;
        }
    }

    public Memory()
    {
    }

    public Memory(int sizeBytes)
    {
        AddRegion(0, sizeBytes, RegionType.Ram);
    }

    public void AddRegion(uint baseAddress, int size, RegionType type)
    {
        var region = new MemoryRegion(baseAddress, size, type);
        _regions.Add(region);
        // Cache base-0 RAM for fast path
        if (baseAddress == 0 && type == RegionType.Ram)
        {
            _fastRam = region.Data;
            _fastRamSize = (uint)size;
            _fastRamLimit = Math.Min(_fastRamSize, _deviceMinAddr);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private MemoryRegion? FindRegion(uint address)
    {
        // Check last-hit cache first (covers repeated ROM accesses)
        var cached = _lastRegion;
        if (cached != null && cached.Contains(address))
            return cached;
        for (int i = 0; i < _regions.Count; i++)
        {
            if (_regions[i].Contains(address))
            {
                _lastRegion = _regions[i];
                return _regions[i];
            }
        }
        return null;
    }

    public void RegisterDevice(uint baseAddress, uint size, IMemoryMappedDevice device)
    {
        for (uint i = 0; i < size; i += 4)
        {
            _deviceMap[baseAddress + i] = device;
        }
        if (baseAddress < _deviceMinAddr)
        {
            _deviceMinAddr = baseAddress;
            _fastRamLimit = Math.Min(_fastRamSize, _deviceMinAddr);
        }
    }

    public void UnregisterDevice(uint baseAddress, uint size)
    {
        for (uint i = 0; i < size; i += 4)
        {
            _deviceMap.Remove(baseAddress + i);
        }
    }

    private IMemoryMappedDevice? FindDevice(uint address)
    {
        uint aligned = address & 0xFFFFFFFC;
        _deviceMap.TryGetValue(aligned, out var device);
        return device;
    }

    // ========================================================================
    // Read/Write — CPU execution (bus error on unmapped access)
    // ========================================================================

    public byte ReadByte(uint address)
    {
        // Fast path: address in base-0 RAM and below any device range
        if (address < _fastRamLimit)
            return _fastRam![address];

        var device = FindDevice(address);
        if (device != null)
            return device.ReadByte(address);

        var region = FindRegion(address);
        if (region != null)
            return region.Data[address - region.BaseAddress];

        throw new BusErrorException(address, false, 0, 0);
    }

    public ushort ReadWord(uint address)
    {
        if (address + 1 < _fastRamLimit)
            return BinaryPrimitives.ReadUInt16BigEndian(_fastRam.AsSpan((int)address, 2));

        var device = FindDevice(address);
        if (device != null)
            return device.ReadWord(address);

        var region = FindRegion(address);
        if (region != null)
        {
            uint offset = address - region.BaseAddress;
            if (offset + 1 < (uint)region.Size)
                return (ushort)((region.Data[offset] << 8) | region.Data[offset + 1]);
        }

        throw new BusErrorException(address, false, 0, 0);
    }

    public uint ReadLong(uint address)
    {
        if (address + 3 < _fastRamLimit)
            return BinaryPrimitives.ReadUInt32BigEndian(_fastRam.AsSpan((int)address, 4));

        var device = FindDevice(address);
        if (device != null)
            return device.ReadLong(address);

        var region = FindRegion(address);
        if (region != null)
        {
            uint offset = address - region.BaseAddress;
            if (offset + 3 < (uint)region.Size)
                return (uint)((region.Data[offset] << 24) | (region.Data[offset + 1] << 16) |
                              (region.Data[offset + 2] << 8) | region.Data[offset + 3]);
        }

        throw new BusErrorException(address, false, 0, 0);
    }

    public void WriteByte(uint address, byte value)
    {
        if (address < _fastRamLimit)
        {
            _fastRam![address] = value;
            return;
        }

        var device = FindDevice(address);
        if (device != null)
        {
            device.WriteByte(address, value);
            return;
        }

        var region = FindRegion(address);
        if (region == null)
            throw new BusErrorException(address, true, 0, 0);

        if (region.Type == RegionType.Rom)
            return;

        region.Data[address - region.BaseAddress] = value;
    }

    public void WriteWord(uint address, ushort value)
    {
        if (address + 1 < _fastRamLimit)
        {
            BinaryPrimitives.WriteUInt16BigEndian(_fastRam.AsSpan((int)address, 2), value);
            return;
        }

        var device = FindDevice(address);
        if (device != null)
        {
            device.WriteWord(address, value);
            return;
        }

        var region = FindRegion(address);
        if (region == null)
            throw new BusErrorException(address, true, 0, 0);

        if (region.Type == RegionType.Rom)
            return;

        uint offset = address - region.BaseAddress;
        if (offset + 1 < (uint)region.Size)
        {
            region.Data[offset] = (byte)(value >> 8);
            region.Data[offset + 1] = (byte)(value & 0xFF);
        }
    }

    public void WriteLong(uint address, uint value)
    {
        if (address + 3 < _fastRamLimit)
        {
            BinaryPrimitives.WriteUInt32BigEndian(_fastRam.AsSpan((int)address, 4), value);
            return;
        }

        var device = FindDevice(address);
        if (device != null)
        {
            device.WriteLong(address, value);
            return;
        }

        var region = FindRegion(address);
        if (region == null)
            throw new BusErrorException(address, true, 0, 0);

        if (region.Type == RegionType.Rom)
            return;

        uint offset = address - region.BaseAddress;
        if (offset + 3 < (uint)region.Size)
        {
            region.Data[offset] = (byte)(value >> 24);
            region.Data[offset + 1] = (byte)((value >> 16) & 0xFF);
            region.Data[offset + 2] = (byte)((value >> 8) & 0xFF);
            region.Data[offset + 3] = (byte)(value & 0xFF);
        }
    }

    // ========================================================================
    // Peek/Poke — Debugger/loader (no exceptions, ROM writable)
    // ========================================================================

    public byte PeekByte(uint address)
    {
        var device = FindDevice(address);
        if (device != null)
            return device.ReadByte(address);

        var region = FindRegion(address);
        if (region != null)
            return region.Data[address - region.BaseAddress];

        return 0xFF;
    }

    public ushort PeekWord(uint address)
    {
        var device = FindDevice(address);
        if (device != null)
            return device.ReadWord(address);

        var region = FindRegion(address);
        if (region != null)
        {
            uint offset = address - region.BaseAddress;
            if (offset + 1 < (uint)region.Size)
                return (ushort)((region.Data[offset] << 8) | region.Data[offset + 1]);
        }

        return 0xFFFF;
    }

    public uint PeekLong(uint address)
    {
        var device = FindDevice(address);
        if (device != null)
            return device.ReadLong(address);

        var region = FindRegion(address);
        if (region != null)
        {
            uint offset = address - region.BaseAddress;
            if (offset + 3 < (uint)region.Size)
                return (uint)((region.Data[offset] << 24) | (region.Data[offset + 1] << 16) |
                              (region.Data[offset + 2] << 8) | region.Data[offset + 3]);
        }

        return 0xFFFFFFFF;
    }

    public void PokeByte(uint address, byte value)
    {
        var region = FindRegion(address);
        if (region != null)
            region.Data[address - region.BaseAddress] = value;
    }

    public void PokeWord(uint address, ushort value)
    {
        var region = FindRegion(address);
        if (region != null)
        {
            uint offset = address - region.BaseAddress;
            if (offset + 1 < (uint)region.Size)
            {
                region.Data[offset] = (byte)(value >> 8);
                region.Data[offset + 1] = (byte)(value & 0xFF);
            }
        }
    }

    public void PokeLong(uint address, uint value)
    {
        var region = FindRegion(address);
        if (region != null)
        {
            uint offset = address - region.BaseAddress;
            if (offset + 3 < (uint)region.Size)
            {
                region.Data[offset] = (byte)(value >> 24);
                region.Data[offset + 1] = (byte)((value >> 16) & 0xFF);
                region.Data[offset + 2] = (byte)((value >> 8) & 0xFF);
                region.Data[offset + 3] = (byte)(value & 0xFF);
            }
        }
    }

    // ========================================================================
    // Bulk operations (debugger/loader)
    // ========================================================================

    public void LoadData(uint address, byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            PokeByte(address + (uint)i, data[i]);
        }
    }

    public byte[] GetRange(uint address, int length)
    {
        var result = new byte[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = PeekByte(address + (uint)i);
        }
        return result;
    }
}

public interface IMemoryMappedDevice
{
    byte ReadByte(uint address);
    ushort ReadWord(uint address);
    uint ReadLong(uint address);
    void WriteByte(uint address, byte value);
    void WriteWord(uint address, ushort value);
    void WriteLong(uint address, uint value);
}
