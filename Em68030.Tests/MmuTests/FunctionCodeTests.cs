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

using Em68030.Core;
using Em68030.Tests.Helpers;
using Xunit;

namespace Em68030.Tests.MmuTests;

/// <summary>
/// FC (Function Code) ベースのルートポインタ選択テスト。
/// FC2=1 (FC 4-7) → SRP (SRE有効時), FC2=0 (FC 0-3) → CRP
/// </summary>
public class FunctionCodeTests
{
    [Fact]
    public void FC_UserData_UsesCRP()
    {
        var mem = new Memory(16 * 1024 * 1024);
        var mmu = new Mmu(mem);

        // Setup separate CRP and SRP with different table bases
        uint crpTableBase = 0x00100000;
        uint srpTableBase = 0x00200000;

        mmu.TC = 0x80C04400; // Enable, PS=12, TIA=4, TIB=4

        // CRP: DT=2, table at crpTableBase
        mmu.CRP = (2UL << 32) | crpTableBase;
        // SRP: DT=2, table at srpTableBase
        mmu.SRP = (2UL << 32) | srpTableBase;

        // Setup page table in CRP tree: VA 0x10000000 → PA 0x01000000
        uint crpLevelAEntry = crpTableBase + (1 * 4); // Level A index 1
        uint crpLevelBTable = 0x00110000;
        mem.WriteLong(crpLevelAEntry, (crpLevelBTable & 0xFFFFFFF0) | 0x02);
        mem.WriteLong(crpLevelBTable, (0x01000000 & 0xFF000000) | 0x01); // page desc

        mmu.FlushAll();

        // FC=1 (user data) → FC2=0 → uses CRP
        uint pa = mmu.Translate(0x10000000, false, false, 1);
        Assert.Equal(0x01000000u, pa);
    }

    [Fact]
    public void FC_SupervisorData_UsesSRP_WhenSREEnabled()
    {
        var mem = new Memory(16 * 1024 * 1024);
        var mmu = new Mmu(mem);

        // TC with SRE=1 (bit 25)
        mmu.TC = 0x82C04400; // Enable + SRE + PS=12 + TIA=4 + TIB=4

        uint crpTableBase = 0x00100000;
        uint srpTableBase = 0x00200000;

        mmu.CRP = (2UL << 32) | crpTableBase;
        mmu.SRP = (2UL << 32) | srpTableBase;

        // Setup page table ONLY in SRP tree
        uint srpLevelAEntry = srpTableBase + (1 * 4);
        uint srpLevelBTable = 0x00210000;
        mem.WriteLong(srpLevelAEntry, (srpLevelBTable & 0xFFFFFFF0) | 0x02);
        mem.WriteLong(srpLevelBTable, (0x05000000 & 0xFF000000) | 0x01);

        mmu.FlushAll();

        // FC=5 (supervisor data), SRE=1 → uses SRP
        uint pa = mmu.Translate(0x10000000, true, false, 5);
        Assert.Equal(0x05000000u, pa);
    }

    [Fact]
    public void FC_SupervisorData_UsesCRP_WhenSREDisabled()
    {
        var mem = new Memory(16 * 1024 * 1024);
        var mmu = new Mmu(mem);

        // TC without SRE (bit 25 = 0)
        mmu.TC = 0x80C04400; // Enable, PS=12, TIA=4, TIB=4, SRE=0

        uint crpTableBase = 0x00100000;

        mmu.CRP = (2UL << 32) | crpTableBase;

        // Setup page table in CRP tree
        uint crpLevelAEntry = crpTableBase + (1 * 4);
        uint crpLevelBTable = 0x00110000;
        mem.WriteLong(crpLevelAEntry, (crpLevelBTable & 0xFFFFFFF0) | 0x02);
        mem.WriteLong(crpLevelBTable, (0x01000000 & 0xFF000000) | 0x01);

        mmu.FlushAll();

        // FC=5 (supervisor data), SRE=0 → uses CRP (not SRP)
        uint pa = mmu.Translate(0x10000000, true, false, 5);
        Assert.Equal(0x01000000u, pa);
    }

    [Fact]
    public void FC_UserInSupervisorMode_StillUsesCRP()
    {
        // MOVES instruction: CPU is in supervisor mode but FC override = 1 (user data)
        var mem = new Memory(16 * 1024 * 1024);
        var cpu = new MC68030(mem);

        cpu.SR = 0x2700; // Supervisor mode

        // TC with SRE=1
        cpu.Mmu.TC = 0x82C04400;

        uint crpTableBase = 0x00100000;
        uint srpTableBase = 0x00200000;

        cpu.Mmu.CRP = (2UL << 32) | crpTableBase;
        cpu.Mmu.SRP = (2UL << 32) | srpTableBase;

        // Setup page table in CRP tree only
        uint crpLevelAEntry = crpTableBase + (1 * 4);
        uint crpLevelBTable = 0x00110000;
        mem.WriteLong(crpLevelAEntry, (crpLevelBTable & 0xFFFFFFF0) | 0x02);
        mem.WriteLong(crpLevelBTable, (0x01000000 & 0xFF000000) | 0x01);

        cpu.Mmu.FlushAll();

        // Simulate MOVES: FC override = 1 (user data)
        // The MMU's Translate uses FC, not CPU mode, for root pointer selection
        // FC=1 → FC2=0 → CRP
        uint pa = cpu.Mmu.Translate(0x10000000, true, false, 1);
        Assert.Equal(0x01000000u, pa);
    }
}
