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
using Xunit;

namespace Em68030.Tests.CpuTests;

/// <summary>
/// データページキャッシュの FC (Function Code) 分離テスト。
///
/// 背景: MC68030 エミュレータのデータアクセス用ページキャッシュは、
/// VA → PA のマッピングを 1 エントリだけキャッシュする。
/// MOVES 命令は FunctionCodeOverride を設定して、スーパーバイザーモードでも
/// ユーザー空間 (FC=1) にアクセスする。SRE=1 の場合、FC に応じて CRP と
/// SRP を使い分けるため、同じ VA でも異なる PA にマッピングされる。
/// キャッシュが FC を無視すると、スーパーバイザーの変換結果を
/// ユーザー空間アクセスに返してしまう。
/// </summary>
public class DataPageCacheTests
{
    // Page table base addresses
    private const uint CrpLevelABase = 0x00100000;
    private const uint CrpLevelBBase = 0x00101000;
    private const uint SrpLevelABase = 0x00200000;
    private const uint SrpLevelBBase = 0x00201000;

    // Physical addresses for user and supervisor data
    private const uint UserDataPA  = 0x01000000;
    private const uint SuperDataPA = 0x02000000;
    private const uint TestVA      = 0x10000000;

    private (Memory mem, MC68030 cpu) CreateFixture()
    {
        // 64MB to accommodate user PA (0x01000000) and super PA (0x02000000)
        var mem = new Memory(64 * 1024 * 1024);
        var cpu = new MC68030(mem);

        cpu.SR = 0x2700; // Supervisor mode
        cpu.A[7] = 0x00800000;
        cpu.SSP = 0x00800000;

        // TC: Enable=1, SRE=1, PS=12(4KB), IS=0, TIA=4, TIB=4
        // 1000 0010 1100 0000 0100 0100 0000 0000 = 0x82C04400
        cpu.Mmu.TC = 0x82C04400;

        // --- CRP page tables (user space: FC bit 2 = 0) ---
        cpu.Mmu.CRP = (2UL << 32) | CrpLevelABase;

        // Level A entry 1 → Level B table
        mem.WriteLong(CrpLevelABase + 1 * 4, (CrpLevelBBase & 0xFFFFFFF0) | 0x02);
        // Level B entry 0 → page descriptor at UserDataPA
        mem.WriteLong(CrpLevelBBase, (UserDataPA & 0xFF000000) | 0x01);

        // Also map VA 0x00000000 identity (for code/vectors)
        uint crpLevelB0 = CrpLevelBBase + 0x40;
        mem.WriteLong(CrpLevelABase + 0 * 4, (crpLevelB0 & 0xFFFFFFF0) | 0x02);
        mem.WriteLong(crpLevelB0, (0x00000000u & 0xFF000000) | 0x01);

        // --- SRP page tables (supervisor space: FC bit 2 = 1) ---
        cpu.Mmu.SRP = (2UL << 32) | SrpLevelABase;

        // Level A entry 1 → Level B table
        mem.WriteLong(SrpLevelABase + 1 * 4, (SrpLevelBBase & 0xFFFFFFF0) | 0x02);
        // Level B entry 0 → page descriptor at SuperDataPA
        mem.WriteLong(SrpLevelBBase, (SuperDataPA & 0xFF000000) | 0x01);

        // Also map VA 0x00000000 identity
        uint srpLevelB0 = SrpLevelBBase + 0x40;
        mem.WriteLong(SrpLevelABase + 0 * 4, (srpLevelB0 & 0xFFFFFFF0) | 0x02);
        mem.WriteLong(srpLevelB0, (0x00000000u & 0xFF000000) | 0x01);

        // Flush ATC to start clean
        cpu.Mmu.FlushAll();

        // Write distinguishable data at each physical address
        mem.WriteByte(UserDataPA,  0xAA);
        mem.WriteByte(SuperDataPA, 0x55);
        mem.WriteWord(UserDataPA,  0xAABB);
        mem.WriteWord(SuperDataPA, 0x5566);
        mem.WriteLong(UserDataPA,  0xAABBCCDD);
        mem.WriteLong(SuperDataPA, 0x55667788);

        return (mem, cpu);
    }

    /// <summary>
    /// スーパーバイザー読み込み後に FunctionCodeOverride で
    /// ユーザー空間を読むと、正しい PA にマッピングされることを確認。
    /// </summary>
    [Fact]
    public void ReadByte_CacheBypassedWhenFCOverridden()
    {
        var (mem, cpu) = CreateFixture();

        // Supervisor read (FC=5) → should get 0x55 from SuperDataPA
        byte superVal = cpu.ReadByte(TestVA);
        Assert.Equal(0x55, superVal);

        // Override FC to user data (FC=1)
        // MOVES decoder calls InvalidateDataCache() before setting FunctionCodeOverride
        cpu.InvalidateDataCache();
        cpu.FunctionCodeOverride = 1;
        byte userVal = cpu.ReadByte(TestVA);
        cpu.FunctionCodeOverride = -1;

        // Must get 0xAA from UserDataPA, NOT 0x55 from cache
        Assert.Equal(0xAA, userVal);
    }

    [Fact]
    public void ReadWord_CacheBypassedWhenFCOverridden()
    {
        var (mem, cpu) = CreateFixture();

        ushort superVal = cpu.ReadWord(TestVA);
        Assert.Equal((ushort)0x5566, superVal);

        cpu.InvalidateDataCache();
        cpu.FunctionCodeOverride = 1;
        ushort userVal = cpu.ReadWord(TestVA);
        cpu.FunctionCodeOverride = -1;

        Assert.Equal((ushort)0xAABB, userVal);
    }

    [Fact]
    public void ReadLong_CacheBypassedWhenFCOverridden()
    {
        var (mem, cpu) = CreateFixture();

        uint superVal = cpu.ReadLong(TestVA);
        Assert.Equal(0x55667788u, superVal);

        cpu.InvalidateDataCache();
        cpu.FunctionCodeOverride = 1;
        uint userVal = cpu.ReadLong(TestVA);
        cpu.FunctionCodeOverride = -1;

        Assert.Equal(0xAABBCCDDu, userVal);
    }

    /// <summary>
    /// FC オーバーライドが無い場合は、キャッシュが正常に機能することを確認。
    /// </summary>
    [Fact]
    public void ReadByte_CacheWorksNormally()
    {
        var (_, cpu) = CreateFixture();

        // First read populates cache
        byte val1 = cpu.ReadByte(TestVA);
        Assert.Equal(0x55, val1);

        // Second read should hit cache and return same value
        byte val2 = cpu.ReadByte(TestVA);
        Assert.Equal(0x55, val2);
    }

    /// <summary>
    /// MOVES 命令実行テスト。
    /// MOVES.B (A0),D0 で SFC=1 (ユーザーデータ) を使い、
    /// ユーザー空間からデータを読むことを検証。
    /// </summary>
    [Fact]
    public void MovesInstruction_ReadsFromUserSpace()
    {
        var (mem, cpu) = CreateFixture();

        // Set up vectors for safety
        cpu.VBR = 0x00000000;
        mem.WriteLong(0x00000008, 0x00002000); // Bus error handler
        mem.WriteWord(0x00002000, 0x4E73);     // RTE

        // Place MOVES.B (A0), D0 at PC = 0x00001000
        // Opcode: 0000 1110 00 010 000 = 0x0E10
        // Extension: A/D=0, Rn=000(D0), dr=0 (EA→Rn): 0x0000
        cpu.PC = 0x00001000;
        mem.WriteWord(0x00001000, 0x0E10);
        mem.WriteWord(0x00001002, 0x0000);

        cpu.SFC = 1;    // Source FC = user data
        cpu.A[0] = TestVA;
        cpu.D[0] = 0;

        // Pre-populate cache with supervisor mapping
        byte supervisorByte = cpu.ReadByte(TestVA);
        Assert.Equal(0x55, supervisorByte);

        // Execute MOVES instruction
        cpu.ExecuteStep();

        // D[0] should contain user data (0xAA), not supervisor data (0x55)
        Assert.Equal(0xAAu, cpu.D[0] & 0xFF);
    }
}
