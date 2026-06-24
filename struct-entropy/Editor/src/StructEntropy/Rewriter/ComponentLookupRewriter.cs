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
    //  ComponentLookupRewriter: ComponentLookup<SourceType> on declaring type
    // --------------------------------------------------------------

    private static int ApplyComponentLookup(
        ModuleDefinition module, MethodDefinition method,
        Relocation reloc, FieldDefinition srcLookupField,
        List<TryGetPeerLocal> tryGetPeerLocals)
    {
        // Find or create peer ComponentLookup<TargetType> field
        var peerLookupField = FindComponentLookupField(method.DeclaringType, reloc.TargetType);
        if (peerLookupField == null)
        {
            if (srcLookupField.FieldType is not GenericInstanceType srcGit) return 0;
            var peerGit = new GenericInstanceType(srcGit.ElementType);
            peerGit.GenericArguments.Add(module.ImportReference(reloc.TargetType));
            peerLookupField = new FieldDefinition(
                $"__zd_{reloc.TargetType.Name}_Lookup",
                srcLookupField.Attributes,
                module.ImportReference(peerGit));
            CopyFieldMetadata(module, srcLookupField, peerLookupField);
            method.DeclaringType.Fields.Add(peerLookupField);

            // Initialize peer lookup in the system's OnUpdate/wherever source lookup is initialized
            InjectLookupFieldInit(module, srcLookupField, peerLookupField, reloc);
        }

        int rewritten = 0;

        var peerLocal = InjectPeerGetItem(module, method, reloc, srcLookupField, peerLookupField);
        if (peerLocal != null)
        {
            rewritten += RedirectFieldAccesses(method, reloc, peerLocal);
            InjectPeerWriteBacks(module, method, reloc, srcLookupField, peerLookupField, peerLocal);
        }

        rewritten += RewriteDirectLookupFieldReads(module, method, reloc, srcLookupField, peerLookupField);
        rewritten += RewriteLookupWholeStructWrites(module, method, reloc, srcLookupField, peerLookupField);
        rewritten += RewriteTryGetComponentFieldAccesses(module, method, reloc, srcLookupField, peerLookupField, tryGetPeerLocals);

        if (peerLocal == null && rewritten == 0)
        {
            StructEntropyLogger.Log($"[SER] WARNING ComponentLookupRewriter: no supported lookup rewrite landed in {method.DeclaringType.Name}.{method.Name}");
            return 0;
        }

        if (rewritten > 0) method.Body.OptimizeMacros();
        StructEntropyLogger.Log($"[SER]   ComponentLookupRewriter in {method.DeclaringType.Name}.{method.Name}: {rewritten} access(es)");
        return rewritten;
    }

    // Injects GetComponentLookup<TargetType>() assignment after GetComponentLookup<SourceType>
    private static void InjectLookupFieldInit(
        ModuleDefinition module,
        FieldDefinition srcLookupField, FieldDefinition peerLookupField,
        Relocation reloc)
    {
        InjectLookupFieldInit(module, srcLookupField, peerLookupField, reloc.SourceType, reloc.TargetType);
    }

    private static void InjectLookupFieldInit(
        ModuleDefinition module,
        FieldDefinition srcLookupField, FieldDefinition peerLookupField,
        TypeDefinition sourceType, TypeDefinition targetType)
    {
        if (peerLookupField == null) return;

        foreach (var type in EnumerateAllTypes(module))
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                if (method.Body.Instructions.Any(i =>
                        i.OpCode.Code == Code.Stfld &&
                        i.Operand is FieldReference existingFr &&
                        existingFr.Resolve() == peerLookupField))
                    return;

                var instrs = method.Body.Instructions;
                for (int i = 0; i < instrs.Count; i++)
                {
                    var stfld = instrs[i];
                    if (stfld.OpCode.Code != Code.Stfld) continue;
                    if (stfld.Operand is not FieldReference fr || fr.Resolve() != srcLookupField) continue;

                    // Find the GetComponentLookup<SourceType> call in preceding ~12 instructions
                    Instruction getCall = null;
                    int getIdx = -1;
                    for (int j = Math.Max(0, i - 12); j < i; j++)
                    {
                        var c = instrs[j];
                        if ((c.OpCode.Code == Code.Call || c.OpCode.Code == Code.Callvirt) &&
                            c.Operand is GenericInstanceMethod gim &&
                            gim.Name == "GetComponentLookup" &&
                            gim.GenericArguments.Count == 1 &&
                            TypeRefFullNameEquals(gim.GenericArguments[0], sourceType.FullName))
                        { getCall = c; getIdx = j; break; }
                    }
                    if (getCall == null) continue;

                    // Clone the complete sequence that provides all stfld arguments.
                    // stfld pops 2 items (instance ref + value), so walk backward from stfldIdx
                    // accumulating enough push coverage, just like PatchGeneratedExecute does.
                    int seqStart = i;
                    {
                        int needed2 = GetPopCount(stfld); // 2 for stfld (instance + value)
                        for (int j2 = i - 1; j2 >= 0 && needed2 > 0; j2--)
                        {
                            seqStart = j2;
                            needed2 -= GetPushCount(instrs[j2]);
                            needed2 += GetPopCount(instrs[j2]);
                            if (needed2 <= 0) break;
                        }
                    }
                    var peerGetRef = BuildGenericMethodRef(module, getCall, targetType);
                    if (peerGetRef == null) continue;

                    var peerLookupCaches = new Dictionary<FieldDefinition, FieldDefinition>();
                    var cloned = new List<Instruction>();
                    bool ok = true;
                    for (int k = seqStart; k < i; k++)
                    {
                        var orig = instrs[k];
                        Instruction c2;
                        if (orig == getCall)
                            c2 = Instruction.Create(orig.OpCode, peerGetRef);
                        else
                        {
                            c2 = CloneInstruction(orig);
                            if (c2 == null) { ok = false; break; }
                        }

                        if (c2.Operand is FieldReference fr2)
                        {
                            var resolvedField = fr2.Resolve();
                            if (resolvedField != null &&
                                resolvedField != srcLookupField &&
                                IsComponentLookupFieldOf(resolvedField, sourceType))
                            {
                                if (!peerLookupCaches.TryGetValue(resolvedField, out var peerCacheField))
                                {
                                    peerCacheField = EnsurePeerComponentLookupField(module, resolvedField, targetType);
                                    peerLookupCaches[resolvedField] = peerCacheField;
                                }

                                if (peerCacheField == null) { ok = false; break; }
                                c2.Operand = module.ImportReference(peerCacheField);
                            }
                        }
                        cloned.Add(c2);
                    }
                    if (!ok) continue;
                    cloned.Add(Instruction.Create(OpCodes.Stfld, module.ImportReference(peerLookupField)));

                    var il = method.Body.GetILProcessor();
                    var insertAfter = stfld;
                    foreach (var ij in cloned) { il.InsertAfter(insertAfter, ij); insertAfter = ij; }
                    StructEntropyLogger.Log($"[SER]   Injected lookup init for {targetType.Name} in {type.Name}.{method.Name}");
                    return;
                }
            }
        }
        StructEntropyLogger.Log($"[SER] WARNING: could not find GetComponentLookup<{sourceType.Name}> assignment to clone");
    }

    // Finds the ComponentLookup<SourceType>.get_Item call, injects peer get_Item + stloc after it.
    // Returns the new peer local, or null on failure.
    private static VariableDefinition InjectPeerGetItem(
        ModuleDefinition module, MethodDefinition method, Relocation reloc,
        FieldDefinition srcLookupField, FieldDefinition peerLookupField)
    {
        var il = method.Body.GetILProcessor();

        foreach (var instr in method.Body.Instructions.ToList())
        {
            if (instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) continue;
            if (!IsGetItemOnComponentLookup(instr, reloc.SourceType)) continue;

            var stlocInstr = instr.Next;
            if (stlocInstr == null || !IsStloc(stlocInstr)) continue;

            var sourceLocal = GetLocalFromInstruction(stlocInstr, method.Body);
            if (sourceLocal == null) continue;

            // Collect entity-producing instructions (between lookup field load and the call)
            var entityInstrs = CollectArgumentSequenceBeforeCall(instr, 0);
            if (entityInstrs == null || entityInstrs.Count == 0) continue;

            // Create peer local
            var peerLocal = new VariableDefinition(module.ImportReference(reloc.TargetType));
            method.Body.Variables.Add(peerLocal);
            method.Body.InitLocals = true;

            // Build peer get_Item call ref
            var peerGetItemRef = BuildGetItemRef(module, instr, reloc.TargetType);
            if (peerGetItemRef == null) continue;

            // Inject after stloc sourceLocal:
            //   ldarg.0 + ldflda peerLookupField + [entity loads] + call peerGetItem + stloc peerLocal
            var injection = new List<Instruction>();
            injection.Add(Instruction.Create(OpCodes.Ldarg_0));
            injection.Add(Instruction.Create(OpCodes.Ldflda, module.ImportReference(peerLookupField)));
            foreach (var ei in entityInstrs)
                injection.Add(ei);
            injection.Add(Instruction.Create(instr.OpCode, peerGetItemRef));
            injection.Add(Instruction.Create(OpCodes.Stloc, peerLocal));

            var insertAfter = stlocInstr;
            foreach (var ij in injection) { il.InsertAfter(insertAfter, ij); insertAfter = ij; }

            return peerLocal;
        }
        return null;
    }

    private static int RewriteDirectLookupFieldReads(
        ModuleDefinition module,
        MethodDefinition method,
        Relocation reloc,
        FieldDefinition srcLookupField,
        FieldDefinition peerLookupField)
    {
        int rewritten = 0;

        foreach (var instr in method.Body.Instructions.ToList())
        {
            if ((instr.OpCode != OpCodes.Ldfld && instr.OpCode != OpCodes.Ldflda) ||
                !IsFieldAccess(instr, reloc.Field, reloc.SourceTypeFullName))
                continue;

            var getItemCall = FindDirectCallInstanceProducer(instr);
            if (getItemCall == null || !IsGetItemOnComponentLookup(getItemCall, reloc.SourceType))
                continue;

            var lookupLoad = FindFieldLoadForCall(getItemCall, srcLookupField);
            if (lookupLoad == null)
                continue;

            var peerGetItemRef = BuildGetItemRef(module, getItemCall, reloc.TargetType);
            if (peerGetItemRef == null)
                continue;

            lookupLoad.Operand = module.ImportReference(peerLookupField);
            getItemCall.Operand = peerGetItemRef;
            instr.Operand = module.ImportReference(reloc.NewField);
            rewritten++;
        }

        return rewritten;
    }

    private static int RewriteLookupWholeStructWrites(
        ModuleDefinition module,
        MethodDefinition method,
        Relocation reloc,
        FieldDefinition srcLookupField,
        FieldDefinition peerLookupField)
    {
        int rewritten = 0;
        var il = method.Body.GetILProcessor();

        foreach (var sourceFieldWrite in method.Body.Instructions.ToList())
        {
            if (sourceFieldWrite.OpCode != OpCodes.Stfld ||
                !IsFieldAccess(sourceFieldWrite, reloc.Field, reloc.SourceTypeFullName))
                continue;

            if (!TryFindSetItemForWholeStructFieldWrite(
                    method,
                    sourceFieldWrite,
                    reloc,
                    srcLookupField,
                    out var setItemCall,
                    out var valueInstrs))
                continue;

            var entityInstrs = CollectArgumentSequenceBeforeCall(setItemCall, 1);
            if (entityInstrs == null || entityInstrs.Count == 0)
                continue;

            var peerGetItemRef = BuildComponentLookupAccessorRef(module, peerLookupField.FieldType, "get_Item", 1);
            var peerSetItemRef = BuildComponentLookupAccessorRef(module, peerLookupField.FieldType, "set_Item", 2);
            if (peerGetItemRef == null || peerSetItemRef == null)
                continue;

            var peerLocal = new VariableDefinition(module.ImportReference(reloc.TargetType));
            method.Body.Variables.Add(peerLocal);
            method.Body.InitLocals = true;

            var entityClonesA = CloneInstructionList(entityInstrs);
            var entityClonesB = CloneInstructionList(entityInstrs);
            if (entityClonesA == null || entityClonesB == null || valueInstrs == null)
                continue;

            var injection = new List<Instruction>
            {
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldflda, module.ImportReference(peerLookupField))
            };
            injection.AddRange(entityClonesA);
            injection.Add(Instruction.Create(OpCodes.Call, peerGetItemRef));
            injection.Add(Instruction.Create(OpCodes.Stloc, peerLocal));
            injection.Add(Instruction.Create(OpCodes.Ldloca, peerLocal));
            injection.AddRange(valueInstrs);
            injection.Add(Instruction.Create(OpCodes.Stfld, module.ImportReference(reloc.NewField)));
            injection.Add(Instruction.Create(OpCodes.Ldarg_0));
            injection.Add(Instruction.Create(OpCodes.Ldflda, module.ImportReference(peerLookupField)));
            injection.AddRange(entityClonesB);
            injection.Add(Instruction.Create(OpCodes.Ldloc, peerLocal));
            injection.Add(Instruction.Create(setItemCall.OpCode, peerSetItemRef));

            if (injection.Any(i => i == null))
                continue;

            var insertAfter = setItemCall;
            foreach (var extra in injection)
            {
                il.InsertAfter(insertAfter, extra);
                insertAfter = extra;
            }

            var popInstance = Instruction.Create(OpCodes.Pop);
            il.InsertAfter(sourceFieldWrite, popInstance);
            ReplaceInstruction(il, sourceFieldWrite, Instruction.Create(OpCodes.Pop));

            rewritten++;
        }

        return rewritten;
    }

    // -----------------------------------------------------------------------
    //  ComponentLookupRewriter sub-pass: TryGetComponent<SourceType> out-local rewrite
    // -----------------------------------------------------------------------
    //
    // Detects the pattern:
    //   ldarg.0
    //   ldflda  ComponentLookup<SourceType>
    //   ldloc   entityLocal
    //   ldloca  sourceOutLocal          // out T
    //   call    TryGetComponent<SourceType>
    //   brfalse skipLabel
    //   ...
    //   ldloc(a) sourceOutLocal
    //   ldfld   SourceType::MovedField  // access to redirect
    //
    // Injects a peer TryGetComponent<TargetType> call after the brfalse, then
    // redirects field accesses on sourceOutLocal to the peer local.
    private static int RewriteTryGetComponentFieldAccesses(
        ModuleDefinition module, MethodDefinition method, Relocation reloc,
        FieldDefinition srcLookupField, FieldDefinition peerLookupField,
        List<TryGetPeerLocal> tryGetPeerLocals)
    {
        int rewritten = 0;
        var il = method.Body.GetILProcessor();
        var snapshot = method.Body.Instructions.ToList();

        foreach (var instr in snapshot)
        {
            // Find call TryGetComponent<SourceType> on the source lookup
            if (instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) continue;
            if (!IsTryGetComponentOnComponentLookup(instr, reloc.SourceType)) continue;

            // The out param (ldloca) is the instruction right before the call
            var outParamLoad = instr.Previous;
            if (outParamLoad == null) continue;
            var sourceOutLocal = GetLocalFromInstruction(outParamLoad, method.Body);
            if (sourceOutLocal == null) continue;
            if (sourceOutLocal.VariableType.FullName != reloc.SourceType.FullName) continue;

            var entityInstrs = CollectArgumentSequenceBeforeCall(instr, 1);
            if (entityInstrs == null || entityInstrs.Count == 0) continue;

            // The brfalse/brtrue after the call
            var branchInstr = instr.Next;
            if (branchInstr == null) continue;
            if (branchInstr.OpCode.Code != Code.Brfalse && branchInstr.OpCode.Code != Code.Brfalse_S &&
                branchInstr.OpCode.Code != Code.Brtrue && branchInstr.OpCode.Code != Code.Brtrue_S)
                continue;

            // Check if any field access on this out local actually touches the moved field
            bool hasMovedFieldAccess = false;
            for (var scan = branchInstr.Next; scan != null; scan = scan.Next)
            {
                if (!IsFieldAccess(scan, reloc.Field, reloc.SourceTypeFullName)) continue;
                var instLoad = FindInstanceLoad(scan);
                if (instLoad == null) continue;
                var loadedLocal = GetLocalFromInstruction(instLoad, method.Body);
                if (loadedLocal == sourceOutLocal)
                {
                    hasMovedFieldAccess = true;
                    break;
                }
            }
            if (!hasMovedFieldAccess) continue;

            var peerLocal = FindOrInjectTryGetPeerLocal(
                module,
                method,
                reloc,
                peerLookupField,
                tryGetPeerLocals,
                sourceOutLocal,
                branchInstr,
                entityInstrs,
                instr);
            if (peerLocal == null) continue;

            // Inject after branchInstr:
            //   ldarg.0
            //   ldflda peerLookupField
            //   [entity load clone]
            //   ldloca peerLocal
            //   call TryGetComponent<TargetType>
            //   pop                              // discard bool - co-instantiation guarantees presence
            // Now redirect field accesses on sourceOutLocal that touch the moved field
            // to use peerLocal instead. Scope: from branchInstr to the branch target (the skip label).
            var scopeEnd = branchInstr.Operand as Instruction;
            bool needsWriteBack = false;
            for (var scan = branchInstr.Next; scan != null && scan != scopeEnd; scan = scan.Next)
            {
                if (!IsFieldAccess(scan, reloc.Field, reloc.SourceTypeFullName)) continue;
                var instLoad = FindInstanceLoad(scan);
                if (instLoad == null) continue;
                var loadedLocal = GetLocalFromInstruction(instLoad, method.Body);
                if (loadedLocal != sourceOutLocal) continue;

                if (scan.OpCode.Code == Code.Stfld || scan.OpCode.Code == Code.Ldflda)
                    needsWriteBack = true;

                // Redirect: change instance load to peer local, change field to new field
                Instruction replacement = IsAddressLoad(instLoad)
                    ? Instruction.Create(OpCodes.Ldloca, peerLocal)
                    : Instruction.Create(OpCodes.Ldloc, peerLocal);
                ReplaceInstruction(il, instLoad, replacement);
                scan.Operand = module.ImportReference(reloc.NewField);
                rewritten++;
            }

            if (needsWriteBack)
                rewritten += InjectTryGetPeerSetItemWriteBack(module, method, reloc, peerLookupField, sourceOutLocal, peerLocal, branchInstr, scopeEnd);
        }

        return rewritten;
    }

    private static VariableDefinition FindOrInjectTryGetPeerLocal(
        ModuleDefinition module,
        MethodDefinition method,
        Relocation reloc,
        FieldDefinition peerLookupField,
        List<TryGetPeerLocal> tryGetPeerLocals,
        VariableDefinition sourceOutLocal,
        Instruction branchInstr,
        List<Instruction> entityInstrs,
        Instruction sourceTryGetCall)
    {
        var existing = tryGetPeerLocals.FirstOrDefault(entry =>
            ReferenceEquals(entry.Method, method) &&
            ReferenceEquals(entry.SourceLocal, sourceOutLocal) &&
            ReferenceEquals(entry.BranchInstruction, branchInstr) &&
            TypeRefFullNameEquals(entry.TargetType, reloc.TargetType.FullName));
        if (existing != null)
            return existing.PeerLocal;

        var peerTryGetRef = BuildGetItemRef(module, sourceTryGetCall, reloc.TargetType);
        if (peerTryGetRef == null)
            return null;

        var peerLocal = new VariableDefinition(module.ImportReference(reloc.TargetType));
        method.Body.Variables.Add(peerLocal);
        method.Body.InitLocals = true;

        var injection = new List<Instruction>
        {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldflda, module.ImportReference(peerLookupField)),
        };
        injection.AddRange(entityInstrs.Select(CloneInstruction));
        injection.Add(Instruction.Create(OpCodes.Ldloca, peerLocal));
        injection.Add(Instruction.Create(sourceTryGetCall.OpCode, peerTryGetRef));
        injection.Add(Instruction.Create(OpCodes.Pop));

        if (injection.Any(i => i == null))
            return null;

        var il = method.Body.GetILProcessor();
        var insertAfter = branchInstr;
        foreach (var instr in injection)
        {
            il.InsertAfter(insertAfter, instr);
            insertAfter = instr;
        }

        tryGetPeerLocals.Add(new TryGetPeerLocal
        {
            Method = method,
            SourceLocal = sourceOutLocal,
            TargetType = reloc.TargetType,
            BranchInstruction = branchInstr,
            PeerLocal = peerLocal,
        });

        return peerLocal;
    }

    private static int InjectTryGetPeerSetItemWriteBack(
        ModuleDefinition module,
        MethodDefinition method,
        Relocation reloc,
        FieldDefinition peerLookupField,
        VariableDefinition sourceOutLocal,
        VariableDefinition peerLocal,
        Instruction scopeStart,
        Instruction scopeEnd)
    {
        var il = method.Body.GetILProcessor();
        int injected = 0;

        for (var scan = scopeStart.Next; scan != null && scan != scopeEnd; scan = scan.Next)
        {
            if (!IsSetItemOnComponentLookup(scan, reloc.SourceType))
                continue;

            var valueLoad = scan.Previous;
            if (GetLocalFromInstruction(valueLoad, method.Body) != sourceOutLocal)
                continue;

            var entityInstrs = CollectArgumentSequenceBeforeCall(scan, 1);
            if (entityInstrs == null || entityInstrs.Count == 0)
                continue;

            var peerSetRef = BuildSetItemRef(module, scan, reloc.TargetType);
            if (peerSetRef == null)
                continue;

            var injection = new List<Instruction>
            {
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldflda, module.ImportReference(peerLookupField)),
            };
            injection.AddRange(entityInstrs);
            injection.Add(Instruction.Create(OpCodes.Ldloc, peerLocal));
            injection.Add(Instruction.Create(scan.OpCode, peerSetRef));

            var insertAfter = scan;
            foreach (var injectedInstr in injection)
            {
                il.InsertAfter(insertAfter, injectedInstr);
                insertAfter = injectedInstr;
            }

            injected++;
        }

        return injected;
    }

    private static bool IsTryGetComponentOnComponentLookup(Instruction instr, TypeDefinition componentType)
    {
        if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
            instr.Operand is not MethodReference mr) return false;
        if (mr.Name != "TryGetComponent") return false;
        var declaring = mr.DeclaringType;
        return declaring is GenericInstanceType git &&
               git.ElementType.Name.StartsWith("ComponentLookup") &&
               git.GenericArguments.Count == 1 &&
               TypeRefFullNameEquals(git.GenericArguments[0], componentType.FullName);
    }

    // Redirects ldfld/ldflda/stfld of reloc.Field on source locals ? to peerLocal + newField
    private static int RedirectFieldAccesses(
        MethodDefinition method, Relocation reloc, VariableDefinition peerLocal)
    {
        int count = 0;
        var il = method.Body.GetILProcessor();
        var snapshot = method.Body.Instructions.ToList();

        foreach (var instr in snapshot)
        {
            if (!IsFieldAccess(instr, reloc.Field, reloc.SourceTypeFullName)) continue;

            var instanceLoad = FindInstanceLoad(instr);
            if (instanceLoad == null) continue;

            var loadedTypeName = GetBaseTypeName(GetLoadedType(instanceLoad, method));
            if (loadedTypeName != reloc.SourceType.FullName) continue;

            // Replace instance load with peerLocal load
            Instruction replacement = IsAddressLoad(instanceLoad)
                ? Instruction.Create(OpCodes.Ldloca, peerLocal)
                : Instruction.Create(OpCodes.Ldloc, peerLocal);
            ReplaceInstruction(il, instanceLoad, replacement);

            instr.Operand = reloc.NewField;
            count++;
        }
        return count;
    }

    // Injects peer write-back after ComponentLookup.set_Item<Src> and ECB.SetComponent<Src>
    private static void InjectPeerWriteBacks(
        ModuleDefinition module, MethodDefinition method, Relocation reloc,
        FieldDefinition srcLookupField, FieldDefinition peerLookupField,
        VariableDefinition peerLocal)
    {
        var il = method.Body.GetILProcessor();
        var snapshot = method.Body.Instructions.ToList();

        foreach (var instr in snapshot)
        {
            if (instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) continue;
            if (instr.Operand is not MethodReference mr) continue;

            // Case A: ComponentLookup<SourceType>.set_Item
            if (IsSetItemOnComponentLookup(instr, reloc.SourceType))
            {
                // Entity load is the second-to-last arg (before the value)
                // Walk back: call pops [ref lookup, Entity, SourceType_value]
                // Entity is 2nd from top before call
                var entityInstrs = CollectArgumentSequenceBeforeCall(instr, 1);
                if (entityInstrs == null) continue;

                var peerSetRef = BuildSetItemRef(module, instr, reloc.TargetType);
                if (peerSetRef == null) continue;

                var injection = new List<Instruction>
                {
                    Instruction.Create(OpCodes.Ldarg_0),
                    Instruction.Create(OpCodes.Ldflda, module.ImportReference(peerLookupField)),
                };
                injection.AddRange(entityInstrs);
                injection.Add(Instruction.Create(OpCodes.Ldloc, peerLocal));
                injection.Add(Instruction.Create(instr.OpCode, peerSetRef));

                var insertAfter = instr;
                foreach (var ij in injection) { il.InsertAfter(insertAfter, ij); insertAfter = ij; }
                continue;
            }

            // Case B: EntityCommandBuffer.SetComponent<SourceType>
            if (IsEcbSetComponentOf(mr, reloc.SourceType))
            {
                // Stack before call: [ref ECB, Entity, SourceType_value]
                var entityInstrs = CollectArgumentSequenceBeforeCall(instr, 1);
                if (entityInstrs == null || entityInstrs.Count == 0) continue;

                var ecbInstrs = CollectArgumentSequenceBeforeCall(instr, 2);
                if (ecbInstrs == null || ecbInstrs.Count == 0) continue;

                var peerSetRef = BuildGenericMethodRef(module, instr, reloc.TargetType);
                if (peerSetRef == null) continue;

                var injection = new List<Instruction>();
                injection.AddRange(ecbInstrs);
                injection.AddRange(entityInstrs);
                injection.Add(Instruction.Create(OpCodes.Ldloc, peerLocal));
                injection.Add(Instruction.Create(instr.OpCode, peerSetRef));

                var insertAfter = instr;
                foreach (var ij in injection) { il.InsertAfter(insertAfter, ij); insertAfter = ij; }
            }
        }
    }

    // --------------------------------------------------------------
}
