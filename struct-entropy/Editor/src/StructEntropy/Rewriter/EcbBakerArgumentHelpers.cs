using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static StructEntropy.Rewriter.ILInstructionHelpers;
using static StructEntropy.Rewriter.TypeResolutionHelpers;

public static partial class StructEntropyRewriter
{
    private static int FindByRefParamLdargIndex(MethodDefinition method, TypeDefinition type)
    {
        for (int i = 0; i < method.Parameters.Count; i++)
        {
            if (method.Parameters[i].ParameterType is ByReferenceType brt &&
                TypeRefFullNameEquals(brt.ElementType, type.FullName))
                return method.HasThis ? i + 1 : i;
        }
        return -1;
    }

    private static int FindValueParamIndex(MethodDefinition method, TypeDefinition type)
    {
        for (int i = 0; i < method.Parameters.Count; i++)
        {
            if (TypeRefFullNameEquals(method.Parameters[i].ParameterType, type.FullName))
                return i;
        }

        return -1;
    }

    private static VariableDefinition FindEcbEntityLocal(MethodDefinition method)
    {
        foreach (var instr in method.Body.Instructions)
        {
            if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
                instr.Operand is not MethodReference mr) continue;
            if (!IsEntityCommandBufferType(mr.DeclaringType.FullName)) continue;
            if (mr.Name != "CreateEntity" && mr.Name != "Instantiate") continue;

            var next = instr.Next;
            if (next != null && IsStloc(next))
                return GetLocalFromInstruction(next, method.Body);
        }
        return null;
    }

    private static VariableDefinition FindBakerEntityLocal(MethodDefinition method)
    {
        foreach (var instr in method.Body.Instructions)
        {
            if (!IsStloc(instr))
                continue;

            var prev = instr.Previous;
            if (prev == null || (prev.OpCode.Code != Code.Call && prev.OpCode.Code != Code.Callvirt))
                continue;
            if (prev.Operand is not MethodReference mr)
                continue;
            if (mr.Name != "GetEntity")
                continue;

            var local = GetLocalFromInstruction(instr, method.Body);
            if (local != null && GetBaseTypeName(local.VariableType) == "Unity.Entities.Entity")
                return local;
        }

        return null;
    }

    private static List<Instruction> FindEcbLoadInMethod(MethodDefinition method)
    {
        // Find ldarga/ldarg/ldloca for an EntityCommandBuffer param or local.
        foreach (var instr in method.Body.Instructions)
        {
            if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
                instr.Operand is not MethodReference mr) continue;
            if (!IsEntityCommandBufferType(mr.DeclaringType.FullName)) continue;

            var scan = instr.Previous;
            var depth = 0;
            while (scan != null && depth < 10)
            {
                if (scan.OpCode.Code == Code.Ldarga || scan.OpCode.Code == Code.Ldarga_S ||
                    scan.OpCode.Code == Code.Ldloca || scan.OpCode.Code == Code.Ldloca_S)
                {
                    TypeReference loadedType = null;
                    if (scan.Operand is ParameterDefinition pd) loadedType = pd.ParameterType;
                    else if (scan.Operand is VariableDefinition vd) loadedType = vd.VariableType;
                    if (loadedType != null && IsEntityCommandBufferType(loadedType.FullName))
                        return new List<Instruction> { CloneInstruction(scan) };
                }
                scan = scan.Previous;
                depth++;
            }
        }
        return null;
    }

    private class AddComponentRef { public OpCode OpCode; public MethodReference MethodRef; }

    private static Dictionary<string, AddComponentRef> SaveEcbAddComponentRefs(
        MethodDefinition method, HashSet<string> groupTypeNames)
    {
        var result = new Dictionary<string, AddComponentRef>();
        foreach (var instr in method.Body.Instructions)
        {
            if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
                instr.Operand is not GenericInstanceMethod gim) continue;
            if (gim.Name != "AddComponent" || gim.GenericArguments.Count != 1) continue;
            var tn = gim.GenericArguments[0].FullName;
            if (groupTypeNames.Contains(tn) && !result.ContainsKey(tn))
                result[tn] = new AddComponentRef { OpCode = instr.OpCode, MethodRef = (MethodReference)instr.Operand };
        }
        return result;
    }

    private static List<Instruction> FindEcbAddComponentEntityArgument(
        MethodDefinition method, HashSet<string> groupTypeNames)
    {
        foreach (var instr in method.Body.Instructions)
        {
            if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
                instr.Operand is not GenericInstanceMethod gim)
                continue;

            if (gim.Name != "AddComponent" ||
                gim.GenericArguments.Count != 1 ||
                !groupTypeNames.Contains(gim.GenericArguments[0].FullName))
                continue;

            if (!IsEntityCommandBufferType(gim.DeclaringType?.FullName ?? ""))
                continue;

            var valueLoad = instr.Previous;
            var valueLocal = GetLocalFromInstruction(valueLoad, method.Body);
            if (valueLocal != null)
            {
                var entityBelowConstruction = CollectEntityArgumentBelowValueLocalConstruction(
                    method,
                    valueLoad,
                    valueLocal);
                if (entityBelowConstruction != null && entityBelowConstruction.Count > 0)
                    return entityBelowConstruction;
            }

            var entityInstrs = CollectArgumentSequenceBeforeCall(instr, 1);
            if (entityInstrs != null && entityInstrs.Count > 0)
                return entityInstrs;
        }

        return null;
    }

    private static List<Instruction> CollectArgumentSequenceBeforeCall(Instruction callInstr, int argsToSkipFromTop)
    {
        var start = FindArgumentSequenceStartBeforeCall(callInstr, argsToSkipFromTop);
        if (start == null) return null;

        var end = callInstr.Previous;
        for (int i = 0; i < argsToSkipFromTop; i++)
        {
            var skipped = FindArgumentSequenceStart(end);
            if (skipped == null) return null;
            end = skipped.Previous;
        }

        var result = new List<Instruction>();
        for (var cur = start; cur != null; cur = cur.Next)
        {
            var clone = CloneInstruction(cur);
            if (clone == null) return null;
            result.Add(clone);
            if (cur == end) break;
        }
        return result;
    }

    private static List<Instruction> CollectEntityArgumentBelowValueLocalConstruction(
        MethodDefinition method,
        Instruction valueLoad,
        VariableDefinition valueLocal)
    {
        if (method == null || valueLoad == null || valueLocal == null)
            return null;

        Instruction firstValueAddressLoad = null;
        var scanned = 0;
        for (var scan = valueLoad.Previous; scan != null && scanned < 48; scan = scan.Previous, scanned++)
        {
            if (IsAddressLoad(scan) && GetLocalFromInstruction(scan, method.Body) == valueLocal)
                firstValueAddressLoad = scan;
        }

        var entityEnd = firstValueAddressLoad?.Previous;
        var entityStart = FindArgumentSequenceStart(entityEnd);
        if (entityStart == null)
            return null;

        return CloneInstructionRange(entityStart, entityEnd);
    }

    // Find entity load instruction before ECB.SetComponent<T> (the arg before the value arg).
    private static Instruction FindArgBeforeValueArg(Instruction callInstr, MethodDefinition method)
    {
        int remaining = 1;
        var current = callInstr.Previous;
        while (current != null && remaining > 0)
        {
            remaining -= GetPushCount(current);
            remaining += GetPopCount(current);
            if (remaining <= 0) break;
            current = current.Previous;
        }
        return current?.Previous;
    }
}
