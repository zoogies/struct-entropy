using Mono.Cecil;
using Mono.Cecil.Cil;
using static StructEntropy.Rewriter.ILInstructionHelpers;

public static partial class StructEntropyRewriter
{
    private static Instruction FindArgumentSequenceStartBeforeCall(Instruction callInstr, int argsToSkipFromTop)
    {
        var end = callInstr?.Previous;
        for (int i = 0; i < argsToSkipFromTop; i++)
        {
            var skipped = FindArgumentSequenceStart(end);
            if (skipped == null)
                return null;
            end = skipped.Previous;
        }

        return FindArgumentSequenceStart(end);
    }

    private static Instruction FindArgumentSequenceStart(Instruction end)
    {
        if (end == null) return null;

        int needed = 1;
        var current = end;
        var start = end;
        while (current != null && needed > 0)
        {
            start = current;
            needed -= GetPushCount(current);
            needed += GetPopCount(current);
            if (needed <= 0) break;
            current = current.Previous;
        }

        return needed <= 0 ? start : null;
    }

    private static Instruction FindCallArgumentSequenceStart(Instruction callInstr)
    {
        if (callInstr == null)
            return null;

        var needed = GetPopCount(callInstr);
        if (needed <= 0)
            return callInstr;

        var current = callInstr.Previous;
        var start = current;
        while (current != null && needed > 0)
        {
            start = current;
            needed -= GetPushCount(current);
            needed += GetPopCount(current);
            if (needed <= 0)
                break;
            current = current.Previous;
        }

        return needed <= 0 ? start : null;
    }

    private static Instruction FindInstanceLoad(Instruction fieldAccess)
    {
        if (fieldAccess.OpCode == OpCodes.Ldfld || fieldAccess.OpCode == OpCodes.Ldflda)
        {
            var prev = fieldAccess.Previous;
            if (prev == null) return null;
            if (IsSimpleLoad(prev)) return prev;
            if (prev.OpCode == OpCodes.Dup)
            {
                var beforeDup = prev.Previous;
                if (beforeDup != null && IsSimpleLoad(beforeDup)) return beforeDup;
            }
            return null;
        }
        if (fieldAccess.OpCode == OpCodes.Stfld)
        {
            int remaining = 1;
            var current = fieldAccess.Previous;
            while (current != null && remaining > 0)
            {
                remaining -= GetPushCount(current);
                remaining += GetPopCount(current);
                if (remaining <= 0) break;
                current = current.Previous;
            }
            if (current == null) return null;
            current = current.Previous;
            if (current == null) return null;
            if (IsSimpleLoad(current)) return current;
            if (current.OpCode == OpCodes.Dup)
            {
                var bd = current.Previous;
                if (bd != null && IsSimpleLoad(bd)) return bd;
            }
            return null;
        }
        return null;
    }

    private static Instruction FindDirectCallInstanceProducer(Instruction fieldAccess)
    {
        if (fieldAccess.OpCode != OpCodes.Ldfld && fieldAccess.OpCode != OpCodes.Ldflda)
            return null;

        var prev = fieldAccess.Previous;
        if (prev != null &&
            (prev.OpCode.Code == Code.Call || prev.OpCode.Code == Code.Callvirt) &&
            prev.Operand is MethodReference)
            return prev;

        if (prev?.OpCode == OpCodes.Dup)
        {
            var beforeDup = prev.Previous;
            if (beforeDup != null &&
                (beforeDup.OpCode.Code == Code.Call || beforeDup.OpCode.Code == Code.Callvirt) &&
                beforeDup.Operand is MethodReference)
                return beforeDup;
        }

        return null;
    }

    private static TypeReference GetLoadedType(Instruction instr, MethodDefinition method)
    {
        var code = instr.OpCode.Code;
        VariableDefinition local = null;
        if (code == Code.Ldloc || code == Code.Ldloc_S ||
            code == Code.Ldloca || code == Code.Ldloca_S)
            local = (VariableDefinition)instr.Operand;
        else if (code == Code.Ldloc_0) local = method.Body.Variables.Count > 0 ? method.Body.Variables[0] : null;
        else if (code == Code.Ldloc_1) local = method.Body.Variables.Count > 1 ? method.Body.Variables[1] : null;
        else if (code == Code.Ldloc_2) local = method.Body.Variables.Count > 2 ? method.Body.Variables[2] : null;
        else if (code == Code.Ldloc_3) local = method.Body.Variables.Count > 3 ? method.Body.Variables[3] : null;
        if (local != null) return local.VariableType;

        if (code == Code.Ldarg || code == Code.Ldarg_S ||
            code == Code.Ldarga || code == Code.Ldarga_S)
            return ((ParameterDefinition)instr.Operand).ParameterType;

        int argIdx = -1;
        if (code == Code.Ldarg_0) argIdx = 0;
        else if (code == Code.Ldarg_1) argIdx = 1;
        else if (code == Code.Ldarg_2) argIdx = 2;
        else if (code == Code.Ldarg_3) argIdx = 3;
        if (argIdx >= 0)
        {
            if (method.HasThis) { if (argIdx == 0) return method.DeclaringType; argIdx--; }
            if (argIdx < method.Parameters.Count) return method.Parameters[argIdx].ParameterType;
        }
        return null;
    }

    private static string GetBaseTypeName(TypeReference tr)
    {
        if (tr == null) return null;
        return (tr is ByReferenceType brt) ? brt.ElementType.FullName : tr.FullName;
    }
}
