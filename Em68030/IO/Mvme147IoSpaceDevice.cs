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
