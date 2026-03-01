using Em68030.Core;
using Em68030.Tests.Helpers;
using Xunit;

namespace Em68030.Tests.CpuTests;

/// <summary>
/// レジスタスナップショットのリグレッションテスト。
/// ExecuteNextFast の即時スナップショットが、プリデクリメントアドレッシングの
/// バスエラー時にレジスタを正しく復元することを検証する。
///
/// 背景: 遅延スナップショット最適化では、EnsureRegSnapshot() を最初のメモリ
/// アクセス時に実行していた。しかし -(An) アドレッシングは ResolveAddress 内で
/// A[n] を変更した後にメモリ書き込みを行うため、スナップショットが変更後の
/// レジスタ値をキャプチャし、バスエラー時の復元が不正になっていた。
/// </summary>
public class RegisterSnapshotTests
{
    private CpuTestFixture CreateFixtureWithBusErrorHandler()
    {
        var f = new CpuTestFixture();

        // Bus error handler at 0x00002000 — just an RTE
        uint handlerAddr = 0x00002000;
        f.Memory.WriteWord(handlerAddr, 0x4E73); // RTE
        f.Cpu.VBR = 0x00000000;
        f.Memory.WriteLong(0x00000008, handlerAddr); // Vector 2 = Bus Error

        return f;
    }

    /// <summary>
    /// MOVE.L D0,-(A0) で A[0] がメモリ境界を超える場合のバスエラー復元テスト。
    /// CpuTestFixture は 16MB RAM (0x00000000-0x00FFFFFF) を提供する。
    /// A[0] = 0x01000004 → pre-dec → write to 0x01000000 (unmapped) → bus error。
    /// 復元後の A[0] は元の 0x01000004 でなければならない。
    /// </summary>
    [Fact]
    public void MovePreDec_BusError_RestoresRegisters()
    {
        var f = CreateFixtureWithBusErrorHandler();

        // Place MOVE.L D0, -(A0) at PC
        // Opcode: 0010 000 100 000 000 = 0x2100
        f.Cpu.PC = 0x00001000;
        f.Memory.WriteWord(0x00001000, 0x2100);

        f.Cpu.D[0] = 0xDEADBEEF;
        f.Cpu.A[0] = 0x01000004; // Just past 16MB — pre-dec by 4 → 0x01000000 (unmapped)

        uint originalA0 = f.Cpu.A[0];
        uint originalA7 = f.Cpu.A[7];

        // Use ExecuteNextFast + HandleBusError (the path used by the emulation loop)
        try
        {
            f.Cpu.ExecuteNextFast();
            Assert.Fail("Expected BusErrorException");
        }
        catch (BusErrorException ex)
        {
            f.Cpu.HandleBusError(ex);
        }

        Assert.False(f.Cpu.Halted, "CPU should not double-fault");

        // A[0] must be restored to pre-instruction value (not the decremented value)
        Assert.Equal(originalA0, f.Cpu.A[0]);

        // The bus error frame (32 bytes) should be pushed from the correct A[7]
        Assert.Equal(originalA7 - 32, f.Cpu.A[7]);
    }

    /// <summary>
    /// MOVE.W D1,-(A2) — word サイズのプリデクリメントでも同じ復元を検証。
    /// </summary>
    [Fact]
    public void MoveWordPreDec_BusError_RestoresRegisters()
    {
        var f = CreateFixtureWithBusErrorHandler();

        // MOVE.W D1, -(A2)
        // Opcode: 0011 010 100 000 001 = 0x3501
        f.Cpu.PC = 0x00001000;
        f.Memory.WriteWord(0x00001000, 0x3501);

        f.Cpu.D[1] = 0x12345678;
        f.Cpu.A[2] = 0x01000002; // pre-dec by 2 → 0x01000000 (unmapped)

        uint originalA2 = f.Cpu.A[2];

        try
        {
            f.Cpu.ExecuteNextFast();
            Assert.Fail("Expected BusErrorException");
        }
        catch (BusErrorException ex)
        {
            f.Cpu.HandleBusError(ex);
        }

        Assert.False(f.Cpu.Halted);
        Assert.Equal(originalA2, f.Cpu.A[2]);
    }

    /// <summary>
    /// CLR.L -(A3) — CLR もプリデクリメント先がバスエラーなら復元が必要。
    /// </summary>
    [Fact]
    public void ClrPreDec_BusError_RestoresRegisters()
    {
        var f = CreateFixtureWithBusErrorHandler();

        // CLR.L -(A3)
        // 0100 0010 10 100 011 = 0x42A3
        f.Cpu.PC = 0x00001000;
        f.Memory.WriteWord(0x00001000, 0x42A3);

        f.Cpu.A[3] = 0x01000004; // pre-dec by 4 → 0x01000000 (unmapped)

        uint originalA3 = f.Cpu.A[3];

        try
        {
            f.Cpu.ExecuteNextFast();
            Assert.Fail("Expected BusErrorException");
        }
        catch (BusErrorException ex)
        {
            f.Cpu.HandleBusError(ex);
        }

        Assert.False(f.Cpu.Halted);
        Assert.Equal(originalA3, f.Cpu.A[3]);
    }

    /// <summary>
    /// オペコードフェッチ時のバスエラーで、前の命令のレジスタ変更が巻き戻されないことを検証。
    ///
    /// 背景: 遅延スナップショット最適化では、ExecuteNextFast が _regSnapshotNeeded = true を
    /// 設定し、グループデコーダーの先頭で EnsureRegSnapshot() を呼ぶ。しかし ExecuteNext() の
    /// FetchWord() でバスエラーが発生すると、EnsureRegSnapshot は呼ばれず _savedA/_savedD は
    /// 前の命令のスナップショット値のまま。HandleBusError がこの古い値で復元すると、
    /// 前の命令のレジスタ変更が不正に巻き戻される。
    /// </summary>
    [Fact]
    public void OpcodeFetchBusError_DoesNotRevertDataRegister()
    {
        var f = CreateFixtureWithBusErrorHandler();

        // Place ADDQ.L #1, D0 at last word in mapped memory (0x00FFFFFE)
        // After fetch, PC = 0x01000000; next fetch is unmapped → bus error
        f.Memory.WriteWord(0x00FFFFFE, 0x5280); // ADDQ.L #1, D0

        f.Cpu.D[0] = 0x00000042;
        f.Cpu.PC = 0x00FFFFFE;

        // Execute ADDQ — succeeds, D[0] becomes 0x43
        f.Cpu.ExecuteNextFast();
        Assert.Equal(0x00000043u, f.Cpu.D[0]);

        // Next ExecuteNextFast: opcode fetch at 0x01000000 → bus error
        uint d0AfterAddq = f.Cpu.D[0]; // 0x43

        try
        {
            f.Cpu.ExecuteNextFast();
            Assert.Fail("Expected BusErrorException from unmapped fetch");
        }
        catch (BusErrorException ex)
        {
            f.Cpu.HandleBusError(ex);
        }

        Assert.False(f.Cpu.Halted, "CPU should not double-fault");
        // D[0] must be 0x43 (post-ADDQ), NOT 0x42 (stale snapshot)
        Assert.Equal(d0AfterAddq, f.Cpu.D[0]);
    }

    /// <summary>
    /// アドレスレジスタ版: ADDQ.L #4,A1 → フェッチバスエラー → A[1] が巻き戻されないことを検証
    /// </summary>
    [Fact]
    public void OpcodeFetchBusError_DoesNotRevertAddrRegister()
    {
        var f = CreateFixtureWithBusErrorHandler();

        // ADDQ.L #4, A1: 0x5889
        f.Memory.WriteWord(0x00FFFFFE, 0x5889);

        f.Cpu.A[1] = 0x00010000;
        f.Cpu.PC = 0x00FFFFFE;

        // Execute ADDQ.L #4, A1 — succeeds, A[1] becomes 0x00010004
        f.Cpu.ExecuteNextFast();
        Assert.Equal(0x00010004u, f.Cpu.A[1]);

        uint a1AfterAddq = f.Cpu.A[1];

        try
        {
            f.Cpu.ExecuteNextFast();
            Assert.Fail("Expected BusErrorException from unmapped fetch");
        }
        catch (BusErrorException ex)
        {
            f.Cpu.HandleBusError(ex);
        }

        Assert.False(f.Cpu.Halted);
        Assert.Equal(a1AfterAddq, f.Cpu.A[1]);
    }

    /// <summary>
    /// D レジスタのスナップショットも検証。
    /// ADDQ.L #1,D0 → MOVE.L D0,-(A0) のシーケンスで、
    /// D[0] がバスエラー後に ADDQ 後の値 (MOVE 前の値) に戻ることを確認。
    /// </summary>
    [Fact]
    public void TwoInstructions_BusErrorOnSecond_RestoresAllRegs()
    {
        var f = CreateFixtureWithBusErrorHandler();

        f.Cpu.PC = 0x00001000;

        // ADDQ.L #1, D0: 0101 000 0 10 000 000 = 0x5280
        f.Memory.WriteWord(0x00001000, 0x5280);

        // MOVE.L D0, -(A0): 0x2100
        f.Memory.WriteWord(0x00001002, 0x2100);

        f.Cpu.D[0] = 0x00000042;
        f.Cpu.A[0] = 0x01000004;

        // Execute first instruction (ADDQ) — should succeed
        f.Cpu.ExecuteNextFast();
        Assert.Equal(0x00000043u, f.Cpu.D[0]); // D0 incremented

        // Execute second instruction (MOVE with bus error)
        uint d0BeforeMove = f.Cpu.D[0]; // 0x43
        uint a0BeforeMove = f.Cpu.A[0]; // 0x01000004

        try
        {
            f.Cpu.ExecuteNextFast();
            Assert.Fail("Expected BusErrorException");
        }
        catch (BusErrorException ex)
        {
            f.Cpu.HandleBusError(ex);
        }

        Assert.False(f.Cpu.Halted);

        // Registers should be restored to state BEFORE the MOVE (after ADDQ)
        Assert.Equal(d0BeforeMove, f.Cpu.D[0]);
        Assert.Equal(a0BeforeMove, f.Cpu.A[0]);
    }
}
