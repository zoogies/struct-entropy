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
    //  InlineForEachRewriter: UncheckedRefRO inline ForEach peer injection
    //  Handles SystemAPI.Query<RefRO<SourceType>> reads by preferring an
    //  existing target peer, then an entity-indexed ComponentLookup for
    //  WithEntityAccess loops, and finally a read-only generated peer accessor
    //  for non-entity loops.
    // --------------------------------------------------------------
    private sealed class UncheckedRefRoSource
    {
        public VariableDefinition SourceLocal;
        public VariableDefinition EnumeratorLocal;
        public TypeDefinition EnumeratorType;
        public List<Instruction> StoreInstructions = new();
    }

    private static int ApplyInlineForEach(
        ModuleDefinition module, MethodDefinition method, Relocation reloc)
    {
        var sources = FindUncheckedRefRoSources(method, reloc).ToList();
        if (sources.Count == 0) return 0;

        int total = 0;
        foreach (var source in sources)
        {
            var existingPeerLocal = FindExistingUncheckedRefRoPeerLocal(method, source, reloc.TargetType);
            if (existingPeerLocal != null)
            {
                int existingPeerRewrites = RewriteUncheckedRefRoFieldAccesses(module, method, reloc, source.SourceLocal, existingPeerLocal);
                if (existingPeerRewrites > 0)
                {
                    StructEntropyLogger.Log($"[SER]   InlineForEachRewriter used existing {reloc.TargetType.Name} query peer in {method.DeclaringType.Name}.{method.Name}: {existingPeerRewrites} access(es)");
                }
                total += existingPeerRewrites;
                continue;
            }

            StructEntropyLogger.Log(
                $"[SER]   InlineForEachRewriter did not find existing {reloc.TargetType.Name} query peer in {method.DeclaringType.Name}.{method.Name}.");

            int entityLookupRewrites = ApplyInlineForEachEntityLookup(module, method, reloc, source);
            if (entityLookupRewrites > 0)
            {
                StructEntropyLogger.Log($"[SER]   InlineForEachRewriter used {reloc.TargetType.Name} lookup by entity in {method.DeclaringType.Name}.{method.Name}: {entityLookupRewrites} access(es)");
                total += entityLookupRewrites;
                continue;
            }

            int entityArrayRewrites = ApplyInlineForEachEntityArrayLookup(module, method, reloc, source);
            if (entityArrayRewrites > 0)
            {
                StructEntropyLogger.Log($"[SER]   InlineForEachRewriter used {reloc.TargetType.Name} entity-array lookup in {method.DeclaringType.Name}.{method.Name}: {entityArrayRewrites} access(es)");
                total += entityArrayRewrites;
            }
        }

        if (total > 0)
        {
            method.Body.OptimizeMacros();
            StructEntropyLogger.Log($"[SER]   InlineForEachRewriter in {method.DeclaringType.Name}.{method.Name}: {total} access(es)");
        }
        return total;
    }

    private static int ApplyInlineForEachEntityLookup(
        ModuleDefinition module,
        MethodDefinition method,
        Relocation reloc,
        UncheckedRefRoSource source)
    {
        if (method?.HasBody != true || method.IsStatic)
            return 0;

        if (!TryFindEntityLocalForIfeSource(method, source, out var entityLocal))
            return 0;

        var lookupField = EnsureStandaloneComponentLookupField(module, method.DeclaringType, reloc.TargetType);
        if (lookupField == null)
            return 0;

        if (!EnsureLookupFieldInitializedInMethod(module, method, lookupField, reloc.TargetType))
            return 0;

        var getItemRef = BuildComponentLookupAccessorRef(module, lookupField.FieldType, "get_Item", 1);
        if (getItemRef == null)
            return 0;

        int count = 0;
        var il = method.Body.GetILProcessor();

        foreach (var instr in method.Body.Instructions.ToList())
        {
            if (instr.OpCode.Code != Code.Ldfld ||
                !TryMatchUncheckedRefRoFieldAccess(method, instr, reloc, out var matchedLocal) ||
                matchedLocal != source.SourceLocal)
                continue;

            var getValueCall = instr.Previous;
            var localLoad = getValueCall?.Previous;
            if (getValueCall == null || localLoad == null)
                continue;

            var thisLoad = Instruction.Create(OpCodes.Ldarg_0);
            ReplaceInstruction(il, localLoad, thisLoad);
            var insertAfter = thisLoad;
            il.InsertAfter(insertAfter, Instruction.Create(OpCodes.Ldflda, module.ImportReference(lookupField)));
            insertAfter = insertAfter.Next;
            il.InsertAfter(insertAfter, Instruction.Create(OpCodes.Ldloc, entityLocal));
            insertAfter = insertAfter.Next;
            il.InsertAfter(insertAfter, Instruction.Create(OpCodes.Call, getItemRef));
            ReplaceInstruction(il, getValueCall, Instruction.Create(OpCodes.Nop));
            instr.Operand = module.ImportReference(reloc.NewField);
            count++;
        }

        return count;
    }

    private static bool EnsureLookupFieldInitializedInMethod(
        ModuleDefinition module,
        MethodDefinition method,
        FieldDefinition lookupField,
        TypeDefinition targetType)
    {
        if (module == null || method?.HasBody != true || lookupField == null || targetType == null)
            return false;

        if (method.Body.Instructions.Any(i =>
                i.OpCode.Code == Code.Stfld &&
                i.Operand is FieldReference fr &&
                fr.Resolve() == lookupField))
            return true;

        if (!TryBuildSystemStateGetComponentLookupSequence(module, method, targetType, out var getLookupPrefix, out var getLookupRef) &&
            !TryBuildSystemApiGetComponentLookupSequence(module, targetType, out getLookupPrefix, out getLookupRef))
            return false;

        var first = method.Body.Instructions.FirstOrDefault();
        if (first == null)
            return false;

        var init = new List<Instruction> { Instruction.Create(OpCodes.Ldarg_0) };
        var clonedPrefix = CloneInstructionList(getLookupPrefix);
        if (clonedPrefix == null)
            return false;
        init.AddRange(clonedPrefix);
        init.Add(Instruction.Create(OpCodes.Call, getLookupRef));
        init.Add(Instruction.Create(OpCodes.Stfld, module.ImportReference(lookupField)));

        if (init.Any(i => i == null))
            return false;

        var il = method.Body.GetILProcessor();
        foreach (var instr in init)
            il.InsertBefore(first, instr);

        StructEntropyLogger.Log($"[SER]   Initialized {targetType.Name} lookup in {method.DeclaringType.Name}.{method.Name}");
        return true;
    }

    private static bool TryBuildSystemStateGetComponentLookupSequence(
        ModuleDefinition module,
        MethodDefinition method,
        TypeDefinition targetType,
        out List<Instruction> prefix,
        out MethodReference getLookupRef)
    {
        prefix = null;
        getLookupRef = null;

        var stateParam = method?.Parameters.FirstOrDefault(p =>
            p.ParameterType is ByReferenceType br &&
            string.Equals(br.ElementType.FullName, "Unity.Entities.SystemState", StringComparison.Ordinal));
        if (module == null || stateParam == null || targetType == null)
            return false;

        foreach (var candidate in EnumerateAllTypes(module).SelectMany(t => t.Methods).Where(m => m.HasBody))
        {
            foreach (var instr in candidate.Body.Instructions)
            {
                if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
                    instr.Operand is not GenericInstanceMethod gim ||
                    gim.Name != "GetComponentLookup" ||
                    gim.DeclaringType.FullName != "Unity.Entities.SystemState" ||
                    gim.GenericArguments.Count != 1 ||
                    gim.Parameters.Count != 1 ||
                    !string.Equals(gim.Parameters[0].ParameterType.FullName, module.TypeSystem.Boolean.FullName, StringComparison.Ordinal))
                    continue;

                var targetRef = BuildGenericMethodRef(module, instr, targetType);
                if (targetRef == null)
                    continue;

                prefix = new List<Instruction>
                {
                    Instruction.Create(OpCodes.Ldarg, stateParam),
                    Instruction.Create(OpCodes.Ldc_I4_1)
                };
                getLookupRef = targetRef;
                return true;
            }
        }

        return false;
    }

    private static int ApplyInlineForEachEntityArrayLookup(
        ModuleDefinition module,
        MethodDefinition method,
        Relocation reloc,
        UncheckedRefRoSource source)
    {
        if (method?.HasBody != true || method.IsStatic)
            return 0;

        var queryField = FindIfeQueryFieldForMethod(method, source.EnumeratorType);
        if (queryField == null)
            return 0;

        if (!TryBuildEntityArrayLookupRefs(
                module,
                reloc.TargetType,
                out var allocatorImplicitRef,
                out var toEntityArrayRef,
                out var nativeArrayEntityType,
                out var nativeArrayGetItemRef,
                out var getEntityManagerRef,
                out var getComponentDataRef))
            return 0;

        var enumeratorStore = method.Body.Instructions.FirstOrDefault(i =>
            IsStloc(i) && GetLocalFromInstruction(i, method.Body) == source.EnumeratorLocal);
        if (enumeratorStore == null)
            return 0;

        var entityArrayLocal = new VariableDefinition(module.ImportReference(nativeArrayEntityType));
        var indexLocal = new VariableDefinition(module.TypeSystem.Int32);
        var entityManagerLocal = new VariableDefinition(module.ImportReference(getEntityManagerRef.ReturnType));
        method.Body.Variables.Add(entityArrayLocal);
        method.Body.Variables.Add(indexLocal);
        method.Body.Variables.Add(entityManagerLocal);
        method.Body.InitLocals = true;

        var il = method.Body.GetILProcessor();
        var insertAfter = enumeratorStore;
        var init = new[]
        {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldflda, module.ImportReference(queryField)),
            Instruction.Create(OpCodes.Ldc_I4_2),
            Instruction.Create(OpCodes.Call, allocatorImplicitRef),
            Instruction.Create(OpCodes.Call, toEntityArrayRef),
            Instruction.Create(OpCodes.Stloc, entityArrayLocal),
            Instruction.Create(OpCodes.Ldc_I4_0),
            Instruction.Create(OpCodes.Stloc, indexLocal),
            Instruction.Create(OpCodes.Ldarg_1),
            Instruction.Create(OpCodes.Call, getEntityManagerRef),
            Instruction.Create(OpCodes.Stloc, entityManagerLocal)
        };
        foreach (var instr in init)
        {
            il.InsertAfter(insertAfter, instr);
            insertAfter = instr;
        }

        int count = 0;
        foreach (var instr in method.Body.Instructions.ToList())
        {
            if (instr.OpCode.Code != Code.Ldfld ||
                !TryMatchUncheckedRefRoFieldAccess(method, instr, reloc, out var matchedLocal) ||
                matchedLocal != source.SourceLocal)
                continue;

            var getValueCall = instr.Previous;
            var localLoad = getValueCall?.Previous;
            if (getValueCall == null || localLoad == null)
                continue;

            var entityManagerLoad = Instruction.Create(OpCodes.Ldloca, entityManagerLocal);
            ReplaceInstruction(il, localLoad, entityManagerLoad);
            var after = entityManagerLoad;
            il.InsertAfter(after, Instruction.Create(OpCodes.Ldloca, entityArrayLocal));
            after = after.Next;
            il.InsertAfter(after, Instruction.Create(OpCodes.Ldloc, indexLocal));
            after = after.Next;
            il.InsertAfter(after, Instruction.Create(OpCodes.Call, nativeArrayGetItemRef));
            after = after.Next;
            il.InsertAfter(after, Instruction.Create(OpCodes.Call, getComponentDataRef));
            ReplaceInstruction(il, getValueCall, Instruction.Create(OpCodes.Nop));
            instr.Operand = module.ImportReference(reloc.NewField);
            count++;
        }

        if (count == 0)
            return 0;

        foreach (var moveNextCall in method.Body.Instructions.ToList())
        {
            if ((moveNextCall.OpCode.Code != Code.Call && moveNextCall.OpCode.Code != Code.Callvirt) ||
                moveNextCall.Operand is not MethodReference mr ||
                mr.Name != "MoveNext" ||
                GetLocalFromInstruction(moveNextCall.Previous, method.Body) != source.EnumeratorLocal)
                continue;

            il.InsertBefore(moveNextCall.Previous, Instruction.Create(OpCodes.Ldloc, indexLocal));
            il.InsertBefore(moveNextCall.Previous, Instruction.Create(OpCodes.Ldc_I4_1));
            il.InsertBefore(moveNextCall.Previous, Instruction.Create(OpCodes.Add));
            il.InsertBefore(moveNextCall.Previous, Instruction.Create(OpCodes.Stloc, indexLocal));
            break;
        }

        return count;
    }

    private static bool TryBuildEntityArrayLookupRefs(
        ModuleDefinition module,
        TypeDefinition targetType,
        out MethodReference allocatorImplicitRef,
        out MethodReference toEntityArrayRef,
        out TypeReference nativeArrayEntityType,
        out MethodReference nativeArrayGetItemRef,
        out MethodReference getEntityManagerRef,
        out MethodReference getComponentDataRef)
    {
        allocatorImplicitRef = null;
        toEntityArrayRef = null;
        nativeArrayEntityType = null;
        nativeArrayGetItemRef = null;
        getEntityManagerRef = null;
        getComponentDataRef = null;

        foreach (var method in EnumerateAllTypes(module).SelectMany(t => t.Methods).Where(m => m.HasBody))
        {
            foreach (var instr in method.Body.Instructions)
            {
                if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
                    instr.Operand is not MethodReference mr)
                    continue;

                if (mr.Name == "op_Implicit" &&
                    mr.DeclaringType.FullName == "Unity.Collections.AllocatorManager/AllocatorHandle")
                    allocatorImplicitRef ??= module.ImportReference(mr);

                if (mr.Name == "ToEntityArray" &&
                    mr.DeclaringType.FullName == "Unity.Entities.EntityQuery" &&
                    mr.ReturnType is GenericInstanceType returnGit &&
                    returnGit.ElementType.FullName.StartsWith("Unity.Collections.NativeArray", StringComparison.Ordinal) &&
                    returnGit.GenericArguments.Count == 1 &&
                    returnGit.GenericArguments[0].FullName == "Unity.Entities.Entity")
                {
                    toEntityArrayRef ??= module.ImportReference(mr);
                    nativeArrayEntityType ??= module.ImportReference(returnGit);
                }

                if (mr.Name == "get_EntityManager" &&
                    mr.DeclaringType.FullName == "Unity.Entities.SystemState")
                    getEntityManagerRef ??= module.ImportReference(mr);

                if (mr.Name == "GetComponentData" &&
                    mr.DeclaringType.FullName == "Unity.Entities.EntityManager" &&
                    mr is GenericInstanceMethod gim &&
                    gim.GenericArguments.Count == 1)
                {
                    var imported = module.ImportReference(gim.ElementMethod);
                    var targetGet = new GenericInstanceMethod(imported);
                    targetGet.GenericArguments.Add(module.ImportReference(targetType));
                    getComponentDataRef ??= module.ImportReference(targetGet);
                }
            }
        }

        if (nativeArrayEntityType != null)
        {
            var nativeArrayDef = nativeArrayEntityType.Resolve();
            var getItemDef = nativeArrayDef?.Methods.FirstOrDefault(m => m.Name == "get_Item" && m.Parameters.Count == 1);
            if (getItemDef != null)
            {
                var getItem = new MethodReference(getItemDef.Name, getItemDef.ReturnType, module.ImportReference(nativeArrayEntityType))
                {
                    HasThis = getItemDef.HasThis,
                    ExplicitThis = getItemDef.ExplicitThis,
                    CallingConvention = getItemDef.CallingConvention
                };
                foreach (var parameter in getItemDef.Parameters)
                    getItem.Parameters.Add(new ParameterDefinition(module.ImportReference(parameter.ParameterType)));
                nativeArrayGetItemRef = module.ImportReference(getItem);
            }
        }

        return allocatorImplicitRef != null &&
               toEntityArrayRef != null &&
               nativeArrayEntityType != null &&
               nativeArrayGetItemRef != null &&
               getEntityManagerRef != null &&
               getComponentDataRef != null;
    }

    private static bool TryFindEntityLocalForIfeSource(
        MethodDefinition method,
        UncheckedRefRoSource source,
        out VariableDefinition entityLocal)
    {
        entityLocal = null;
        if (method?.HasBody != true || source == null)
            return false;

        foreach (var store in source.StoreInstructions)
        {
            if (TryFindEntityLocalFromDeconstructTemps(method, store, out entityLocal))
                return true;

            if (!TryFindTupleLocalForDeconstructStore(method, store, out var tupleLocal))
                continue;

            foreach (var instr in method.Body.Instructions)
            {
                if (!IsStloc(instr))
                    continue;

                var candidate = GetLocalFromInstruction(instr, method.Body);
                if (!IsEntityLocal(candidate))
                    continue;

                var fieldLoad = instr.Previous;
                var tupleLoad = fieldLoad?.Previous;
                if (fieldLoad?.OpCode.Code != Code.Ldfld ||
                    fieldLoad.Operand is not FieldReference fr ||
                    !fr.Name.StartsWith("Item", StringComparison.Ordinal) ||
                    GetLocalFromInstruction(tupleLoad, method.Body) != tupleLocal)
                    continue;

                entityLocal = candidate;
                return true;
            }
        }

        var entityLocals = method.Body.Variables.Where(IsEntityLocal).ToList();
        if (entityLocals.Count == 1)
        {
            entityLocal = entityLocals[0];
            return true;
        }

        return false;
    }

    private static bool TryFindEntityLocalFromDeconstructTemps(
        MethodDefinition method,
        Instruction sourceStore,
        out VariableDefinition entityLocal)
    {
        entityLocal = null;
        if (method?.HasBody != true || sourceStore == null || !IsStloc(sourceStore))
            return false;

        var sourceTemp = GetLocalFromInstruction(sourceStore.Previous, method.Body);
        if (sourceTemp == null)
            return false;

        Instruction deconstructCall = null;
        for (var scan = sourceStore.Previous; scan != null; scan = scan.Previous)
        {
            if ((scan.OpCode.Code != Code.Call && scan.OpCode.Code != Code.Callvirt) ||
                scan.Operand is not MethodReference mr ||
                mr.Name != "Deconstruct")
                continue;

            deconstructCall = scan;
            break;
        }

        if (deconstructCall == null)
            return false;

        bool hasSourceTempOut = false;
        VariableDefinition entityTemp = null;
        int budget = 8;
        for (var scan = deconstructCall.Previous; scan != null && budget-- > 0; scan = scan.Previous)
        {
            var local = GetLocalFromInstruction(scan, method.Body);
            if (local == sourceTemp)
            {
                hasSourceTempOut = true;
                continue;
            }

            if (local == null || !IsEntityLocal(local))
                continue;

            entityTemp ??= local;
        }

        if (!hasSourceTempOut || !IsEntityLocal(entityTemp))
            return false;

        foreach (var instr in method.Body.Instructions)
        {
            if (!IsStloc(instr))
                continue;

            var candidate = GetLocalFromInstruction(instr, method.Body);
            if (!IsEntityLocal(candidate))
                continue;

            var loaded = GetLocalFromInstruction(instr.Previous, method.Body);
            if (loaded != entityTemp)
                continue;

            entityLocal = candidate;
            return true;
        }

        return false;
    }

    private static bool TryFindTupleLocalForDeconstructStore(
        MethodDefinition method,
        Instruction store,
        out VariableDefinition tupleLocal)
    {
        tupleLocal = null;
        if (method?.HasBody != true || store == null || !IsStloc(store))
            return false;

        var fieldLoad = store.Previous;
        var tupleLoad = fieldLoad?.Previous;
        if (fieldLoad?.OpCode.Code != Code.Ldfld ||
            fieldLoad.Operand is not FieldReference fr ||
            !fr.Name.StartsWith("Item", StringComparison.Ordinal))
            return false;

        tupleLocal = GetLocalFromInstruction(tupleLoad, method.Body);
        return tupleLocal != null;
    }

    private static bool IsEntityLocal(VariableDefinition local)
        => local != null &&
           string.Equals(local.VariableType.FullName, "Unity.Entities.Entity", StringComparison.Ordinal);

    private static VariableDefinition FindExistingUncheckedRefRoPeerLocal(
        MethodDefinition method,
        UncheckedRefRoSource source,
        TypeDefinition targetType)
    {
        if (method?.Body == null || source?.EnumeratorLocal == null || targetType == null)
            return null;

        foreach (var local in method.Body.Variables)
        {
            if (!IsUncheckedRefRoTypeOf(local.VariableType, targetType))
                continue;

            if (!TryFindEnumeratorProducerForUncheckedRefLocal(
                    method,
                    local,
                    out var enumLocal,
                    out var enumType,
                    out _) ||
                enumLocal != source.EnumeratorLocal ||
                enumType != source.EnumeratorType)
                continue;

            return local;
        }

        return null;
    }

    private static void EnsureIfePeerQuerySupport(
        ModuleDefinition module, MethodDefinition outerMethod,
        UncheckedRefRoSource source, Relocation reloc)
    {
        EnsureIfePeerQuerySupport(module, outerMethod, source.EnumeratorType, reloc);
    }

    private static void EnsureIfePeerQuerySupport(
        ModuleDefinition module, MethodDefinition outerMethod,
        TypeDefinition enumeratorType, Relocation reloc)
    {
        EnsureIfeCompleteDependencies(module, enumeratorType, reloc);

        var queryField = FindIfeQueryFieldForMethod(outerMethod, enumeratorType);
        if (queryField == null) return;

        var assignQueries = outerMethod.DeclaringType?.Methods.FirstOrDefault(m => m.Name == "__AssignQueries" && m.HasBody);
        if (assignQueries == null) return;

        EnsureQueryIncludesPeer(assignQueries, queryField, reloc.SourceType, reloc.TargetType);
    }

    private static void EnsureIfeCompleteDependencies(
        ModuleDefinition module, TypeDefinition enumeratorType, Relocation reloc)
    {
        var ifeType = enumeratorType?.DeclaringType;
        var completeDeps = ifeType?.Methods.FirstOrDefault(m => m.Name == "CompleteDependencies" && m.HasBody);
        if (completeDeps == null) return;

        if (completeDeps.Body.Instructions.Any(i =>
                (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt) &&
                i.Operand is GenericInstanceMethod existing &&
                existing.Name.StartsWith("CompleteDependencyBefore", StringComparison.Ordinal) &&
                existing.GenericArguments.Count == 1 &&
                TypeRefFullNameEquals(existing.GenericArguments[0], reloc.TargetType.FullName)))
            return;

        var sourceCall = completeDeps.Body.Instructions.FirstOrDefault(i =>
            (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt) &&
            i.Operand is GenericInstanceMethod gim &&
            gim.Name.StartsWith("CompleteDependencyBefore", StringComparison.Ordinal) &&
            gim.GenericArguments.Count == 1 &&
            TypeRefFullNameEquals(gim.GenericArguments[0], reloc.SourceType.FullName));
        if (sourceCall == null) return;

        var peerCallRef = BuildGenericMethodRef(module, sourceCall, reloc.TargetType);
        if (peerCallRef == null) return;

        var instanceLoad = sourceCall.Previous;
        if (instanceLoad == null) return;

        var il = completeDeps.Body.GetILProcessor();
        var clonedLoad = CloneInstruction(instanceLoad);
        if (clonedLoad == null) return;

        il.InsertAfter(sourceCall, clonedLoad);
        il.InsertAfter(clonedLoad, Instruction.Create(sourceCall.OpCode, peerCallRef));
    }

    private static FieldDefinition FindIfeQueryFieldForMethod(MethodDefinition outerMethod, TypeDefinition enumeratorType)
    {
        var ifeType = enumeratorType?.DeclaringType;
        if (ifeType == null || !outerMethod.HasBody) return null;

        var outerType = outerMethod.DeclaringType;
        if (outerType != null)
        {
            var suffix = ifeType.Name.StartsWith("IFE_", StringComparison.Ordinal)
                ? ifeType.Name.Substring("IFE_".Length)
                : ifeType.Name;
            var directName = $"__query_{suffix}";
            var direct = outerType.Fields.FirstOrDefault(f =>
                f.Name == directName &&
                f.FieldType.FullName == "Unity.Entities.EntityQuery");
            if (direct != null) return direct;
        }

        foreach (var instr in outerMethod.Body.Instructions)
        {
            if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
                instr.Operand is not MethodReference mr ||
                mr.Name != "Query" ||
                mr.DeclaringType.Resolve() != ifeType)
                continue;

            int depth = 0;
            for (var scan = instr.Previous; scan != null && depth < 16; scan = scan.Previous, depth++)
            {
                if ((scan.OpCode == OpCodes.Ldfld || scan.OpCode == OpCodes.Ldflda) &&
                    scan.Operand is FieldReference fr &&
                    fr.FieldType.FullName == "Unity.Entities.EntityQuery" &&
                    fr.Resolve() is FieldDefinition fd &&
                    fd.DeclaringType == outerMethod.DeclaringType)
                    return fd;
            }
        }

        return null;
    }

    private static void EnsureQueryIncludesPeer(
        MethodDefinition assignQueries, FieldDefinition queryField,
        TypeDefinition sourceType, TypeDefinition targetType)
    {
        var instrs = assignQueries.Body.Instructions;
        var stfld = instrs.FirstOrDefault(i =>
            i.OpCode.Code == Code.Stfld &&
            i.Operand is FieldReference fr &&
            fr.Resolve() == queryField);
        if (stfld == null) return;

        for (var cur = stfld.Previous; cur != null; cur = cur.Previous)
        {
            if ((cur.OpCode.Code == Code.Call || cur.OpCode.Code == Code.Callvirt) &&
                cur.Operand is GenericInstanceMethod gim &&
                gim.GenericArguments.Count == 1 &&
                TypeRefFullNameEquals(gim.GenericArguments[0], targetType.FullName) &&
                gim.Name.StartsWith("WithAll", StringComparison.Ordinal))
                return;

            if (cur.OpCode.Code == Code.Stfld) break;
        }

        var sourceWithAllCall = instrs.TakeWhile(i => i != stfld).LastOrDefault(i =>
            (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt) &&
            i.Operand is GenericInstanceMethod gim &&
            gim.GenericArguments.Count == 1 &&
            TypeRefFullNameEquals(gim.GenericArguments[0], sourceType.FullName) &&
            gim.Name == "WithAll");
        if (sourceWithAllCall == null) return;

        var peerWithAllRef = BuildGenericMethodRef(assignQueries.Module, sourceWithAllCall, targetType);
        if (peerWithAllRef == null) return;

        var buildReceiverLoad = stfld.Previous;
        while (buildReceiverLoad != null &&
               !(IsAddressLoad(buildReceiverLoad) && GetLocalFromInstruction(buildReceiverLoad, assignQueries.Body) != null))
            buildReceiverLoad = buildReceiverLoad.Previous;
        if (buildReceiverLoad == null) return;

        var stlocAfterSource = sourceWithAllCall.Next;
        if (stlocAfterSource == null || !IsStloc(stlocAfterSource)) return;

        var sourceBuilderLocal = GetLocalFromInstruction(stlocAfterSource, assignQueries.Body);
        var buildBuilderLocal = GetLocalFromInstruction(buildReceiverLoad, assignQueries.Body);
        if (sourceBuilderLocal == null || buildBuilderLocal == null || sourceBuilderLocal != buildBuilderLocal) return;

        var il = assignQueries.Body.GetILProcessor();
        var clonedLoad = CloneInstruction(buildReceiverLoad);
        var clonedStore = CloneInstruction(stlocAfterSource);
        if (clonedLoad == null || clonedStore == null) return;

        il.InsertBefore(buildReceiverLoad, clonedLoad);
        il.InsertBefore(buildReceiverLoad, Instruction.Create(sourceWithAllCall.OpCode, peerWithAllRef));
        il.InsertBefore(buildReceiverLoad, clonedStore);
    }

    // Retarget EntityManager.GetComponentData<SourceType>(entity) to TargetType.
    // Handles both direct chains:
    //   call GetComponentData<Src>(Entity)
    //   ldfld Src::Field
    // and the common spill form:
    //   call GetComponentData<Src>(Entity)
    //   stloc srcLocal
    //   ...
    //   ldloc[a] srcLocal
    //   ldfld Src::Field
}
