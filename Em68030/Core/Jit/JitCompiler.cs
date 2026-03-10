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

    // Phase 2: reflection for bailout support
    private static readonly FieldInfo FiJitExecutedCount =
        typeof(MC68030).GetField("_jitExecutedCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo FiJitExecutedCycles =
        typeof(MC68030).GetField("_jitExecutedCycles", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo FiJitReadResult =
        typeof(MC68030).GetField("_jitReadResult", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiTryReadLongCached =
        typeof(MC68030).GetMethod("TryReadLongCached", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Maximum instructions per block.</summary>
    private const int MaxBlockLength = 64;
    /// <summary>Minimum instructions per block (skip compiling very short blocks).</summary>
    // MinBlockLength moved to MC68030.JitMinBlockLength (configurable via Settings)

    /// <summary>
    /// Try to compile a basic block starting at startPC / startPhysAddr.
    /// Returns null if the first instruction is not compilable.
    /// </summary>
    // Scanned instruction data
    private record struct ScannedInsn(ushort Opcode, ushort ExtWord, InsnKind Kind, byte ByteLength);

    public CompiledBlock? TryCompile(MC68030 cpu, uint startPC, uint startPhysAddr)
    {
        // Phase 1: Scan forward
        var insns = new List<ScannedInsn>();
        uint scanPA = startPhysAddr;
        int totalBytes = 0;

        for (int i = 0; i < MaxBlockLength; i++)
        {
            ushort opcode;
            try { opcode = cpu.Memory.ReadWord(scanPA); }
            catch { break; }

            var kind = Classify(opcode);
            if (kind == InsnKind.Unsupported)
                break;

            ushort extWord = 0;
            byte byteLen = 2;
            bool isMultiWord = kind is InsnKind.LeaD16AnAr or InsnKind.LeaD8AnXnAr
                            or InsnKind.BranchW or InsnKind.BranchAlwaysW
                            or InsnKind.MoveLD16AnDm or InsnKind.MoveLDmD16An;
            if (isMultiWord)
            {
                try { extWord = cpu.Memory.ReadWord(scanPA + 2); }
                catch { break; }
                byteLen = 4;
                if (kind == InsnKind.LeaD8AnXnAr && (extWord & 0x0100) != 0)
                    break; // full extension word not supported
            }

            // RTS terminates the block
            if (kind == InsnKind.Rts)
            {
                insns.Add(new(opcode, extWord, kind, byteLen));
                totalBytes += byteLen;
                break;
            }

            bool isBranch = kind is InsnKind.Branch or InsnKind.BranchAlways
                         or InsnKind.BranchW or InsnKind.BranchAlwaysW;
            if (isBranch)
            {
                uint pcAfterFetch = startPC + (uint)totalBytes + 2;
                int disp = kind is InsnKind.BranchW or InsnKind.BranchAlwaysW
                    ? (short)extWord
                    : (sbyte)(opcode & 0xFF);
                uint target = (uint)(pcAfterFetch + disp);
                if (target <= startPC)
                    break;

                insns.Add(new(opcode, extWord, kind, byteLen));
                totalBytes += byteLen;
                break;
            }

            insns.Add(new(opcode, extWord, kind, byteLen));
            totalBytes += byteLen;
            scanPA += byteLen;
        }

        if (insns.Count == 0)
            return null;

        // Phase 2: Dead flag elimination
        var needsFlags = new bool[insns.Count];
        needsFlags[insns.Count - 1] = true;
        bool flagsLive = true;
        for (int i = insns.Count - 1; i >= 0; i--)
        {
            var kind = insns[i].Kind;
            if (kind is InsnKind.Branch or InsnKind.BranchAlways
                     or InsnKind.BranchW or InsnKind.BranchAlwaysW)
            {
                needsFlags[i] = false;
                flagsLive = kind is InsnKind.Branch or InsnKind.BranchW;
            }
            else if (kind is InsnKind.Nop or InsnKind.AddqAn or InsnKind.SubqAn
                     or InsnKind.MoveaLDnAn or InsnKind.MoveaLAnAm
                     or InsnKind.ExgDnDm or InsnKind.ExgAnAm or InsnKind.ExgDnAn
                     or InsnKind.LeaAnAr or InsnKind.LeaD16AnAr or InsnKind.LeaD8AnXnAr
                     or InsnKind.MoveLDmIndAn or InsnKind.MoveLDmD16An or InsnKind.Rts)
            {
                needsFlags[i] = false;
            }
            else if (kind == InsnKind.BtstDnDm)
            {
                needsFlags[i] = flagsLive;
                // BTST only changes Z — don't kill prior flag setters
            }
            else
            {
                needsFlags[i] = flagsLive;
                flagsLive = false;
            }
        }

        // Phase 3: Compute cumulative cycles, instrPCs, RegisterOnly
        int byteLen2 = totalBytes;
        uint blockStartPC = startPC;

        int totalCyclesComputed = 0;
        var cumulativeCycles = new int[insns.Count + 1];
        var instrPCs = new uint[insns.Count];
        bool registerOnly = true;
        {
            uint instrPC = startPC;
            for (int i = 0; i < insns.Count; i++)
            {
                cumulativeCycles[i] = totalCyclesComputed;
                instrPCs[i] = instrPC;
                totalCyclesComputed += InstructionDecoder.GetCycles(insns[i].Opcode);
                instrPC += insns[i].ByteLength;
                var k = insns[i].Kind;
                if (k is InsnKind.MoveLIndAnDm or InsnKind.MoveLPostIncAnDm
                    or InsnKind.MoveLDmIndAn or InsnKind.MoveLD16AnDm
                    or InsnKind.MoveLDmD16An or InsnKind.Rts)
                    registerOnly = false;
            }
            cumulativeCycles[insns.Count] = totalCyclesComputed;
        }

        // Phase 4: Emit IL
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
        var locAddr = il.DeclareLocal(typeof(uint));   // local4: memory address (Phase 2)

        // Load cpu.D array into local
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MiGetD);
        il.Emit(OpCodes.Stloc, locD);

        // Load initial CCR into local
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MiGetCCR);
        il.Emit(OpCodes.Stloc, locCCR);

        uint pc = blockStartPC;
        for (int i = 0; i < insns.Count; i++)
        {
            var insn = insns[i];
            pc += insn.ByteLength;
            ushort opcode = insn.Opcode;
            ushort extWord = insn.ExtWord;
            var kind = insn.Kind;

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
                // Phase 1A: LEA
                case InsnKind.LeaAnAr:
                    EmitLeaAnAr(il, opcode);
                    break;
                case InsnKind.LeaD16AnAr:
                    EmitLeaD16AnAr(il, opcode, extWord);
                    break;
                case InsnKind.LeaD8AnXnAr:
                    EmitLeaD8AnXnAr(il, locD, locTmp, opcode, extWord);
                    break;
                // Phase 1B: Bcc.W/BRA.W
                case InsnKind.BranchAlwaysW:
                {
                    short disp16 = (short)extWord;
                    uint pcAfterOpcode = pc - 2; // displacement relative to opcode_addr + 2
                    pc = (uint)(pcAfterOpcode + disp16);
                    // Fall through to return with updated pc (like BRA.B)
                    break;
                }
                case InsnKind.BranchW:
                {
                    int cond = (opcode >> 8) & 0xF;
                    short disp16 = (short)extWord;
                    uint pcAfterOpcode = pc - 2;
                    uint targetPC = (uint)(pcAfterOpcode + disp16);

                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, locCCR);
                    il.Emit(OpCodes.Callvirt, MiSetCCR);

                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldc_I4, cond);
                    il.Emit(OpCodes.Callvirt, MiEvalCond);

                    var lblNotTaken = il.DefineLabel();
                    il.Emit(OpCodes.Brfalse, lblNotTaken);

                    if (!registerOnly)
                        EmitSetFullExecution(il, insns.Count, totalCyclesComputed);
                    il.Emit(OpCodes.Ldc_I4, (int)targetPC);
                    il.Emit(OpCodes.Conv_U4);
                    il.Emit(OpCodes.Ret);

                    il.MarkLabel(lblNotTaken);
                    break;
                }
                // Phase 1C: MULU/MULS
                case InsnKind.MuluWDnDm:
                    EmitMuluMuls(il, locD, locCCR, locTmp, opcode, needsFlags[i], signed: false);
                    break;
                case InsnKind.MulsWDnDm:
                    EmitMuluMuls(il, locD, locCCR, locTmp, opcode, needsFlags[i], signed: true);
                    break;
                // Phase 1D: BTST
                case InsnKind.BtstDnDm:
                    EmitBtstDnDm(il, locD, locCCR, opcode, needsFlags[i]);
                    break;
                // Phase 1E: Byte/Word operations
                case InsnKind.AddBDnDm: case InsnKind.AddWDnDm:
                    EmitAluBWDnDm(il, locD, locCCR, locTmp, opcode, needsFlags[i],
                        kind == InsnKind.AddBDnDm ? 0 : 1, isAdd: true);
                    break;
                case InsnKind.SubBDnDm: case InsnKind.SubWDnDm:
                    EmitAluBWDnDm(il, locD, locCCR, locTmp, opcode, needsFlags[i],
                        kind == InsnKind.SubBDnDm ? 0 : 1, isAdd: false);
                    break;
                case InsnKind.CmpBDnDm: case InsnKind.CmpWDnDm:
                    EmitCmpBWDnDm(il, locD, locCCR, locTmp, opcode,
                        kind == InsnKind.CmpBDnDm ? 0 : 1);
                    break;
                case InsnKind.AndBDnDm: case InsnKind.AndWDnDm:
                    EmitLogicBWDnDm(il, locD, locCCR, locTmp, opcode, needsFlags[i],
                        kind == InsnKind.AndBDnDm ? 0 : 1, LogicOp.And);
                    break;
                case InsnKind.OrBDnDm: case InsnKind.OrWDnDm:
                    EmitLogicBWDnDm(il, locD, locCCR, locTmp, opcode, needsFlags[i],
                        kind == InsnKind.OrBDnDm ? 0 : 1, LogicOp.Or);
                    break;
                case InsnKind.EorBDnDm: case InsnKind.EorWDnDm:
                    EmitLogicBWDnDm(il, locD, locCCR, locTmp, opcode, needsFlags[i],
                        kind == InsnKind.EorBDnDm ? 0 : 1, LogicOp.Eor);
                    break;
                case InsnKind.AddqBDn: case InsnKind.AddqWDn:
                    EmitAddqSubqBWDn(il, locD, locCCR, locTmp, opcode, needsFlags[i],
                        kind == InsnKind.AddqBDn ? 0 : 1, isAdd: true);
                    break;
                case InsnKind.SubqBDn: case InsnKind.SubqWDn:
                    EmitAddqSubqBWDn(il, locD, locCCR, locTmp, opcode, needsFlags[i],
                        kind == InsnKind.SubqBDn ? 0 : 1, isAdd: false);
                    break;
                case InsnKind.ClrBDn: case InsnKind.ClrWDn:
                    EmitClrBWDn(il, locD, locCCR, opcode, needsFlags[i],
                        kind == InsnKind.ClrBDn ? 0 : 1);
                    break;
                case InsnKind.TstBDn: case InsnKind.TstWDn:
                    EmitTstBWDn(il, locD, locCCR, opcode, needsFlags[i],
                        kind == InsnKind.TstBDn ? 0 : 1);
                    break;
                case InsnKind.NegBDn: case InsnKind.NegWDn:
                    EmitNegBWDn(il, locD, locCCR, locTmp, opcode, needsFlags[i],
                        kind == InsnKind.NegBDn ? 0 : 1);
                    break;
                case InsnKind.NotBDn: case InsnKind.NotWDn:
                    EmitNotBWDn(il, locD, locCCR, locTmp, opcode, needsFlags[i],
                        kind == InsnKind.NotBDn ? 0 : 1);
                    break;
                case InsnKind.BranchAlways:
                {
                    int disp8 = (sbyte)(opcode & 0xFF);
                    pc = (uint)(pc + disp8);
                    break;
                }
                case InsnKind.Branch:
                {
                    int cond = (opcode >> 8) & 0xF;
                    int disp8 = (sbyte)(opcode & 0xFF);
                    uint targetPC = (uint)(pc + disp8);

                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, locCCR);
                    il.Emit(OpCodes.Callvirt, MiSetCCR);

                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldc_I4, cond);
                    il.Emit(OpCodes.Callvirt, MiEvalCond);

                    var lblNotTaken = il.DefineLabel();
                    il.Emit(OpCodes.Brfalse, lblNotTaken);

                    if (!registerOnly)
                        EmitSetFullExecution(il, insns.Count, totalCyclesComputed);
                    il.Emit(OpCodes.Ldc_I4, (int)targetPC);
                    il.Emit(OpCodes.Conv_U4);
                    il.Emit(OpCodes.Ret);

                    il.MarkLabel(lblNotTaken);
                    break;
                }
                case InsnKind.Nop:
                    break;

                // Phase 2: Memory access instructions
                case InsnKind.MoveLIndAnDm:
                {
                    int srcReg = opcode & 7;
                    int dstReg = (opcode >> 9) & 7;
                    EmitMemoryRead(il, locD, locCCR, locTmp, locAddr, srcReg, dstReg,
                        needsFlags[i], i, cumulativeCycles[i], instrPCs[i], addDisp: 0, postInc: false);
                    break;
                }
                case InsnKind.MoveLPostIncAnDm:
                {
                    int srcReg = opcode & 7;
                    int dstReg = (opcode >> 9) & 7;
                    EmitMemoryRead(il, locD, locCCR, locTmp, locAddr, srcReg, dstReg,
                        needsFlags[i], i, cumulativeCycles[i], instrPCs[i], addDisp: 0, postInc: true);
                    break;
                }
                case InsnKind.MoveLD16AnDm:
                {
                    int srcReg = opcode & 7;
                    int dstReg = (opcode >> 9) & 7;
                    short disp16 = (short)extWord;
                    EmitMemoryRead(il, locD, locCCR, locTmp, locAddr, srcReg, dstReg,
                        needsFlags[i], i, cumulativeCycles[i], instrPCs[i], addDisp: disp16, postInc: false);
                    break;
                }
                case InsnKind.MoveLDmIndAn:
                case InsnKind.MoveLDmD16An:
                {
                    // Writes always bail out (no write page cache)
                    EmitAlwaysBailout(il, locCCR, i, cumulativeCycles[i], instrPCs[i]);
                    break;
                }
                case InsnKind.Rts:
                {
                    // RTS: PC = ReadLong(A7); A7 += 4
                    EmitRts(il, locCCR, locAddr, i, cumulativeCycles[i], instrPCs[i],
                        insns.Count, totalCyclesComputed);
                    // RTS terminates block — skip normal exit below
                    goto emitDelegate;
                }
            }
        }

        // Write back CCR (unless last was Bcc which already wrote it)
        var lastKind = insns[^1].Kind;
        if (lastKind is not (InsnKind.Branch or InsnKind.BranchW))
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloc, locCCR);
            il.Emit(OpCodes.Callvirt, MiSetCCR);
        }

        // For non-register-only blocks, set bailout side-channel to full execution
        if (!registerOnly)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4, insns.Count);
            il.Emit(OpCodes.Stfld, FiJitExecutedCount);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4, totalCyclesComputed);
            il.Emit(OpCodes.Stfld, FiJitExecutedCycles);
        }

        il.Emit(OpCodes.Ldc_I4, (int)pc);
        il.Emit(OpCodes.Conv_U4);
        il.Emit(OpCodes.Ret);

    emitDelegate:
        var del = (Func<MC68030, uint>)dm.CreateDelegate(typeof(Func<MC68030, uint>));
        return new CompiledBlock(startPhysAddr, insns.Count, totalCyclesComputed, byteLen2, registerOnly, del);
    }

    // ================================================================
    // Instruction classification
    // ================================================================

    private enum InsnKind
    {
        Unsupported,
        Moveq, MoveLDnDm,
        AddLDnDm, SubLDnDm, CmpLDnDm, AndLDnDm, OrLDnDm, EorLDnDm,
        BranchAlways, Branch, Nop,
        AddqLDn, SubqLDn, AddqAn, SubqAn,
        ClrLDn, TstLDn, MoveLAnDn, MoveaLDnAn, MoveaLAnAm,
        AslImmLDn, AsrImmLDn, LslImmLDn, LsrImmLDn,
        ExgDnDm, ExgAnAm, ExgDnAn,
        SwapDn, ExtWDn, ExtLDn, ExtbLDn, NegLDn, NotLDn,
        // Phase 1A: LEA
        LeaAnAr, LeaD16AnAr, LeaD8AnXnAr,
        // Phase 1B: 16-bit displacement branches
        BranchW, BranchAlwaysW,
        // Phase 1C: Multiply
        MuluWDnDm, MulsWDnDm,
        // Phase 1D: Bit test
        BtstDnDm,
        // Phase 1E: Byte/Word variants
        AddBDnDm, AddWDnDm, SubBDnDm, SubWDnDm,
        CmpBDnDm, CmpWDnDm,
        AndBDnDm, AndWDnDm, OrBDnDm, OrWDnDm, EorBDnDm, EorWDnDm,
        AddqBDn, AddqWDn, SubqBDn, SubqWDn,
        ClrBDn, ClrWDn, TstBDn, TstWDn,
        NegBDn, NegWDn, NotBDn, NotWDn,
        // Phase 2: Memory access instructions
        MoveLIndAnDm, MoveLPostIncAnDm, MoveLDmIndAn,
        MoveLD16AnDm, MoveLDmD16An, Rts,
    }

    private static InsnKind Classify(ushort opcode)
    {
        if (opcode == 0x4E71)
            return InsnKind.Nop;
        if (opcode == 0x4E75)
            return InsnKind.Rts;

        int group = (opcode >> 12) & 0xF;

        switch (group)
        {
            case 0x0: // BTST Dn,Dm
            {
                int eaMode = (opcode >> 3) & 7;
                if ((opcode & 0x01C0) == 0x0100 && eaMode == 0)
                    return InsnKind.BtstDnDm;
                break;
            }

            case 0x7:
                if ((opcode & 0x0100) == 0)
                    return InsnKind.Moveq;
                break;

            case 0x2:
            {
                int srcMode = (opcode >> 3) & 7;
                int dstMode = (opcode >> 6) & 7;
                if (srcMode == 0 && dstMode == 0) return InsnKind.MoveLDnDm;
                if (srcMode == 1 && dstMode == 0) return InsnKind.MoveLAnDn;
                if (srcMode == 0 && dstMode == 1) return InsnKind.MoveaLDnAn;
                if (srcMode == 1 && dstMode == 1) return InsnKind.MoveaLAnAm;
                // Phase 2: Memory access MOVE.L
                if (srcMode == 2 && dstMode == 0) return InsnKind.MoveLIndAnDm;
                if (srcMode == 3 && dstMode == 0) return InsnKind.MoveLPostIncAnDm;
                if (srcMode == 0 && dstMode == 2) return InsnKind.MoveLDmIndAn;
                if (srcMode == 5 && dstMode == 0) return InsnKind.MoveLD16AnDm;
                if (srcMode == 0 && dstMode == 5) return InsnKind.MoveLDmD16An;
                break;
            }

            case 0x4:
            {
                int eaMode = (opcode >> 3) & 7;
                // LEA: 0100 rrr 111 mmm rrr
                if ((opcode & 0xF1C0) == 0x41C0)
                {
                    if (eaMode == 2) return InsnKind.LeaAnAr;
                    if (eaMode == 5) return InsnKind.LeaD16AnAr;
                    if (eaMode == 6) return InsnKind.LeaD8AnXnAr;
                    // Don't break — eaMode=0 overlaps with EXTB.L etc.
                }
                // CLR: size in bits 7-6
                if ((opcode & 0xFF00) == 0x4200 && eaMode == 0)
                {
                    int sz = (opcode >> 6) & 3;
                    if (sz == 0) return InsnKind.ClrBDn;
                    if (sz == 1) return InsnKind.ClrWDn;
                    if (sz == 2) return InsnKind.ClrLDn;
                }
                // TST: size in bits 7-6
                if ((opcode & 0xFF00) == 0x4A00 && eaMode == 0)
                {
                    int sz = (opcode >> 6) & 3;
                    if (sz == 0) return InsnKind.TstBDn;
                    if (sz == 1) return InsnKind.TstWDn;
                    if (sz == 2) return InsnKind.TstLDn;
                }
                if ((opcode & 0xFFF8) == 0x4840) return InsnKind.SwapDn;
                if ((opcode & 0xFFF8) == 0x4880) return InsnKind.ExtWDn;
                if ((opcode & 0xFFF8) == 0x48C0) return InsnKind.ExtLDn;
                if ((opcode & 0xFFF8) == 0x49C0) return InsnKind.ExtbLDn;
                // NEG: size in bits 7-6
                if ((opcode & 0xFF00) == 0x4400 && eaMode == 0)
                {
                    int sz = (opcode >> 6) & 3;
                    if (sz == 0) return InsnKind.NegBDn;
                    if (sz == 1) return InsnKind.NegWDn;
                    if (sz == 2) return InsnKind.NegLDn;
                }
                // NOT: size in bits 7-6
                if ((opcode & 0xFF00) == 0x4600 && eaMode == 0)
                {
                    int sz = (opcode >> 6) & 3;
                    if (sz == 0) return InsnKind.NotBDn;
                    if (sz == 1) return InsnKind.NotWDn;
                    if (sz == 2) return InsnKind.NotLDn;
                }
                break;
            }

            case 0xD:
            {
                int opMode = (opcode >> 6) & 7;
                int eaMode = (opcode >> 3) & 7;
                if (eaMode == 0)
                {
                    if (opMode == 0) return InsnKind.AddBDnDm;
                    if (opMode == 1) return InsnKind.AddWDnDm;
                    if (opMode == 2) return InsnKind.AddLDnDm;
                }
                break;
            }

            case 0x9:
            {
                int opMode = (opcode >> 6) & 7;
                int eaMode = (opcode >> 3) & 7;
                if (eaMode == 0)
                {
                    if (opMode == 0) return InsnKind.SubBDnDm;
                    if (opMode == 1) return InsnKind.SubWDnDm;
                    if (opMode == 2) return InsnKind.SubLDnDm;
                }
                break;
            }

            case 0xB:
            {
                int opMode = (opcode >> 6) & 7;
                int eaMode = (opcode >> 3) & 7;
                if (eaMode == 0)
                {
                    if (opMode == 0) return InsnKind.CmpBDnDm;
                    if (opMode == 1) return InsnKind.CmpWDnDm;
                    if (opMode == 2) return InsnKind.CmpLDnDm;
                    if (opMode == 4) return InsnKind.EorBDnDm;
                    if (opMode == 5) return InsnKind.EorWDnDm;
                    if (opMode == 6) return InsnKind.EorLDnDm;
                }
                break;
            }

            case 0xC:
            {
                int opMode = (opcode >> 6) & 7;
                int eaMode = (opcode >> 3) & 7;
                if (eaMode == 0)
                {
                    if (opMode == 0) return InsnKind.AndBDnDm;
                    if (opMode == 1) return InsnKind.AndWDnDm;
                    if (opMode == 2) return InsnKind.AndLDnDm;
                }
                if (opMode == 3 && eaMode == 0) return InsnKind.MuluWDnDm;
                if (opMode == 7 && eaMode == 0) return InsnKind.MulsWDnDm;
                int mode = (opcode >> 3) & 7;
                if (opMode == 5 && mode == 0) return InsnKind.ExgDnDm;
                if (opMode == 5 && mode == 1) return InsnKind.ExgAnAm;
                if (opMode == 6 && mode == 1) return InsnKind.ExgDnAn;
                break;
            }

            case 0x8:
            {
                int opMode = (opcode >> 6) & 7;
                int eaMode = (opcode >> 3) & 7;
                if (eaMode == 0)
                {
                    if (opMode == 0) return InsnKind.OrBDnDm;
                    if (opMode == 1) return InsnKind.OrWDnDm;
                    if (opMode == 2) return InsnKind.OrLDnDm;
                }
                break;
            }

            case 0x5:
            {
                int size = (opcode >> 6) & 3;
                if (size == 3) break;
                int eaMode = (opcode >> 3) & 7;
                bool isSub = (opcode & 0x0100) != 0;
                if (eaMode == 0)
                {
                    if (size == 0) return isSub ? InsnKind.SubqBDn : InsnKind.AddqBDn;
                    if (size == 1) return isSub ? InsnKind.SubqWDn : InsnKind.AddqWDn;
                    if (size == 2) return isSub ? InsnKind.SubqLDn : InsnKind.AddqLDn;
                }
                if (eaMode == 1)
                    return isSub ? InsnKind.SubqAn : InsnKind.AddqAn;
                break;
            }

            case 0xE:
            {
                int sizeField = (opcode >> 6) & 3;
                if (sizeField != 2) break;
                if ((opcode & 0x0020) != 0) break;
                int shiftType = (opcode >> 3) & 3;
                if (shiftType > 1) break;
                bool isLeft = (opcode & 0x0100) != 0;
                if (shiftType == 0)
                    return isLeft ? InsnKind.AslImmLDn : InsnKind.AsrImmLDn;
                else
                    return isLeft ? InsnKind.LslImmLDn : InsnKind.LsrImmLDn;
            }

            case 0x6:
            {
                int cond = (opcode >> 8) & 0xF;
                int disp8 = opcode & 0xFF;
                if (cond == 1) break;
                if (disp8 == 0xFF) break;
                if (disp8 == 0x00)
                {
                    if (cond == 0) return InsnKind.BranchAlwaysW;
                    return InsnKind.BranchW;
                }
                if (cond == 0) return InsnKind.BranchAlways;
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

    // ================================================================
    // Phase 1A: LEA emission helpers
    // ================================================================

    /// <summary>LEA (An),Ar — A[dst] = A[src]</summary>
    private static void EmitLeaAnAr(ILGenerator il, ushort opcode)
    {
        int src = opcode & 7;
        int dst = (opcode >> 9) & 7;
        // A[dst] = A[src]
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MiGetA);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4, src);
        il.Emit(OpCodes.Ldelem_U4);
        var locRes = il.DeclareLocal(typeof(uint));
        il.Emit(OpCodes.Stloc, locRes);
        il.Emit(OpCodes.Ldc_I4, dst);
        il.Emit(OpCodes.Ldloc, locRes);
        il.Emit(OpCodes.Stelem_I4);
    }

    /// <summary>LEA d16(An),Ar — A[dst] = A[src] + sign_ext(d16)</summary>
    private static void EmitLeaD16AnAr(ILGenerator il, ushort opcode, ushort extWord)
    {
        int src = opcode & 7;
        int dst = (opcode >> 9) & 7;
        int d16 = (short)extWord;

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MiGetA);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4, src);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Ldc_I4, d16);
        il.Emit(OpCodes.Add);
        var locRes = il.DeclareLocal(typeof(uint));
        il.Emit(OpCodes.Stloc, locRes);
        il.Emit(OpCodes.Ldc_I4, dst);
        il.Emit(OpCodes.Ldloc, locRes);
        il.Emit(OpCodes.Stelem_I4);
    }

    /// <summary>LEA d8(An,Xn),Ar — A[dst] = A[src] + Xn*scale + d8</summary>
    private static void EmitLeaD8AnXnAr(ILGenerator il, LocalBuilder locD, LocalBuilder locTmp,
        ushort opcode, ushort extWord)
    {
        int baseSrc = opcode & 7;
        int dst = (opcode >> 9) & 7;
        int d8 = (sbyte)(extWord & 0xFF);
        int indexReg = (extWord >> 12) & 7;
        bool indexIsAddr = (extWord & 0x8000) != 0;
        bool indexIsLong = (extWord & 0x0800) != 0;
        int scale = (extWord >> 9) & 3;

        // Load base: A[baseSrc]
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MiGetA);
        il.Emit(OpCodes.Ldc_I4, baseSrc);
        il.Emit(OpCodes.Ldelem_U4);

        // Load index register
        if (indexIsAddr)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, MiGetA);
            il.Emit(OpCodes.Ldc_I4, indexReg);
            il.Emit(OpCodes.Ldelem_U4);
        }
        else
        {
            il.Emit(OpCodes.Ldloc, locD);
            il.Emit(OpCodes.Ldc_I4, indexReg);
            il.Emit(OpCodes.Ldelem_U4);
        }

        // Sign-extend from word if needed
        if (!indexIsLong)
        {
            il.Emit(OpCodes.Conv_I2); // truncate to signed word
            il.Emit(OpCodes.Conv_I4); // sign-extend to int32
        }

        // Apply scale
        if (scale > 0)
        {
            il.Emit(OpCodes.Ldc_I4, scale);
            il.Emit(OpCodes.Shl);
        }

        // base + index
        il.Emit(OpCodes.Add);

        // + d8
        if (d8 != 0)
        {
            il.Emit(OpCodes.Ldc_I4, d8);
            il.Emit(OpCodes.Add);
        }

        il.Emit(OpCodes.Stloc, locTmp);

        // A[dst] = result
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MiGetA);
        il.Emit(OpCodes.Ldc_I4, dst);
        il.Emit(OpCodes.Ldloc, locTmp);
        il.Emit(OpCodes.Stelem_I4);
    }

    // ================================================================
    // Phase 1C: MULU/MULS emission
    // ================================================================

    private static void EmitMuluMuls(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        LocalBuilder locTmp, ushort opcode, bool setFlags, bool signed)
    {
        int srcReg = opcode & 7;
        int dstReg = (opcode >> 9) & 7;

        // Load D[dst] low 16 bits
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldelem_U4);
        if (signed) il.Emit(OpCodes.Conv_I2); // sign-extend to int
        else { il.Emit(OpCodes.Ldc_I4, 0xFFFF); il.Emit(OpCodes.And); }

        // Load D[src] low 16 bits
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, srcReg);
        il.Emit(OpCodes.Ldelem_U4);
        if (signed) il.Emit(OpCodes.Conv_I2);
        else { il.Emit(OpCodes.Ldc_I4, 0xFFFF); il.Emit(OpCodes.And); }

        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Stloc, locTmp);

        // D[dst] = result
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

    // ================================================================
    // Phase 1D: BTST emission
    // ================================================================

    private static void EmitBtstDnDm(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        ushort opcode, bool setFlags)
    {
        if (!setFlags) return; // BTST only affects Z flag

        int srcReg = (opcode >> 9) & 7; // bit number register
        int dstReg = opcode & 7;        // test target

        // bitNum = D[src] & 31
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, srcReg);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Ldc_I4, 31);
        il.Emit(OpCodes.And);

        // (D[dst] >> bitNum) & 1
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldelem_U4);

        // Need to swap: we want D[dst] >> bitNum, but stack has bitNum, D[dst]
        // Use temp local
        var locBitNum = il.DeclareLocal(typeof(int));
        var locTarget = il.DeclareLocal(typeof(uint));
        // Stack: bitNum, D[dst]
        il.Emit(OpCodes.Stloc, locTarget);
        il.Emit(OpCodes.Stloc, locBitNum);

        il.Emit(OpCodes.Ldloc, locTarget);
        il.Emit(OpCodes.Ldloc, locBitNum);
        il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.And);

        // If bit is set → Z=0; if clear → Z=1
        var lblBitSet = il.DefineLabel();
        var lblDone = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, lblBitSet);

        // Bit clear: set Z
        il.Emit(OpCodes.Ldloc, locCCR);
        il.Emit(OpCodes.Ldc_I4, 0x04);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Conv_U1);
        il.Emit(OpCodes.Stloc, locCCR);
        il.Emit(OpCodes.Br, lblDone);

        // Bit set: clear Z
        il.MarkLabel(lblBitSet);
        il.Emit(OpCodes.Ldloc, locCCR);
        il.Emit(OpCodes.Ldc_I4, ~0x04);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Conv_U1);
        il.Emit(OpCodes.Stloc, locCCR);

        il.MarkLabel(lblDone);
    }

    // ================================================================
    // Phase 1E: Byte/Word ALU emission helpers
    // ================================================================

    private static readonly MethodInfo MiAluAddByte = typeof(Alu).GetMethod("AddByte",
        new[] { typeof(byte), typeof(byte), typeof(byte), typeof(bool) })!;
    private static readonly MethodInfo MiAluAddWord = typeof(Alu).GetMethod("AddWord",
        new[] { typeof(ushort), typeof(ushort), typeof(byte), typeof(bool) })!;
    private static readonly MethodInfo MiAluSubByte = typeof(Alu).GetMethod("SubByte",
        new[] { typeof(byte), typeof(byte), typeof(byte), typeof(bool) })!;
    private static readonly MethodInfo MiAluSubWord = typeof(Alu).GetMethod("SubWord",
        new[] { typeof(ushort), typeof(ushort), typeof(byte), typeof(bool) })!;

    /// <summary>ADD.B/W or SUB.B/W Dn,Dm — preserves upper bits</summary>
    private static void EmitAluBWDnDm(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        LocalBuilder locTmp, ushort opcode, bool setFlags, int size, bool isAdd)
    {
        int srcReg = opcode & 7;
        int dstReg = (opcode >> 9) & 7;
        bool isByte = size == 0;
        uint mask = isByte ? 0xFFFFFF00u : 0xFFFF0000u;

        // Call Alu.AddByte/AddWord/SubByte/SubWord(D[dst], D[src], CCR, false)
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldelem_U4);
        if (isByte) il.Emit(OpCodes.Conv_U1); else il.Emit(OpCodes.Conv_U2);

        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, srcReg);
        il.Emit(OpCodes.Ldelem_U4);
        if (isByte) il.Emit(OpCodes.Conv_U1); else il.Emit(OpCodes.Conv_U2);

        il.Emit(OpCodes.Ldloc, locCCR);
        il.Emit(OpCodes.Ldc_I4_0); // withExtend = false

        var mi = isAdd ? (isByte ? MiAluAddByte : MiAluAddWord) : (isByte ? MiAluSubByte : MiAluSubWord);
        il.Emit(OpCodes.Call, mi);

        var tupleType = isByte ? typeof(ValueTuple<byte, byte>) : typeof(ValueTuple<ushort, byte>);
        var locTuple = il.DeclareLocal(tupleType);
        il.Emit(OpCodes.Stloc, locTuple);

        // D[dst] = (D[dst] & mask) | result
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)mask));
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldloca, locTuple);
        il.Emit(OpCodes.Ldfld, tupleType.GetField("Item1")!);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Stelem_I4);

        if (setFlags)
        {
            il.Emit(OpCodes.Ldloca, locTuple);
            il.Emit(OpCodes.Ldfld, tupleType.GetField("Item2")!);
            il.Emit(OpCodes.Stloc, locCCR);
        }
    }

    /// <summary>CMP.B/W Dn,Dm — compare only, no register update</summary>
    private static void EmitCmpBWDnDm(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        LocalBuilder locTmp, ushort opcode, int size)
    {
        int srcReg = opcode & 7;
        int dstReg = (opcode >> 9) & 7;
        bool isByte = size == 0;

        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldelem_U4);
        if (isByte) il.Emit(OpCodes.Conv_U1); else il.Emit(OpCodes.Conv_U2);

        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, srcReg);
        il.Emit(OpCodes.Ldelem_U4);
        if (isByte) il.Emit(OpCodes.Conv_U1); else il.Emit(OpCodes.Conv_U2);

        il.Emit(OpCodes.Ldloc, locCCR);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Call, isByte ? MiAluSubByte : MiAluSubWord);

        var tupleType = isByte ? typeof(ValueTuple<byte, byte>) : typeof(ValueTuple<ushort, byte>);
        var locTuple = il.DeclareLocal(tupleType);
        il.Emit(OpCodes.Stloc, locTuple);

        // CCR = (CCR & 0x10) | (newCcr & 0x0F)
        il.Emit(OpCodes.Ldloc, locCCR);
        il.Emit(OpCodes.Ldc_I4, 0x10);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldloca, locTuple);
        il.Emit(OpCodes.Ldfld, tupleType.GetField("Item2")!);
        il.Emit(OpCodes.Ldc_I4, 0x0F);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Conv_U1);
        il.Emit(OpCodes.Stloc, locCCR);
    }

    /// <summary>AND/OR/EOR .B/.W Dn,Dm — byte/word logic</summary>
    private static void EmitLogicBWDnDm(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        LocalBuilder locTmp, ushort opcode, bool setFlags, int size, LogicOp op)
    {
        bool isByte = size == 0;
        uint mask = isByte ? 0xFFFFFF00u : 0xFFFF0000u;
        int signBit = isByte ? 0x80 : 0x8000;
        int valMask = isByte ? 0xFF : 0xFFFF;

        int srcReg, dstReg;
        if (op == LogicOp.Eor)
        {
            srcReg = (opcode >> 9) & 7; dstReg = opcode & 7;
        }
        else
        {
            srcReg = opcode & 7; dstReg = (opcode >> 9) & 7;
        }

        // Load D[dst] low bits
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Ldc_I4, valMask);
        il.Emit(OpCodes.And);

        // Load D[src] low bits
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, srcReg);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Ldc_I4, valMask);
        il.Emit(OpCodes.And);

        switch (op)
        {
            case LogicOp.And: il.Emit(OpCodes.And); break;
            case LogicOp.Or: il.Emit(OpCodes.Or); break;
            case LogicOp.Eor: il.Emit(OpCodes.Xor); break;
        }
        il.Emit(OpCodes.Stloc, locTmp);

        // D[dst] = (D[dst] & mask) | result
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)mask));
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldloc, locTmp);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Stelem_I4);

        if (setFlags)
        {
            il.Emit(OpCodes.Ldloc, locTmp);
            if (isByte)
                EmitSetNZVC_FromStack_Byte(il, locCCR);
            else
                EmitSetNZVC_FromStack_Word(il, locCCR);
        }
    }

    /// <summary>ADDQ/SUBQ .B/.W #imm,Dn</summary>
    private static void EmitAddqSubqBWDn(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        LocalBuilder locTmp, ushort opcode, bool setFlags, int size, bool isAdd)
    {
        int dstReg = opcode & 7;
        int data = (opcode >> 9) & 7;
        if (data == 0) data = 8;
        bool isByte = size == 0;
        uint mask = isByte ? 0xFFFFFF00u : 0xFFFF0000u;

        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldelem_U4);
        if (isByte) il.Emit(OpCodes.Conv_U1); else il.Emit(OpCodes.Conv_U2);

        il.Emit(OpCodes.Ldc_I4, data);
        if (isByte) il.Emit(OpCodes.Conv_U1); else il.Emit(OpCodes.Conv_U2);

        il.Emit(OpCodes.Ldloc, locCCR);
        il.Emit(OpCodes.Ldc_I4_0);

        var mi = isAdd ? (isByte ? MiAluAddByte : MiAluAddWord) : (isByte ? MiAluSubByte : MiAluSubWord);
        il.Emit(OpCodes.Call, mi);

        var tupleType = isByte ? typeof(ValueTuple<byte, byte>) : typeof(ValueTuple<ushort, byte>);
        var locTuple = il.DeclareLocal(tupleType);
        il.Emit(OpCodes.Stloc, locTuple);

        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)mask));
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldloca, locTuple);
        il.Emit(OpCodes.Ldfld, tupleType.GetField("Item1")!);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Stelem_I4);

        if (setFlags)
        {
            il.Emit(OpCodes.Ldloca, locTuple);
            il.Emit(OpCodes.Ldfld, tupleType.GetField("Item2")!);
            il.Emit(OpCodes.Stloc, locCCR);
        }
    }

    /// <summary>CLR.B/W Dn</summary>
    private static void EmitClrBWDn(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        ushort opcode, bool setFlags, int size)
    {
        int reg = opcode & 7;
        bool isByte = size == 0;
        uint mask = isByte ? 0xFFFFFF00u : 0xFFFF0000u;

        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)mask));
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Stelem_I4);

        if (setFlags)
        {
            il.Emit(OpCodes.Ldloc, locCCR);
            il.Emit(OpCodes.Ldc_I4, 0x10);
            il.Emit(OpCodes.And);
            il.Emit(OpCodes.Ldc_I4, 0x04);
            il.Emit(OpCodes.Or);
            il.Emit(OpCodes.Conv_U1);
            il.Emit(OpCodes.Stloc, locCCR);
        }
    }

    /// <summary>TST.B/W Dn</summary>
    private static void EmitTstBWDn(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        ushort opcode, bool setFlags, int size)
    {
        if (!setFlags) return;
        int reg = opcode & 7;
        bool isByte = size == 0;

        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldelem_U4);
        if (isByte)
            EmitSetNZVC_FromStack_Byte(il, locCCR);
        else
            EmitSetNZVC_FromStack_Word(il, locCCR);
    }

    /// <summary>NEG.B/W Dn</summary>
    private static void EmitNegBWDn(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        LocalBuilder locTmp, ushort opcode, bool setFlags, int size)
    {
        int reg = opcode & 7;
        bool isByte = size == 0;
        uint mask = isByte ? 0xFFFFFF00u : 0xFFFF0000u;

        // Call Alu.SubByte/SubWord(0, D[reg], CCR, false)
        if (isByte) il.Emit(OpCodes.Ldc_I4_0); else il.Emit(OpCodes.Ldc_I4_0);
        if (isByte) il.Emit(OpCodes.Conv_U1); else il.Emit(OpCodes.Conv_U2);

        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldelem_U4);
        if (isByte) il.Emit(OpCodes.Conv_U1); else il.Emit(OpCodes.Conv_U2);

        il.Emit(OpCodes.Ldloc, locCCR);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Call, isByte ? MiAluSubByte : MiAluSubWord);

        var tupleType = isByte ? typeof(ValueTuple<byte, byte>) : typeof(ValueTuple<ushort, byte>);
        var locTuple = il.DeclareLocal(tupleType);
        il.Emit(OpCodes.Stloc, locTuple);

        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)mask));
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldloca, locTuple);
        il.Emit(OpCodes.Ldfld, tupleType.GetField("Item1")!);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Stelem_I4);

        if (setFlags)
        {
            il.Emit(OpCodes.Ldloca, locTuple);
            il.Emit(OpCodes.Ldfld, tupleType.GetField("Item2")!);
            il.Emit(OpCodes.Stloc, locCCR);
        }
    }

    /// <summary>NOT.B/W Dn</summary>
    private static void EmitNotBWDn(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        LocalBuilder locTmp, ushort opcode, bool setFlags, int size)
    {
        int reg = opcode & 7;
        bool isByte = size == 0;
        uint mask = isByte ? 0xFFFFFF00u : 0xFFFF0000u;
        int valMask = isByte ? 0xFF : 0xFFFF;

        // val = ~D[reg] & mask
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Not);
        il.Emit(OpCodes.Ldc_I4, valMask);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Stloc, locTmp);

        // D[reg] = (D[reg] & upper_mask) | val
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, reg);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)mask));
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldloc, locTmp);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Stelem_I4);

        if (setFlags)
        {
            il.Emit(OpCodes.Ldloc, locTmp);
            if (isByte)
                EmitSetNZVC_FromStack_Byte(il, locCCR);
            else
                EmitSetNZVC_FromStack_Word(il, locCCR);
        }
    }

    /// <summary>
    /// Helper: with a uint8 value on the stack (as uint), compute NZ flags for 8-bit result.
    /// </summary>
    private static void EmitSetNZVC_FromStack_Byte(ILGenerator il, LocalBuilder locCCR)
    {
        var locVal = il.DeclareLocal(typeof(uint));
        il.Emit(OpCodes.Stloc, locVal);

        il.Emit(OpCodes.Ldloc, locCCR);
        il.Emit(OpCodes.Ldc_I4, 0x10);
        il.Emit(OpCodes.And);

        il.Emit(OpCodes.Ldloc, locVal);
        il.Emit(OpCodes.Ldc_I4, 0xFF);
        il.Emit(OpCodes.And);
        var lblNotZero = il.DefineLabel();
        var lblDone = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, lblNotZero);
        il.Emit(OpCodes.Ldc_I4, 0x04);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Br, lblDone);

        il.MarkLabel(lblNotZero);
        il.Emit(OpCodes.Ldloc, locVal);
        il.Emit(OpCodes.Ldc_I4, 0x80);
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

    // ================================================================
    // Phase 2: Memory access IL emission helpers
    // ================================================================

    /// <summary>Emit IL to set _jitExecutedCount and _jitExecutedCycles for full execution.</summary>
    private static void EmitSetFullExecution(ILGenerator il, int instrCount, int totalCycles)
    {
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, instrCount);
        il.Emit(OpCodes.Stfld, FiJitExecutedCount);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, totalCycles);
        il.Emit(OpCodes.Stfld, FiJitExecutedCycles);
    }

    /// <summary>Emit bailout: write CCR, set executedCount/Cycles, return instrPC.</summary>
    private static void EmitBailout(ILGenerator il, LocalBuilder locCCR,
        int instrIndex, int cumCycles, uint instrPC)
    {
        // Write back CCR
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, locCCR);
        il.Emit(OpCodes.Callvirt, MiSetCCR);
        // Set executed count
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, instrIndex);
        il.Emit(OpCodes.Stfld, FiJitExecutedCount);
        // Set executed cycles
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, cumCycles);
        il.Emit(OpCodes.Stfld, FiJitExecutedCycles);
        // Return instrPC
        il.Emit(OpCodes.Ldc_I4, (int)instrPC);
        il.Emit(OpCodes.Conv_U4);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>Emit always-bailout (for write instructions).</summary>
    private static void EmitAlwaysBailout(ILGenerator il, LocalBuilder locCCR,
        int instrIndex, int cumCycles, uint instrPC)
    {
        EmitBailout(il, locCCR, instrIndex, cumCycles, instrPC);
    }

    /// <summary>
    /// Emit MOVE.L memory read: (An), (An)+, or d16(An) → Dm.
    /// Calls TryReadLongCached; on miss → bailout.
    /// </summary>
    private static void EmitMemoryRead(ILGenerator il, LocalBuilder locD, LocalBuilder locCCR,
        LocalBuilder locTmp, LocalBuilder locAddr,
        int srcReg, int dstReg, bool needsFlags,
        int instrIndex, int cumCycles, uint instrPC,
        int addDisp, bool postInc)
    {
        var lblBailout = il.DefineLabel();
        var lblContinue = il.DefineLabel();

        // Compute address: cpu.A[srcReg] + addDisp
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MiGetA);
        il.Emit(OpCodes.Ldc_I4, srcReg);
        il.Emit(OpCodes.Ldelem_U4);
        if (addDisp != 0)
        {
            il.Emit(OpCodes.Ldc_I4, addDisp);
            il.Emit(OpCodes.Add);
        }
        il.Emit(OpCodes.Stloc, locAddr);

        // Call cpu.TryReadLongCached(addr)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, locAddr);
        il.Emit(OpCodes.Call, MiTryReadLongCached);
        il.Emit(OpCodes.Brfalse, lblBailout);

        // Cache hit: load result from cpu._jitReadResult
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, FiJitReadResult);
        il.Emit(OpCodes.Stloc, locTmp);

        // Store to D[dstReg]
        il.Emit(OpCodes.Ldloc, locD);
        il.Emit(OpCodes.Ldc_I4, dstReg);
        il.Emit(OpCodes.Ldloc, locTmp);
        il.Emit(OpCodes.Stelem_I4);

        // Post-increment: A[srcReg] += 4
        if (postInc)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, MiGetA);
            il.Emit(OpCodes.Ldc_I4, srcReg);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, MiGetA);
            il.Emit(OpCodes.Ldc_I4, srcReg);
            il.Emit(OpCodes.Ldelem_U4);
            il.Emit(OpCodes.Ldc_I4, 4);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stelem_I4);
        }

        // Update flags if needed
        if (needsFlags)
        {
            il.Emit(OpCodes.Ldloc, locTmp);
            EmitSetNZVC_FromStack(il, locCCR);
        }

        il.Emit(OpCodes.Br, lblContinue);

        // Bailout path
        il.MarkLabel(lblBailout);
        EmitBailout(il, locCCR, instrIndex, cumCycles, instrPC);

        il.MarkLabel(lblContinue);
    }

    /// <summary>
    /// Emit RTS: PC = ReadLong(A7); A7 += 4. Uses TryReadLongCached for stack read.
    /// RTS is a block terminator — emits its own ret.
    /// </summary>
    private static void EmitRts(ILGenerator il, LocalBuilder locCCR, LocalBuilder locAddr,
        int instrIndex, int cumCycles, uint instrPC,
        int totalInstrCount, int totalCycles)
    {
        var lblBailout = il.DefineLabel();

        // addr = cpu.A[7]
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MiGetA);
        il.Emit(OpCodes.Ldc_I4, 7);
        il.Emit(OpCodes.Ldelem_U4);
        il.Emit(OpCodes.Stloc, locAddr);

        // Call cpu.TryReadLongCached(addr)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, locAddr);
        il.Emit(OpCodes.Call, MiTryReadLongCached);
        il.Emit(OpCodes.Brfalse, lblBailout);

        // Cache hit: A7 += 4
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MiGetA);
        il.Emit(OpCodes.Ldc_I4, 7);
        il.Emit(OpCodes.Ldloc, locAddr);
        il.Emit(OpCodes.Ldc_I4, 4);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stelem_I4);

        // Write back CCR
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, locCCR);
        il.Emit(OpCodes.Callvirt, MiSetCCR);

        // Set full execution counts
        EmitSetFullExecution(il, totalInstrCount, totalCycles);

        // Return new PC from _jitReadResult
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, FiJitReadResult);
        il.Emit(OpCodes.Ret);

        // Bailout path
        il.MarkLabel(lblBailout);
        EmitBailout(il, locCCR, instrIndex, cumCycles, instrPC);
    }
}
