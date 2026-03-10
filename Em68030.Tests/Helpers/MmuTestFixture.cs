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

namespace Em68030.Tests.Helpers;

/// <summary>
/// MMU テスト用ページテーブル構築ヘルパー。
/// 2レベルページテーブル（TIA=4, TIB=4, PS=12(4KB)）を構築する。
///
/// TC = 0x80C04400
///   bit 31: Enable=1
///   bits 23-20: PS=0xC (12 → 4KB pages)
///   bits 15-12: TIA=4
///   bits 11-8:  TIB=4
///
/// CRP format (64-bit):
///   Upper 32 bits: DT=2 (short-format table descriptor)
///   Lower 32 bits: table base address (aligned to 16 bytes)
///
/// Page table layout:
///   Level A: 16 entries (4 bits → 16), each 4 bytes = 64 bytes
///   Level B: 16 entries per Level A entry, each 4 bytes = 64 bytes per sub-table
/// </summary>
public class MmuTestFixture
{
    public Memory Memory { get; }
    public Mmu Mmu { get; }

    // Page table base addresses in physical memory
    private const uint LevelATableBase = 0x00100000; // 1MB mark
    private const uint LevelBTablesBase = 0x00101000; // After Level A table

    // TC: Enable=1, PS=12(4KB), IS=0, TIA=4, TIB=4
    // Bit layout: E(1) SRE(0) FCL(0) PS(C=12) IS(0) TIA(4) TIB(4) TIC(0) TID(0)
    // 0x80C04400 = 1000_0000_1100_0000_0100_0100_0000_0000
    private const uint TcValue = 0x80C04400;

    public MmuTestFixture()
    {
        Memory = new Memory(16 * 1024 * 1024); // 16MB RAM
        Mmu = new Mmu(Memory);

        // Setup TC
        Mmu.TC = TcValue;

        // Setup CRP: DT=2 (short-format), table base at LevelATableBase
        // Upper long: DT=2 in bits 1-0
        // Lower long: table address
        ulong crpUpper = 2; // DT=2
        ulong crpLower = LevelATableBase;
        Mmu.CRP = (crpUpper << 32) | crpLower;

        // Initialize Level A table with zeros (invalid entries)
        for (uint i = 0; i < 16 * 4; i += 4)
        {
            Memory.WriteLong(LevelATableBase + i, 0x00000000);
        }
    }

    /// <summary>
    /// VA→PA マッピングを構築する。
    /// VA のビット構成（TC=0x80C04400 の場合）:
    ///   bits 31-28: Level A index (4 bits → 0-15)
    ///   bits 27-24: Level B index (4 bits → 0-15)
    ///   bits 23-12: Unused (PS=12 なので page offset の上位ビット)
    ///   bits 11-0:  Page offset (4KB)
    ///
    /// 実際にはIS=0, TIA=4 なので shift=32-0=32 → shift-=4 → shift=28
    ///   Level A index = (VA >> 28) & 0xF
    ///   Level B index = (VA >> 24) & 0xF
    ///   Page offset = VA & 0x00FFFFFF (PS=12 だが early termination で shift=24)
    /// </summary>
    public void SetupPageTableEntry(uint va, uint pa, bool wp = false, bool modified = false)
    {
        int levelAIndex = (int)((va >> 28) & 0xF);
        int levelBIndex = (int)((va >> 24) & 0xF);

        // Ensure Level A entry points to a valid Level B table
        uint levelAEntryAddr = LevelATableBase + (uint)(levelAIndex * 4);
        uint levelBTableAddr = LevelBTablesBase + (uint)(levelAIndex * 64); // 16 entries * 4 bytes each

        // Level A descriptor: DT=2 (short table pointer), address = Level B table base
        uint levelADesc = (levelBTableAddr & 0xFFFFFFF0) | 0x02; // DT=2
        Memory.WriteLong(levelAEntryAddr, levelADesc);

        // Level B descriptor: DT=1 (page descriptor), address = physical page
        uint levelBEntryAddr = levelBTableAddr + (uint)(levelBIndex * 4);
        // Page descriptor format for early termination at level B:
        // Physical address in upper bits (matching shift=24), DT=1
        uint pageDesc = (pa & 0xFF000000) | 0x01; // DT=1 (page descriptor)
        if (wp) pageDesc |= 0x04;        // WP bit
        if (modified) pageDesc |= 0x10;  // Modified bit
        // Used bit (0x08) is set by hardware on access

        Memory.WriteLong(levelBEntryAddr, pageDesc);
    }

    /// <summary>
    /// 指定 VA に無効ページエントリを設定する。
    /// Level A に有効なテーブルを設定し、Level B を DT=0（無効）にする。
    /// </summary>
    public void SetupInvalidPage(uint va)
    {
        int levelAIndex = (int)((va >> 28) & 0xF);
        int levelBIndex = (int)((va >> 24) & 0xF);

        uint levelAEntryAddr = LevelATableBase + (uint)(levelAIndex * 4);
        uint levelBTableAddr = LevelBTablesBase + (uint)(levelAIndex * 64);

        // Ensure Level A entry is valid
        uint levelADesc = (levelBTableAddr & 0xFFFFFFF0) | 0x02; // DT=2
        Memory.WriteLong(levelAEntryAddr, levelADesc);

        // Level B entry: DT=0 (invalid)
        uint levelBEntryAddr = levelBTableAddr + (uint)(levelBIndex * 4);
        Memory.WriteLong(levelBEntryAddr, 0x00000000); // DT=0
    }

    /// <summary>
    /// WP (Write Protected) ページを設定するショートカット。
    /// </summary>
    public void SetupWriteProtectedPage(uint va, uint pa)
    {
        SetupPageTableEntry(va, pa, wp: true, modified: false);
    }

    /// <summary>
    /// ATC をフラッシュして、次のアクセスで必ず TableWalk させる。
    /// </summary>
    public void FlushAtc()
    {
        Mmu.FlushAll();
    }
}
