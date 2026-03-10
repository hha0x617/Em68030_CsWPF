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

namespace Em68030.IO;

using Em68030.Core;

public class ConsoleDevice : IMemoryMappedDevice
{
    private uint _baseAddress;
    private readonly byte[] _registers = new byte[256];

    public event Action<char>? CharOutput;
    public event Action<string>? StringOutput;
    public Func<char>? CharInput;
    public Func<string>? StringInput;

    public uint BaseAddress
    {
        get => _baseAddress;
        set => _baseAddress = value;
    }

    public ConsoleDevice(uint baseAddress = 0x00FF0000)
    {
        _baseAddress = baseAddress;
    }

    // Console I/O register offsets
    // 0x00: Data register (R/W) - character data
    // 0x04: Status register (R) - bit 0: data available, bit 1: ready to send
    // 0x08: Command register (W) - write to trigger I/O operation

    public byte ReadByte(uint address)
    {
        uint offset = address - _baseAddress;
        if (offset < (uint)_registers.Length)
            return _registers[offset];
        return 0;
    }

    public ushort ReadWord(uint address)
    {
        return (ushort)((ReadByte(address) << 8) | ReadByte(address + 1));
    }

    public uint ReadLong(uint address)
    {
        return (uint)((ReadWord(address) << 16) | ReadWord(address + 2));
    }

    public void WriteByte(uint address, byte value)
    {
        uint offset = address - _baseAddress;
        if (offset < (uint)_registers.Length)
            _registers[offset] = value;

        if (offset == 0) // Data register - output character
        {
            CharOutput?.Invoke((char)value);
        }
    }

    public void WriteWord(uint address, ushort value)
    {
        WriteByte(address, (byte)(value >> 8));
        WriteByte(address + 1, (byte)(value & 0xFF));
    }

    public void WriteLong(uint address, uint value)
    {
        WriteWord(address, (ushort)(value >> 16));
        WriteWord(address + 2, (ushort)(value & 0xFFFF));
    }

    // TRAP #15 handler - called by CPU when TRAP #15 is executed
    public void HandleTrap(MC68030 cpu)
    {
        uint function = cpu.D[0];

        switch (function)
        {
            case 0: // Display null-terminated string at (A1)
                {
                    var sb = new System.Text.StringBuilder();
                    uint addr = cpu.A[1];
                    byte ch;
                    while ((ch = cpu.ReadByte(addr)) != 0)
                    {
                        sb.Append((char)ch);
                        addr++;
                    }
                    StringOutput?.Invoke(sb.ToString());
                }
                break;

            case 1: // Read one character -> D1.B
                {
                    char ch = CharInput?.Invoke() ?? '\0';
                    cpu.D[1] = (cpu.D[1] & 0xFFFFFF00) | (byte)ch;
                }
                break;

            case 2: // Display number in D1.L
                StringOutput?.Invoke(((int)cpu.D[1]).ToString());
                break;

            case 3: // Read string to buffer at (A1)
                {
                    string? input = StringInput?.Invoke();
                    if (input != null)
                    {
                        uint addr = cpu.A[1];
                        foreach (char c in input)
                        {
                            cpu.WriteByte(addr, (byte)c);
                            addr++;
                        }
                        cpu.WriteByte(addr, 0); // null terminate
                    }
                }
                break;

            case 4: // Read number -> D1.L
                {
                    string? input = StringInput?.Invoke();
                    if (input != null && int.TryParse(input, out int val))
                        cpu.D[1] = (uint)val;
                }
                break;

            case 5: // Display character in D1.B
                CharOutput?.Invoke((char)(cpu.D[1] & 0xFF));
                break;

            case 9: // Program termination
                cpu.Halted = true;
                cpu.StopReason = "Program terminated (TRAP #15, D0=9)";
                break;
        }
    }
}
