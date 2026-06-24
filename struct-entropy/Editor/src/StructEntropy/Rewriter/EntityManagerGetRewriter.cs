using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using StructEntropy.Rewriter;
using static StructEntropy.Rewriter.ILInstructionHelpers;
using static StructEntropy.Rewriter.TypeResolutionHelpers;

public static partial class StructEntropyRewriter
{
    // --------------------------------------------------------------
    //  EntityManagerGetRewriter: EntityManager.GetComponentData chains
    // --------------------------------------------------------------
    private static int ApplyEntityManagerGet(
        ModuleDefinition module, MethodDefinition method, Relocation reloc)
    {
        int count = 0;
        var il = method.Body.GetILProcessor();
        var snapshot = method.Body.Instructions.ToList();

        foreach (var instr in snapshot)
        {
            if (!IsFieldAccess(instr, reloc.Field, reloc.SourceTypeFullName)) continue;

            Instruction getComponentCall = null;
            bool isDirectChain = false;
            var directProducer = FindDirectCallInstanceProducer(instr);
            if (directProducer != null && IsEntityManagerGetComponentDataCall(directProducer, reloc.SourceType))
            {
                getComponentCall = directProducer;
                isDirectChain = true;
            }
            else
            {
                var instanceLoad = FindInstanceLoad(instr);
                if (instanceLoad == null) continue;

                var loadedTypeName = GetBaseTypeName(GetLoadedType(instanceLoad, method));
                if (loadedTypeName != reloc.SourceType.FullName) continue;

                var sourceLocal = GetLocalFromInstruction(instanceLoad, method.Body);
                if (sourceLocal == null) continue;

                getComponentCall = FindSingleGetComponentDataProducer(method, sourceLocal, reloc.SourceType);
                if (getComponentCall == null) continue;
            }

            var peerGetRef = BuildGenericMethodRef(module, getComponentCall, reloc.TargetType);
            if (peerGetRef == null) continue;

            if (isDirectChain)
            {
                getComponentCall.Operand = peerGetRef;
            }
            else
            {
                var instanceLoad = FindInstanceLoad(instr);
                if (instanceLoad == null) continue;

                var sourceLocal = GetLocalFromInstruction(instanceLoad, method.Body);
                if (sourceLocal == null) continue;

                var targetLocal = new VariableDefinition(module.ImportReference(reloc.TargetType));
                method.Body.Variables.Add(targetLocal);
                method.Body.InitLocals = true;

                var clonedSetup = CloneCallSetup(getComponentCall);
                if (clonedSetup == null || clonedSetup.Count == 0)
                {
                    method.Body.Variables.Remove(targetLocal);
                    continue;
                }

                foreach (var clone in clonedSetup)
                    il.InsertBefore(instanceLoad, clone);

                var injectedCall = Instruction.Create(getComponentCall.OpCode, peerGetRef);
                il.InsertBefore(instanceLoad, injectedCall);
                il.InsertBefore(instanceLoad, Instruction.Create(OpCodes.Stloc, targetLocal));

                var replacement = IsAddressLoad(instanceLoad)
                    ? Instruction.Create(OpCodes.Ldloca, targetLocal)
                    : Instruction.Create(OpCodes.Ldloc, targetLocal);
                ReplaceInstruction(il, instanceLoad, replacement);

                RewriteSetComponentDataWriteBack(method, reloc, sourceLocal, targetLocal);
            }

            instr.Operand = reloc.NewField;
            count++;
        }

        if (count > 0)
        {
            method.Body.OptimizeMacros();
            StructEntropyLogger.Log($"[SER]   EntityManagerGetRewriter in {method.DeclaringType.Name}.{method.Name}: {count} access(es)");
        }
        return count;
    }

    private static List<Instruction> CloneCallSetup(Instruction callInstr)
    {
        if (callInstr == null)
            return null;

        int remaining = GetPopCount(callInstr);
        if (remaining <= 0)
            return new List<Instruction>();

        var source = new List<Instruction>();
        var current = callInstr.Previous;

        while (current != null && remaining > 0)
        {
            source.Insert(0, current);
            remaining -= GetPushCount(current);
            remaining += GetPopCount(current);
            current = current.Previous;
        }

        if (remaining > 0)
            return null;

        var clones = new List<Instruction>(source.Count);
        foreach (var instr in source)
        {
            var clone = CloneInstruction(instr);
            if (clone == null)
                return null;
            clones.Add(clone);
        }

        return clones;
    }

    private static void RewriteSetComponentDataWriteBack(
        MethodDefinition method,
        Relocation reloc,
        VariableDefinition sourceLocal,
        VariableDefinition targetLocal)
    {
        if (method == null || sourceLocal == null || targetLocal == null)
            return;

        var il = method.Body.GetILProcessor();
        var snapshot = method.Body.Instructions.ToList();

        foreach (var instr in snapshot)
        {
            if (!IsEntityManagerSetComponentDataCall(instr, reloc.SourceType))
                continue;

            var valueLoad = instr.Previous;
            if (valueLoad == null || !IsValueLoad(valueLoad))
                continue;

            if (GetLocalFromInstruction(valueLoad, method.Body) != sourceLocal)
                continue;

            var peerSetRef = BuildGenericMethodRef(method.Module, instr, reloc.TargetType);
            if (peerSetRef == null)
                continue;

            ReplaceInstruction(il, valueLoad, Instruction.Create(OpCodes.Ldloc, targetLocal));
            instr.Operand = peerSetRef;
        }
    }

    // --------------------------------------------------------------
}
