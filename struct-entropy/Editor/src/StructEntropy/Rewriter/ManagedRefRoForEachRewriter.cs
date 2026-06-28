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
    //  ManagedRefRoForEachRewriter
    //  --------------------------------------------------------------
    //  Rewrites the managed (non-Burst) fallback copies of
    //  SystemAPI.Query<RefRO<Src>>() foreach loops. Newer Entities source-gen emits these
    //  alongside the Burst __OnUpdate_<hash> twin; they use Unity.Entities.RefRO<Src> plus a
    //  QueryEnumerable IEnumerator, which the UncheckedRefRO-based InlineForEachRewriter does
    //  not match (so the field reads would orphan after relocation).
    //
    //  get_Current of the managed enumerator has two shapes:
    //    A) WithEntityAccess(): ValueTuple<RefRO<Src>, Entity[, ...]>  -> entity is in scope
    //         redirect  refRoLocal.ValueRO.Field  ->  ComponentLookup<Target>[entity].Field
    //    B) plain Query():      RefRO<Src>                              -> no entity in scope
    //         (handled by ApplyManagedRefRoForEachEntityArray)
    // --------------------------------------------------------------

    private static int ApplyManagedRefRoForEach(
        ModuleDefinition module, MethodDefinition method, Relocation reloc)
    {
        if (method?.HasBody != true || method.IsStatic)
            return 0;

        var sourceLocals = new List<VariableDefinition>();
        foreach (var instr in method.Body.Instructions)
        {
            if (!TryMatchManagedRefRoRead(method, instr, reloc, out var local))
                continue;
            if (!sourceLocals.Contains(local))
                sourceLocals.Add(local);
        }
        if (sourceLocals.Count == 0)
            return 0;

        int total = 0;
        foreach (var sourceLocal in sourceLocals)
        {
            if (TryFindEntityLocalForManagedRefRoLocal(method, sourceLocal, out var entityLocal))
            {
                total += RewriteManagedRefRoViaEntityLookup(module, method, reloc, sourceLocal, entityLocal);
                continue;
            }

            total += ApplyManagedRefRoForEachEntityArray(module, method, reloc, sourceLocal);
        }

        if (total > 0)
        {
            method.Body.OptimizeMacros();
            StructEntropyLogger.Log($"[SER]   ManagedRefRoForEachRewriter in {method.DeclaringType.Name}.{method.Name}: {total} access(es)");
        }
        return total;
    }

    // Matches  ldloca refRoLocal ; call RefRO<Src>::get_ValueRO() ; ldfld Src::Field.
    private static bool TryMatchManagedRefRoRead(
        MethodDefinition method, Instruction instr, Relocation reloc, out VariableDefinition sourceLocal)
    {
        sourceLocal = null;
        if (!IsFieldAccess(instr, reloc.Field, reloc.SourceTypeFullName))
            return false;

        var getValueCall = instr.Previous;
        if (getValueCall == null ||
            (getValueCall.OpCode.Code != Code.Call && getValueCall.OpCode.Code != Code.Callvirt) ||
            getValueCall.Operand is not MethodReference mr ||
            mr.Name != "get_ValueRO" ||
            !IsRefRoType(mr.DeclaringType, reloc.SourceType))
            return false;

        var localLoad = getValueCall.Previous;
        if (localLoad == null || !IsAddressLoad(localLoad))
            return false;

        sourceLocal = GetLocalFromInstruction(localLoad, method.Body);
        return sourceLocal != null && IsRefRoType(sourceLocal.VariableType, reloc.SourceType);
    }

    // Unity.Entities.RefRO<componentType> — explicitly NOT the Burst UncheckedRefRO variant.
    private static bool IsRefRoType(TypeReference typeRef, TypeDefinition componentType)
    {
        return typeRef is GenericInstanceType git &&
               git.ElementType.Name.StartsWith("RefRO", StringComparison.Ordinal) &&
               !git.ElementType.Name.StartsWith("UncheckedRefRO", StringComparison.Ordinal) &&
               git.GenericArguments.Count == 1 &&
               TypeRefFullNameEquals(git.GenericArguments[0], componentType.FullName);
    }

    // Pattern A: the RefRO<Src> local is deconstructed from a ValueTuple whose sibling Item is an
    // Entity (WithEntityAccess()). Find that Entity local.
    private static bool TryFindEntityLocalForManagedRefRoLocal(
        MethodDefinition method, VariableDefinition sourceLocal, out VariableDefinition entityLocal)
    {
        entityLocal = null;
        if (method?.HasBody != true)
            return false;

        foreach (var store in method.Body.Instructions)
        {
            if (!IsStloc(store) || GetLocalFromInstruction(store, method.Body) != sourceLocal)
                continue;

            var item1Load = store.Previous;
            if (item1Load?.OpCode.Code != Code.Ldfld ||
                item1Load.Operand is not FieldReference item1Fr ||
                !item1Fr.Name.StartsWith("Item", StringComparison.Ordinal))
                continue;

            int budget = 8;
            for (var scan = store.Next; scan != null && budget-- > 0; scan = scan.Next)
            {
                if (scan.OpCode.Code != Code.Ldfld ||
                    scan.Operand is not FieldReference fr2 ||
                    !fr2.Name.StartsWith("Item", StringComparison.Ordinal) ||
                    string.Equals(fr2.Name, item1Fr.Name, StringComparison.Ordinal))
                    continue;

                var entStore = scan.Next;
                if (entStore == null || !IsStloc(entStore))
                    continue;

                var candidate = GetLocalFromInstruction(entStore, method.Body);
                if (IsEntityLocal(candidate))
                {
                    entityLocal = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    private static int RewriteManagedRefRoViaEntityLookup(
        ModuleDefinition module, MethodDefinition method, Relocation reloc,
        VariableDefinition sourceLocal, VariableDefinition entityLocal)
    {
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
            if (!TryMatchManagedRefRoRead(method, instr, reloc, out var matchedLocal) ||
                matchedLocal != sourceLocal)
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

    // Pattern B: plain SystemAPI.Query<RefRO<Src>>() (no WithEntityAccess), so no entity is in
    // scope. Materialize the query's entities via ToEntityArray and redirect each read to
    // EntityManager.GetComponentData<Target>(entityArray[i]).Field, advancing i per MoveNext.
    // Mirrors ApplyInlineForEachEntityArrayLookup for the managed IEnumerator loop shape.
    private static int ApplyManagedRefRoForEachEntityArray(
        ModuleDefinition module, MethodDefinition method, Relocation reloc, VariableDefinition sourceLocal)
    {
        if (method?.HasBody != true || method.IsStatic)
            return 0;

        if (!TryFindManagedEnumeratorLocal(method, sourceLocal, out var enumeratorLocal))
            return 0;

        var queryField = FindManagedLoopSourceQueryField(method.DeclaringType, method, reloc.SourceType);
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
            IsStloc(i) && GetLocalFromInstruction(i, method.Body) == enumeratorLocal);
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
            if (!TryMatchManagedRefRoRead(method, instr, reloc, out var matchedLocal) ||
                matchedLocal != sourceLocal)
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

        // Advance the entity-array index in lockstep with the enumerator. Inserting before the
        // MoveNext receiver load is safe: the loop's initial/back branches anchor to the original
        // receiver-load instruction, so the increment only runs on fall-through between iterations.
        foreach (var moveNextCall in method.Body.Instructions.ToList())
        {
            if ((moveNextCall.OpCode.Code != Code.Call && moveNextCall.OpCode.Code != Code.Callvirt) ||
                moveNextCall.Operand is not MethodReference mr ||
                mr.Name != "MoveNext" ||
                GetLocalFromInstruction(moveNextCall.Previous, method.Body) != enumeratorLocal)
                continue;

            il.InsertBefore(moveNextCall.Previous, Instruction.Create(OpCodes.Ldloc, indexLocal));
            il.InsertBefore(moveNextCall.Previous, Instruction.Create(OpCodes.Ldc_I4_1));
            il.InsertBefore(moveNextCall.Previous, Instruction.Create(OpCodes.Add));
            il.InsertBefore(moveNextCall.Previous, Instruction.Create(OpCodes.Stloc, indexLocal));
            break;
        }

        return count;
    }

    // The managed enumerator local: ldloc enum ; callvirt IEnumerator::get_Current ; stloc sourceLocal.
    private static bool TryFindManagedEnumeratorLocal(
        MethodDefinition method, VariableDefinition sourceLocal, out VariableDefinition enumeratorLocal)
    {
        enumeratorLocal = null;
        if (method?.HasBody != true)
            return false;

        foreach (var store in method.Body.Instructions)
        {
            if (!IsStloc(store) || GetLocalFromInstruction(store, method.Body) != sourceLocal)
                continue;

            var getCurrent = store.Previous;
            if (getCurrent == null ||
                (getCurrent.OpCode.Code != Code.Call && getCurrent.OpCode.Code != Code.Callvirt) ||
                getCurrent.Operand is not MethodReference mr ||
                mr.Name != "get_Current")
                continue;

            var enumLoad = getCurrent.Previous;
            var candidate = GetLocalFromInstruction(enumLoad, method.Body);
            if (candidate != null)
            {
                enumeratorLocal = candidate;
                return true;
            }
        }

        return false;
    }

    // The managed Query<RefRO<Src>>() foreach shares the system's source EntityQuery with its Burst
    // __<MethodName>_<hash> twin, which loads __query_<hash>_N before IFE_<hash>::Query(...).
    private static FieldDefinition FindManagedLoopSourceQueryField(
        TypeDefinition systemType, MethodDefinition managedMethod, TypeDefinition sourceType)
    {
        if (systemType == null || managedMethod == null || sourceType == null)
            return null;

        var twinPrefix = "__" + managedMethod.Name + "_";
        foreach (var twin in systemType.Methods.Where(m =>
                     m.HasBody && m.Name.StartsWith(twinPrefix, StringComparison.Ordinal)))
        {
            foreach (var instr in twin.Body.Instructions)
            {
                if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
                    instr.Operand is not MethodReference mr ||
                    mr.Name != "Query" ||
                    mr.DeclaringType == null ||
                    !mr.DeclaringType.Name.StartsWith("IFE_", StringComparison.Ordinal))
                    continue;

                var ifeType = mr.DeclaringType.Resolve();
                if (ifeType == null || FindComponentTypeHandleField(ifeType, sourceType) == null)
                    continue;

                for (var scan = instr.Previous; scan != null; scan = scan.Previous)
                {
                    if ((scan.OpCode.Code == Code.Ldfld || scan.OpCode.Code == Code.Ldflda) &&
                        scan.Operand is FieldReference fr &&
                        fr.Name.StartsWith("__query_", StringComparison.Ordinal) &&
                        fr.FieldType.FullName == "Unity.Entities.EntityQuery")
                    {
                        var fd = fr.Resolve();
                        if (fd != null && fd.DeclaringType == systemType)
                            return fd;
                    }
                }
            }
        }

        return null;
    }
}
