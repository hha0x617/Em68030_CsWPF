using System.Reflection;
using System.Reflection.Emit;

namespace Em68030.Core.Jit;

/// <summary>
/// Scans basic blocks and compiles them to .NET IL via DynamicMethod.
/// Only handles register-only instructions (no memory access = no bus errors).
/// </summary>
public class JitCompiler
{
    // Pre-fetched reflection info for IL generation
    private static readonly FieldInfo FiD = typeof(MC68030).GetField(
        "<D>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? typeof(MC68030).GetProperty("D")!.GetMethod!.ReturnType.GetFields()[0]; // fallback

    private static readonly MethodInfo MiGetD = typeof(MC68030).GetProperty("D")!.GetMethod!;
    private static readonly MethodInfo MiGetA = typeof(MC68030).GetProperty("A")!.GetMethod!;
    private static readonly MethodInfo MiGetPC = typeof(MC68030).GetProperty("PC")!.GetMethod!;
    private static readonly MethodInfo MiSetPC = typeof(MC68030).GetProperty("PC")!.SetMethod!;
    private static readonly MethodInfo MiGetSR = typeof(MC68030).GetProperty("SR")!.GetMethod!;
    private static readonly MethodInfo MiSetSR = typeof(MC68030).GetProperty("SR")!.SetMethod!;
    private static readonly MethodInfo MiGetCCR = typeof(MC68030).GetProperty("CCR")!.GetMethod!;
    private static readonly MethodInfo MiSetCCR = typeof(MC68030).GetProperty("CCR")!.SetMethod!;
    private static readonly MethodInfo MiEvalCond = typeof(MC68030).GetMethod("EvaluateCondition")!;

    private static readonly MethodInfo MiAluAddLong = typeof(Alu).GetMethod("AddLong")!;
    private static readonly MethodInfo MiAluSubLong = typeof(Alu).GetMethod("SubLong")!;
    private static readonly MethodInfo MiAluShiftLeft =
        typeof(Alu).GetMethod("ShiftLeft", new[] { typeof(uint), typeof(int), typeof(byte) })!;
    private static readonly MethodInfo MiAluArithShiftRight =
        typeof(Alu).GetMethod("ArithShiftRight", new[] { typeof(uint), typeof(int) })!;
    private static readonly MethodInfo MiAluLogicalShiftRight =
        typeof(Alu).GetMethod("LogicalShiftRight", new[] { typeof(uint), typeof(int) })!;

    /// <summary>Maximum instructions per block.</summary>
    private const int MaxBlockLength = 64;
    /// <summary>Minimum instructions per block (skip compiling very short blocks).</summary>
    // MinBlockLength moved to MC68030.JitMinBlockLength (configurable via Settings)

    /// <summary>
    /// Try to compile a basic block starting at startPC / startPhysAddr.
    /// Returns null if the first instruction is not compilable.
    /// </summary>
    public CompiledBlock? TryCompile(MC68030 cpu, uint startPC, uint startPhysAddr)
    {
        // Phase 1: Scan forward to find the extent of the basic block.
        var opcodes = new List<ushort>();
        uint scanPA = startPhysAddr;

        for (int i = 0; i < MaxBlockLength; i++)
        {
            ushort opcode;
            try { opcode = cpu.Memory.ReadWord(scanPA); }
            catch { break; }

            var kind = Classify(opcode);
            if (kind == InsnKind.Unsupported)
                break;

            // Backward branches must NOT be included in JIT blocks.
            // The MainViewModel loop detector checks for same-PC after each ExecuteNextFast() call.
            // If a JIT block contains a backward branch to its own start, the block returns the
            // same PC every time, causing false infinite-loop detection on legitimate polling loops.
            // Leaving backward branches to the interpreter ensures PC varies per call.
            if (kind == InsnKind.Branch || kind == InsnKind.BranchAlways)
            {
                int disp8 = (sbyte)(opcode & 0xFF);
                // PC after fetch = startPC + (opcodes.Count * 2) + 2
                // target = PC_after_fetch + disp8
                uint pcAfterFetch = startPC + (uint)(opcodes.Count * 2) + 2;
                uint target = (uint)(pcAfterFetch + disp8);
                if (target <= startPC)
                    break; // Backward branch — terminate block before it (not included)

                opcodes.Add(opcode);
                scanPA += 2;
                break; // Forward branch — include and terminate
            }

            opcodes.Add(opcode);
            scanPA += 2;
        }

        if (opcodes.Count == 0)
            return null;

        // Phase 2: Dead flag elimination — find which instructions need flag updates
        var needsFlags = new bool[opcodes.Count];
        // Last instruction always needs flags (successor may read them)
        needsFlags[opcodes.Count - 1] = true;
        // Walk backwards: if an instruction overwrites all of NZVC, earlier ones don't need to set them
        bool flagsLive = true;
        for (int i = opcodes.Count - 1; i >= 0; i--)
        {
            var kind = Classify(opcodes[i]);
            if (kind == InsnKind.Branch || kind == InsnKind.BranchAlways)
            {
                // Branches read flags (Bcc) or don't touch them (BRA); flags are live before a Bcc
                needsFlags[i] = false; // branch itself doesn't set flags
                flagsLive = kind == InsnKind.Branch; // Bcc reads flags, so prior insn must set them
            }
            else if (kind == InsnKind.Nop || kind == InsnKind.AddqAn || kind == InsnKind.SubqAn
                     || kind == InsnKind.MoveaLDnAn || kind == InsnKind.MoveaLAnAm
                     || kind == InsnKind.ExgDnDm || kind == InsnKind.ExgAnAm || kind == InsnKind.ExgDnAn)
            {
                needsFlags[i] = false; // NOP/ADDQ An/SUBQ An/MOVEA/EXG don't touch flags
            }
            else
            {
                // Arithmetic/logic/move: sets NZVC
                needsFlags[i] = flagsLive;
                // These instructions overwrite NZVC, so unless they're a CMP (which is flagsOnly),
                // prior flag-setting is dead
                flagsLive = false;
            }
        }
        // But: if the very first position has flagsLive=false from the backward pass,
        // that's fine — it means a later instruction will overwrite.

        // Phase 3: Emit IL
        int byteLen = opcodes.Count * 2;
        uint blockStartPC = startPC;

        var dm = new DynamicMethod(
            $"JitBlock_{startPhysAddr:X8}",
            typeof(uint),                    // return type = next PC
            new[] { typeof(MC68030) },       // arg0 = cpu
            typeof(JitCompiler).Module,
            true);                           // skip visibility checks

        var il = dm.GetILGenerator();

        // Local variables
        var locD = il.DeclareLocal(typeof(uint[]));   // local0: cpu.D
        var locCCR = il.DeclareLocal(typeof(byte));    // local1: current CCR
        var locTmp = il.DeclareLocal(typeof(uint));    // local2: temp
        var locCcrOut = il.DeclareLocal(typeof(byte)); // local3: ccr output from Alu

        // Load cpu.D array into local
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MiGetD);
        il.Emit(OpCodes.Stloc, locD);

        // Load initial CCR into local
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MiGetCCR);
        il.Emit(OpCodes.Stloc, locCCR);

        uint pc = blockStartPC;
        for (int i = 0; i < opcodes.Count; i++)
        {
            pc += 2; // PC advances past opcode (same as FetchWord)
            ushort opcode = opcodes[i];
            var kind = Classify(opcode);

            switch (kind)
            {
                case InsnKind.Moveq:
                    EmitMoveq(il, locD, locCCR, opcode, needsFlags[i]);
                    break;
                case InsnKind.MoveLDnDm:
                    EmitMoveLDnDm(il, locD, locCCR, locTmp, opcode, needsFlags[i]);
                    break;
                case InsnKind.AddLDnDm:
                    EmitAluLDnDm(il, locD, locCCR, locTmp, locCcrOut, opcode, needsFlags[i], isAdd: true);
                    break;
                case InsnKind.SubLDnDm:
                    EmitAluLDnDm(il, locD, locCCR, locTmp, locCcrOut, opcode, needsFlags[i], isAdd: false);
                    break;
                case InsnKind.CmpLDnDm:
                    EmitCmpLDnDm(il, locD, locCCR, locTmp, locCcrOut, opcode);
                    break;
                case InsnKind.AndLDnDm:
                    EmitLogicLDnDm(il, locD, locCCR, locTmp, opcode, needsFlags[i], LogicOp.And);
                    break;
                case InsnKind.OrLDnDm:
                    EmitLogicLDnDm(il, locD, locCCR, locTmp, opcode, needsFlags[i], LogicOp.Or);
                    break;
                case InsnKind.EorLDnDm:
                    EmitLogicLDnDm(il, locD, locCCR, locTmp, opcode, needsFlags[i], LogicOp.Eor);
                    break;
                case InsnKind.AddqLDn:
                    EmitAddqSubqLDn(il, locD, locCCR, locTmp, locCcrOut, opcode, needsFlags[i], isAdd: true);
                    break;
                case InsnKind.SubqLDn:
                    EmitAddqSubqLDn(il, locD, locCCR, locTmp, locCcrOut, opcode, needsFlags[i], isAdd: false);
                    break;
                case InsnKind.AddqAn:
                    EmitAddqSubqAn(il, opcode, isAdd: true);
                    break;
                case InsnKind.SubqAn:
                    EmitAddqSubqAn(il, opcode, isAdd: false);
                    break;
                case InsnKind.ClrLDn:
                    EmitClrLDn(il, locD, locCCR, opcode, needsFlags[i]);
                    break;
                case InsnKind.TstLDn:
                    EmitTstLDn(il, locD, locCCR, opcode, needsFlags[i]);
                    break;
                case InsnKind.MoveLAnDn:
                    EmitMoveLAnDn(il, locD, locCCR, locTmp, opcode, needsFlags[i]);
                    break;
                case InsnKind.MoveaLDnAn:
                    EmitMoveaLDnAn(il, locD, opcode);
                    break;
                case InsnKind.MoveaLAnAm:
                    EmitMoveaLAnAm(il, opcode);
                    break;
                case InsnKind.AslImmLDn:
                case InsnKind.LslImmLDn:
                    EmitShiftImmLDn(il, locD, locCCR, opcode, needsFlags[i], MiAluShiftLeft, hasOldCcr: true);
                    break;
                case InsnKind.AsrImmLDn:
                    EmitShiftImmLDn(il, locD, locCCR, opcode, needsFlags[i], MiAluArithShiftRight, hasOldCcr: false);
                    break;
                case InsnKind.LsrImmLDn:
                    EmitShiftImmLDn(il, locD, locCCR, opcode, needsFlags[i], MiAluLogicalShiftRight, hasOldCcr: false);
                    break;
                case InsnKind.ExgDnDm:
                    EmitExgDnDm(il, locD, locTmp, opcode);
                    break;
                case InsnKind.ExgAnAm:
                    EmitExgAnAm(il, locTmp, opcode);
                    break;
                case InsnKind.ExgDnAn:
                    EmitExgDnAn(il, locD, locTmp, opcode);
                    break;
                case InsnKind.SwapDn:
                    EmitSwapDn(il, locD, locCCR, locTmp, opcode, needsFlags[i]);
                    break;
                case InsnKind.ExtWDn:
                    EmitExtWDn(il, locD, locCCR, locTmp, opcode, needsFlags[i]);
                    break;
                case InsnKind.ExtLDn:
                    EmitExtLDn(il, locD, locCCR, locTmp, opcode, needsFlags[i]);
                    break;
                case InsnKind.ExtbLDn:
                    EmitExtbLDn(il, locD, locCCR, locTmp, opcode, needsFlags[i]);
                    break;
                case InsnKind.NegLDn:
                    EmitNegLDn(il, locD, locCCR, locTmp, opcode, needsFlags[i]);
                    break;
                case InsnKind.NotLDn:
                    EmitNotLDn(il, locD, locCCR, locTmp, opcode, needsFlags[i]);
                    break;
                case InsnKind.BranchAlways:
                {
                    int disp8 = (sbyte)(opcode & 0xFF);
                    pc = (uint)(pc + disp8);
                    // BRA.B: just fall through to return with updated pc
                    break;
                }
                case InsnKind.Branch:
                {
                    int cond = (opcode >> 8) & 0xF;
                    int disp8 = (sbyte)(opcode & 0xFF);
                    uint targetPC = (uint)(pc + disp8);

                    // Write back CCR before evaluating condition
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, locCCR);
                    il.Emit(OpCodes.Callvirt, MiSetCCR);

                    // if (cpu.EvaluateCondition(cond)) return targetPC; else return pc;
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldc_I4, cond);
                    il.Emit(OpCodes.Callvirt, MiEvalCond);

                    var lblNotTaken = il.DefineLabel();
                    il.Emit(OpCodes.Brfalse, lblNotTaken);

                    // Taken: return targetPC
                    il.Emit(OpCodes.Ldc_I4, (int)targetPC);
                    il.Emit(OpCodes.Conv_U4);
                    il.Emit(OpCodes.Ret);

                    // Not taken: continue to return fallthrough pc
                    il.MarkLabel(lblNotTaken);
                    break;
                }
                case InsnKind.Nop:
                    // Nothing to emit
                    break;
            }
        }

        // Write back CCR to cpu (unless last instruction was Bcc which already wrote it back)
        var lastKind = Classify(opcodes[^1]);
        if (lastKind != InsnKind.Branch)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloc, locCCR);
            il.Emit(OpCodes.Callvirt, MiSetCCR);
        }

        // Return next PC
        il.Emit(OpCodes.Ldc_I4, (int)pc);
        il.Emit(OpCodes.Conv_U4);
        il.Emit(OpCodes.Ret);

        // Compute total cycles from cycle table
        int totalCycles = 0;
        foreach (var op in opcodes)
            totalCycles += InstructionDecoder.GetCycles(op);

        var del = (Func<MC68030, uint>)dm.CreateDelegate(typeof(Func<MC68030, uint>));
        return new CompiledBlock(startPhysAddr, opcodes.Count, totalCycles, byteLen, del);
    }

    // ================================================================
    // Instruction classification
    // ================================================================

    private enum InsnKind
    {
        Unsupported,
        Moveq,          // MOVEQ #imm,Dn
        MoveLDnDm,      // MOVE.L Dn,Dm
        AddLDnDm,       // ADD.L Dn,Dm (EA→Dn form)
        SubLDnDm,       // SUB.L Dn,Dm (EA→Dn form)
        CmpLDnDm,       // CMP.L Dn,Dm
        AndLDnDm,       // AND.L Dn,Dm (EA→Dn form)
        OrLDnDm,        // OR.L Dn,Dm (EA→Dn form)
        EorLDnDm,       // EOR.L Dn,Dm (Dn→EA form, EA=Dn)
        BranchAlways,   // BRA.B disp8
        Branch,         // Bcc.B disp8
        Nop,            // NOP (0x4E71)
        AddqLDn,        // ADDQ.L #imm, Dn
        SubqLDn,        // SUBQ.L #imm, Dn
        AddqAn,         // ADDQ #imm, An (no flags)
        SubqAn,         // SUBQ #imm, An (no flags)
        ClrLDn,         // CLR.L Dn (D[reg]=0, Z=1)
        TstLDn,         // TST.L Dn (test D[reg], set NZ)
        MoveLAnDn,      // MOVE.L An,Dn (D[dst]=A[src], set NZ)
        MoveaLDnAn,     // MOVEA.L Dn,An (A[dst]=D[src], no flags)
        MoveaLAnAm,     // MOVEA.L An,Am (A[dst]=A[src], no flags)
        AslImmLDn,      // ASL.L #imm, Dn
        AsrImmLDn,      // ASR.L #imm, Dn
        LslImmLDn,      // LSL.L #imm, Dn
        LsrImmLDn,      // LSR.L #imm, Dn
        ExgDnDm,        // EXG Dn,Dm
        ExgAnAm,        // EXG An,Am
        ExgDnAn,        // EXG Dn,An
        SwapDn,         // SWAP Dn
        ExtWDn,         // EXT.W Dn
        ExtLDn,         // EXT.L Dn
        ExtbLDn,        // EXTB.L Dn
        NegLDn,         // NEG.L Dn
        NotLDn,         // NOT.L Dn
    }

    private static InsnKind Classify(ushort opcode)
    {
        // NOP: 0x4E71
        if (opcode == 0x4E71)
            return InsnKind.Nop;

        int group = (opcode >> 12) & 0xF;

        switch (group)
        {
            case 0x7: // MOVEQ: 0111 rrr 0 iiiiiiii
                if ((opcode & 0x0100) == 0)
                    return InsnKind.Moveq;
                break;

            case 0x2: // MOVE.L — register-only variants
            {
                int srcMode = (opcode >> 3) & 7;
                int dstMode = (opcode >> 6) & 7;
                if (srcMode == 0 && dstMode == 0)
                    return InsnKind.MoveLDnDm;
                if (srcMode == 1 && dstMode == 0)
                    return InsnKind.MoveLAnDn;
                if (srcMode == 0 && dstMode == 1)
                    return InsnKind.MoveaLDnAn;
                if (srcMode == 1 && dstMode == 1)
                    return InsnKind.MoveaLAnAm;
                break;
            }

            case 0x4: // CLR.L Dn / TST.L Dn / SWAP / EXT / NEG / NOT
            {
                int eaMode = (opcode >> 3) & 7;
                if ((opcode & 0xFFC0) == 0x4280 && eaMode == 0)
                    return InsnKind.ClrLDn;
                if ((opcode & 0xFFC0) == 0x4A80 && eaMode == 0)
                    return InsnKind.TstLDn;
                if ((opcode & 0xFFF8) == 0x4840)
                    return InsnKind.SwapDn;
                if ((opcode & 0xFFF8) == 0x4880)
                    return InsnKind.ExtWDn;
                if ((opcode & 0xFFF8) == 0x48C0)
                    return InsnKind.ExtLDn;
                if ((opcode & 0xFFF8) == 0x49C0)
                    return InsnKind.ExtbLDn;
                if ((opcode & 0xFFC0) == 0x4480 && eaMode == 0)
                    return InsnKind.NegLDn;
                if ((opcode & 0xFFC0) == 0x4680 && eaMode == 0)
                    return InsnKind.NotLDn;
                break;
            }

            case 0xD: // ADD: 1101 ddd ooo mmm rrr
            {
                int opMode = (opcode >> 6) & 7;
                int eaMode = (opcode >> 3) & 7;
                // ADD.L EA,Dn: opMode=010 (=2), EA=Dn (eaMode=0)
                if (opMode == 2 && eaMode == 0)
                    return InsnKind.AddLDnDm;
                break;
            }

            case 0x9: // SUB: 1001 ddd ooo mmm rrr
            {
                int opMode = (opcode >> 6) & 7;
                int eaMode = (opcode >> 3) & 7;
                // SUB.L EA,Dn: opMode=010 (=2), EA=Dn (eaMode=0)
                if (opMode == 2 && eaMode == 0)
                    return InsnKind.SubLDnDm;
                break;
            }

            case 0xB: // CMP/EOR: 1011 ddd ooo mmm rrr
            {
                int opMode = (opcode >> 6) & 7;
                int eaMode = (opcode >> 3) & 7;
                // CMP.L EA,Dn: opMode=010 (=2), EA=Dn (eaMode=0)
                if (opMode == 2 && eaMode == 0)
                    return InsnKind.CmpLDnDm;
                // EOR.L Dn,EA: opMode=110 (=6), EA=Dn (eaMode=0)
                if (opMode == 6 && eaMode == 0)
                    return InsnKind.EorLDnDm;
                break;
            }

            case 0xC: // AND / EXG: 1100 ddd ooo mmm rrr
            {
                int opMode = (opcode >> 6) & 7;
                int eaMode = (opcode >> 3) & 7;
                // AND.L EA,Dn: opMode=010 (=2), EA=Dn (eaMode=0)
                if (opMode == 2 && eaMode == 0)
                    return InsnKind.AndLDnDm;
                // EXG: opMode 5 = Dn↔Dn or An↔An, opMode 6 = Dn↔An
                int mode = (opcode >> 3) & 7;
                if (opMode == 5 && mode == 0) return InsnKind.ExgDnDm;
                if (opMode == 5 && mode == 1) return InsnKind.ExgAnAm;
                if (opMode == 6 && mode == 1) return InsnKind.ExgDnAn;
                break;
            }

            case 0x8: // OR: 1000 ddd ooo mmm rrr
            {
                int opMode = (opcode >> 6) & 7;
                int eaMode = (opcode >> 3) & 7;
                // OR.L EA,Dn: opMode=010 (=2), EA=Dn (eaMode=0)
                if (opMode == 2 && eaMode == 0)
                    return InsnKind.OrLDnDm;
                break;
            }

            case 0x5: // ADDQ / SUBQ: 0101 qqq s ss mmm rrr
            {
                int size = (opcode >> 6) & 3;
                if (size == 3) break; // size==3 is Scc/DBcc
                int eaMode = (opcode >> 3) & 7;
                bool isSub = (opcode & 0x0100) != 0;
                if (eaMode == 0 && size == 2) // Dn, .L only
                    return isSub ? InsnKind.SubqLDn : InsnKind.AddqLDn;
                if (eaMode == 1) // An, any size (always 32-bit)
                    return isSub ? InsnKind.SubqAn : InsnKind.AddqAn;
                break;
            }

            case 0xE: // Shift: 1110 ccc d ss ir tt rrr
            {
                int sizeField = (opcode >> 6) & 3;
                if (sizeField != 2) break;        // .L only
                if ((opcode & 0x0020) != 0) break; // immediate count only (ir=0)
                int shiftType = (opcode >> 3) & 3;
                if (shiftType > 1) break;         // ASL/ASR, LSL/LSR only
                bool isLeft = (opcode & 0x0100) != 0;
                if (shiftType == 0)
                    return isLeft ? InsnKind.AslImmLDn : InsnKind.AsrImmLDn;
                else
                    return isLeft ? InsnKind.LslImmLDn : InsnKind.LsrImmLDn;
            }

            case 0x6: // Bcc / BRA / BSR
            {
                int cond = (opcode >> 8) & 0xF;
                int disp8 = opcode & 0xFF;
                // Only 8-bit displacement (not 0x00=16-bit ext, not 0xFF=32-bit ext)
                if (disp8 == 0x00 || disp8 == 0xFF)
                    break;
                if (cond == 1) // BSR — not supported (pushes return address)
                    break;
                if (cond == 0)
                    return InsnKind.BranchAlways;
                return InsnKind.Branch;
            }
        }

        return InsnKind.Unsupported;
    }

    // ================================================================
    // IL emission helpers
    // ================================================================

    /// <summary>MOVEQ #imm8,Dn — sign-extend 8-bit to 32-bit, store in D[reg]</summary>
    private static void EmitMoveq(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        ushort opcode, bool setFlags)
    {
        int reg = (opcode >> 9) & 7;
        int imm8 = (sbyte)(opcode & 0xFF);
        uint val = (uint)imm8;

        // D[reg] = val
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldc_I4, (int)val);
        il.Emit(OpCodes.Conv_U4);
        il.Emit(OpCodes.Stelem_I4);

        if (setFlags)
        {
            // CCR: N and Z based on val, V=0, C=0 (preserve X)
            byte ccr = 0;
            if (val == 0) ccr = 0x04;
            else if ((val & 0x80000000) != 0) ccr = 0x08;
            // CCR = (CCR & 0x10) | ccr  — preserve X bit
            il.Emit(OpCodes.Ldloc, locCCR);
            il.Emit(OpCodes.Ldc_I4, 0x10);
            il.Emit(OpCodes.And);
            il.Emit(OpCodes.Ldc_I4, (int)ccr);
            il.Emit(OpCodes.Or);
            il.Emit(OpCodes.Conv_U1);
            il.Emit(OpCodes.Stloc, locCCR);
        }
    }

    /// <summary>MOVE.L Dn,Dm — copy D[src] to D[dst], set NZ flags</summary>
    private static void EmitMoveLDnDm(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        LocalBuilder locTmp, ushort opcode, bool setFlags)
    {
        int src = opcode & 7;
        int dst = (opcode >> 9) & 7;

        // uint val = D[src]
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, src);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Stloc, locTmp);

        // D[dst] = val
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dst);
        il.Emit(OpCodes.Ldloc, locTmp);
        il.Emit(OpCodes.Stelem_I4);

        if (setFlags)
        {
            il.Emit(OpCodes.Ldloc, locTmp);
            EmitSetNZVC_FromStack(il, locCCR);
        }
    }

    private enum LogicOp { And, Or, Eor }

    /// <summary>AND.L/OR.L/EOR.L Dn,Dm</summary>
    private static void EmitLogicLDnDm(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        LocalBuilder locTmp, ushort opcode, bool setFlags, LogicOp op)
    {
        int eaReg = opcode & 7;  // source EA register
        int dnReg = (opcode >> 9) & 7; // Dn field

        // For AND/OR: opMode=2 means EA→Dn (result in Dn, EA is source)
        // For EOR: opMode=6 means Dn→EA (result in EA reg, Dn is source)
        int srcReg, dstReg;
        if (op == LogicOp.Eor)
        {
            srcReg = dnReg; dstReg = eaReg;
        }
        else
        {
            srcReg = eaReg; dstReg = dnReg;
        }

        // Load D[dstReg]
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldelem_U4);

        // Load D[srcReg]
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, srcReg);
        il.Emit(OpCodes.Ldelem_U4);

        // Apply operation
        switch (op)
        {
            case LogicOp.And: il.Emit(OpCodes.And); break;
            case LogicOp.Or:  il.Emit(OpCodes.Or); break;
            case LogicOp.Eor: il.Emit(OpCodes.Xor); break;
        }

        // Store result to locTmp
        il.Emit(OpCodes.Stloc, locTmp);

        // D[dstReg] = result
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldloc, locTmp);
        il.Emit(OpCodes.Stelem_I4);

        if (setFlags)
        {
            il.Emit(OpCodes.Ldloc, locTmp);
            EmitSetNZVC_FromStack(il, locCCR);
        }
    }

    /// <summary>ADD.L/SUB.L Dn,Dm via Alu.AddLong/SubLong</summary>
    private static void EmitAluLDnDm(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        LocalBuilder locTmp, LocalBuilder locCcrOut, ushort opcode, bool setFlags, bool isAdd)
    {
        int srcReg = opcode & 7;
        int dstReg = (opcode >> 9) & 7;

        // Call Alu.AddLong(D[dst], D[src], CCR) or Alu.SubLong(D[dst], D[src], CCR)
        // Load D[dst]
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldelem_U4);

        // Load D[src]
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, srcReg);
        il.Emit(OpCodes.Ldelem_U4);

        // Load CCR
        il.Emit(OpCodes.Ldloc, locCCR);

        // withExtend = false
        il.Emit(OpCodes.Ldc_I4_0);

        // Call Alu method — returns ValueTuple<uint, byte>
        il.Emit(OpCodes.Call, isAdd ? MiAluAddLong : MiAluSubLong);

        // The return is a ValueTuple<uint, byte> on the stack.
        // We need to decompose it. Store the tuple in a local.
        var locTuple = il.DeclareLocal(typeof(ValueTuple<uint, byte>));
        il.Emit(OpCodes.Stloc, locTuple);

        // result = tuple.Item1
        il.Emit(OpCodes.Ldloca, locTuple);
        il.Emit(OpCodes.Ldfld, typeof(ValueTuple<uint, byte>).GetField("Item1")!);
        il.Emit(OpCodes.Stloc, locTmp);

        // D[dst] = result
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldloc, locTmp);
        il.Emit(OpCodes.Stelem_I4);

        if (setFlags)
        {
            // CCR = tuple.Item2 (Alu sets all 5 flags including X)
            il.Emit(OpCodes.Ldloca, locTuple);
            il.Emit(OpCodes.Ldfld, typeof(ValueTuple<uint, byte>).GetField("Item2")!);
            il.Emit(OpCodes.Stloc, locCCR);
        }
    }

    /// <summary>CMP.L Dn,Dm — subtract but discard result, update CCR (not X)</summary>
    private static void EmitCmpLDnDm(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        LocalBuilder locTmp, LocalBuilder locCcrOut, ushort opcode)
    {
        int srcReg = opcode & 7;
        int dstReg = (opcode >> 9) & 7;

        // Call Alu.SubLong(D[dst], D[src], CCR)
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldelem_U4);

        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, srcReg);
        il.Emit(OpCodes.Ldelem_U4);

        il.Emit(OpCodes.Ldloc, locCCR);
        il.Emit(OpCodes.Ldc_I4_0); // withExtend = false

        il.Emit(OpCodes.Call, MiAluSubLong);

        var locTuple = il.DeclareLocal(typeof(ValueTuple<uint, byte>));
        il.Emit(OpCodes.Stloc, locTuple);

        // CMP updates CCR bits 0-3 (NZVC) but NOT X (bit 4)
        // CCR = (CCR & 0x10) | (newCcr & 0x0F)
        il.Emit(OpCodes.Ldloc, locCCR);
        il.Emit(OpCodes.Ldc_I4, 0x10);
        il.Emit(OpCodes.And);

        il.Emit(OpCodes.Ldloca, locTuple);
        il.Emit(OpCodes.Ldfld, typeof(ValueTuple<uint, byte>).GetField("Item2")!);
        il.Emit(OpCodes.Ldc_I4, 0x0F);
        il.Emit(OpCodes.And);

        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Conv_U1);
        il.Emit(OpCodes.Stloc, locCCR);
    }

    /// <summary>ADDQ.L/SUBQ.L #imm,Dn via Alu.AddLong/SubLong</summary>
    private static void EmitAddqSubqLDn(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        LocalBuilder locTmp, LocalBuilder locCcrOut, ushort opcode, bool setFlags, bool isAdd)
    {
        int dstReg = opcode & 7;
        int data = (opcode >> 9) & 7;
        if (data == 0) data = 8;

        // Call Alu.AddLong(D[dst], (uint)data, CCR) or Alu.SubLong(D[dst], (uint)data, CCR)
        // Load D[dst]
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldelem_U4);

        // Load immediate
        il.Emit(OpCodes.Ldc_I4, data);
        il.Emit(OpCodes.Conv_U4);

        // Load CCR
        il.Emit(OpCodes.Ldloc, locCCR);

        // withExtend = false
        il.Emit(OpCodes.Ldc_I4_0);

        // Call Alu method — returns ValueTuple<uint, byte>
        il.Emit(OpCodes.Call, isAdd ? MiAluAddLong : MiAluSubLong);

        var locTuple = il.DeclareLocal(typeof(ValueTuple<uint, byte>));
        il.Emit(OpCodes.Stloc, locTuple);

        // result = tuple.Item1
        il.Emit(OpCodes.Ldloca, locTuple);
        il.Emit(OpCodes.Ldfld, typeof(ValueTuple<uint, byte>).GetField("Item1")!);
        il.Emit(OpCodes.Stloc, locTmp);

        // D[dst] = result
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldloc, locTmp);
        il.Emit(OpCodes.Stelem_I4);

        if (setFlags)
        {
            // CCR = tuple.Item2
            il.Emit(OpCodes.Ldloca, locTuple);
            il.Emit(OpCodes.Ldfld, typeof(ValueTuple<uint, byte>).GetField("Item2")!);
            il.Emit(OpCodes.Stloc, locCCR);
        }
    }

    /// <summary>ADDQ/SUBQ #imm,An — 32-bit add/sub, no flags affected</summary>
    private static void EmitAddqSubqAn(ILGenerator il, ushort opcode, bool isAdd)
    {
        int reg = opcode & 7;
        int data = (opcode >> 9) & 7;
        if (data == 0) data = 8;

        // Load cpu.A array
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MiGetA);

        // Duplicate array ref for store
        il.Emit(OpCodes.Dup);

        // Load A[reg]
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldelem_U4);

        // Load immediate
        il.Emit(OpCodes.Ldc_I4, data);
        il.Emit(OpCodes.Conv_U4);

        // Add or subtract
        if (isAdd)
            il.Emit(OpCodes.Add);
        else
            il.Emit(OpCodes.Sub);

        // Store A[reg] = result
        // Stack: arrayRef, result — need: arrayRef, index, result
        // We need to restructure. Let me use a temp local.
        var locRes = il.DeclareLocal(typeof(uint));
        il.Emit(OpCodes.Stloc, locRes);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldloc, locRes);
        il.Emit(OpCodes.Stelem_I4);
    }

    /// <summary>CLR.L Dn — D[reg]=0, set Z=1, N=V=C=0, preserve X</summary>
    private static void EmitClrLDn(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        ushort opcode, bool setFlags)
    {
        int reg = opcode & 7;

        // D[reg] = 0
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stelem_I4);

        if (setFlags)
        {
            // CCR = (CCR & 0x10) | 0x04
            il.Emit(OpCodes.Ldloc, locCCR);
            il.Emit(OpCodes.Ldc_I4, 0x10);
            il.Emit(OpCodes.And);
            il.Emit(OpCodes.Ldc_I4, 0x04);
            il.Emit(OpCodes.Or);
            il.Emit(OpCodes.Conv_U1);
            il.Emit(OpCodes.Stloc, locCCR);
        }
    }

    /// <summary>TST.L Dn — test D[reg], set NZ flags, V=C=0, preserve X</summary>
    private static void EmitTstLDn(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        ushort opcode, bool setFlags)
    {
        int reg = opcode & 7;

        if (setFlags)
        {
            il.Emit(OpCodes.Ldloc, locD);
            il.Emit(OpCodes.Ldc_I4, reg);
            il.Emit(OpCodes.Ldelem_U4);
            EmitSetNZVC_FromStack(il, locCCR);
        }
        // If !setFlags, TST doesn't modify any register — nothing to emit
    }

    /// <summary>MOVE.L An,Dn — D[dst]=A[src], set NZ flags</summary>
    private static void EmitMoveLAnDn(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        LocalBuilder locTmp, ushort opcode, bool setFlags)
    {
        int srcAn = opcode & 7;
        int dstDn = (opcode >> 9) & 7;

        // uint val = A[src]
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MiGetA);
        il.Emit(OpCodes.Ldc_I4, srcAn);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Stloc, locTmp);

        // D[dst] = val
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstDn);
        il.Emit(OpCodes.Ldloc, locTmp);
        il.Emit(OpCodes.Stelem_I4);

        if (setFlags)
        {
            il.Emit(OpCodes.Ldloc, locTmp);
            EmitSetNZVC_FromStack(il, locCCR);
        }
    }

    /// <summary>MOVEA.L Dn,An — A[dst]=D[src], no flags</summary>
    private static void EmitMoveaLDnAn(ILGenerator il, LocalBuilder locD, ushort opcode)
    {
        int srcDn = opcode & 7;
        int dstAn = (opcode >> 9) & 7;

        // A[dst] = D[src]
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MiGetA);
        il.Emit(OpCodes.Ldc_I4, dstAn);
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, srcDn);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Stelem_I4);
    }

    /// <summary>MOVEA.L An,Am — A[dst]=A[src], no flags</summary>
    private static void EmitMoveaLAnAm(ILGenerator il, ushort opcode)
    {
        int srcAn = opcode & 7;
        int dstAn = (opcode >> 9) & 7;

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MiGetA);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4, srcAn);
        il.Emit(OpCodes.Ldelem_U4);
        var locRes = il.DeclareLocal(typeof(uint));
        il.Emit(OpCodes.Stloc, locRes);
        il.Emit(OpCodes.Ldc_I4, dstAn);
        il.Emit(OpCodes.Ldloc, locRes);
        il.Emit(OpCodes.Stelem_I4);
    }

    /// <summary>ASL.L/ASR.L/LSL.L/LSR.L #imm,Dn via Alu shift methods</summary>
    private static void EmitShiftImmLDn(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        ushort opcode, bool setFlags, MethodInfo aluMethod, bool hasOldCcr)
    {
        int reg = opcode & 7;
        int count = (opcode >> 9) & 7;
        if (count == 0) count = 8;

        // Load D[reg] (uint)
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldelem_U4);

        // Load count (int)
        il.Emit(OpCodes.Ldc_I4, count);

        // ShiftLeft takes old ccr as third param; ArithShiftRight/LogicalShiftRight do not
        if (hasOldCcr)
            il.Emit(OpCodes.Ldloc, locCCR);

        // Call Alu method — returns ValueTuple<uint, byte>
        il.Emit(OpCodes.Call, aluMethod);

        var locTuple = il.DeclareLocal(typeof(ValueTuple<uint, byte>));
        il.Emit(OpCodes.Stloc, locTuple);

        // result = tuple.Item1
        var locTmp = il.DeclareLocal(typeof(uint));
        il.Emit(OpCodes.Ldloca, locTuple);
        il.Emit(OpCodes.Ldfld, typeof(ValueTuple<uint, byte>).GetField("Item1")!);
        il.Emit(OpCodes.Stloc, locTmp);

        // D[reg] = result
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldloc, locTmp);
        il.Emit(OpCodes.Stelem_I4);

        if (setFlags)
        {
            // CCR = tuple.Item2
            il.Emit(OpCodes.Ldloca, locTuple);
            il.Emit(OpCodes.Ldfld, typeof(ValueTuple<uint, byte>).GetField("Item2")!);
            il.Emit(OpCodes.Stloc, locCCR);
        }
    }

    /// <summary>EXG Dn,Dm — swap D[src] and D[dst]</summary>
    private static void EmitExgDnDm(ILGenerator il, LocalBuilder locD, LocalBuilder locTmp, ushort opcode)
    {
        int srcReg = (opcode >> 9) & 7;
        int dstReg = opcode & 7;

        // tmp = D[src]
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, srcReg);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Stloc, locTmp);

        // D[src] = D[dst]
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, srcReg);
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Stelem_I4);

        // D[dst] = tmp
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldloc, locTmp);
        il.Emit(OpCodes.Stelem_I4);
    }

    /// <summary>EXG An,Am — swap A[src] and A[dst]</summary>
    private static void EmitExgAnAm(ILGenerator il, LocalBuilder locTmp, ushort opcode)
    {
        int srcReg = (opcode >> 9) & 7;
        int dstReg = opcode & 7;

        // tmp = A[src]
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MiGetA);
        il.Emit(OpCodes.Ldc_I4, srcReg);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Stloc, locTmp);

        // A[src] = A[dst]
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MiGetA);
        il.Emit(OpCodes.Ldc_I4, srcReg);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MiGetA);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Stelem_I4);

        // A[dst] = tmp
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MiGetA);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldloc, locTmp);
        il.Emit(OpCodes.Stelem_I4);
    }

    /// <summary>EXG Dn,An — swap D[src] and A[dst]</summary>
    private static void EmitExgDnAn(ILGenerator il, LocalBuilder locD, LocalBuilder locTmp, ushort opcode)
    {
        int srcReg = (opcode >> 9) & 7; // Dn
        int dstReg = opcode & 7;        // An

        // tmp = D[src]
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, srcReg);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Stloc, locTmp);

        // D[src] = A[dst]
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, srcReg);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MiGetA);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Stelem_I4);

        // A[dst] = tmp
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MiGetA);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldloc, locTmp);
        il.Emit(OpCodes.Stelem_I4);
    }

    /// <summary>SWAP Dn — swap upper and lower 16 bits of D[reg]</summary>
    private static void EmitSwapDn(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        LocalBuilder locTmp, ushort opcode, bool setFlags)
    {
        int reg = opcode & 7;

        // val = D[reg]
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Stloc, locTmp);

        // D[reg] = (val >> 16) | (val << 16)
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldloc, locTmp);
        il.Emit(OpCodes.Ldc_I4, 16);
        il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Ldloc, locTmp);
        il.Emit(OpCodes.Ldc_I4, 16);
        il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Stelem_I4);

        if (setFlags)
        {
            // Load the result for flag computation
            il.Emit(OpCodes.Ldloc, locD);
            il.Emit(OpCodes.Ldc_I4, reg);
            il.Emit(OpCodes.Ldelem_U4);
            EmitSetNZVC_FromStack(il, locCCR);
        }
    }

    /// <summary>EXT.W Dn — sign-extend byte to word, upper 16 bits unchanged</summary>
    private static void EmitExtWDn(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        LocalBuilder locTmp, ushort opcode, bool setFlags)
    {
        int reg = opcode & 7;

        // val = (int16_t)(int8_t)D[reg] — sign-extend byte to word
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Conv_I1);   // truncate to signed byte
        il.Emit(OpCodes.Conv_I2);   // sign-extend to int16
        il.Emit(OpCodes.Conv_U2);   // make unsigned for masking
        il.Emit(OpCodes.Stloc, locTmp); // locTmp = sign-extended word (as uint16)

        // D[reg] = (D[reg] & 0xFFFF0000) | locTmp
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)0xFFFF0000));
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldloc, locTmp);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Stelem_I4);

        if (setFlags)
        {
            // Flags based on 16-bit result
            il.Emit(OpCodes.Ldloc, locTmp);
            EmitSetNZVC_FromStack_Word(il, locCCR);
        }
    }

    /// <summary>EXT.L Dn — sign-extend word to long</summary>
    private static void EmitExtLDn(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        LocalBuilder locTmp, ushort opcode, bool setFlags)
    {
        int reg = opcode & 7;

        // D[reg] = (int32_t)(int16_t)D[reg]
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Conv_I2);   // truncate to signed word
        il.Emit(OpCodes.Conv_I4);   // sign-extend to int32
        il.Emit(OpCodes.Stelem_I4);

        if (setFlags)
        {
            il.Emit(OpCodes.Ldloc, locD);
            il.Emit(OpCodes.Ldc_I4, reg);
            il.Emit(OpCodes.Ldelem_U4);
            EmitSetNZVC_FromStack(il, locCCR);
        }
    }

    /// <summary>EXTB.L Dn — sign-extend byte to long (68020+)</summary>
    private static void EmitExtbLDn(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        LocalBuilder locTmp, ushort opcode, bool setFlags)
    {
        int reg = opcode & 7;

        // D[reg] = (int32_t)(int8_t)D[reg]
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Conv_I1);   // truncate to signed byte
        il.Emit(OpCodes.Conv_I4);   // sign-extend to int32
        il.Emit(OpCodes.Stelem_I4);

        if (setFlags)
        {
            il.Emit(OpCodes.Ldloc, locD);
            il.Emit(OpCodes.Ldc_I4, reg);
            il.Emit(OpCodes.Ldelem_U4);
            EmitSetNZVC_FromStack(il, locCCR);
        }
    }

    /// <summary>NEG.L Dn — negate (0 - D[reg]), sets all 5 flags via Alu.SubLong</summary>
    private static void EmitNegLDn(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        LocalBuilder locTmp, ushort opcode, bool setFlags)
    {
        int reg = opcode & 7;

        // Call Alu.SubLong(0, D[reg], CCR, false)
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Conv_U4);

        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldelem_U4);

        il.Emit(OpCodes.Ldloc, locCCR);
        il.Emit(OpCodes.Ldc_I4_0); // withExtend = false

        il.Emit(OpCodes.Call, MiAluSubLong);

        var locTuple = il.DeclareLocal(typeof(ValueTuple<uint, byte>));
        il.Emit(OpCodes.Stloc, locTuple);

        // D[reg] = tuple.Item1
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldloca, locTuple);
        il.Emit(OpCodes.Ldfld, typeof(ValueTuple<uint, byte>).GetField("Item1")!);
        il.Emit(OpCodes.Stelem_I4);

        if (setFlags)
        {
            il.Emit(OpCodes.Ldloca, locTuple);
            il.Emit(OpCodes.Ldfld, typeof(ValueTuple<uint, byte>).GetField("Item2")!);
            il.Emit(OpCodes.Stloc, locCCR);
        }
    }

    /// <summary>NOT.L Dn — bitwise complement, sets NZ, V=0, C=0</summary>
    private static void EmitNotLDn(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        LocalBuilder locTmp, ushort opcode, bool setFlags)
    {
        int reg = opcode & 7;

        // D[reg] = ~D[reg]
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Not);
        il.Emit(OpCodes.Stelem_I4);

        if (setFlags)
        {
            il.Emit(OpCodes.Ldloc, locD);
            il.Emit(OpCodes.Ldc_I4, reg);
            il.Emit(OpCodes.Ldelem_U4);
            EmitSetNZVC_FromStack(il, locCCR);
        }
    }

    /// <summary>
    /// Helper: with a uint value on the evaluation stack, compute NZ flags (V=0, C=0),
    /// preserve X bit, and store into locCCR.
    /// Consumes the value from the stack.
    /// </summary>
    private static void EmitSetNZVC_FromStack(ILGenerator il, LocalBuilder locCCR)
    {
        // Stack has: uint val
        var locVal = il.DeclareLocal(typeof(uint));
        il.Emit(OpCodes.Stloc, locVal);

        // Start with X bit preserved
        il.Emit(OpCodes.Ldloc, locCCR);
        il.Emit(OpCodes.Ldc_I4, 0x10);
        il.Emit(OpCodes.And);
        // stack: (ccr & 0x10)

        // Check zero
        il.Emit(OpCodes.Ldloc, locVal);
        var lblNotZero = il.DefineLabel();
        var lblDone = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, lblNotZero);
        // val == 0: OR in Z flag (0x04)
        il.Emit(OpCodes.Ldc_I4, 0x04);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Br, lblDone);

        il.MarkLabel(lblNotZero);
        // Check negative
        il.Emit(OpCodes.Ldloc, locVal);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)0x80000000));
        il.Emit(OpCodes.And);
        var lblNotNeg = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, lblNotNeg);
        // N flag
        il.Emit(OpCodes.Ldc_I4, 0x08);
        il.Emit(OpCodes.Or);
        il.MarkLabel(lblNotNeg);

        il.MarkLabel(lblDone);
        il.Emit(OpCodes.Conv_U1);
        il.Emit(OpCodes.Stloc, locCCR);
    }

    /// <summary>
    /// Helper: with a uint16 value on the stack (as uint), compute NZ flags for 16-bit result.
    /// N = bit 15, Z = value == 0, V=0, C=0, preserve X.
    /// </summary>
    private static void EmitSetNZVC_FromStack_Word(ILGenerator il, LocalBuilder locCCR)
    {
        var locVal = il.DeclareLocal(typeof(uint));
        il.Emit(OpCodes.Stloc, locVal);

        // Start with X bit preserved
        il.Emit(OpCodes.Ldloc, locCCR);
        il.Emit(OpCodes.Ldc_I4, 0x10);
        il.Emit(OpCodes.And);

        // Check zero (16-bit: val & 0xFFFF == 0)
        il.Emit(OpCodes.Ldloc, locVal);
        il.Emit(OpCodes.Ldc_I4, 0xFFFF);
        il.Emit(OpCodes.And);
        var lblNotZero = il.DefineLabel();
        var lblDone = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, lblNotZero);
        il.Emit(OpCodes.Ldc_I4, 0x04);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Br, lblDone);

        il.MarkLabel(lblNotZero);
        // Check negative (bit 15)
        il.Emit(OpCodes.Ldloc, locVal);
        il.Emit(OpCodes.Ldc_I4, 0x8000);
        il.Emit(OpCodes.And);
        var lblNotNeg = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, lblNotNeg);
        il.Emit(OpCodes.Ldc_I4, 0x08);
        il.Emit(OpCodes.Or);
        il.MarkLabel(lblNotNeg);

        il.MarkLabel(lblDone);
        il.Emit(OpCodes.Conv_U1);
        il.Emit(OpCodes.Stloc, locCCR);
    }
}
