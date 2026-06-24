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
    //  GeneralFieldRedirectRewriter: general field redirect (ECB init, locals, RefRO)
    // --------------------------------------------------------------

    private static int ApplyGeneralFieldRedirect(
        ModuleDefinition module, MethodDefinition method,
        Relocation reloc, List<PendingEcbMerge> ecbMerges)
    {
        var peerLocal = FindPreferredPeerLocal(method, reloc.TargetType) ?? FindLocalOfType(method.Body, reloc.TargetType);
        if (peerLocal == null) return 0;

        // If the peer local has initobj AND the method has ECB operations, use a temp local and
        // defer the ECB merge fixup.  Without ECB operations the initobj is just a value-type
        // zero-init (e.g. the foreach variable in a SystemAPI.Query value-type loop) and we can
        // redirect directly without any ECB plumbing.
        if (HasInitObjForLocal(method, peerLocal) &&
            (MethodHasEcbOperations(method) || MethodHasBakerAddComponentOperations(method)))
        {
            var tempLocal = new VariableDefinition(module.ImportReference(reloc.TargetType));
            method.Body.Variables.Add(tempLocal);
            method.Body.InitLocals = true;

            ecbMerges.Add(new PendingEcbMerge
            {
                Method       = method,
                ExistingLocal = peerLocal,
                TempLocal    = tempLocal,
                NewField     = reloc.NewField,
                ExistingType = reloc.TargetType,
            });

            peerLocal = tempLocal;
        }

        int count = RedirectFieldAccesses(method, reloc, peerLocal);
        int writeBacks = 0;
        string writeBackFailureReason = null;
        if (count > 0)
        {
            writeBacks = InjectEntityManagerTargetWriteBacksAfterSourceWrites(module, method, reloc, peerLocal, out writeBackFailureReason);
            if (writeBacks == 0 && MethodHasEntityManagerSetComponentDataCall(method, reloc.SourceType))
            {
                throw new InvalidOperationException(
                    $"[SER] GeneralFieldRedirectRewriter redirected {reloc.SourceType.Name}.{reloc.Field.Name} in {method.FullName} but could not emit EntityManager writeback for {reloc.TargetType.Name}: {writeBackFailureReason ?? "unknown"}.");
            }
            method.Body.OptimizeMacros();
        }
        StructEntropyLogger.Log(
            writeBacks > 0
                ? $"[SER]   GeneralFieldRedirectRewriter in {method.DeclaringType.Name}.{method.Name}: {count} access(es), {writeBacks} EntityManager writeback(s)"
                : $"[SER]   GeneralFieldRedirectRewriter in {method.DeclaringType.Name}.{method.Name}: {count} access(es)");
        return count;
    }

    private static int InjectEntityManagerTargetWriteBacksAfterSourceWrites(
        ModuleDefinition module,
        MethodDefinition method,
        Relocation reloc,
        VariableDefinition peerLocal,
        out string failureReason)
    {
        failureReason = null;
        if (module == null || method?.HasBody != true || reloc == null || peerLocal == null)
        {
            failureReason = "missing_argument";
            return 0;
        }

        int count = 0;
        var il = method.Body.GetILProcessor();
        bool sawSourceSet = false;
        foreach (var sourceSetCall in method.Body.Instructions.ToList())
        {
            if (!IsEntityManagerSetComponentDataCall(sourceSetCall, reloc.SourceType))
                continue;

            sawSourceSet = true;
            var targetSetRef = BuildGenericMethodRef(module, sourceSetCall, reloc.TargetType);
            if (targetSetRef == null)
            {
                failureReason = "build_target_set_ref_failed";
                continue;
            }

            if (!TryCollectEntityManagerSetComponentDataReceiverAndEntity(
                    sourceSetCall,
                    out var entityManagerInstrs,
                    out var entityInstrs) &&
                !TryCollectSimpleEntityManagerSetComponentDataReceiverAndEntity(
                    method,
                    sourceSetCall,
                    out entityManagerInstrs,
                    out entityInstrs))
            {
                failureReason = DescribeEntityManagerSetComponentDataStack(sourceSetCall);
                continue;
            }

            failureReason = null;

            if (entityManagerInstrs == null || entityInstrs == null)
            {
                failureReason = "null_argument_clones";
                continue;
            }

            var injection = new List<Instruction>();
            injection.AddRange(entityManagerInstrs);
            injection.AddRange(entityInstrs);
            injection.Add(Instruction.Create(OpCodes.Ldloc, peerLocal));
            injection.Add(Instruction.Create(sourceSetCall.OpCode, targetSetRef));

            var insertAfter = sourceSetCall;
            foreach (var instr in injection)
            {
                il.InsertAfter(insertAfter, instr);
                insertAfter = instr;
            }

            count++;
        }

        if (!sawSourceSet)
            failureReason = "source_set_component_data_not_found";

        return count;
    }

    private static string DescribeEntityManagerSetComponentDataStack(Instruction setComponentDataCall)
    {
        var valueLoad = setComponentDataCall?.Previous;
        var entityLoad = valueLoad?.Previous;
        var entityManagerLoad = entityLoad?.Previous;
        return $"stack_shape value={FormatInstructionForDiagnostic(valueLoad)} entity={FormatInstructionForDiagnostic(entityLoad)} receiver={FormatInstructionForDiagnostic(entityManagerLoad)}";
    }

    private static string FormatInstructionForDiagnostic(Instruction instr)
    {
        if (instr == null)
            return "<null>";

        var operand = instr.Operand == null ? string.Empty : $":{instr.Operand}";
        return $"{instr.OpCode.Code}{operand}";
    }

    private static bool TryCollectSimpleEntityManagerSetComponentDataReceiverAndEntity(
        MethodDefinition method,
        Instruction setComponentDataCall,
        out List<Instruction> entityManagerInstrs,
        out List<Instruction> entityInstrs)
    {
        entityManagerInstrs = null;
        entityInstrs = null;

        var valueLoad = setComponentDataCall?.Previous;
        if (valueLoad == null)
            return false;

        if (!IsValueLoad(valueLoad))
            return false;

        var entityLoad = FindStackPreservedEntityLoadBeforeValue(method, valueLoad);
        var entityManagerLoad = entityLoad?.Previous;
        if (entityLoad == null || entityManagerLoad == null)
            return false;

        if (!IsEntityManagerReceiverLoad(entityManagerLoad))
            return false;

        var entityManagerClone = CloneInstruction(entityManagerLoad);
        var entityClone = CloneInstruction(entityLoad);
        if (entityManagerClone == null || entityClone == null)
            return false;

        entityManagerInstrs = new List<Instruction> { entityManagerClone };
        entityInstrs = new List<Instruction> { entityClone };
        return true;
    }

    private static Instruction FindStackPreservedEntityLoadBeforeValue(MethodDefinition method, Instruction valueLoad)
    {
        for (var scan = valueLoad?.Previous; scan != null; scan = scan.Previous)
        {
            if (!IsSimpleLoad(scan))
                continue;

            var loadedTypeName = GetBaseTypeName(GetLoadedType(scan, method));
            if (loadedTypeName == "Unity.Entities.Entity")
                return scan;
        }

        return null;
    }

    private static bool IsEntityManagerReceiverLoad(Instruction instr)
    {
        if (instr == null)
            return false;

        if ((instr.OpCode.Code == Code.Ldsflda || instr.OpCode.Code == Code.Ldsfld) &&
            instr.Operand is FieldReference fieldRef)
        {
            return GetBaseTypeName(fieldRef.FieldType) == "Unity.Entities.EntityManager";
        }

        return false;
    }

    private static bool TryCollectEntityManagerSetComponentDataReceiverAndEntity(
        Instruction setComponentDataCall,
        out List<Instruction> entityManagerInstrs,
        out List<Instruction> entityInstrs)
    {
        entityManagerInstrs = null;
        entityInstrs = null;

        if (setComponentDataCall == null)
            return false;

        var valueEnd = setComponentDataCall.Previous;
        if (valueEnd == null)
            return false;

        var valueStart = FindArgumentSequenceStart(valueEnd);
        if (valueStart?.Previous == null)
            return false;

        var entityEnd = valueStart.Previous;
        var entityStart = FindArgumentSequenceStart(entityEnd);
        if (entityStart?.Previous == null)
            return false;

        var entityManagerEnd = entityStart.Previous;
        var entityManagerStart = FindArgumentSequenceStart(entityManagerEnd);
        if (entityManagerStart == null)
            return false;

        entityManagerInstrs = CloneInstructionRange(entityManagerStart, entityManagerEnd);
        entityInstrs = CloneInstructionRange(entityStart, entityEnd);
        return entityManagerInstrs != null &&
               entityManagerInstrs.Count > 0 &&
               entityInstrs != null &&
               entityInstrs.Count > 0;
    }

    private static List<Instruction> CloneInstructionRange(Instruction start, Instruction end)
    {
        if (start == null || end == null)
            return null;

        var result = new List<Instruction>();
        for (var cur = start; cur != null; cur = cur.Next)
        {
            var clone = CloneInstruction(cur);
            if (clone == null)
                return null;

            result.Add(clone);
            if (cur == end)
                return result;
        }

        return null;
    }

    private static bool MethodHasEntityManagerSetComponentDataCall(MethodDefinition method, TypeDefinition componentType)
    {
        return method?.HasBody == true &&
               method.Body.Instructions.Any(instr => IsEntityManagerSetComponentDataCall(instr, componentType));
    }

    private static int PreserveMovedFieldsOnTargetWholeStructWrites(
        ModuleDefinition module,
        List<Relocation> relocations)
    {
        if (module == null || relocations == null || relocations.Count == 0)
            return 0;

        int total = 0;
        foreach (var type in EnumerateAllTypes(module))
        {
            foreach (var method in type.Methods.Where(m => m.HasBody).ToList())
            {
                foreach (var reloc in relocations.Where(r => r.NewField != null))
                    total += PreserveMovedFieldOnTargetWholeStructWrites(module, method, reloc);
            }
        }

        return total;
    }

    private static int PreserveMovedFieldOnTargetWholeStructWrites(
        ModuleDefinition module,
        MethodDefinition method,
        Relocation reloc)
    {
        int count = 0;
        var il = method.Body.GetILProcessor();
        foreach (var stobj in method.Body.Instructions.ToList())
        {
            if (stobj.OpCode.Code != Code.Stobj ||
                stobj.Operand is not TypeReference typeRef ||
                GetBaseTypeName(typeRef) != reloc.TargetType.FullName)
                continue;

            var valueLoad = stobj.Previous;
            var valueLocal = GetLocalFromInstruction(valueLoad, method.Body);
            if (valueLocal == null || GetBaseTypeName(valueLocal.VariableType) != reloc.TargetType.FullName)
                continue;

            if (ValueLocalAlreadyStoresMovedField(method, valueLoad, valueLocal, reloc.NewField))
                continue;

            var targetAddressLoad = FindPreservedTargetAddressBeforeValue(method, valueLoad, valueLocal, reloc.TargetType);
            if (targetAddressLoad == null)
                continue;

            var clonedTargetAddress = CloneInstruction(targetAddressLoad);
            if (clonedTargetAddress == null)
                continue;

            il.InsertBefore(valueLoad, Instruction.Create(OpCodes.Ldloca, valueLocal));
            il.InsertBefore(valueLoad, clonedTargetAddress);
            il.InsertBefore(valueLoad, Instruction.Create(OpCodes.Ldfld, module.ImportReference(reloc.NewField)));
            il.InsertBefore(valueLoad, Instruction.Create(OpCodes.Stfld, module.ImportReference(reloc.NewField)));
            count++;
        }

        count += PreserveMovedFieldOnTargetEntityManagerSetComponentData(module, method, reloc);
        count += PreserveMovedFieldOnTargetComponentLookupSetItem(module, method, reloc);
        count += PreserveMovedFieldOnTargetEcbSetComponent(module, method, reloc);

        if (count > 0)
        {
            method.Body.OptimizeMacros();
            StructEntropyLogger.Log($"[SER]   Preserved {reloc.NewField.Name} across {count} target whole-struct write(s) in {method.DeclaringType.Name}.{method.Name}");
        }

        return count;
    }

    private static int PreserveMovedFieldOnTargetComponentLookupSetItem(
        ModuleDefinition module,
        MethodDefinition method,
        Relocation reloc)
    {
        int count = 0;
        var il = method.Body.GetILProcessor();

        foreach (var call in method.Body.Instructions.ToList())
        {
            if (!IsSetItemOnComponentLookup(call, reloc.TargetType))
                continue;

            var valueLoad = call.Previous;
            var valueLocal = GetLocalFromInstruction(valueLoad, method.Body);
            if (valueLocal == null || GetBaseTypeName(valueLocal.VariableType) != reloc.TargetType.FullName)
                continue;

            if (ValueLocalAlreadyStoresMovedField(method, valueLoad, valueLocal, reloc.NewField))
                continue;

            if (call.Operand is not MethodReference setRef)
                continue;

            var lookupInstrs = CollectArgumentSequenceBeforeCall(call, 2);
            var entityInstrs = CollectArgumentSequenceBeforeCall(call, 1);
            var getItemRef = BuildComponentLookupAccessorRef(module, setRef.DeclaringType, "get_Item", 1);
            if (lookupInstrs == null || entityInstrs == null || getItemRef == null)
                continue;

            il.InsertBefore(valueLoad, Instruction.Create(OpCodes.Ldloca, valueLocal));
            foreach (var instr in lookupInstrs)
                il.InsertBefore(valueLoad, instr);
            foreach (var instr in entityInstrs)
                il.InsertBefore(valueLoad, instr);
            il.InsertBefore(valueLoad, Instruction.Create(OpCodes.Call, getItemRef));
            il.InsertBefore(valueLoad, Instruction.Create(OpCodes.Ldfld, module.ImportReference(reloc.NewField)));
            il.InsertBefore(valueLoad, Instruction.Create(OpCodes.Stfld, module.ImportReference(reloc.NewField)));

            count++;
        }

        return count;
    }

    private static int PreserveMovedFieldOnTargetEcbSetComponent(
        ModuleDefinition module,
        MethodDefinition method,
        Relocation reloc)
    {
        int count = 0;
        var il = method.Body.GetILProcessor();

        foreach (var call in method.Body.Instructions.ToList())
        {
            if ((call.OpCode.Code != Code.Call && call.OpCode.Code != Code.Callvirt) ||
                call.Operand is not MethodReference mr ||
                !IsEcbSetComponentOf(mr, reloc.TargetType))
                continue;

            var valueLoad = call.Previous;
            var valueLocal = GetLocalFromInstruction(valueLoad, method.Body);
            if (valueLocal == null || GetBaseTypeName(valueLocal.VariableType) != reloc.TargetType.FullName)
            {
                throw new InvalidOperationException(
                    $"[SER] Cannot preserve {reloc.NewField.Name} across ECB SetComponent<{reloc.TargetType.Name}> in {method.FullName}: unsupported value stack shape.");
            }

            if (ValueLocalAlreadyStoresMovedField(method, valueLoad, valueLocal, reloc.NewField))
                continue;

            List<Instruction> sourceValueInstrs = null;
            var targetParam = FindByRefParameter(method, reloc.TargetType);
            if (targetParam != null)
            {
                sourceValueInstrs = new List<Instruction>
                {
                    Instruction.Create(OpCodes.Ldarg, targetParam),
                    Instruction.Create(OpCodes.Ldfld, module.ImportReference(reloc.NewField)),
                };
            }
            else if (IsUserIjeExecuteCandidate(method))
            {
                var lookupField = EnsureStandaloneComponentLookupField(module, method.DeclaringType, reloc.TargetType);
                if (lookupField != null &&
                    EnsureStandaloneLookupFieldInit(module, method.DeclaringType, lookupField, reloc.TargetType))
                {
                    var entityInstrs = CollectArgumentSequenceBeforeCall(call, 1);
                    if (entityInstrs == null)
                        entityInstrs = CollectEntityArgumentBelowValueLocalConstruction(method, valueLoad, valueLocal);

                    var getItemRef = BuildComponentLookupAccessorRef(module, lookupField.FieldType, "get_Item", 1);
                    if (entityInstrs != null && getItemRef != null)
                    {
                        sourceValueInstrs = new List<Instruction>
                        {
                            Instruction.Create(OpCodes.Ldarg_0),
                            Instruction.Create(OpCodes.Ldflda, module.ImportReference(lookupField)),
                        };
                        sourceValueInstrs.AddRange(entityInstrs);
                        sourceValueInstrs.Add(Instruction.Create(OpCodes.Call, getItemRef));
                        sourceValueInstrs.Add(Instruction.Create(OpCodes.Ldfld, module.ImportReference(reloc.NewField)));
                    }
                }
            }

            if (sourceValueInstrs == null || sourceValueInstrs.Count == 0)
            {
                throw new InvalidOperationException(
                    $"[SER] Cannot preserve {reloc.NewField.Name} across ECB SetComponent<{reloc.TargetType.Name}> in {method.FullName}: no current target value source.");
            }

            il.InsertBefore(valueLoad, Instruction.Create(OpCodes.Ldloca, valueLocal));
            foreach (var instr in sourceValueInstrs)
                il.InsertBefore(valueLoad, instr);
            il.InsertBefore(valueLoad, Instruction.Create(OpCodes.Stfld, module.ImportReference(reloc.NewField)));
            count++;
        }

        return count;
    }

    private static int PreserveMovedFieldOnTargetEntityManagerSetComponentData(
        ModuleDefinition module,
        MethodDefinition method,
        Relocation reloc)
    {
        int count = 0;
        var il = method.Body.GetILProcessor();

        foreach (var call in method.Body.Instructions.ToList())
        {
            if (!IsEntityManagerSetComponentDataCall(call, reloc.TargetType))
                continue;

            var valueLoad = call.Previous;
            var valueLocal = GetLocalFromInstruction(valueLoad, method.Body);
            if (valueLocal == null || GetBaseTypeName(valueLocal.VariableType) != reloc.TargetType.FullName)
                continue;

            if (ValueLocalAlreadyStoresMovedField(method, valueLoad, valueLocal, reloc.NewField))
                continue;

            var entityManagerInstrs = CollectArgumentSequenceBeforeCall(call, 2);
            var entityInstrs = CollectArgumentSequenceBeforeCall(call, 1);
            if (entityManagerInstrs == null || entityInstrs == null)
                continue;

            var getComponentDataRef = BuildEntityManagerGetComponentDataRef(module, call, reloc.TargetType);
            if (getComponentDataRef == null)
                continue;

            var insertion = new List<Instruction>
            {
                Instruction.Create(OpCodes.Ldloca, valueLocal),
            };
            insertion.AddRange(entityManagerInstrs);
            insertion.AddRange(entityInstrs);
            insertion.Add(Instruction.Create(OpCodes.Call, getComponentDataRef));
            insertion.Add(Instruction.Create(OpCodes.Ldfld, module.ImportReference(reloc.NewField)));
            insertion.Add(Instruction.Create(OpCodes.Stfld, module.ImportReference(reloc.NewField)));

            foreach (var instr in insertion)
                il.InsertBefore(valueLoad, instr);

            count++;
        }

        return count;
    }

    private static Instruction FindPreservedTargetAddressBeforeValue(
        MethodDefinition method,
        Instruction valueLoad,
        VariableDefinition valueLocal,
        TypeDefinition targetType)
    {
        for (var scan = valueLoad?.Previous; scan != null; scan = scan.Previous)
        {
            if (!IsTargetAddressOrByRefLoad(scan, method, targetType))
                continue;

            var local = GetLocalFromInstruction(scan, method.Body);
            if (local != null && local == valueLocal)
                continue;

            return scan;
        }

        return null;
    }

    private static bool IsTargetAddressOrByRefLoad(
        Instruction instr,
        MethodDefinition method,
        TypeDefinition targetType)
    {
        if (instr == null || method == null || targetType == null)
            return false;

        var loadedType = GetLoadedType(instr, method);
        if (GetBaseTypeName(loadedType) != targetType.FullName)
            return false;

        return IsAddressLoad(instr) || loadedType is ByReferenceType;
    }

    private static bool ValueLocalAlreadyStoresMovedField(
        MethodDefinition method,
        Instruction valueLoad,
        VariableDefinition valueLocal,
        FieldReference movedField)
    {
        for (var scan = valueLoad?.Previous; scan != null; scan = scan.Previous)
        {
            if (scan.OpCode.Code == Code.Initobj &&
                GetLocalFromInstruction(scan.Previous, method.Body) == valueLocal)
                return false;

            if (scan.OpCode.Code == Code.Stfld &&
                scan.Operand is FieldReference fieldRef &&
                FieldReferenceMatches(fieldRef, movedField) &&
                GetLocalFromInstruction(FindInstanceLoad(scan), method.Body) == valueLocal)
                return true;

            if (scan.OpCode.Code == Code.Ldflda &&
                scan.Operand is FieldReference addressFieldRef &&
                FieldReferenceMatches(addressFieldRef, movedField) &&
                GetLocalFromInstruction(FindInstanceLoad(scan), method.Body) == valueLocal &&
                FieldAddressIsWrittenBefore(addressFieldRef, scan, valueLoad))
                return true;
        }

        return false;
    }

    private static bool FieldAddressIsWrittenBefore(
        FieldReference field,
        Instruction fieldAddressLoad,
        Instruction stopBefore)
    {
        int depth = 0;
        for (var scan = fieldAddressLoad?.Next; scan != null && scan != stopBefore && depth < 24; scan = scan.Next, depth++)
        {
            switch (scan.OpCode.Code)
            {
                case Code.Stind_I:
                case Code.Stind_I1:
                case Code.Stind_I2:
                case Code.Stind_I4:
                case Code.Stind_I8:
                case Code.Stind_R4:
                case Code.Stind_R8:
                case Code.Stind_Ref:
                case Code.Stobj:
                case Code.Initobj:
                    return true;
            }

            if ((scan.OpCode.Code == Code.Call || scan.OpCode.Code == Code.Callvirt) &&
                scan.Operand is MethodReference mr &&
                MethodMayMutateByRefField(mr, field))
                return true;
        }

        return false;
    }

    private static bool MethodMayMutateByRefField(MethodReference method, FieldReference field)
    {
        if (method == null || field == null)
            return false;

        if (!method.HasThis && method.Parameters.Count == 0)
            return false;

        var fieldTypeName = GetBaseTypeName(field.FieldType);
        return method.Parameters.Any(p => GetBaseTypeName(p.ParameterType) == fieldTypeName) ||
               method.DeclaringType?.FullName == fieldTypeName;
    }

    private static bool FieldReferenceMatches(FieldReference left, FieldReference right)
    {
        if (left == null || right == null)
            return false;

        if (left == right || left.Resolve() == right.Resolve())
            return true;

        return string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
               string.Equals(left.DeclaringType?.FullName, right.DeclaringType?.FullName, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------
    //  ECB merge fixup (replaces Baker merge for ECB-based methods)
    // --------------------------------------------------------------

    private static void FixupEcbMerges(
        ModuleDefinition module, List<PendingEcbMerge> merges,
        List<TypeDefinition> groupTypes)
    {
        var byMethod = merges
            .Where(m => MethodHasEcbOperations(m.Method))
            .GroupBy(m => m.Method)
            .ToDictionary(g => g.Key, g => g.ToList());
        var groupTypeNames = new HashSet<string>(groupTypes.Select(t => t.FullName));

        foreach (var kvp in byMethod)
        {
            var method = kvp.Key;
            var methodMerges = kvp.Value;
            var il = method.Body.GetILProcessor();

            var entityInstrs = FindEcbAddComponentEntityArgument(method, groupTypeNames);

            // Find ECB parameter/local
            var ecbLoad = FindEcbLoadInMethod(method);

            // Save AddComponent method refs before removal
            var addRefs = SaveEcbAddComponentRefs(method, groupTypeNames);

            if (entityInstrs == null || entityInstrs.Count == 0)
                throw new InvalidOperationException($"[SER] ECB merge fixup could not find AddComponent entity argument in {method.FullName}");
            if (ecbLoad == null || ecbLoad.Count == 0)
                throw new InvalidOperationException($"[SER] ECB merge fixup could not find ECB load in {method.FullName}");

            var missingAddRefs = methodMerges
                .Select(m => m.ExistingType.FullName)
                .Distinct()
                .Where(tn => !addRefs.ContainsKey(tn))
                .ToList();
            if (missingAddRefs.Count > 0)
            {
                throw new InvalidOperationException(
                    $"[SER] ECB merge fixup missing AddComponent refs in {method.FullName}: {string.Join(", ", missingAddRefs)}");
            }

            // Step 1: Remove original ECB.AddComponent<GroupType> calls (pop approach)
            foreach (var instr in method.Body.Instructions.ToList())
            {
                if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
                    instr.Operand is not GenericInstanceMethod gim) continue;
                if (gim.Name != "AddComponent" || gim.GenericArguments.Count != 1) continue;
                if (!groupTypeNames.Contains(gim.GenericArguments[0].FullName)) continue;

                // prev should be ldloc T_value - replace with pop (consumes entity from stack)
                var prev = instr.Previous;
                if (prev != null && (IsValueLoad(prev) || IsAddressLoad(prev)))
                    ReplaceInstruction(il, prev, Instruction.Create(OpCodes.Pop));
                // replace call with pop (consumes ECB ref from stack)
                ReplaceInstruction(il, instr, Instruction.Create(OpCodes.Pop));
            }

            // Step 2: Merge temp locals ? existing locals before ret
            var ret = method.Body.Instructions.Last();
            foreach (var merge in methodMerges)
            {
                il.InsertBefore(ret, Instruction.Create(OpCodes.Ldloca, merge.ExistingLocal));
                il.InsertBefore(ret, Instruction.Create(OpCodes.Ldloc, merge.TempLocal));
                il.InsertBefore(ret, Instruction.Create(OpCodes.Ldfld, merge.NewField));
                il.InsertBefore(ret, Instruction.Create(OpCodes.Stfld, merge.NewField));
            }

            // Step 3: Reconstruct ECB.AddComponent<T> calls at the end.
            // We remove all group-type AddComponent calls above, so we must re-emit every
            // originally observed group component whose local value still exists in the method.
            if (entityInstrs != null && ecbLoad != null)
            {
                var emittedTypes = new HashSet<string>();

                foreach (var addTypeName in addRefs.Keys.OrderBy(x => x))
                {
                    if (emittedTypes.Contains(addTypeName))
                        continue;

                    var addType = groupTypes.FirstOrDefault(t => t.FullName == addTypeName);
                    if (addType == null)
                        continue;

                    var localForType = FindLocalOfType(method.Body, addType);
                    if (localForType == null)
                    {
                        throw new InvalidOperationException(
                            $"[SER] ECB merge fixup could not find local for {addType.FullName} in {method.FullName}");
                    }

                    emittedTypes.Add(addTypeName);
                    var acRef = addRefs[addTypeName];
                    foreach (var loadInstr in ecbLoad)
                        il.InsertBefore(ret, CloneInstruction(loadInstr) ?? Instruction.Create(OpCodes.Nop));
                    var clonedEntityInstrs = CloneInstructionList(entityInstrs);
                    if (clonedEntityInstrs == null || clonedEntityInstrs.Count == 0)
                        throw new InvalidOperationException(
                            $"[SER] ECB merge fixup could not clone AddComponent entity argument in {method.FullName}");
                    foreach (var entityInstr in clonedEntityInstrs)
                        il.InsertBefore(ret, entityInstr);
                    il.InsertBefore(ret, Instruction.Create(OpCodes.Ldloc, localForType));
                    il.InsertBefore(ret, Instruction.Create(acRef.OpCode, acRef.MethodRef));
                }
            }

            method.Body.OptimizeMacros();
        }
    }

    private static void FixupBakerMerges(
        ModuleDefinition module, List<PendingEcbMerge> merges,
        List<TypeDefinition> groupTypes)
    {
        var byMethod = merges
            .Where(m => MethodHasBakerAddComponentOperations(m.Method))
            .GroupBy(m => m.Method)
            .ToDictionary(g => g.Key, g => g.ToList());
        var groupTypeNames = new HashSet<string>(groupTypes.Select(t => t.FullName));

        foreach (var kvp in byMethod)
        {
            var method = kvp.Key;
            var methodMerges = kvp.Value;
            var il = method.Body.GetILProcessor();
            var entityLocal = FindBakerEntityLocal(method);
            var addRefs = SaveEcbAddComponentRefs(method, groupTypeNames);

            if (entityLocal == null)
                throw new InvalidOperationException($"[SER] Baker merge fixup could not find entity local in {method.FullName}");

            var missingAddRefs = methodMerges
                .Select(m => m.ExistingType.FullName)
                .Distinct()
                .Where(tn => !addRefs.ContainsKey(tn))
                .ToList();
            if (missingAddRefs.Count > 0)
            {
                throw new InvalidOperationException(
                    $"[SER] Baker merge fixup missing AddComponent refs in {method.FullName}: {string.Join(", ", missingAddRefs)}");
            }

            foreach (var instr in method.Body.Instructions.ToList())
            {
                if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
                    instr.Operand is not GenericInstanceMethod gim)
                    continue;
                if (gim.Name != "AddComponent" || gim.GenericArguments.Count != 1)
                    continue;
                if (!groupTypeNames.Contains(gim.GenericArguments[0].FullName))
                    continue;

                var prev = instr.Previous;
                if (prev != null && (IsValueLoad(prev) || IsAddressLoad(prev)))
                    ReplaceInstruction(il, prev, Instruction.Create(OpCodes.Pop));
                ReplaceInstruction(il, instr, Instruction.Create(OpCodes.Pop));
            }

            var ret = method.Body.Instructions.Last();
            foreach (var merge in methodMerges)
            {
                il.InsertBefore(ret, Instruction.Create(OpCodes.Ldloca, merge.ExistingLocal));
                il.InsertBefore(ret, Instruction.Create(OpCodes.Ldloc, merge.TempLocal));
                il.InsertBefore(ret, Instruction.Create(OpCodes.Ldfld, merge.NewField));
                il.InsertBefore(ret, Instruction.Create(OpCodes.Stfld, merge.NewField));
            }

            var emittedTypes = new HashSet<string>();
            foreach (var addTypeName in addRefs.Keys.OrderBy(x => x))
            {
                if (emittedTypes.Contains(addTypeName))
                    continue;

                var addType = groupTypes.FirstOrDefault(t => t.FullName == addTypeName);
                if (addType == null)
                    continue;

                var localForType = FindPreferredPeerLocal(method, addType) ?? FindLocalOfType(method.Body, addType);
                if (localForType == null)
                {
                    throw new InvalidOperationException(
                        $"[SER] Baker merge fixup could not find local for {addType.FullName} in {method.FullName}");
                }

                emittedTypes.Add(addTypeName);
                var addRef = addRefs[addTypeName];
                il.InsertBefore(ret, Instruction.Create(OpCodes.Ldarg_0));
                il.InsertBefore(ret, Instruction.Create(OpCodes.Ldloc, entityLocal));

                bool byRefValue = addRef.MethodRef.Parameters.Count >= 2 &&
                                  addRef.MethodRef.Parameters[1].ParameterType is ByReferenceType;
                il.InsertBefore(ret, byRefValue
                    ? Instruction.Create(OpCodes.Ldloca, localForType)
                    : Instruction.Create(OpCodes.Ldloc, localForType));
                il.InsertBefore(ret, Instruction.Create(addRef.OpCode, addRef.MethodRef));
            }

            method.Body.OptimizeMacros();
        }
    }

    // --------------------------------------------------------------
}
