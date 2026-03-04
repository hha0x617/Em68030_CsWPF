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

    /// <summary>Maximum instructions per block.</summary>
    private const int MaxBlockLength = 64;

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
            else if (kind == InsnKind.Nop)
            {
                needsFlags[i] = false; // NOP doesn't touch flags
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

        var del = (Func<MC68030, uint>)dm.CreateDelegate(typeof(Func<MC68030, uint>));
        return new CompiledBlock(startPhysAddr, opcodes.Count, byteLen, del);
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

            case 0x2: // MOVE.L — check Dn→Dm: dstMode=000, srcMode=000
            {
                int srcMode = (opcode >> 3) & 7;
                int dstMode = (opcode >> 6) & 7;
                if (srcMode == 0 && dstMode == 0)
                    return InsnKind.MoveLDnDm;
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

            case 0xC: // AND: 1100 ddd ooo mmm rrr
            {
                int opMode = (opcode >> 6) & 7;
                int eaMode = (opcode >> 3) & 7;
                // AND.L EA,Dn: opMode=010 (=2), EA=Dn (eaMode=0)
                if (opMode == 2 && eaMode == 0)
                    return InsnKind.AndLDnDm;
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
}
