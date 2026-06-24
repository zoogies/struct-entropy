using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace StructEntropy.Rewriter
{
    internal static class ILInstructionHelpers
    {
        public static List<Instruction> CloneInstructionList(IEnumerable<Instruction> instructions)
        {
            var clones = new List<Instruction>();
            foreach (var instruction in instructions)
            {
                var clone = CloneInstruction(instruction);
                if (clone == null)
                    return null;
                clones.Add(clone);
            }
            return clones;
        }

        public static Instruction FindFieldLoadForCall(Instruction callInstr, FieldReference field)
        {
            if (callInstr == null || field == null)
                return null;

            for (var scan = callInstr.Previous; scan != null; scan = scan.Previous)
            {
                if ((scan.OpCode.Code == Code.Call || scan.OpCode.Code == Code.Callvirt) ||
                    scan.OpCode.FlowControl == FlowControl.Branch ||
                    scan.OpCode.FlowControl == FlowControl.Cond_Branch)
                    break;

                if ((scan.OpCode == OpCodes.Ldfld || scan.OpCode == OpCodes.Ldflda) &&
                    scan.Operand is FieldReference fr &&
                    fr.Resolve() == field)
                    return scan;
            }

            return null;
        }

        public static IEnumerable<Instruction> EnumerateInstructionRange(Instruction start, Instruction end)
        {
            for (var cur = start; cur != null; cur = cur.Next)
            {
                yield return cur;
                if (cur == end)
                    yield break;
            }
        }

        public static Instruction CloneInstruction(Instruction original)
        {
            if (original == null) return null;
            var code = original.OpCode.Code;
            if (code == Code.Ldloc_0) return Instruction.Create(OpCodes.Ldloc_0);
            if (code == Code.Ldloc_1) return Instruction.Create(OpCodes.Ldloc_1);
            if (code == Code.Ldloc_2) return Instruction.Create(OpCodes.Ldloc_2);
            if (code == Code.Ldloc_3) return Instruction.Create(OpCodes.Ldloc_3);
            if (code == Code.Ldarg_0) return Instruction.Create(OpCodes.Ldarg_0);
            if (code == Code.Ldarg_1) return Instruction.Create(OpCodes.Ldarg_1);
            if (code == Code.Ldarg_2) return Instruction.Create(OpCodes.Ldarg_2);
            if (code == Code.Ldarg_3) return Instruction.Create(OpCodes.Ldarg_3);
            if (code == Code.Stloc_0) return Instruction.Create(OpCodes.Stloc_0);
            if (code == Code.Stloc_1) return Instruction.Create(OpCodes.Stloc_1);
            if (code == Code.Stloc_2) return Instruction.Create(OpCodes.Stloc_2);
            if (code == Code.Stloc_3) return Instruction.Create(OpCodes.Stloc_3);
            if (code == Code.Ldloc || code == Code.Ldloc_S)
                return Instruction.Create(original.OpCode, (VariableDefinition)original.Operand);
            if (code == Code.Ldloca || code == Code.Ldloca_S)
                return Instruction.Create(original.OpCode, (VariableDefinition)original.Operand);
            if (code == Code.Stloc || code == Code.Stloc_S)
                return Instruction.Create(original.OpCode, (VariableDefinition)original.Operand);
            if (code == Code.Ldsfld || code == Code.Ldsflda)
                return Instruction.Create(original.OpCode, (FieldReference)original.Operand);
            if (code == Code.Ldarg || code == Code.Ldarg_S)
                return Instruction.Create(original.OpCode, (ParameterDefinition)original.Operand);
            if (code == Code.Ldarga || code == Code.Ldarga_S)
                return Instruction.Create(original.OpCode, (ParameterDefinition)original.Operand);
            if (code == Code.Ldfld || code == Code.Ldflda || code == Code.Stfld)
                return Instruction.Create(original.OpCode, (FieldReference)original.Operand);
            if (code == Code.Ldobj || code == Code.Sizeof)
                return Instruction.Create(original.OpCode, (TypeReference)original.Operand);
            if (code == Code.Call || code == Code.Callvirt)
                return Instruction.Create(original.OpCode, (MethodReference)original.Operand);
            if (code == Code.Ldc_I4_0) return Instruction.Create(OpCodes.Ldc_I4_0);
            if (code == Code.Ldc_I4_1) return Instruction.Create(OpCodes.Ldc_I4_1);
            if (code == Code.Ldc_I4_2) return Instruction.Create(OpCodes.Ldc_I4_2);
            if (code == Code.Ldc_I4_S) return Instruction.Create(OpCodes.Ldc_I4_S, (sbyte)original.Operand);
            if (code == Code.Ldc_I4) return Instruction.Create(OpCodes.Ldc_I4, (int)original.Operand);
            if (code == Code.Nop) return Instruction.Create(OpCodes.Nop);
            return null;
        }

        public static void ReplaceInstruction(ILProcessor il, Instruction oldInstr, Instruction newInstr)
        {
            foreach (var instr in il.Body.Instructions)
            {
                if (instr.Operand is Instruction tgt && tgt == oldInstr) instr.Operand = newInstr;
                if (instr.Operand is Instruction[] targets)
                    for (int i = 0; i < targets.Length; i++)
                        if (targets[i] == oldInstr) targets[i] = newInstr;
            }
            if (il.Body.HasExceptionHandlers)
            {
                foreach (var h in il.Body.ExceptionHandlers)
                {
                    if (h.TryStart == oldInstr) h.TryStart = newInstr;
                    if (h.TryEnd == oldInstr) h.TryEnd = newInstr;
                    if (h.HandlerStart == oldInstr) h.HandlerStart = newInstr;
                    if (h.HandlerEnd == oldInstr) h.HandlerEnd = newInstr;
                    if (h.FilterStart == oldInstr) h.FilterStart = newInstr;
                }
            }
            il.Replace(oldInstr, newInstr);
        }

        public static VariableDefinition GetLocalFromInstruction(Instruction instr, MethodBody body)
        {
            var code = instr.OpCode.Code;
            if (code == Code.Ldloc || code == Code.Ldloc_S ||
                code == Code.Ldloca || code == Code.Ldloca_S ||
                code == Code.Stloc || code == Code.Stloc_S)
                return (VariableDefinition)instr.Operand;
            if (body == null || !body.HasVariables) return null;
            if (code == Code.Ldloc_0 || code == Code.Stloc_0) return body.Variables.Count > 0 ? body.Variables[0] : null;
            if (code == Code.Ldloc_1 || code == Code.Stloc_1) return body.Variables.Count > 1 ? body.Variables[1] : null;
            if (code == Code.Ldloc_2 || code == Code.Stloc_2) return body.Variables.Count > 2 ? body.Variables[2] : null;
            if (code == Code.Ldloc_3 || code == Code.Stloc_3) return body.Variables.Count > 3 ? body.Variables[3] : null;
            return null;
        }

        public static bool TryGetArgIndex(Instruction instr, bool hasThis, out int index)
        {
            index = -1;
            switch (instr.OpCode.Code)
            {
                case Code.Ldarg_0: index = 0; return true;
                case Code.Ldarg_1: index = 1; return true;
                case Code.Ldarg_2: index = 2; return true;
                case Code.Ldarg_3: index = 3; return true;
                case Code.Ldarg_S:
                case Code.Ldarg:
                case Code.Ldarga:
                case Code.Ldarga_S:
                    if (instr.Operand is ParameterDefinition pd)
                    {
                        index = hasThis ? pd.Index + 1 : pd.Index;
                        return true;
                    }
                    if (instr.Operand is byte b) { index = b; return true; }
                    if (instr.Operand is int i) { index = i; return true; }
                    return false;
                default: return false;
            }
        }

        public static bool TryGetParamIndex(Instruction instr, MethodDefinition method, out int parameterIndex)
        {
            parameterIndex = -1;
            if (!TryGetArgIndex(instr, method.HasThis, out int argIndex))
                return false;

            parameterIndex = method.HasThis ? argIndex - 1 : argIndex;
            return parameterIndex >= 0 && parameterIndex < method.Parameters.Count;
        }

        public static bool IsSimpleLoad(Instruction instr)
        {
            var code = instr.OpCode.Code;
            return code == Code.Ldloc || code == Code.Ldloc_0 || code == Code.Ldloc_1 ||
                   code == Code.Ldloc_2 || code == Code.Ldloc_3 || code == Code.Ldloc_S ||
                   code == Code.Ldloca || code == Code.Ldloca_S ||
                   code == Code.Ldarg || code == Code.Ldarg_0 || code == Code.Ldarg_1 ||
                   code == Code.Ldarg_2 || code == Code.Ldarg_3 || code == Code.Ldarg_S ||
                   code == Code.Ldarga || code == Code.Ldarga_S;
        }

        public static bool IsAddressLoad(Instruction instr)
        {
            var code = instr.OpCode.Code;
            return code == Code.Ldloca || code == Code.Ldloca_S ||
                   code == Code.Ldarga || code == Code.Ldarga_S;
        }

        public static bool IsStloc(Instruction instr)
        {
            var code = instr.OpCode.Code;
            return code == Code.Stloc || code == Code.Stloc_0 || code == Code.Stloc_1 ||
                   code == Code.Stloc_2 || code == Code.Stloc_3 || code == Code.Stloc_S;
        }

        public static bool IsValueLoad(Instruction instr)
        {
            var code = instr.OpCode.Code;
            return code == Code.Ldloc || code == Code.Ldloc_S ||
                   code == Code.Ldloc_0 || code == Code.Ldloc_1 ||
                   code == Code.Ldloc_2 || code == Code.Ldloc_3;
        }

        public static int GetPushCount(Instruction instr)
        {
            switch (instr.OpCode.StackBehaviourPush)
            {
                case StackBehaviour.Push0: return 0;
                case StackBehaviour.Push1: case StackBehaviour.Pushi:
                case StackBehaviour.Pushi8: case StackBehaviour.Pushr4:
                case StackBehaviour.Pushr8: case StackBehaviour.Pushref: return 1;
                case StackBehaviour.Push1_push1: return 2;
                case StackBehaviour.Varpush:
                    return (instr.OpCode.FlowControl == FlowControl.Call &&
                            instr.Operand is IMethodSignature ms &&
                            ms.ReturnType.FullName == "System.Void") ? 0 : 1;
                default: return 0;
            }
        }

        public static int GetPopCount(Instruction instr)
        {
            switch (instr.OpCode.StackBehaviourPop)
            {
                case StackBehaviour.Pop0: return 0;
                case StackBehaviour.Pop1: case StackBehaviour.Popi:
                case StackBehaviour.Popref: return 1;
                case StackBehaviour.Pop1_pop1: case StackBehaviour.Popi_pop1:
                case StackBehaviour.Popi_popi: case StackBehaviour.Popi_popi8:
                case StackBehaviour.Popi_popr4: case StackBehaviour.Popi_popr8:
                case StackBehaviour.Popref_pop1: case StackBehaviour.Popref_popi: return 2;
                case StackBehaviour.Popi_popi_popi: case StackBehaviour.Popref_popi_popi:
                case StackBehaviour.Popref_popi_popi8: case StackBehaviour.Popref_popi_popr4:
                case StackBehaviour.Popref_popi_popr8: case StackBehaviour.Popref_popi_popref: return 3;
                case StackBehaviour.Varpop:
                    if (instr.OpCode.FlowControl == FlowControl.Call && instr.Operand is IMethodSignature ms)
                    {
                        int cnt = ms.Parameters.Count;
                        if (ms.HasThis && !ms.ExplicitThis) cnt++;
                        return cnt;
                    }
                    return 0;
                default: return 0;
            }
        }
    }
}
