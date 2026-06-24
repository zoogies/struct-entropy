using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using StructEntropy.Rewriter;
using static StructEntropy.Rewriter.ILInstructionHelpers;
using static StructEntropy.Rewriter.TypeResolutionHelpers;

public static partial class StructEntropyRewriter
{
    private static IEnumerable<UncheckedRefRoSource> FindUncheckedRefRoSources(
        MethodDefinition method, Relocation reloc)
    {
        var result = new List<UncheckedRefRoSource>();

        foreach (var instr in method.Body.Instructions)
        {
            if (!TryMatchUncheckedRefRoFieldAccess(method, instr, reloc, out var sourceLocal))
                continue;

            var existing = result.FirstOrDefault(x => x.SourceLocal == sourceLocal);
            if (existing != null) continue;

            if (!TryFindEnumeratorProducerForUncheckedRefLocal(method, sourceLocal, out var enumLocal, out var enumType, out var stores))
                continue;

            result.Add(new UncheckedRefRoSource
            {
                SourceLocal = sourceLocal,
                EnumeratorLocal = enumLocal,
                EnumeratorType = enumType,
                StoreInstructions = stores
            });
        }

        return result;
    }

    private static bool TryMatchUncheckedRefRoFieldAccess(
        MethodDefinition method, Instruction instr, Relocation reloc,
        out VariableDefinition sourceLocal)
    {
        sourceLocal = null;
        if (!IsFieldAccess(instr, reloc.Field, reloc.SourceTypeFullName)) return false;

        var getValueCall = instr.Previous;
        if (getValueCall == null ||
            (getValueCall.OpCode.Code != Code.Call && getValueCall.OpCode.Code != Code.Callvirt) ||
            getValueCall.Operand is not MethodReference mr ||
            mr.Name != "get_ValueRO" ||
            mr.DeclaringType is not GenericInstanceType git ||
            !git.ElementType.Name.StartsWith("UncheckedRefRO") ||
            git.GenericArguments.Count != 1 ||
            !TypeRefFullNameEquals(git.GenericArguments[0], reloc.SourceType.FullName))
            return false;

        var localLoad = getValueCall.Previous;
        if (localLoad == null || !IsAddressLoad(localLoad)) return false;

        sourceLocal = GetLocalFromInstruction(localLoad, method.Body);
        return sourceLocal != null && IsUncheckedRefRoTypeOf(sourceLocal.VariableType, reloc.SourceType);
    }

    private static bool TryFindEnumeratorProducerForUncheckedRefLocal(
        MethodDefinition method, VariableDefinition sourceLocal,
        out VariableDefinition enumLocal, out TypeDefinition enumType, out List<Instruction> stores)
    {
        enumLocal = null;
        enumType = null;
        stores = new List<Instruction>();

        foreach (var instr in method.Body.Instructions)
        {
            if (!IsStloc(instr) || GetLocalFromInstruction(instr, method.Body) != sourceLocal)
                continue;

            if (!TryFindEnumeratorCurrentBeforeStore(instr, method, sourceLocal.VariableType, out var currentEnumLocal, out var currentEnumType))
                continue;

            if (enumLocal != null && enumLocal != currentEnumLocal)
                return false;

            enumLocal = currentEnumLocal;
            enumType = currentEnumType;
            stores.Add(instr);
        }

        return enumLocal != null && enumType != null && stores.Count > 0;
    }

    private static bool TryFindEnumeratorCurrentBeforeStore(
        Instruction storeInstr, MethodDefinition method, TypeReference sourceLocalType,
        out VariableDefinition enumLocal, out TypeDefinition enumType)
    {
        enumLocal = null;
        enumType = null;

        for (int depth = 0; depth < 8; depth++)
        {
            storeInstr = storeInstr.Previous;
            if (storeInstr == null) break;

            if ((storeInstr.OpCode.Code != Code.Call && storeInstr.OpCode.Code != Code.Callvirt) ||
                storeInstr.Operand is not MethodReference mr ||
                mr.Name != "get_Current" ||
                !MethodReturnsOrContainsType(mr.ReturnType, sourceLocalType))
                continue;

            var enumLoad = storeInstr.Previous;
            if (enumLoad == null || !IsAddressLoad(enumLoad)) return false;

            enumLocal = GetLocalFromInstruction(enumLoad, method.Body);
            enumType = enumLocal?.VariableType.Resolve();
            return enumLocal != null &&
                   enumType != null &&
                   enumType.Name == "Enumerator" &&
                   enumType.DeclaringType != null;
        }

        return false;
    }

    private static void InjectPeerCurrentLoads(
        ModuleDefinition module, MethodDefinition method, UncheckedRefRoSource source,
        VariableDefinition peerLocal, MethodDefinition peerCurrentMethod)
    {
        InjectPeerCurrentLoads(module, method, source.EnumeratorLocal, source.StoreInstructions, peerLocal, peerCurrentMethod);
    }

    private static void InjectPeerCurrentLoads(
        ModuleDefinition module, MethodDefinition method, VariableDefinition enumeratorLocal,
        IEnumerable<Instruction> storeInstructions, VariableDefinition peerLocal, MethodDefinition peerCurrentMethod)
    {
        var il = method.Body.GetILProcessor();
        var peerCurrentRef = module.ImportReference(peerCurrentMethod);

        foreach (var store in storeInstructions)
        {
            var insertAfter = store;
            var injected = new[]
            {
                Instruction.Create(OpCodes.Ldloca, enumeratorLocal),
                Instruction.Create(OpCodes.Call, peerCurrentRef),
                Instruction.Create(OpCodes.Stloc, peerLocal)
            };

            foreach (var instr in injected)
            {
                il.InsertAfter(insertAfter, instr);
                insertAfter = instr;
            }
        }
    }

    private static int RewriteUncheckedRefRoFieldAccesses(
        ModuleDefinition module, MethodDefinition method, Relocation reloc,
        VariableDefinition sourceLocal, VariableDefinition peerLocal)
    {
        int count = 0;
        var il = method.Body.GetILProcessor();

        foreach (var instr in method.Body.Instructions.ToList())
        {
            if (!TryMatchUncheckedRefRoFieldAccess(method, instr, reloc, out var matchedLocal) ||
                matchedLocal != sourceLocal)
                continue;

            var getValueCall = instr.Previous;
            var localLoad = getValueCall?.Previous;
            if (getValueCall == null || localLoad == null) continue;

            var peerGetValueRef = BuildGetItemRef(module, getValueCall, reloc.TargetType);
            if (peerGetValueRef == null) continue;

            ReplaceInstruction(il, localLoad, Instruction.Create(OpCodes.Ldloca, peerLocal));
            getValueCall.Operand = peerGetValueRef;
            instr.Operand = reloc.NewField;
            count++;
        }

        return count;
    }

    private static TypeReference BuildPeerUncheckedRefType(
        ModuleDefinition module, TypeReference sourceLocalType, TypeDefinition targetType)
    {
        if (sourceLocalType is not GenericInstanceType sourceGit) return null;
        var peerGit = new GenericInstanceType(sourceGit.ElementType);
        peerGit.GenericArguments.Add(module.ImportReference(targetType));
        return module.ImportReference(peerGit);
    }

    private static MethodDefinition EnsureIfePeerCurrentAccessor(
        ModuleDefinition module, TypeDefinition enumeratorType, Relocation reloc)
    {
        return EnsureIfePeerCurrentAccessor(module, enumeratorType, reloc, null);
    }

    private static MethodDefinition EnsureIfePeerCurrentAccessor(
        ModuleDefinition module, TypeDefinition enumeratorType, Relocation reloc, TypeReference sourceLocalType)
    {
        string accessMode = GetUncheckedRefAccessMode(sourceLocalType);
        string accessorSuffix = string.Equals(accessMode, "RW", StringComparison.Ordinal) ? "_RW" : string.Empty;

        var peerCurrent = enumeratorType.Methods.FirstOrDefault(m => m.Name == $"__zd_get_Current_{reloc.TargetType.Name}{accessorSuffix}");
        if (peerCurrent != null) return peerCurrent;

        var ifeType = enumeratorType.DeclaringType;
        if (ifeType == null)
            return null;

        var resolvedChunkType = ifeType.NestedTypes.FirstOrDefault(t => t.Name == "ResolvedChunk");
        var typeHandleType = ifeType.NestedTypes.FirstOrDefault(t => t.Name == "TypeHandle");
        if (resolvedChunkType == null || typeHandleType == null)
            return null;

        var srcTypeHandleField = FindComponentTypeHandleField(typeHandleType, reloc.SourceType);
        var sourceGet = resolvedChunkType.Methods.FirstOrDefault(m => m.Name == "Get" && m.Parameters.Count == 1);
        var sourceUnsafeCall = sourceGet?.Body?.Instructions.FirstOrDefault(i =>
            (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt) &&
            i.Operand is GenericInstanceMethod gim &&
            (gim.Name.Contains("UnsafeGetUncheckedRefRO") || gim.Name.Contains("UnsafeGetUncheckedRefRW")) &&
            gim.GenericArguments.Count == 1 &&
            TypeRefFullNameEquals(gim.GenericArguments[0], reloc.SourceType.FullName));
        bool resolvedChunkStoresHandle = SourceUncheckedRefCallNeedsResolvedHandle(sourceUnsafeCall);
        var srcResolvedHandleField = resolvedChunkStoresHandle
            ? FindComponentTypeHandleField(resolvedChunkType, reloc.SourceType)
            : null;
        if (srcTypeHandleField == null || (resolvedChunkStoresHandle && srcResolvedHandleField == null))
            return null;

        var srcResolvedIntPtrField = resolvedChunkStoresHandle
            ? FindSiblingIntPtrField(resolvedChunkType, srcResolvedHandleField)
            : FindUncheckedRefIntPtrFieldUsedByResolvedChunkGet(sourceGet, sourceUnsafeCall, resolvedChunkType);
        if (srcResolvedIntPtrField == null)
            return null;

        var peerHandleField = FindComponentTypeHandleField(typeHandleType, reloc.TargetType, accessMode);
        FieldDefinition peerResolvedHandleField = null;
        FieldDefinition peerResolvedIntPtrField = null;
        if (peerHandleField != null)
        {
            string existingPrefix = GetItemPrefix(peerHandleField.Name);
            if (!string.IsNullOrEmpty(existingPrefix))
            {
                peerResolvedHandleField = resolvedChunkType.Fields.FirstOrDefault(f => f.Name == $"{existingPrefix}_TypeHandle");
                peerResolvedIntPtrField = resolvedChunkType.Fields.FirstOrDefault(f => f.Name == $"{existingPrefix}_IntPtr");
            }
        }

        if (peerHandleField == null ||
            (resolvedChunkStoresHandle && peerResolvedHandleField == null) ||
            peerResolvedIntPtrField == null)
        {
            var nextIndex = GetNextItemIndex(typeHandleType.Fields.Concat(resolvedChunkType.Fields));
            var prefix = $"item{nextIndex}";
            string handleModeSuffix = string.Equals(accessMode, "RW", StringComparison.Ordinal) ? "RW" : "RO";

            if (peerHandleField == null)
            {
                peerHandleField = new FieldDefinition(
                    $"{prefix}_ComponentTypeHandle_{handleModeSuffix}",
                    srcTypeHandleField.Attributes,
                    BuildComponentTypeHandleType(module, srcTypeHandleField.FieldType, reloc.TargetType));
                CopyFieldMetadata(module, srcTypeHandleField, peerHandleField);
                typeHandleType.Fields.Add(peerHandleField);
            }

            if (resolvedChunkStoresHandle && peerResolvedHandleField == null)
            {
                peerResolvedHandleField = new FieldDefinition(
                    $"{prefix}_TypeHandle",
                    srcResolvedHandleField.Attributes,
                    BuildComponentTypeHandleType(module, srcResolvedHandleField.FieldType, reloc.TargetType));
                CopyFieldMetadata(module, srcResolvedHandleField, peerResolvedHandleField);
                resolvedChunkType.Fields.Add(peerResolvedHandleField);
            }

            if (peerResolvedIntPtrField == null)
            {
                peerResolvedIntPtrField = new FieldDefinition(
                    $"{prefix}_IntPtr",
                    srcResolvedIntPtrField.Attributes,
                    module.TypeSystem.IntPtr);
                resolvedChunkType.Fields.Add(peerResolvedIntPtrField);
            }
        }

        InjectPeerHandleInit(module, typeHandleType, reloc, srcTypeHandleField, peerHandleField);
        InjectPeerHandleUpdate(module, typeHandleType, reloc, srcTypeHandleField, peerHandleField);
        InjectPeerResolve(module, typeHandleType, resolvedChunkType, reloc, srcTypeHandleField, peerHandleField, peerResolvedHandleField, peerResolvedIntPtrField);

        var peerGetMethod = EnsureResolvedChunkPeerGetMethod(module, resolvedChunkType, reloc, peerResolvedHandleField, peerResolvedIntPtrField, sourceLocalType);
        if (peerGetMethod == null) return null;

        var sourceCurrent = enumeratorType.Methods.FirstOrDefault(m => m.Name == "get_Current");
        if (sourceCurrent == null) return null;

        peerCurrent = new MethodDefinition(
            $"__zd_get_Current_{reloc.TargetType.Name}{accessorSuffix}",
            sourceCurrent.Attributes,
            module.ImportReference(peerGetMethod.ReturnType));

        var body = peerCurrent.Body;
        body.InitLocals = false;
        var il = body.GetILProcessor();
        var resolvedChunkField = enumeratorType.Fields.FirstOrDefault(f => f.Name == "_resolvedChunk");
        var currentEntityIndexField = enumeratorType.Fields.FirstOrDefault(f => f.Name == "_currentEntityIndex");
        if (resolvedChunkField == null || currentEntityIndexField == null) return null;

        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldflda, module.ImportReference(resolvedChunkField)));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, module.ImportReference(currentEntityIndexField)));
        il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(peerGetMethod)));
        il.Append(Instruction.Create(OpCodes.Ret));

        enumeratorType.Methods.Add(peerCurrent);
        return peerCurrent;
    }

    private static MethodDefinition EnsureResolvedChunkPeerGetMethod(
        ModuleDefinition module, TypeDefinition resolvedChunkType, Relocation reloc,
        FieldDefinition peerResolvedHandleField, FieldDefinition peerResolvedIntPtrField,
        TypeReference sourceLocalType)
    {
        string accessMode = GetUncheckedRefAccessMode(sourceLocalType);
        string accessorSuffix = string.Equals(accessMode, "RW", StringComparison.Ordinal) ? "_RW" : string.Empty;
        var method = resolvedChunkType.Methods.FirstOrDefault(m => m.Name == $"__zd_Get_{reloc.TargetType.Name}{accessorSuffix}");
        if (method != null) return method;

        var sourceGet = resolvedChunkType.Methods.FirstOrDefault(m => m.Name == "Get" && m.Parameters.Count == 1);
        if (sourceGet == null) return null;

        var sourceUnsafeCall = sourceGet.Body.Instructions.FirstOrDefault(i =>
            (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt) &&
            i.Operand is GenericInstanceMethod gim &&
            (gim.Name.Contains("UnsafeGetUncheckedRefRO") || gim.Name.Contains("UnsafeGetUncheckedRefRW")) &&
            gim.GenericArguments.Count == 1 &&
            TypeRefFullNameEquals(gim.GenericArguments[0], reloc.SourceType.FullName));
        if (sourceUnsafeCall == null) return null;
        bool needsResolvedHandle = SourceUncheckedRefCallNeedsResolvedHandle(sourceUnsafeCall);

        var peerUnsafeRef = BuildGenericMethodRef(module, sourceUnsafeCall, reloc.TargetType);
        if (peerUnsafeRef == null) return null;

        var peerReturnType = BuildPeerUncheckedRefType(module, ((MethodReference)sourceUnsafeCall.Operand).ReturnType, reloc.TargetType);
        if (peerReturnType == null) return null;

        method = new MethodDefinition(
            $"__zd_Get_{reloc.TargetType.Name}{accessorSuffix}",
            sourceGet.Attributes,
            peerReturnType);
        method.Parameters.Add(new ParameterDefinition(sourceGet.Parameters[0].Name, sourceGet.Parameters[0].Attributes, sourceGet.Parameters[0].ParameterType));

        var il = method.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, module.ImportReference(peerResolvedIntPtrField)));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        if (needsResolvedHandle)
        {
            if (peerResolvedHandleField == null) return null;
            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Ldflda, module.ImportReference(peerResolvedHandleField)));
        }
        il.Append(Instruction.Create(OpCodes.Call, peerUnsafeRef));
        il.Append(Instruction.Create(OpCodes.Ret));

        resolvedChunkType.Methods.Add(method);
        return method;
    }

    private static void InjectPeerHandleUpdate(
        ModuleDefinition module, TypeDefinition typeHandleType, Relocation reloc,
        FieldDefinition srcTypeHandleField, FieldDefinition peerHandleField)
    {
        var updateMethod = typeHandleType.Methods.FirstOrDefault(m => m.Name == "Update" && m.HasBody);
        if (updateMethod == null) return;
        if (updateMethod.Body.Instructions.Any(i => i.Operand is FieldReference fr && fr.Resolve() == peerHandleField))
            return;

        var srcUpdateCall = updateMethod.Body.Instructions.FirstOrDefault(i =>
            (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt) &&
            i.Operand is MethodReference mr &&
            mr.Name == "Update" &&
            mr.DeclaringType is GenericInstanceType git &&
            git.ElementType.Name.StartsWith("ComponentTypeHandle") &&
            git.GenericArguments.Count == 1 &&
            TypeRefFullNameEquals(git.GenericArguments[0], reloc.SourceType.FullName));
        if (srcUpdateCall == null) return;

        var peerUpdateRef = BuildGetItemRef(module, srcUpdateCall, reloc.TargetType);
        if (peerUpdateRef == null) return;

        var ret = updateMethod.Body.Instructions.Last();
        var il = updateMethod.Body.GetILProcessor();
        il.InsertBefore(ret, Instruction.Create(OpCodes.Ldarg_0));
        il.InsertBefore(ret, Instruction.Create(OpCodes.Ldflda, module.ImportReference(peerHandleField)));
        il.InsertBefore(ret, Instruction.Create(OpCodes.Ldarg_1));
        il.InsertBefore(ret, Instruction.Create(OpCodes.Call, peerUpdateRef));
    }

    private static void InjectPeerResolve(
        ModuleDefinition module, TypeDefinition typeHandleType, TypeDefinition resolvedChunkType, Relocation reloc,
        FieldDefinition srcTypeHandleField, FieldDefinition peerHandleField,
        FieldDefinition peerResolvedHandleField, FieldDefinition peerResolvedIntPtrField)
    {
        var resolveMethod = typeHandleType.Methods.FirstOrDefault(m => m.Name == "Resolve" && m.HasBody);
        if (resolveMethod == null) return;
        if (resolveMethod.Body.Instructions.Any(i => i.Operand is FieldReference fr && fr.Resolve() == peerResolvedIntPtrField))
            return;

        var srcUnsafeCall = resolveMethod.Body.Instructions.FirstOrDefault(i =>
            (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt) &&
            i.Operand is GenericInstanceMethod gim &&
            gim.Name.Contains("UnsafeGetChunkNativeArray") &&
            gim.GenericArguments.Count == 1 &&
            TypeRefFullNameEquals(gim.GenericArguments[0], reloc.SourceType.FullName));
        if (srcUnsafeCall == null) return;

        var peerUnsafeRef = BuildGenericMethodRef(module, srcUnsafeCall, reloc.TargetType);
        if (peerUnsafeRef == null) return;

        var ret = resolveMethod.Body.Instructions.LastOrDefault(i => i.OpCode == OpCodes.Ret);
        var resolvedLocal = resolveMethod.Body.Variables.FirstOrDefault(v => v.VariableType.Resolve() == resolvedChunkType);
        var archetypeChunkParam = resolveMethod.Parameters.FirstOrDefault();
        if (ret == null || resolvedLocal == null || archetypeChunkParam == null) return;

        // Resolve methods for value-type ResolvedChunk commonly end with:
        //   ldloc.<resolvedLocal>
        //   ret
        // Injecting before the ret mutates the local after the return-value copy is
        // already on the stack, so the caller receives a stale chunk without the peer fields.
        // Anchor before the final resolved-local load when present.
        var returnLoad = ret.Previous != null && IsValueLoad(ret.Previous) &&
                         GetLocalFromInstruction(ret.Previous, resolveMethod.Body) == resolvedLocal
            ? ret.Previous
            : ret;

        var il = resolveMethod.Body.GetILProcessor();
        if (peerResolvedHandleField != null)
        {
            il.InsertBefore(returnLoad, Instruction.Create(OpCodes.Ldloca, resolvedLocal));
            il.InsertBefore(returnLoad, Instruction.Create(OpCodes.Ldarg_0));
            il.InsertBefore(returnLoad, Instruction.Create(OpCodes.Ldfld, module.ImportReference(peerHandleField)));
            il.InsertBefore(returnLoad, Instruction.Create(OpCodes.Stfld, module.ImportReference(peerResolvedHandleField)));
        }
        il.InsertBefore(returnLoad, Instruction.Create(OpCodes.Ldloca, resolvedLocal));
        il.InsertBefore(returnLoad, Instruction.Create(OpCodes.Ldarga, archetypeChunkParam));
        il.InsertBefore(returnLoad, Instruction.Create(OpCodes.Ldarg_0));
        il.InsertBefore(returnLoad, Instruction.Create(OpCodes.Ldflda, module.ImportReference(peerHandleField)));
        il.InsertBefore(returnLoad, Instruction.Create(OpCodes.Call, peerUnsafeRef));
        il.InsertBefore(returnLoad, Instruction.Create(OpCodes.Stfld, module.ImportReference(peerResolvedIntPtrField)));
    }

    private static FieldDefinition FindSiblingIntPtrField(TypeDefinition resolvedChunkType, FieldDefinition handleField)
    {
        var prefix = GetItemPrefix(handleField.Name);
        if (prefix == null) return null;
        return resolvedChunkType.Fields.FirstOrDefault(f => f.Name == $"{prefix}_IntPtr");
    }

    private static FieldDefinition FindUncheckedRefIntPtrFieldUsedByResolvedChunkGet(
        MethodDefinition sourceGet, Instruction sourceUnsafeCall, TypeDefinition resolvedChunkType)
    {
        if (sourceGet?.HasBody != true || sourceUnsafeCall == null || resolvedChunkType == null)
            return null;

        // In player builds, some IFEs store only IntPtr fields in ResolvedChunk.
        // QueryEnumerableWithEntity variants can load multiple IntPtr fields (component + entity),
        // so choose the field that directly feeds the UnsafeGetUncheckedRef* call rather than
        // requiring uniqueness across the whole method.
        for (var instr = sourceUnsafeCall.Previous; instr != null; instr = instr.Previous)
        {
            if (instr.Operand is not FieldReference fr)
                continue;

            if (fr.FieldType.FullName != resolvedChunkType.Module.TypeSystem.IntPtr.FullName)
                continue;

            var resolved = fr.Resolve();
            if (resolved == null || resolved.DeclaringType != resolvedChunkType)
                continue;

            return resolved;
        }
        return null;
    }

    private static bool SourceUncheckedRefCallNeedsResolvedHandle(Instruction sourceUnsafeCall)
    {
        if (sourceUnsafeCall?.Operand is not MethodReference mr)
            return false;

        return mr.Parameters.Count >= 3;
    }

    private static int GetNextItemIndex(IEnumerable<FieldDefinition> fields)
    {
        int max = 0;
        foreach (var field in fields)
        {
            var prefix = GetItemPrefix(field.Name);
            if (prefix == null || prefix.Length <= 4) continue;
            if (int.TryParse(prefix.Substring(4), out int idx) && idx > max)
                max = idx;
        }
        return max + 1;
    }

    private static string GetItemPrefix(string fieldName)
    {
        if (string.IsNullOrEmpty(fieldName) || !fieldName.StartsWith("item", StringComparison.Ordinal))
            return null;

        int underscore = fieldName.IndexOf('_');
        return underscore > 4 ? fieldName.Substring(0, underscore) : null;
    }

    private static TypeReference BuildComponentTypeHandleType(
        ModuleDefinition module, TypeReference sourceHandleType, TypeDefinition targetType)
    {
        if (sourceHandleType is not GenericInstanceType srcGit) return null;
        var peerGit = new GenericInstanceType(srcGit.ElementType);
        peerGit.GenericArguments.Add(module.ImportReference(targetType));
        return module.ImportReference(peerGit);
    }

    private static bool IsUncheckedRefRoTypeOf(TypeReference typeRef, TypeDefinition componentType)
    {
        if (typeRef is not GenericInstanceType git) return false;
        return git.ElementType.Name.StartsWith("UncheckedRefRO") &&
               git.GenericArguments.Count == 1 &&
               TypeRefFullNameEquals(git.GenericArguments[0], componentType.FullName);
    }

    private static string GetUncheckedRefAccessMode(TypeReference typeRef)
    {
        if (typeRef is not GenericInstanceType git)
            return null;

        if (git.ElementType.Name.StartsWith("UncheckedRefRW", StringComparison.Ordinal))
            return "RW";

        if (git.ElementType.Name.StartsWith("UncheckedRefRO", StringComparison.Ordinal))
            return "RO";

        return null;
    }

    private static bool HandleAccessModeMatches(string fieldName, string requiredAccessMode)
    {
        if (string.IsNullOrEmpty(requiredAccessMode))
            return true;

        return string.Equals(GetHandleAccessMode(fieldName), requiredAccessMode, StringComparison.Ordinal);
    }

    private static string GetHandleAccessMode(string fieldName)
    {
        if (string.IsNullOrEmpty(fieldName))
            return string.Empty;

        if (fieldName.IndexOf("_RW", StringComparison.OrdinalIgnoreCase) >= 0)
            return "RW";

        if (fieldName.IndexOf("_RO", StringComparison.OrdinalIgnoreCase) >= 0)
            return "RO";

        return string.Empty;
    }

    private static bool MethodReturnsOrContainsType(TypeReference returnType, TypeReference targetType)
    {
        if (returnType == null || targetType == null) return false;
        if (returnType.FullName == targetType.FullName) return true;
        if (returnType is GenericInstanceType git)
            return git.GenericArguments.Any(arg => MethodReturnsOrContainsType(arg, targetType));
        return false;
    }
}
