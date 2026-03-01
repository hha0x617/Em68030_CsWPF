using Em68030.Core;

namespace Em68030.Tests.Helpers;

/// <summary>
/// CPU 全体テスト用セットアップ。
/// 16MB RAM、Supervisor モード、SSP 設定済みの MC68030 を提供する。
/// </summary>
public class CpuTestFixture
{
    public Memory Memory { get; }
    public MC68030 Cpu { get; }

    public CpuTestFixture()
    {
        Memory = new Memory(16 * 1024 * 1024); // 16MB
        // Write initial SSP and PC at physical address 0 (reset vectors)
        // SSP at address 0x00000000
        Memory.WriteLong(0x00000000, 0x00800000);
        // PC at address 0x00000004
        Memory.WriteLong(0x00000004, 0x00001000);

        Cpu = new MC68030(Memory);
        Cpu.SR = 0x2700; // Supervisor mode, interrupt mask 7
        Cpu.A[7] = 0x00800000; // SSP
        Cpu.SSP = 0x00800000;
    }

    /// <summary>
    /// VBR + ベクタテーブルをセットアップする。
    /// 各ベクタにハンドラアドレスを書き込む。
    /// </summary>
    public void SetupVectorTable(uint vbr, uint busErrorHandler)
    {
        Cpu.VBR = vbr;
        // Vector 2 = Bus Error
        Memory.WriteLong(vbr + 2 * 4, busErrorHandler);
    }

    /// <summary>
    /// 指定アドレスに RTE 命令 (0x4E73) を配置する。
    /// </summary>
    public void PlaceRteAt(uint address)
    {
        Memory.WriteWord(address, 0x4E73);
    }

    /// <summary>
    /// 指定アドレスに NOP 命令 (0x4E71) を配置する。
    /// </summary>
    public void PlaceNopAt(uint address)
    {
        Memory.WriteWord(address, 0x4E71);
    }
}
