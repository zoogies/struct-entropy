using System;
using System.Linq;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class AccessPatternClassifier
{
    internal static VariableDefinition GetLocalFromInstruction(Instruction instr, MethodBody body)
    {
        if (instr == null || body == null || !body.HasVariables)
            return null;

        return instr.OpCode.Code switch
        {
            Code.Ldloc or Code.Ldloc_S or Code.Ldloca or Code.Ldloca_S or Code.Stloc or Code.Stloc_S
                => instr.Operand as VariableDefinition,
            Code.Ldloc_0 or Code.Stloc_0 => body.Variables.Count > 0 ? body.Variables[0] : null,
            Code.Ldloc_1 or Code.Stloc_1 => body.Variables.Count > 1 ? body.Variables[1] : null,
            Code.Ldloc_2 or Code.Stloc_2 => body.Variables.Count > 2 ? body.Variables[2] : null,
            Code.Ldloc_3 or Code.Stloc_3 => body.Variables.Count > 3 ? body.Variables[3] : null,
            _ => null
        };
    }

    internal static List<FieldAccessSite> BuildFieldAccessSites(
        IEnumerable<MethodDefinition> allMethods,
        IEnumerable<CandidateGroup> candidateGroups,
        Dictionary<string, ComponentTypeInfo> knownComponents)
    {
        var sites = new List<FieldAccessSite>();
        var seen = new HashSet<string>();

        foreach (var group in candidateGroups)
        {
            var groupComponents = new HashSet<string>(group.Components.Select(c => c.Type.FullName));

            foreach (var method in allMethods)
            {
                foreach (var instr in method.Body.Instructions)
                {
                    if (instr.Operand is not FieldReference fieldRef)
                        continue;

                    if (!knownComponents.ContainsKey(fieldRef.DeclaringType.FullName))
                        continue;

                    if (!groupComponents.Contains(fieldRef.DeclaringType.FullName))
                        continue;

                    if (fieldRef.Resolve() is not FieldDefinition resolvedField || resolvedField.IsStatic)
                        continue;

                    if (resolvedField.DeclaringType != null && resolvedField.DeclaringType.FullName != fieldRef.DeclaringType.FullName)
                        continue;

                    var accessKind = ClassifyAccessKind(method, instr, fieldRef.DeclaringType.FullName, groupComponents);
                    var capabilities = DetermineRequiredCapabilities(method, accessKind, fieldRef.DeclaringType.FullName);
                    var requiredComponents = DetermineRequiredEntityComponents(method, fieldRef.DeclaringType.FullName, groupComponents);
                    DetermineAvailableUncheckedRefComponents(method, groupComponents, out var availableRwComponents, out var availableRoComponents);
                    bool explicitEntityIndirection =
                        accessKind == AccessKind.ComponentLookupByEntity ||
                        accessKind == AccessKind.ComponentLookupTryGetComponent ||
                        accessKind == AccessKind.EntityManagerGetComponentData ||
                        accessKind == AccessKind.EntityManagerWholeStructWrite ||
                        accessKind == AccessKind.EcbWholeStructWrite;

                    var siteLabel = $"{method.FullName}@{instr.OpCode.Code}:{fieldRef.FullName}";
                    if (!seen.Add(siteLabel))
                        continue;

                    sites.Add(new FieldAccessSite(
                        method.FullName,
                        fieldRef.FullName,
                        fieldRef.DeclaringType.FullName,
                        accessKind,
                        instr.OpCode.Code.ToString(),
                        explicitEntityIndirection,
                        requiredComponents,
                        availableRwComponents,
                        availableRoComponents,
                        capabilities,
                        siteLabel));
                }
            }
        }

        return sites;
    }

    private static AccessKind ClassifyAccessKind(MethodDefinition method, Instruction instr, string sourceComponentFullName, HashSet<string> groupComponents)
    {
        // Locals materialized from ComponentLookup<T>.get_Item(entity) are entity-indirected
        // even when later field stores are written back through ComponentLookup<T>.set_Item.
        // Classify these before the whole-struct write-back heuristic so they use the
        // ComponentLookup peer rewriter instead of being blocked as init-only stores.
        if (instr.OpCode.Code == Code.Ldfld || instr.OpCode.Code == Code.Stfld || instr.OpCode.Code == Code.Ldflda)
        {
            if (IsFieldOnComponentLookupGetItemLocal(method, instr, sourceComponentFullName))
                return AccessKind.ComponentLookupByEntity;
        }

        if (instr.OpCode.Code == Code.Stfld)
        {
            if (IsSourceTempWholeStructSetItem(method, instr, sourceComponentFullName))
                return AccessKind.WholeStructInitStore;

            if (IsSourceTempEntityManagerWholeStructWrite(method, instr, sourceComponentFullName))
                return AccessKind.EntityManagerWholeStructWrite;

            if (IsSourceTempEcbWholeStructWrite(method, instr, sourceComponentFullName))
                return AccessKind.EcbWholeStructWrite;

            var prev = instr.Previous;
            if (prev != null && prev.OpCode.Code == Code.Initobj)
                return AccessKind.WholeStructInitStore;
        }

        // Check if this field access is on a local populated by TryGetComponent<SourceType>
        // Must run BEFORE the ldflda→AddressTakenField check so that compound assignments
        // (e.g. moon.Field += val) on TryGetComponent out-locals are classified correctly.
        if (instr.OpCode.Code == Code.Ldfld || instr.OpCode.Code == Code.Stfld || instr.OpCode.Code == Code.Ldflda)
        {
            if (IsFieldOnEntityManagerGetComponentDataLocal(method, instr, sourceComponentFullName))
                return AccessKind.EntityManagerGetComponentData;

            if (IsFieldOnTryGetComponentOutLocal(method, instr, sourceComponentFullName))
                return AccessKind.ComponentLookupTryGetComponent;
        }

        if (instr.OpCode.Code == Code.Ldflda)
            return AccessKind.AddressTakenField;

        var loadProducer = instr.Previous;
        if (loadProducer != null &&
            (loadProducer.OpCode.Code == Code.Call || loadProducer.OpCode.Code == Code.Callvirt) &&
            loadProducer.Operand is MethodReference mr)
        {
            if (mr.DeclaringType is GenericInstanceType uncheckedGit &&
                uncheckedGit.GenericArguments.Count == 1 &&
                (uncheckedGit.GenericArguments[0].Resolve()?.FullName == sourceComponentFullName ||
                 uncheckedGit.GenericArguments[0].FullName == sourceComponentFullName))
            {
                if ((mr.Name == "get_ValueRO" || mr.Name == "get_ValueRW") &&
                    uncheckedGit.ElementType.Name.StartsWith("UncheckedRefRW", StringComparison.Ordinal))
                    return AccessKind.UncheckedRefRwComponent;

                // UncheckedRefRO<T>.get_ValueRO() is the access pattern produced in the
                // outer method body when iterating SystemAPI.Query<RefRO<T>>().  The
                // InlineForEachRewriter (ApplyInlineForEach) handles these by extending the
                // IFE enumerator type in-place, so classify as IfeSingleComponent so that
                // IfePeerInjection capability is assigned for validation/rewriting.
                if (mr.Name == "get_ValueRO" &&
                    uncheckedGit.ElementType.Name.StartsWith("UncheckedRefRO", StringComparison.Ordinal))
                    return AccessKind.IfeSingleComponent;
            }

            if (mr.Name == "get_Item" &&
                mr.DeclaringType is GenericInstanceType lookupGit &&
                lookupGit.ElementType.FullName.StartsWith("Unity.Entities.ComponentLookup"))
            {
                return AccessKind.ComponentLookupByEntity;
            }

            if (IsEntityManagerGetComponentDataCall(loadProducer, sourceComponentFullName))
            {
                return AccessKind.EntityManagerGetComponentData;
            }
        }

        if (IsIfeEnumeratorMethod(method) && method.Name == "get_Current")
        {
            var tupleComponents = ExtractGroupComponentsFromTupleReturn(method.ReturnType, groupComponents);
            return tupleComponents.Count > 1 ? AccessKind.IfeTupleComponent : AccessKind.IfeSingleComponent;
        }

        if (method.Name == "Execute" &&
            method.Parameters.Any(p =>
            {
                var tr = p.ParameterType is ByReferenceType br ? br.ElementType : p.ParameterType;
                return tr.FullName == sourceComponentFullName;
            }))
        {
            return AccessKind.IJobEntityParameter;
        }

        return AccessKind.DirectFieldUse;
    }

    private static bool HasSourceExecuteParameter(MethodDefinition method, string sourceComponentFullName, bool byReference)
    {
        if (method?.Name != "Execute")
            return false;

        foreach (var parameter in method.Parameters)
        {
            if (byReference)
            {
                if (parameter.ParameterType is ByReferenceType br &&
                    br.ElementType.FullName == sourceComponentFullName)
                    return true;
            }
            else if (parameter.ParameterType.FullName == sourceComponentFullName)
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<RewriteCapability> DetermineRequiredCapabilities(
        MethodDefinition method,
        AccessKind kind,
        string sourceComponentFullName)
    {
        var caps = new HashSet<RewriteCapability>();
        switch (kind)
        {
            case AccessKind.ComponentLookupByEntity:
                caps.Add(RewriteCapability.LookupGetItemRead);
                break;
            case AccessKind.EntityManagerGetComponentData:
                caps.Add(RewriteCapability.EntityManagerGetComponentDataRedirect);
                break;
            case AccessKind.IJobEntityParameter:
                caps.Add(RewriteCapability.IJobEntityPeerInjection);
                break;
            case AccessKind.IfeSingleComponent:
            case AccessKind.IfeTupleComponent:
                caps.Add(RewriteCapability.IfePeerInjection);
                break;
            case AccessKind.UncheckedRefRwComponent:
                caps.Add(RewriteCapability.UncheckedRefRwPeerRedirect);
                break;
            case AccessKind.AddressTakenField:
                caps.Add(RewriteCapability.LdfldaPeerRedirect);
                break;
            case AccessKind.ComponentLookupTryGetComponent:
                caps.Add(RewriteCapability.LookupTryGetComponentRedirect);
                break;
            case AccessKind.WholeStructInitStore:
                caps.Add(RewriteCapability.WholeStructInitRewrite);
                caps.Add(RewriteCapability.LookupSetItemWholeStructWrite);
                break;
            case AccessKind.EntityManagerWholeStructWrite:
                caps.Add(RewriteCapability.EntityManagerWholeStructWriteRedirect);
                break;
            case AccessKind.EcbWholeStructWrite:
                caps.Add(RewriteCapability.EcbWholeStructWriteRedirect);
                break;
            default:
                caps.Add(RewriteCapability.DirectFieldRedirect);
                break;
        }

        if (HasSourceExecuteParameter(method, sourceComponentFullName, true))
            caps.Add(RewriteCapability.IJobEntityPeerInjection);

        if (HasSourceExecuteParameter(method, sourceComponentFullName, false))
            caps.Add(RewriteCapability.IJobEntityByValuePeerMaterialization);

        return caps;
    }

    private static HashSet<string> DetermineRequiredEntityComponents(
        MethodDefinition method,
        string sourceComponentFullName,
        HashSet<string> groupComponents)
    {
        var required = new HashSet<string>();
        if (IsIfeEnumeratorMethod(method) && method.Name == "get_Current")
        {
            foreach (var component in ExtractGroupComponentsFromTupleReturn(method.ReturnType, groupComponents))
                required.Add(component);
        }

        if (method.Name == "Execute")
        {
            foreach (var parameter in method.Parameters)
            {
                var tr = parameter.ParameterType is ByReferenceType br ? br.ElementType : parameter.ParameterType;
                if (groupComponents.Contains(tr.FullName))
                    required.Add(tr.FullName);
            }
        }

        if (method?.HasBody == true)
        {
            foreach (var variable in method.Body.Variables)
            {
                if (variable.VariableType is not GenericInstanceType git ||
                    git.GenericArguments.Count != 1)
                    continue;

                string containerName = git.ElementType.Name;
                if (!containerName.StartsWith("UncheckedRefRW", StringComparison.Ordinal) &&
                    !containerName.StartsWith("UncheckedRefRO", StringComparison.Ordinal))
                    continue;

                var componentType = git.GenericArguments[0].Resolve()?.FullName ?? git.GenericArguments[0].FullName;
                if (groupComponents.Contains(componentType))
                    required.Add(componentType);
            }
        }

        if (required.Count == 0)
            required.Add(sourceComponentFullName);

        return required;
    }

    private static void DetermineAvailableUncheckedRefComponents(
        MethodDefinition method,
        HashSet<string> groupComponents,
        out HashSet<string> availableRwComponents,
        out HashSet<string> availableRoComponents)
    {
        availableRwComponents = new HashSet<string>(StringComparer.Ordinal);
        availableRoComponents = new HashSet<string>(StringComparer.Ordinal);

        if (method?.HasBody != true)
            return;

        foreach (var variable in method.Body.Variables)
        {
            if (variable.VariableType is not GenericInstanceType git ||
                git.GenericArguments.Count != 1)
                continue;

            var componentType = git.GenericArguments[0].Resolve()?.FullName ?? git.GenericArguments[0].FullName;
            if (!groupComponents.Contains(componentType))
                continue;

            string containerName = git.ElementType.Name;
            if (containerName.StartsWith("UncheckedRefRW", StringComparison.Ordinal))
            {
                availableRwComponents.Add(componentType);
                availableRoComponents.Add(componentType);
            }
            else if (containerName.StartsWith("UncheckedRefRO", StringComparison.Ordinal))
            {
                availableRoComponents.Add(componentType);
            }
        }
    }

    private static bool IsIfeEnumeratorMethod(MethodDefinition method)
    {
        return method?.DeclaringType?.Name == "Enumerator" &&
               method.DeclaringType.DeclaringType != null &&
               method.DeclaringType.DeclaringType.Name.StartsWith("IFE_", StringComparison.Ordinal);
    }

    private static Instruction FindFieldInstanceLoad(Instruction fieldAccess)
    {
        if (fieldAccess == null)
            return null;

        if (fieldAccess.OpCode.Code == Code.Ldfld || fieldAccess.OpCode.Code == Code.Ldflda)
            return fieldAccess.Previous;

        if (fieldAccess.OpCode.Code != Code.Stfld)
            return null;

        int remaining = 1;
        var current = fieldAccess.Previous;
        while (current != null && remaining > 0)
        {
            remaining -= GetPushCount(current);
            remaining += GetPopCount(current);
            if (remaining <= 0)
                break;
            current = current.Previous;
        }

        return current?.Previous;
    }

    private static int GetPushCount(Instruction instr)
    {
        switch (instr.OpCode.StackBehaviourPush)
        {
            case StackBehaviour.Push0: return 0;
            case StackBehaviour.Push1:
            case StackBehaviour.Pushi:
            case StackBehaviour.Pushi8:
            case StackBehaviour.Pushr4:
            case StackBehaviour.Pushr8:
            case StackBehaviour.Pushref:
                return 1;
            case StackBehaviour.Push1_push1:
                return 2;
            case StackBehaviour.Varpush:
                return instr.OpCode.FlowControl == FlowControl.Call &&
                       instr.Operand is IMethodSignature ms &&
                       ms.ReturnType.FullName == "System.Void"
                    ? 0
                    : 1;
            default:
                return 0;
        }
    }

    private static int GetPopCount(Instruction instr)
    {
        switch (instr.OpCode.StackBehaviourPop)
        {
            case StackBehaviour.Pop0: return 0;
            case StackBehaviour.Pop1:
            case StackBehaviour.Popi:
            case StackBehaviour.Popref:
                return 1;
            case StackBehaviour.Pop1_pop1:
            case StackBehaviour.Popi_pop1:
            case StackBehaviour.Popi_popi:
            case StackBehaviour.Popi_popi8:
            case StackBehaviour.Popi_popr4:
            case StackBehaviour.Popi_popr8:
            case StackBehaviour.Popref_pop1:
            case StackBehaviour.Popref_popi:
                return 2;
            case StackBehaviour.Popi_popi_popi:
            case StackBehaviour.Popref_popi_popi:
            case StackBehaviour.Popref_popi_popi8:
            case StackBehaviour.Popref_popi_popr4:
            case StackBehaviour.Popref_popi_popr8:
            case StackBehaviour.Popref_popi_popref:
                return 3;
            case StackBehaviour.Varpop:
                if (instr.OpCode.FlowControl == FlowControl.Call && instr.Operand is IMethodSignature ms)
                {
                    int count = ms.Parameters.Count;
                    if (ms.HasThis && !ms.ExplicitThis)
                        count++;
                    return count;
                }

                return 0;
            default:
                return 0;
        }
    }

    private static bool IsSourceTempWholeStructSetItem(
        MethodDefinition method,
        Instruction fieldStore,
        string sourceComponentFullName)
    {
        if (method?.HasBody != true || fieldStore?.OpCode.Code != Code.Stfld)
            return false;

        var instanceLoad = fieldStore.Previous?.Previous;
        var sourceLocal = GetLocalFromInstruction(instanceLoad, method.Body);
        if (sourceLocal == null || sourceLocal.VariableType.FullName != sourceComponentFullName)
            return false;

        int budget = 12;
        for (var scan = fieldStore.Next; scan != null && budget-- > 0; scan = scan.Next)
        {
            if ((scan.OpCode.Code == Code.Call || scan.OpCode.Code == Code.Callvirt) &&
                scan.Operand is MethodReference mr &&
                mr.Name == "set_Item" &&
                mr.DeclaringType is GenericInstanceType git &&
                git.ElementType.FullName.StartsWith("Unity.Entities.ComponentLookup") &&
                git.GenericArguments.Count == 1 &&
                git.GenericArguments[0].Resolve()?.FullName == sourceComponentFullName)
            {
                var valueLoad = scan.Previous;
                var valueLocal = GetLocalFromInstruction(valueLoad, method.Body);
                return valueLocal == sourceLocal;
            }
        }

        return false;
    }

    private static bool IsSourceTempEntityManagerWholeStructWrite(
        MethodDefinition method,
        Instruction fieldStore,
        string sourceComponentFullName)
    {
        if (method?.HasBody != true || fieldStore?.OpCode.Code != Code.Stfld)
            return false;

        var instanceLoad = fieldStore.Previous?.Previous;
        var sourceLocal = GetLocalFromInstruction(instanceLoad, method.Body);
        if (sourceLocal == null || sourceLocal.VariableType.FullName != sourceComponentFullName)
            return false;

        int budget = 16;
        for (var scan = fieldStore.Next; scan != null && budget-- > 0; scan = scan.Next)
        {
            if ((scan.OpCode.Code != Code.Call && scan.OpCode.Code != Code.Callvirt) ||
                scan.Operand is not MethodReference mr)
                continue;

            if (mr.Name == "SetComponentData" &&
                mr.DeclaringType.FullName == "Unity.Entities.EntityManager" &&
                mr is GenericInstanceMethod gim &&
                gim.GenericArguments.Count == 1 &&
                (gim.GenericArguments[0].Resolve()?.FullName == sourceComponentFullName ||
                 gim.GenericArguments[0].FullName == sourceComponentFullName))
                return true;
        }

        return false;
    }

    private static bool IsSourceTempEcbWholeStructWrite(
        MethodDefinition method,
        Instruction fieldStore,
        string sourceComponentFullName)
    {
        if (method?.HasBody != true || fieldStore?.OpCode.Code != Code.Stfld)
            return false;

        var instanceLoad = fieldStore.Previous?.Previous;
        var sourceLocal = GetLocalFromInstruction(instanceLoad, method.Body);
        if (sourceLocal == null || sourceLocal.VariableType.FullName != sourceComponentFullName)
            return false;

        int budget = 16;
        for (var scan = fieldStore.Next; scan != null && budget-- > 0; scan = scan.Next)
        {
            if ((scan.OpCode.Code != Code.Call && scan.OpCode.Code != Code.Callvirt) ||
                scan.Operand is not MethodReference mr)
                continue;

            if ((mr.Name == "SetComponent" || mr.Name == "AddComponent") &&
                IsEntityCommandBufferType(mr.DeclaringType?.FullName) &&
                mr is GenericInstanceMethod gim &&
                gim.GenericArguments.Count == 1 &&
                (gim.GenericArguments[0].Resolve()?.FullName == sourceComponentFullName ||
                 gim.GenericArguments[0].FullName == sourceComponentFullName))
                return true;
        }

        return false;
    }

    private static bool IsEntityCommandBufferType(string fullName)
        => fullName == "Unity.Entities.EntityCommandBuffer" ||
           fullName == "Unity.Entities.EntityCommandBuffer/ParallelWriter";

    private static bool IsEntityManagerGetComponentDataCall(
        Instruction instr,
        string componentFullName)
    {
        if ((instr?.OpCode.Code != Code.Call && instr?.OpCode.Code != Code.Callvirt) ||
            instr.Operand is not MethodReference mr)
            return false;

        if (mr.Name != "GetComponentData" ||
            mr.DeclaringType.FullName != "Unity.Entities.EntityManager" ||
            mr is not GenericInstanceMethod gim ||
            gim.GenericArguments.Count != 1)
            return false;

        var typeArg = gim.GenericArguments[0];
        return typeArg.Resolve()?.FullName == componentFullName ||
               typeArg.FullName == componentFullName;
    }

    private static bool IsFieldOnEntityManagerGetComponentDataLocal(
        MethodDefinition method,
        Instruction fieldAccess,
        string sourceComponentFullName)
    {
        if (method?.HasBody != true)
            return false;

        var instanceLoad = FindFieldInstanceLoad(fieldAccess);
        var sourceLocal = GetLocalFromInstruction(instanceLoad, method.Body);
        if (sourceLocal == null || sourceLocal.VariableType.FullName != sourceComponentFullName)
            return false;

        Instruction producer = null;
        foreach (var instr in method.Body.Instructions)
        {
            if (!IsStoreLocalCode(instr.OpCode.Code) ||
                GetLocalFromInstruction(instr, method.Body) != sourceLocal)
                continue;

            if (!IsEntityManagerGetComponentDataCall(instr.Previous, sourceComponentFullName))
                return false;

            if (producer != null && producer != instr.Previous)
                return false;

            producer = instr.Previous;
        }

        return producer != null;
    }

    private static bool IsFieldOnComponentLookupGetItemLocal(
        MethodDefinition method,
        Instruction fieldAccess,
        string sourceComponentFullName)
    {
        if (method?.HasBody != true)
            return false;

        var instanceLoad = FindFieldInstanceLoad(fieldAccess);
        var sourceLocal = GetLocalFromInstruction(instanceLoad, method.Body);
        if (sourceLocal == null || sourceLocal.VariableType.FullName != sourceComponentFullName)
            return false;

        Instruction producer = null;
        foreach (var instr in method.Body.Instructions)
        {
            if (!IsStoreLocalCode(instr.OpCode.Code) ||
                GetLocalFromInstruction(instr, method.Body) != sourceLocal)
                continue;

            var candidate = instr.Previous;
            if (!IsComponentLookupGetItemCall(candidate, sourceComponentFullName))
                return false;

            if (producer != null && producer != candidate)
                return false;

            producer = candidate;
        }

        return producer != null;
    }

    private static bool IsComponentLookupGetItemCall(Instruction instr, string sourceComponentFullName)
    {
        if ((instr?.OpCode.Code != Code.Call && instr?.OpCode.Code != Code.Callvirt) ||
            instr.Operand is not MethodReference mr)
            return false;

        if (mr.Name != "get_Item" ||
            mr.DeclaringType is not GenericInstanceType git ||
            !git.ElementType.FullName.StartsWith("Unity.Entities.ComponentLookup", StringComparison.Ordinal) ||
            git.GenericArguments.Count != 1)
            return false;

        var typeArg = git.GenericArguments[0];
        return typeArg.Resolve()?.FullName == sourceComponentFullName ||
               typeArg.FullName == sourceComponentFullName;
    }

    private static bool IsFieldOnTryGetComponentOutLocal(
        MethodDefinition method,
        Instruction fieldAccess,
        string sourceComponentFullName)
    {
        if (method?.HasBody != true)
            return false;

        var instanceLoad = FindFieldInstanceLoad(fieldAccess);
        if (instanceLoad == null)
            return false;

        var sourceLocal = GetLocalFromInstruction(instanceLoad, method.Body);
        if (sourceLocal == null || sourceLocal.VariableType.FullName != sourceComponentFullName)
            return false;

        int budget = 40;
        for (var scan = fieldAccess.Previous; scan != null && budget-- > 0; scan = scan.Previous)
        {
            if ((scan.OpCode.Code != Code.Call && scan.OpCode.Code != Code.Callvirt) ||
                scan.Operand is not MethodReference mr)
                continue;

            if (!IsTryGetComponentCallFor(mr, sourceComponentFullName))
                continue;

            var outParamLoad = scan.Previous;
            if (outParamLoad == null)
                continue;

            var outLocal = GetLocalFromInstruction(outParamLoad, method.Body);
            if (outLocal == sourceLocal)
                return true;
        }

        return false;
    }

    private static bool IsTryGetComponentCallFor(MethodReference mr, string sourceComponentFullName)
    {
        if (mr == null || mr.Name != "TryGetComponent")
            return false;

        if (mr is GenericInstanceMethod gim && gim.GenericArguments.Count == 1)
        {
            var typeArg = gim.GenericArguments[0];
            return typeArg.Resolve()?.FullName == sourceComponentFullName ||
                   typeArg.FullName == sourceComponentFullName;
        }

        if (mr.DeclaringType is GenericInstanceType git &&
            git.ElementType.FullName.StartsWith("Unity.Entities.ComponentLookup", StringComparison.Ordinal) &&
            git.GenericArguments.Count == 1)
        {
            var typeArg = git.GenericArguments[0];
            return typeArg.Resolve()?.FullName == sourceComponentFullName ||
                   typeArg.FullName == sourceComponentFullName;
        }

        return false;
    }

    internal static HashSet<string> ExtractGroupComponentsFromTupleReturn(TypeReference typeRef, HashSet<string> originalGroup)
    {
        var found = new HashSet<string>();
        CollectTupleComponentsRecursive(typeRef, originalGroup, found);
        return found;
    }

    private static void CollectTupleComponentsRecursive(TypeReference typeRef, HashSet<string> originalGroup, HashSet<string> sink)
    {
        if (typeRef == null)
            return;

        if (originalGroup.Contains(typeRef.FullName))
            sink.Add(typeRef.FullName);

        if (typeRef is GenericInstanceType git)
        {
            if (git.ElementType != null && git.ElementType.FullName.StartsWith("System.ValueTuple"))
            {
                foreach (var ga in git.GenericArguments)
                    CollectTupleComponentsRecursive(ga, originalGroup, sink);
                return;
            }

            foreach (var ga in git.GenericArguments)
                CollectTupleComponentsRecursive(ga, originalGroup, sink);
            return;
        }

        if (typeRef is TypeSpecification ts && ts.ElementType != null)
            CollectTupleComponentsRecursive(ts.ElementType, originalGroup, sink);
    }

    private static bool IsStoreLocalCode(Code code)
    {
        return code == Code.Stloc || code == Code.Stloc_S ||
               code == Code.Stloc_0 || code == Code.Stloc_1 ||
               code == Code.Stloc_2 || code == Code.Stloc_3;
    }
}
