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
    private static FieldDefinition FindComponentLookupField(TypeDefinition type, TypeDefinition componentType)
    {
        if (type == null) return null;
        return type.Fields.FirstOrDefault(f =>
            f.FieldType is GenericInstanceType git &&
            git.ElementType.Name.StartsWith("ComponentLookup") &&
            git.GenericArguments.Count == 1 &&
            TypeRefFullNameEquals(git.GenericArguments[0], componentType.FullName));
    }

    private static bool IsComponentLookupFieldOf(FieldDefinition field, TypeDefinition componentType)
    {
        if (field == null || componentType == null) return false;
        return field.FieldType is GenericInstanceType git &&
               git.ElementType.Name.StartsWith("ComponentLookup") &&
               git.GenericArguments.Count == 1 &&
               TypeRefFullNameEquals(git.GenericArguments[0], componentType.FullName);
    }

    private static FieldDefinition EnsurePeerComponentLookupField(
        ModuleDefinition module, FieldDefinition srcField, TypeDefinition targetType)
    {
        var owner = srcField.DeclaringType;
        if (owner == null || srcField.FieldType is not GenericInstanceType srcGit) return null;

        var sourceType = srcGit.GenericArguments.Count == 1 ? srcGit.GenericArguments[0].Resolve() : null;
        if (sourceType == null) return null;

        var existing = owner.Fields.FirstOrDefault(f =>
            f.FieldType is GenericInstanceType git &&
            git.ElementType.FullName == srcGit.ElementType.FullName &&
            git.GenericArguments.Count == 1 &&
            TypeRefFullNameEquals(git.GenericArguments[0], targetType.FullName));
        if (existing != null)
        {
            InjectLookupFieldInit(module, srcField, existing, sourceType, targetType);
            return existing;
        }

        var peerGit = new GenericInstanceType(srcGit.ElementType);
        peerGit.GenericArguments.Add(module.ImportReference(targetType));

        var peerField = new FieldDefinition(
            $"__zd_{targetType.Name}_{srcField.Name}",
            srcField.Attributes,
            module.ImportReference(peerGit));
        CopyFieldMetadata(module, srcField, peerField);
        owner.Fields.Add(peerField);
        InjectLookupFieldInit(module, srcField, peerField, sourceType, targetType);
        return peerField;
    }

    private static FieldDefinition EnsureStandaloneComponentLookupField(
        ModuleDefinition module,
        TypeDefinition owner,
        TypeDefinition targetType)
    {
        if (module == null || owner == null || targetType == null)
            return null;

        var existing = FindComponentLookupField(owner, targetType);
        if (existing != null)
            return existing;

        var template = EnumerateAllTypes(module)
            .SelectMany(t => t.Fields)
            .FirstOrDefault(f =>
                f.FieldType is GenericInstanceType git &&
                git.ElementType.Name.StartsWith("ComponentLookup", StringComparison.Ordinal));
        if (template?.FieldType is not GenericInstanceType templateGit)
            return null;

        var lookupGit = new GenericInstanceType(templateGit.ElementType);
        lookupGit.GenericArguments.Add(module.ImportReference(targetType));

        var field = new FieldDefinition(
            $"__zd_{targetType.Name}_Lookup",
            template.Attributes,
            module.ImportReference(lookupGit));
        AddReadOnlyAttributeFromTemplate(module, field);
        owner.Fields.Add(field);
        return field;
    }

    private static void AddReadOnlyAttributeFromTemplate(ModuleDefinition module, FieldDefinition field)
    {
        if (module == null || field == null ||
            field.CustomAttributes.Any(a => a.AttributeType.FullName == "Unity.Collections.ReadOnlyAttribute"))
            return;

        var template = EnumerateAllTypes(module)
            .SelectMany(t => t.Fields)
            .SelectMany(f => f.CustomAttributes)
            .FirstOrDefault(a => a.AttributeType.FullName == "Unity.Collections.ReadOnlyAttribute");
        if (template != null)
            field.CustomAttributes.Add(CloneCustomAttribute(module, template));
    }

    private static bool EnsureStandaloneLookupFieldInit(
        ModuleDefinition module,
        TypeDefinition jobStruct,
        FieldDefinition lookupField,
        TypeDefinition targetType)
    {
        if (module == null || jobStruct == null || lookupField == null || targetType == null)
            return false;

        if (EnumerateAllTypes(module).Any(t => t.Methods.Any(m =>
                m.HasBody &&
                m.Body.Instructions.Any(i =>
                    i.OpCode.Code == Code.Stfld &&
                    i.Operand is FieldReference fr &&
                    fr.Resolve() == lookupField))))
            return true;

        if (!TryBuildSystemApiGetComponentLookupSequence(module, targetType, out var getLookupPrefix, out var getLookupRef))
            return false;

        foreach (var method in EnumerateAllTypes(module).SelectMany(t => t.Methods).Where(m => m.HasBody))
        {
            foreach (var scheduleCall in method.Body.Instructions.ToList())
            {
                if (!IsScheduleCall(scheduleCall))
                    continue;

                var local = FindJobLocalForScheduleCall(method, scheduleCall, jobStruct);
                if (local == null)
                    continue;

                var il = method.Body.GetILProcessor();
                il.InsertBefore(scheduleCall, Instruction.Create(OpCodes.Ldloca, local));
                foreach (var instr in CloneInstructionList(getLookupPrefix))
                    il.InsertBefore(scheduleCall, instr);
                il.InsertBefore(scheduleCall, Instruction.Create(OpCodes.Call, getLookupRef));
                il.InsertBefore(scheduleCall, Instruction.Create(OpCodes.Stfld, module.ImportReference(lookupField)));

                method.Body.OptimizeMacros();
                StructEntropyLogger.Log($"[SER]   Injected standalone lookup init for {targetType.Name} in {method.DeclaringType.Name}.{method.Name}");
                return true;
            }
        }

        return InjectStandaloneLookupInitAfterJobLocalStore(module, jobStruct, lookupField, targetType, getLookupPrefix, getLookupRef);
    }

    private static bool InjectStandaloneLookupInitAfterJobLocalStore(
        ModuleDefinition module,
        TypeDefinition jobStruct,
        FieldDefinition lookupField,
        TypeDefinition targetType,
        List<Instruction> getLookupPrefix,
        MethodReference getLookupRef)
    {
        foreach (var method in EnumerateAllTypes(module).SelectMany(t => t.Methods).Where(m => m.HasBody))
        {
            var local = method.Body.Variables
                .FirstOrDefault(v => TypeRefFullNameEquals(v.VariableType, jobStruct.FullName));
            if (local == null)
                continue;

            var store = method.Body.Instructions
                .LastOrDefault(i => IsStloc(i) && GetLocalFromInstruction(i, method.Body) == local);
            if (store == null)
                continue;

            var il = method.Body.GetILProcessor();
            var insertAfter = store;
            var init = new List<Instruction> { Instruction.Create(OpCodes.Ldloca, local) };
            init.AddRange(CloneInstructionList(getLookupPrefix));
            init.Add(Instruction.Create(OpCodes.Call, getLookupRef));
            init.Add(Instruction.Create(OpCodes.Stfld, module.ImportReference(lookupField)));
            foreach (var instr in init)
            {
                il.InsertAfter(insertAfter, instr);
                insertAfter = instr;
            }

            method.Body.OptimizeMacros();
            StructEntropyLogger.Log($"[SER]   Injected standalone lookup init for {targetType.Name} in {method.DeclaringType.Name}.{method.Name}");
            return true;
        }

        return false;
    }

    private static bool IsScheduleCall(Instruction instr)
        => (instr?.OpCode.Code == Code.Call || instr?.OpCode.Code == Code.Callvirt) &&
           instr.Operand is MethodReference mr &&
           mr.Name.StartsWith("Schedule", StringComparison.Ordinal);

    private static VariableDefinition FindJobLocalForScheduleCall(
        MethodDefinition method,
        Instruction scheduleCall,
        TypeDefinition jobStruct)
    {
        if (method == null || scheduleCall == null || jobStruct == null)
            return null;

        for (var scan = scheduleCall.Previous; scan != null; scan = scan.Previous)
        {
            var local = GetLocalFromInstruction(scan, method.Body);
            if (local != null && TypeRefFullNameEquals(local.VariableType, jobStruct.FullName))
                return local;
        }

        return null;
    }

    private static bool TryBuildSystemApiGetComponentLookupSequence(
        ModuleDefinition module,
        TypeDefinition targetType,
        out List<Instruction> prefix,
        out MethodReference getLookupRef)
    {
        prefix = null;
        getLookupRef = null;

        foreach (var method in EnumerateAllTypes(module).SelectMany(t => t.Methods).Where(m => m.HasBody))
        {
            foreach (var instr in method.Body.Instructions)
            {
                if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
                    instr.Operand is not GenericInstanceMethod gim ||
                    gim.Name != "GetComponentLookup" ||
                    gim.GenericArguments.Count != 1)
                    continue;

                var genericRef = BuildGenericMethodRef(module, instr, targetType);
                if (genericRef == null)
                    continue;

                List<Instruction> clonedPrefix;
                if (GetPopCount(instr) == 0)
                {
                    clonedPrefix = new List<Instruction>();
                }
                else
                {
                    var seqStart = FindCallArgumentSequenceStart(instr);
                    if (seqStart == null)
                        continue;

                    clonedPrefix = CloneInstructionRange(seqStart, instr.Previous);
                    if (clonedPrefix == null ||
                        clonedPrefix.Any(i =>
                            (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt) &&
                            i.Operand is MethodReference mr &&
                            mr.DeclaringType.Name.StartsWith("ComponentLookup", StringComparison.Ordinal)))
                        continue;
                }

                prefix = clonedPrefix;
                getLookupRef = genericRef;
                return true;
            }
        }

        return false;
    }

    private static void CopyFieldMetadata(ModuleDefinition module, FieldDefinition source, FieldDefinition target)
    {
        if (source == null || target == null) return;

        foreach (var attr in source.CustomAttributes)
            target.CustomAttributes.Add(CloneCustomAttribute(module, attr));
    }

    private static CustomAttribute CloneCustomAttribute(ModuleDefinition module, CustomAttribute source)
    {
        var clone = new CustomAttribute(module.ImportReference(source.Constructor));

        foreach (var arg in source.ConstructorArguments)
            clone.ConstructorArguments.Add(CloneCustomAttributeArgument(module, arg));

        foreach (var field in source.Fields)
            clone.Fields.Add(new CustomAttributeNamedArgument(
                field.Name,
                CloneCustomAttributeArgument(module, field.Argument)));

        foreach (var prop in source.Properties)
            clone.Properties.Add(new CustomAttributeNamedArgument(
                prop.Name,
                CloneCustomAttributeArgument(module, prop.Argument)));

        return clone;
    }

    private static CustomAttributeArgument CloneCustomAttributeArgument(
        ModuleDefinition module, CustomAttributeArgument source)
    {
        var argType = module.ImportReference(source.Type);

        if (source.Value is CustomAttributeArgument[] arr)
        {
            var cloned = arr.Select(x => CloneCustomAttributeArgument(module, x)).ToArray();
            return new CustomAttributeArgument(argType, cloned);
        }

        if (source.Value is TypeReference tr)
            return new CustomAttributeArgument(argType, module.ImportReference(tr));

        return new CustomAttributeArgument(argType, source.Value);
    }

    private static FieldDefinition FindComponentTypeHandleField(TypeDefinition rootType, TypeDefinition componentType)
        => FindComponentTypeHandleField(rootType, componentType, null);

    private static FieldDefinition FindComponentTypeHandleField(
        TypeDefinition rootType, TypeDefinition componentType, string requiredAccessMode)
    {
        foreach (var f in rootType.Fields)
        {
            if (f.FieldType is GenericInstanceType git &&
                git.ElementType.Name.StartsWith("ComponentTypeHandle") &&
                git.GenericArguments.Count == 1 &&
                TypeRefFullNameEquals(git.GenericArguments[0], componentType.FullName) &&
                HandleAccessModeMatches(f.Name, requiredAccessMode))
                return f;
        }
        foreach (var nested in rootType.NestedTypes)
        {
            var found = FindComponentTypeHandleField(nested, componentType, requiredAccessMode);
            if (found != null) return found;
        }
        return null;
    }

    private static TypeDefinition FindTypeOwningField(TypeDefinition root, FieldDefinition field)
    {
        if (root.Fields.Contains(field)) return root;
        foreach (var nested in root.NestedTypes)
        {
            var r = FindTypeOwningField(nested, field);
            if (r != null) return r;
        }
        return null;
    }

    private static TypeDefinition FindTypeInModule(ModuleDefinition module, string fullName)
        => EnumerateAllTypes(module).FirstOrDefault(t => t.FullName == fullName);

    private static VariableDefinition FindLocalOfType(MethodBody body, TypeDefinition type)
    {
        if (body == null || !body.HasVariables) return null;
        return body.Variables.FirstOrDefault(v => GetBaseTypeName(v.VariableType) == type.FullName);
    }

    private static VariableDefinition FindPreferredPeerLocal(MethodDefinition method, TypeDefinition targetType)
    {
        if (method?.Body == null || targetType == null)
            return null;

        foreach (var instr in method.Body.Instructions)
        {
            if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
                instr.Operand is not MethodReference mr)
                continue;

            bool isTargetMaterializer =
                IsBakerAddComponentCall(mr, targetType) ||
                IsEcbAddComponentCall(mr, targetType) ||
                IsEntityManagerSetComponentDataMethod(mr, targetType);

            if (!isTargetMaterializer)
                continue;

            var valueLoad = instr.Previous;
            if (valueLoad == null || !IsValueLoad(valueLoad))
                continue;

            var local = GetLocalFromInstruction(valueLoad, method.Body);
            if (local != null && GetBaseTypeName(local.VariableType) == targetType.FullName)
                return local;
        }

        return null;
    }
}
