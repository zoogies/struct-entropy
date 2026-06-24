using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static StructEntropy.Rewriter.ILInstructionHelpers;
using static StructEntropy.Rewriter.TypeResolutionHelpers;

public static partial class StructEntropyRewriter
{
    private static ParameterDefinition FindByRefParameter(MethodDefinition method, TypeDefinition type)
    {
        if (method == null || type == null)
            return null;

        return method.Parameters.FirstOrDefault(p =>
            p.ParameterType is ByReferenceType br &&
            TypeRefFullNameEquals(br.ElementType, type.FullName));
    }

    private static bool IsUserIjeExecute(MethodDefinition method, TypeDefinition sourceType)
    {
        if (!method.HasBody || method.Name != "Execute") return false;
        if (method.Parameters.Count >= 1 &&
            method.Parameters[0].ParameterType.Name.StartsWith("ArchetypeChunk"))
            return false;
        return method.Parameters.Any(p =>
            (p.ParameterType is ByReferenceType brt &&
             TypeRefFullNameEquals(brt.ElementType, sourceType.FullName)) ||
            TypeRefFullNameEquals(p.ParameterType, sourceType.FullName));
    }

    private static bool IsGeneratedIjeExecute(MethodDefinition method)
        => method.Name == "Execute" && method.HasBody &&
           method.Parameters.Count >= 1 &&
           method.Parameters[0].ParameterType.Name.StartsWith("ArchetypeChunk");

    private static bool CjImplementsIJobChunk(TypeDefinition type)
        => type.Interfaces.Any(i => i.InterfaceType.FullName == "Unity.Entities.IJobChunk");

    private static bool MethodAccessesField(MethodDefinition method, FieldReference field, string declTypeName)
        // After Stage 1 removes the field from its type, field.DeclaringType becomes null.
        // Use reference equality as the primary check so the null doesn't break detection.
        => method.HasBody && method.Body.Instructions.Any(i =>
            i.Operand == field ||
            (i.Operand is FieldReference fr &&
             (fr.DeclaringType?.FullName == declTypeName) &&
             fr.Name == field.Name));

    private static bool IsFieldAccess(Instruction instr, FieldReference field, string declTypeName)
        // Use reference equality as primary check - fr.DeclaringType is null after Stage 1 removal.
        => (instr.OpCode == OpCodes.Ldfld || instr.OpCode == OpCodes.Ldflda || instr.OpCode == OpCodes.Stfld)
           && (instr.Operand == field ||
               (instr.Operand is FieldReference fr &&
                fr.DeclaringType?.FullName == declTypeName &&
                fr.Name == field.Name));

    private static bool IsGetItemOnComponentLookup(Instruction instr, TypeDefinition componentType)
    {
        if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
            instr.Operand is not MethodReference mr) return false;
        if (mr.Name != "get_Item") return false;
        var declaring = mr.DeclaringType;
        return declaring is GenericInstanceType git &&
               git.ElementType.Name.StartsWith("ComponentLookup") &&
               git.GenericArguments.Count == 1 &&
               TypeRefFullNameEquals(git.GenericArguments[0], componentType.FullName);
    }

    private static bool IsSetItemOnComponentLookup(Instruction instr, TypeDefinition componentType)
    {
        if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
            instr.Operand is not MethodReference mr) return false;
        if (mr.Name != "set_Item") return false;
        var declaring = mr.DeclaringType;
        return declaring is GenericInstanceType git &&
               git.ElementType.Name.StartsWith("ComponentLookup") &&
               git.GenericArguments.Count == 1 &&
               TypeRefFullNameEquals(git.GenericArguments[0], componentType.FullName);
    }

    private static bool MethodHasEcbOperations(MethodDefinition method)
        => method.HasBody && method.Body.Instructions.Any(i =>
            (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt) &&
            i.Operand is MethodReference mr &&
            IsEntityCommandBufferType(mr.DeclaringType?.FullName ?? ""));

    private static bool IsUserIjeExecuteCandidate(MethodDefinition method)
        => method?.HasBody == true &&
           method.Name == "Execute" &&
           !IsGeneratedIjeExecute(method);

    private static bool MethodHasBakerAddComponentOperations(MethodDefinition method)
        => method.HasBody && method.Body.Instructions.Any(i =>
            (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt) &&
            i.Operand is MethodReference mr &&
            IsBakerAddComponentMethod(mr));

    private static bool IsEcbSetComponentOf(MethodReference mr, TypeDefinition componentType)
    {
        if (mr.Name != "SetComponent") return false;
        if (!IsEntityCommandBufferType(mr.DeclaringType.FullName)) return false;
        return mr is GenericInstanceMethod gim &&
               gim.GenericArguments.Count == 1 &&
               TypeRefFullNameEquals(gim.GenericArguments[0], componentType.FullName);
    }

    private static bool IsEcbAddComponentCall(MethodReference mr, TypeDefinition componentType)
    {
        if (mr == null || componentType == null)
            return false;

        if (mr.Name != "AddComponent" || !IsEntityCommandBufferType(mr.DeclaringType?.FullName ?? ""))
            return false;

        return mr is GenericInstanceMethod gim &&
               gim.GenericArguments.Count == 1 &&
               TypeRefFullNameEquals(gim.GenericArguments[0], componentType.FullName);
    }

    private static bool IsEntityCommandBufferType(string fullName)
        => fullName == "Unity.Entities.EntityCommandBuffer" ||
           fullName == "Unity.Entities.EntityCommandBuffer/ParallelWriter";

    private static bool IsBakerAddComponentMethod(MethodReference mr)
    {
        if (mr == null || mr.Name != "AddComponent")
            return false;

        string declaringTypeName = mr.DeclaringType?.Resolve()?.FullName ?? mr.DeclaringType?.FullName;
        return declaringTypeName == "Unity.Entities.Baker`1" || declaringTypeName == "Unity.Entities.IBaker";
    }

    private static bool IsEntityManagerGetComponentDataCall(Instruction instr, TypeDefinition componentType)
    {
        if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
            instr.Operand is not MethodReference mr) return false;
        if (mr.Name != "GetComponentData") return false;
        if (mr.DeclaringType.FullName != "Unity.Entities.EntityManager") return false;
        return mr is GenericInstanceMethod gim &&
               gim.GenericArguments.Count == 1 &&
               TypeRefFullNameEquals(gim.GenericArguments[0], componentType.FullName);
    }

    private static bool IsEntityManagerSetComponentDataCall(Instruction instr, TypeDefinition componentType)
    {
        if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
            instr.Operand is not MethodReference mr) return false;
        if (mr.Name != "SetComponentData") return false;
        if (mr.DeclaringType.FullName != "Unity.Entities.EntityManager") return false;
        return mr is GenericInstanceMethod gim &&
               gim.GenericArguments.Count == 1 &&
               TypeRefFullNameEquals(gim.GenericArguments[0], componentType.FullName);
    }

    private static bool IsBakerAddComponentCall(MethodReference mr, TypeDefinition componentType)
    {
        if (mr == null || componentType == null)
            return false;

        if (!IsBakerAddComponentMethod(mr))
            return false;

        return mr is GenericInstanceMethod gim &&
               gim.GenericArguments.Count == 1 &&
               TypeRefFullNameEquals(gim.GenericArguments[0], componentType.FullName);
    }

    private static bool IsEntityManagerSetComponentDataMethod(MethodReference mr, TypeDefinition componentType)
    {
        if (mr == null || componentType == null)
            return false;

        if (mr.Name != "SetComponentData" || mr.DeclaringType.FullName != "Unity.Entities.EntityManager")
            return false;

        return mr is GenericInstanceMethod gim &&
               gim.GenericArguments.Count == 1 &&
               TypeRefFullNameEquals(gim.GenericArguments[0], componentType.FullName);
    }

    private static Instruction FindSingleGetComponentDataProducer(
        MethodDefinition method, VariableDefinition local, TypeDefinition componentType)
    {
        Instruction result = null;

        foreach (var instr in method.Body.Instructions)
        {
            if (!IsStloc(instr)) continue;
            if (GetLocalFromInstruction(instr, method.Body) != local) continue;

            var prev = instr.Previous;
            if (prev == null || !IsEntityManagerGetComponentDataCall(prev, componentType))
                return null;

            if (result != null && result != prev)
                return null;

            result = prev;
        }

        return result;
    }
}
