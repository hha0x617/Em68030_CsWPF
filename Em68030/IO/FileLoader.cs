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

using System.IO;
using Em68030.Core;

public static class FileLoader
{
    public static uint LoadBinary(Memory memory, string filePath, uint loadAddress)
    {
        byte[] data = File.ReadAllBytes(filePath);
        memory.LoadData(loadAddress, data);
        return (uint)data.Length;
    }

    public static (uint startAddress, uint endAddress, uint entryPoint, bool hasEntryPoint) LoadSRecord(Memory memory, string filePath)
    {
        uint minAddr = uint.MaxValue;
        uint maxAddr = 0;
        uint entryPoint = 0;
        bool hasEntryPoint = false;

        foreach (string line in File.ReadAllLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line) || line[0] != 'S')
                continue;

            char type = line[1];
            int byteCount = Convert.ToInt32(line.Substring(2, 2), 16);
            byte[] recordData = new byte[byteCount];
            for (int i = 0; i < byteCount; i++)
                recordData[i] = Convert.ToByte(line.Substring(4 + i * 2, 2), 16);

            // Verify checksum
            byte sum = 0;
            for (int i = 0; i < byteCount; i++)
                sum += recordData[i];
            // sum should be 0xFF (complement of checksum)

            uint address;
            int dataOffset;

            switch (type)
            {
                case '0': // Header
                    continue;

                case '1': // Data with 16-bit address
                    address = (uint)((recordData[0] << 8) | recordData[1]);
                    dataOffset = 2;
                    break;

                case '2': // Data with 24-bit address
                    address = (uint)((recordData[0] << 16) | (recordData[1] << 8) | recordData[2]);
                    dataOffset = 3;
                    break;

                case '3': // Data with 32-bit address
                    address = (uint)((recordData[0] << 24) | (recordData[1] << 16) |
                                     (recordData[2] << 8) | recordData[3]);
                    dataOffset = 4;
                    break;

                case '7': // End record with 32-bit start address
                    entryPoint = (uint)((recordData[0] << 24) | (recordData[1] << 16) |
                                        (recordData[2] << 8) | recordData[3]);
                    hasEntryPoint = true;
                    continue;

                case '8': // End record with 24-bit start address
                    entryPoint = (uint)((recordData[0] << 16) | (recordData[1] << 8) | recordData[2]);
                    hasEntryPoint = true;
                    continue;

                case '9': // End record with 16-bit start address
                    entryPoint = (uint)((recordData[0] << 8) | recordData[1]);
                    hasEntryPoint = true;
                    continue;

                case '5': // Record count
                case '6':
                    continue;

                default:
                    continue;
            }

            int dataLength = byteCount - dataOffset - 1; // -1 for checksum
            for (int i = 0; i < dataLength; i++)
            {
                memory.PokeByte(address + (uint)i, recordData[dataOffset + i]);
            }

            if (address < minAddr) minAddr = address;
            uint end = address + (uint)dataLength;
            if (end > maxAddr) maxAddr = end;
        }

        if (minAddr == uint.MaxValue) minAddr = 0;
        return (minAddr, maxAddr, entryPoint, hasEntryPoint);
    }

    // ====================================================================
    // ELF Loader (32-bit big-endian, for MC68030)
    // ====================================================================

    public static ElfLoadResult LoadElf(Memory memory, string filePath)
    {
        byte[] data = File.ReadAllBytes(filePath);

        // Validate ELF magic: 0x7F 'E' 'L' 'F'
        if (data.Length < 52 ||
            data[0] != 0x7F || data[1] != (byte)'E' ||
            data[2] != (byte)'L' || data[3] != (byte)'F')
            throw new InvalidOperationException("Not a valid ELF file.");

        // EI_CLASS: must be ELFCLASS32 (1)
        if (data[4] != 1)
            throw new InvalidOperationException("Only 32-bit ELF files are supported.");

        // EI_DATA: must be ELFDATA2MSB (2) for big-endian (MC68030)
        byte eiData = data[5];
        bool bigEndian = eiData == 2;
        if (!bigEndian && eiData != 1)
            throw new InvalidOperationException("Unknown ELF data encoding.");

        // Parse ELF header
        ushort e_type = ReadU16(data, 16, bigEndian);
        ushort e_machine = ReadU16(data, 18, bigEndian);
        uint e_entry = ReadU32(data, 24, bigEndian);
        uint e_phoff = ReadU32(data, 28, bigEndian);
        ushort e_phentsize = ReadU16(data, 42, bigEndian);
        ushort e_phnum = ReadU16(data, 44, bigEndian);

        // Validate: ET_EXEC(2) or ET_DYN(3), EM_68K = 4
        if (e_type != 2 && e_type != 3)
            throw new InvalidOperationException($"ELF type {e_type} is not executable.");

        // Load PT_LOAD segments
        uint minAddr = uint.MaxValue;
        uint maxAddr = 0;
        int segmentsLoaded = 0;

        for (int i = 0; i < e_phnum; i++)
        {
            uint phOffset = e_phoff + (uint)(i * e_phentsize);
            if (phOffset + e_phentsize > data.Length)
                break;

            uint p_type = ReadU32(data, phOffset, bigEndian);
            if (p_type != 1) continue; // PT_LOAD = 1

            uint p_offset = ReadU32(data, phOffset + 4, bigEndian);
            uint p_vaddr = ReadU32(data, phOffset + 8, bigEndian);
            // p_paddr at phOffset + 12
            uint p_filesz = ReadU32(data, phOffset + 16, bigEndian);
            uint p_memsz = ReadU32(data, phOffset + 20, bigEndian);

            // Load file data into memory
            if (p_filesz > 0 && p_offset + p_filesz <= data.Length)
            {
                for (uint j = 0; j < p_filesz; j++)
                    memory.PokeByte(p_vaddr + j, data[p_offset + j]);
            }

            // Zero-fill BSS (memsz > filesz)
            for (uint j = p_filesz; j < p_memsz; j++)
                memory.PokeByte(p_vaddr + j, 0);

            if (p_vaddr < minAddr) minAddr = p_vaddr;
            uint end = p_vaddr + p_memsz;
            if (end > maxAddr) maxAddr = end;
            segmentsLoaded++;
        }

        if (segmentsLoaded == 0)
            throw new InvalidOperationException("No loadable segments found in ELF file.");

        if (minAddr == uint.MaxValue) minAddr = 0;

        return new ElfLoadResult
        {
            EntryPoint = e_entry,
            StartAddress = minAddr,
            EndAddress = maxAddr,
            Machine = e_machine,
            SegmentsLoaded = segmentsLoaded
        };
    }

    /// <summary>
    /// Check if a file looks like an ELF binary (by magic number).
    /// </summary>
    public static bool IsElfFile(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            byte[] magic = new byte[4];
            if (fs.Read(magic, 0, 4) < 4) return false;
            return magic[0] == 0x7F && magic[1] == (byte)'E' &&
                   magic[2] == (byte)'L' && magic[3] == (byte)'F';
        }
        catch { return false; }
    }

    private static ushort ReadU16(byte[] data, uint offset, bool bigEndian)
    {
        if (bigEndian)
            return (ushort)((data[offset] << 8) | data[offset + 1]);
        return (ushort)((data[offset + 1] << 8) | data[offset]);
    }

    private static uint ReadU32(byte[] data, uint offset, bool bigEndian)
    {
        if (bigEndian)
            return (uint)((data[offset] << 24) | (data[offset + 1] << 16) |
                          (data[offset + 2] << 8) | data[offset + 3]);
        return (uint)((data[offset + 3] << 24) | (data[offset + 2] << 16) |
                      (data[offset + 1] << 8) | data[offset]);
    }

    public static string? FindLstFile(string filePath)
    {
        string dir = Path.GetDirectoryName(filePath) ?? "";
        string baseName = Path.GetFileNameWithoutExtension(filePath);

        string lstPath = Path.Combine(dir, baseName + ".lst");
        if (File.Exists(lstPath)) return lstPath;

        lstPath = Path.Combine(dir, baseName + ".LST");
        if (File.Exists(lstPath)) return lstPath;

        return null;
    }

    public static List<LstLine> LoadLstFile(string lstPath)
    {
        var lines = new List<LstLine>();
        foreach (string rawLine in File.ReadAllLines(lstPath))
        {
            var lstLine = ParseLstLine(rawLine);
            lines.Add(lstLine);
        }
        return lines;
    }

    private static LstLine ParseLstLine(string line)
    {
        var result = new LstLine { RawText = line };

        if (line.Length < 8) return result;

        // Try to parse address from first column (common LST format)
        // Format: XXXXXXXX  XXXX XXXX  mnemonic operands  ; comment
        string addrPart = line.Substring(0, Math.Min(8, line.Length)).Trim();
        if (uint.TryParse(addrPart, System.Globalization.NumberStyles.HexNumber, null, out uint addr))
        {
            result.Address = addr;
            result.HasAddress = true;
        }

        return result;
    }
}

public class LstLine
{
    public string RawText { get; set; } = "";
    public uint Address { get; set; }
    public bool HasAddress { get; set; }
}

public class ElfLoadResult
{
    public uint EntryPoint { get; set; }
    public uint StartAddress { get; set; }
    public uint EndAddress { get; set; }
    public ushort Machine { get; set; }
    public int SegmentsLoaded { get; set; }

    public string MachineDescription => Machine switch
    {
        4 => "MC68000",
        _ => $"Unknown ({Machine})"
    };
}
