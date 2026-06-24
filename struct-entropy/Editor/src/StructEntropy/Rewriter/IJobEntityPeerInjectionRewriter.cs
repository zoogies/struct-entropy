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
    //  IJobEntityPeerInjectionRewriter: IJobEntity user Execute with direct ref param
    // --------------------------------------------------------------

    private static int ApplyIJobEntityPeerInjection(
        ModuleDefinition module, MethodDefinition userExecute, Relocation reloc)
    {
        if (!MethodAccessesField(userExecute, reloc.Field, reloc.SourceTypeFullName)) return 0;

        // Find the declaring IJobChunk type
        var jobStruct = userExecute.DeclaringType;
        if (!CjImplementsIJobChunk(jobStruct)) return 0;

        // Reuse an existing target Execute parameter when possible.
        // Injecting a duplicate peer for a target type already present in the job signature
        // produces invalid/generated wrapper shapes for Burst.
        var peerParam = FindExistingExecutePeerParameter(userExecute, reloc.TargetType);
        bool injectedPeerParam = false;
        if (peerParam == null)
        {
            var peerParamType = new ByReferenceType(module.ImportReference(reloc.TargetType));
            peerParam = new ParameterDefinition(
                $"__zd_{reloc.TargetType.Name}",
                Mono.Cecil.ParameterAttributes.None,
                peerParamType);
            userExecute.Parameters.Add(peerParam);
            injectedPeerParam = true;
        }

        int rewritten = 0;
        int srcArgIdx = FindByRefParamLdargIndex(userExecute, reloc.SourceType);
        if (srcArgIdx >= 0)
        {
            rewritten = RewriteByRefParamFieldAccesses(
                userExecute, reloc.Field, reloc.SourceTypeFullName, reloc.NewField, srcArgIdx, peerParam);
        }
        else
        {
            int srcValueParamIdx = FindValueParamIndex(userExecute, reloc.SourceType);
            if (srcValueParamIdx < 0)
            {
                StructEntropyLogger.Log($"[SER] IJobEntityPeerInjectionRewriter: could not find {reloc.SourceType.Name} param in {jobStruct.Name}.Execute");
                if (injectedPeerParam)
                    userExecute.Parameters.Remove(peerParam);
                return 0;
            }

            rewritten = RewriteByValueParamFieldAccesses(
                userExecute, reloc.Field, reloc.SourceTypeFullName, reloc.NewField, srcValueParamIdx, peerParam);
        }

        if (rewritten == 0)
        {
            StructEntropyLogger.Log($"[SER] IJobEntityPeerInjectionRewriter: no field accesses redirected in {jobStruct.Name}.Execute");
        }

        // Only inject wrapper support when a peer param was actually added.
        if (injectedPeerParam)
            ApplyIjeWrapperPatches(module, jobStruct, userExecute, reloc, peerParam);

        StructEntropyLogger.Log($"[SER]   IJobEntityPeerInjectionRewriter in {jobStruct.Name}.Execute: {rewritten} access(es)");
        userExecute.Body.OptimizeMacros();
        return rewritten;
    }

    private static ParameterDefinition FindExistingExecutePeerParameter(MethodDefinition method, TypeDefinition targetType)
    {
        if (method == null || targetType == null)
            return null;

        foreach (var parameter in method.Parameters)
        {
            if (parameter.ParameterType is ByReferenceType br &&
                TypeRefFullNameEquals(br.ElementType, targetType.FullName))
                return parameter;
        }

        return null;
    }

    // Rewrites ldarg[srcIdx] + ldfld/ldflda SourceField ? ldarg peerParam + ldfld/ldflda NewField
    private static int RewriteByRefParamFieldAccesses(
        MethodDefinition method, FieldReference srcField, string srcTypeName,
        FieldReference newField, int srcLdargIdx, ParameterDefinition peerParam)
    {
        int count = 0;
        var il = method.Body.GetILProcessor();
        var snapshot = method.Body.Instructions.ToList();

        foreach (var instr in snapshot)
        {
            if (!IsFieldAccess(instr, srcField, srcTypeName)) continue;

            var prev = instr.Previous;
            if (prev == null) continue;

            // For ldfld/ldflda: instance is immediately before (or prev might be dup)
            Instruction instanceLoad = null;
            if (instr.OpCode == OpCodes.Ldfld || instr.OpCode == OpCodes.Ldflda)
            {
                instanceLoad = (prev.OpCode == OpCodes.Dup) ? prev.Previous : prev;
            }
            else if (instr.OpCode == OpCodes.Stfld)
            {
                // Stack: [instance, value] ? walk back past value to find instance
                int rem = 1;
                var cur = instr.Previous;
                while (cur != null && rem > 0)
                {
                    rem -= GetPushCount(cur); rem += GetPopCount(cur);
                    if (rem <= 0) break;
                    cur = cur.Previous;
                }
                instanceLoad = cur?.Previous;
            }

            if (instanceLoad == null) continue;

            // Check if instance load is ldarg of source param
            if (!TryGetArgIndex(instanceLoad, method.HasThis, out int argIdx) || argIdx != srcLdargIdx) continue;

            // Replace with ldarg peerParam
            var newLoad = Instruction.Create(OpCodes.Ldarg, peerParam);
            ReplaceInstruction(il, instanceLoad, newLoad);
            instr.Operand = newField;
            count++;
        }
        return count;
    }

    private static int RewriteByValueParamFieldAccesses(
        MethodDefinition method, FieldReference srcField, string srcTypeName,
        FieldReference newField, int srcParamIdx, ParameterDefinition peerParam)
    {
        int count = 0;
        var il = method.Body.GetILProcessor();
        var snapshot = method.Body.Instructions.ToList();

        foreach (var instr in snapshot)
        {
            if (!IsFieldAccess(instr, srcField, srcTypeName))
                continue;

            var instanceLoad = FindInstanceLoad(instr);
            if (instanceLoad == null)
                continue;

            if (!TryGetParamIndex(instanceLoad, method, out int paramIdx) || paramIdx != srcParamIdx)
                continue;

            var replacement = Instruction.Create(OpCodes.Ldarg, peerParam);
            ReplaceInstruction(il, instanceLoad, replacement);
            instr.Operand = newField;
            count++;
        }

        return count;
    }

    // Injects ComponentTypeHandle<TargetType> field, patches generated Execute, and __AssignHandles
    private static void ApplyIjeWrapperPatches(
        ModuleDefinition module, TypeDefinition jobStruct,
        MethodDefinition userExecute, Relocation reloc,
        ParameterDefinition peerParam)
    {
        // Find generated Execute (IJobChunk pattern: first param is ArchetypeChunk)
        var genExecute = jobStruct.Methods.FirstOrDefault(m =>
            m.HasBody && m.Name == "Execute" &&
            m.Parameters.Count >= 1 &&
            m.Parameters[0].ParameterType.Name.StartsWith("ArchetypeChunk"));
        if (genExecute == null)
        {
            StructEntropyLogger.Log($"[SER] IjeWrapper: no generated Execute on {jobStruct.Name}");
            return;
        }

        // Find ComponentTypeHandle<SourceType> (may be on nested __TypeHandle struct)
        var srcHandleField = FindComponentTypeHandleField(jobStruct, reloc.SourceType);
        if (srcHandleField == null)
        {
            StructEntropyLogger.Log($"[SER] IjeWrapper: no ComponentTypeHandle<{reloc.SourceType.Name}> on {jobStruct.Name}");
            return;
        }

        var handleOwner = FindTypeOwningField(jobStruct, srcHandleField) ?? jobStruct;

        // Stage 3: add ComponentTypeHandle<TargetType> if not already present
        bool alreadyHas = handleOwner.Fields.Any(f =>
            f.FieldType is GenericInstanceType g &&
            g.GenericArguments.Count == 1 &&
            TypeRefFullNameEquals(g.GenericArguments[0], reloc.TargetType.FullName) &&
            g.ElementType.Name.StartsWith("ComponentTypeHandle"));
        if (alreadyHas) return;

        var peerGit = new GenericInstanceType(((GenericInstanceType)srcHandleField.FieldType).ElementType);
        peerGit.GenericArguments.Add(module.ImportReference(reloc.TargetType));
        var peerHandleField = new FieldDefinition(
            $"__zd_{reloc.TargetType.Name}_ComponentTypeHandle",
            srcHandleField.Attributes,
            module.ImportReference(peerGit));
        CopyFieldMetadata(module, srcHandleField, peerHandleField);
        handleOwner.Fields.Add(peerHandleField);

        // Stage 4: inject GetComponentTypeHandle<TargetType> in __AssignHandles
        InjectPeerHandleInit(module, handleOwner, reloc, srcHandleField, peerHandleField);
        InjectPeerHandleUpdate(module, handleOwner, reloc, srcHandleField, peerHandleField);

        // Patch generated Execute: inject peer array init + update call sites
        PatchGeneratedExecute(module, jobStruct, genExecute, userExecute, reloc, srcHandleField, peerHandleField, peerParam);
    }

    // Finds where srcHandleField is initialized and injects peer handle init after it
    private static void InjectPeerHandleInit(
        ModuleDefinition module, TypeDefinition ownerType,
        Relocation reloc, FieldDefinition srcHandleField, FieldDefinition peerHandleField)
    {
        foreach (var type in EnumerateAllTypes(module))
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                var instrs = method.Body.Instructions;
                for (int i = 0; i < instrs.Count; i++)
                {
                    var stfld = instrs[i];
                    if (stfld.OpCode.Code != Code.Stfld) continue;
                    if (stfld.Operand is not FieldReference fr || fr.Resolve() != srcHandleField) continue;

                    // Find GetComponentTypeHandle<SourceType> call
                    Instruction getHandleCall = null; int getHandleIdx = -1;
                    for (int j = Math.Max(0, i - 8); j < i; j++)
                    {
                        var c = instrs[j];
                        if ((c.OpCode.Code == Code.Call || c.OpCode.Code == Code.Callvirt) &&
                            c.Operand is GenericInstanceMethod gim &&
                            gim.Name == "GetComponentTypeHandle" && gim.GenericArguments.Count == 1 &&
                            TypeRefFullNameEquals(gim.GenericArguments[0], reloc.SourceType.FullName))
                        { getHandleCall = c; getHandleIdx = j; break; }
                    }
                    if (getHandleCall == null) continue;

                    var peerGetHandleRef = BuildGenericMethodRef(module, getHandleCall, reloc.TargetType);
                    if (peerGetHandleRef == null) continue;

                    // Clone the full sequence that feeds the original stfld:
                    // instance + GetComponentTypeHandle<T> value.
                    int seqStart = i;
                    int needed = GetPopCount(stfld);
                    for (int j = i - 1; j >= 0 && needed > 0; j--)
                    {
                        seqStart = j;
                        needed -= GetPushCount(instrs[j]);
                        needed += GetPopCount(instrs[j]);
                        if (needed <= 0) break;
                    }

                    var cloned = new List<Instruction>(); bool ok = true;
                    for (int k = seqStart; k < i; k++)
                    {
                        var orig = instrs[k];
                        Instruction c2 = (orig == getHandleCall)
                            ? Instruction.Create(orig.OpCode, peerGetHandleRef)
                            : CloneInstruction(orig);
                        if (c2 == null) { ok = false; break; }
                        // Redirect srcHandleField references to peerHandleField
                        if (c2.Operand is FieldReference fr2 && fr2.Resolve() == srcHandleField)
                            c2.Operand = module.ImportReference(peerHandleField);
                        cloned.Add(c2);
                    }
                    if (!ok) continue;
                    cloned.Add(Instruction.Create(OpCodes.Stfld, module.ImportReference(peerHandleField)));

                    var il = method.Body.GetILProcessor();
                    var after = stfld;
                    foreach (var ij in cloned) { il.InsertAfter(after, ij); after = ij; }
                    StructEntropyLogger.Log($"[SER]   Injected TypeHandle init for {reloc.TargetType.Name} in {type.Name}.{method.Name}");
                    return;
                }
            }
        }
        StructEntropyLogger.Log($"[SER] WARNING: could not find GetComponentTypeHandle<{reloc.SourceType.Name}> stfld to clone");
    }

    // Patches the generated IJobChunk.Execute to inject a peer IntPtr array and update call sites
    private static void PatchGeneratedExecute(
        ModuleDefinition module, TypeDefinition jobStruct,
        MethodDefinition genExecute, MethodDefinition userExecute,
        Relocation reloc, FieldDefinition srcHandleField, FieldDefinition peerHandleField,
        ParameterDefinition peerParam)
    {
        var instrs = genExecute.Body.Instructions;

        // Find UnsafeGetChunkNativeArrayIntPtr<SourceType> call and its stloc
        Instruction srcGetIntPtrCall = null; int srcGetIntPtrIdx = -1;
        for (int i = 0; i < instrs.Count; i++)
        {
            var instr = instrs[i];
            if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
                instr.Operand is not MethodReference mr) continue;
            if ((!mr.Name.Contains("UnsafeGetChunkNativeArrayIntPtr") &&
                 !mr.Name.Contains("UnsafeGetChunkNativeArrayReadOnlyIntPtr")) ||
                mr is not GenericInstanceMethod gim || gim.GenericArguments.Count < 1) continue;
            if (!TypeRefFullNameEquals(gim.GenericArguments[0], reloc.SourceType.FullName)) continue;
            srcGetIntPtrCall = instr; srcGetIntPtrIdx = i; break;
        }

        if (srcGetIntPtrCall == null)
        {
            // Dump all calls in the generated Execute to help diagnose the missing ptr call
            var callNames = instrs
                .Where(x => x.OpCode.Code == Code.Call || x.OpCode.Code == Code.Callvirt)
                .Select(x => x.Operand is MethodReference mr2 ? mr2.Name : "?")
                .Distinct().ToList();
            StructEntropyLogger.Log($"[SER] IjeWrapper: no UnsafeGetChunkNativeArrayIntPtr<{reloc.SourceType.Name}> in generated Execute of {jobStruct.Name}. Calls found: [{string.Join(", ", callNames)}]");
            return;
        }

        var stlocAfter = srcGetIntPtrIdx + 1 < instrs.Count ? instrs[srcGetIntPtrIdx + 1] : null;
        if (stlocAfter == null || !IsStloc(stlocAfter)) return;
        var srcArrayLocal = GetLocalFromInstruction(stlocAfter, genExecute.Body);
        if (srcArrayLocal == null) return;

        // Find ldflda of srcHandleField in preceding instructions
        int ldfldaIdx = -1;
        for (int j = Math.Max(0, srcGetIntPtrIdx - 8); j < srcGetIntPtrIdx; j++)
        {
            if (instrs[j].Operand is FieldReference fr3 && fr3.Resolve() == srcHandleField)
            { ldfldaIdx = j; break; }
        }
        if (ldfldaIdx < 0) return;

        // Walk backward from ldfldaIdx to find the start of the complete argument sequence.
        // The call consumes GetPopCount args (e.g., 2 for static(ArchetypeChunk, ComponentTypeHandle&)).
        // We need to include both the chunk-loading instructions (ldarg.1 + ldobj) AND the handle
        // sequence (ldarg.0 + ldflda... + ldflda handle).
        int handleSeqStart = ldfldaIdx; int needed = GetPopCount(srcGetIntPtrCall);
        for (int j = ldfldaIdx - 1; j >= 0 && needed > 0; j--)
        {
            handleSeqStart = j;
            needed -= GetPushCount(instrs[j]); needed += GetPopCount(instrs[j]);
            if (needed <= 0) break;
        }

        var peerGetIntPtrRef = BuildGenericMethodRef(module, srcGetIntPtrCall, reloc.TargetType);
        if (peerGetIntPtrRef == null) return;

        var peerIntPtrLocal = new VariableDefinition(srcArrayLocal.VariableType);
        genExecute.Body.Variables.Add(peerIntPtrLocal);
        genExecute.Body.InitLocals = true;

        // Clone the sequence [handleSeqStart..srcGetIntPtrIdx] with srcHandleField replaced
        var clonedSeq = new List<Instruction>(); bool cloneOk = true;
        for (int k = handleSeqStart; k <= srcGetIntPtrIdx; k++)
        {
            var orig = instrs[k];
            Instruction clone;
            if (orig.Operand is FieldReference fr4 && fr4.Resolve() == srcHandleField)
                clone = Instruction.Create(orig.OpCode, module.ImportReference(peerHandleField));
            else if ((orig.OpCode.Code == Code.Call || orig.OpCode.Code == Code.Callvirt) &&
                     orig.Operand is MethodReference mr2 &&
                     (mr2.Name.Contains("UnsafeGetChunkNativeArrayIntPtr") ||
                      mr2.Name.Contains("UnsafeGetChunkNativeArrayReadOnlyIntPtr")))
                clone = Instruction.Create(orig.OpCode, peerGetIntPtrRef);
            else { clone = CloneInstruction(orig); if (clone == null) { cloneOk = false; break; } }
            clonedSeq.Add(clone);
        }
        if (!cloneOk) { StructEntropyLogger.Log($"[SER] IjeWrapper: clone failed for {jobStruct.Name}"); return; }
        clonedSeq.Add(Instruction.Create(OpCodes.Stloc, peerIntPtrLocal));

        var il = genExecute.Body.GetILProcessor();
        var insertPt = stlocAfter;
        foreach (var ij in clonedSeq) { il.InsertAfter(insertPt, ij); insertPt = ij; }

        // Find UnsafeGetRefToNativeArrayPtrElement<SourceType> call in generated Execute
        Instruction srcUnsafeGetRefCall = null;
        foreach (var instr in genExecute.Body.Instructions)
        {
            if ((instr.OpCode.Code == Code.Call || instr.OpCode.Code == Code.Callvirt) &&
                instr.Operand is GenericInstanceMethod gim2 &&
                gim2.Name.Contains("UnsafeGetRefToNativeArrayPtrElement") &&
                gim2.GenericArguments.Count >= 1 &&
                TypeRefFullNameEquals(gim2.GenericArguments[0], reloc.SourceType.FullName))
            { srcUnsafeGetRefCall = instr; break; }
        }
        if (srcUnsafeGetRefCall == null)
        {
            StructEntropyLogger.Log($"[SER] IjeWrapper: no UnsafeGetRefToNativeArrayPtrElement<{reloc.SourceType.Name}> in {jobStruct.Name} generated Execute");
            return;
        }

        var peerGetRefRef = BuildGenericMethodRef(module, srcUnsafeGetRefCall, reloc.TargetType);
        if (peerGetRefRef == null) return;

        // Patch each call site to userExecute: inject ldloc peerIntPtr + index + peerGetRef before it
        var snapshot = genExecute.Body.Instructions.ToList();
        foreach (var callInstr in snapshot)
        {
            if ((callInstr.OpCode.Code != Code.Call && callInstr.OpCode.Code != Code.Callvirt) ||
                callInstr.Operand is not MethodReference mr5 || mr5.Resolve() != userExecute) continue;

            // Walk back to find source UnsafeGetRefToNativeArrayPtrElement<SourceType>
            Instruction foundSrc = null;
            var scan = callInstr.Previous; int rem2 = 60;
            while (scan != null && rem2-- > 0)
            {
                if ((scan.OpCode.Code == Code.Call || scan.OpCode.Code == Code.Callvirt) &&
                    scan.Operand is GenericInstanceMethod gim3 &&
                    gim3.Name.Contains("UnsafeGetRefToNativeArrayPtrElement") &&
                    gim3.GenericArguments.Count >= 1 &&
                    TypeRefFullNameEquals(gim3.GenericArguments[0], reloc.SourceType.FullName))
                { foundSrc = scan; break; }
                scan = scan.Previous;
            }
            if (foundSrc == null) continue;

            var indexLoad = foundSrc.Previous;
            if (indexLoad == null || !IsSimpleLoad(indexLoad)) continue;
            var indexClone = CloneInstruction(indexLoad);
            if (indexClone == null) continue;

            il.InsertBefore(callInstr, Instruction.Create(OpCodes.Ldloc, peerIntPtrLocal));
            il.InsertBefore(callInstr, indexClone);
            il.InsertBefore(callInstr, Instruction.Create(srcUnsafeGetRefCall.OpCode, peerGetRefRef));

            callInstr.Operand = userExecute; // refresh operand to pick up new signature
        }

        genExecute.Body.OptimizeMacros();
        StructEntropyLogger.Log($"[SER]   Patched generated Execute in {jobStruct.Name}");
    }

    // --------------------------------------------------------------
}
