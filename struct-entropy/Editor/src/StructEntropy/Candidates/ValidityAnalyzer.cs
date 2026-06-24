using System;
using System.Linq;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static StructEntropy.Rewriter.TypeResolutionHelpers;

internal static class ValidityAnalyzer
{
    internal static List<RelocationOpportunity> BuildRelocationOpportunities(
        IEnumerable<CandidateGroup> candidateGroups,
        IEnumerable<EntityShapeFact> entityShapes,
        IEnumerable<FieldAccessSite> fieldAccessSites,
        IEnumerable<MethodDefinition> allMethods)
    {
        var opportunities = new List<RelocationOpportunity>();
        var seen = new HashSet<string>();

        foreach (var group in candidateGroups)
        {
            foreach (var sourceComponent in group.Components)
            {
                var sourceFields = sourceComponent.Type.Fields
                    .Where(f => !f.IsStatic && !f.IsRuntimeSpecialName && !f.IsSpecialName)
                    .ToList();

                foreach (var field in sourceFields)
                {
                    var sourceSites = fieldAccessSites
                        .Where(site => site.FieldFullName == field.FullName)
                        .ToList();

                    foreach (var targetComponent in group.Components)
                    {
                        if (targetComponent.Type.FullName == sourceComponent.Type.FullName)
                            continue;

                        var key = $"{field.FullName}->{targetComponent.Type.FullName}";
                        if (!seen.Add(key))
                            continue;

                        bool semanticAllowed = true;
                        bool rewriteSupportedNow = true;
                        var semanticReasons = new List<string>();
                        var rewriteBlockers = new List<string>();
                        var requiredCapabilities = new HashSet<RewriteCapability>(
                            sourceSites.SelectMany(site => site.RequiredCapabilities));
                        AddTargetWholeStructWriteObligations(
                            allMethods,
                            targetComponent.Type.FullName,
                            requiredCapabilities,
                            rewriteBlockers,
                            ref rewriteSupportedNow);

                        if (ExistsShapeWithSourceWithoutTarget(entityShapes, sourceComponent.Type.FullName, targetComponent.Type.FullName))
                        {
                            semanticAllowed = false;
                            semanticReasons.Add("entity_shape_source_without_target");
                        }

                        if (IsEmptyTagComponent(targetComponent.Type))
                        {
                            semanticAllowed = false;
                            semanticReasons.Add("empty_tag_target_component");
                        }

                        if (HasMaterialPropertyAttribute(sourceComponent.Type, field))
                        {
                            semanticAllowed = false;
                            semanticReasons.Add("source_material_property_component");
                        }

                        if (HasMaterialPropertyAttribute(targetComponent.Type, null))
                        {
                            semanticAllowed = false;
                            semanticReasons.Add("target_material_property_component");
                        }

                        if (sourceSites.Count == 0)
                        {
                            semanticAllowed = false;
                            semanticReasons.Add("no_observed_field_access_sites");
                        }

                        foreach (var capability in requiredCapabilities)
                        {
                            if (!IsCapabilitySupportedNow(capability))
                            {
                                rewriteSupportedNow = false;
                                rewriteBlockers.Add($"unsupported_capability:{capability}");
                            }
                        }

                        foreach (var site in sourceSites)
                        {
                            if (!IsFieldAccessSiteRewriteSupported(
                                site,
                                targetComponent.Type.FullName,
                                fieldAccessSites,
                                allMethods,
                                out var blocker))
                            {
                                rewriteSupportedNow = false;
                                rewriteBlockers.Add(blocker);
                            }
                        }

                        opportunities.Add(new RelocationOpportunity(
                            sourceComponent.Type.FullName,
                            field.FullName,
                            targetComponent.Type.FullName,
                            semanticAllowed,
                            rewriteSupportedNow,
                            requiredCapabilities,
                            semanticReasons.Distinct().OrderBy(x => x).ToList(),
                            rewriteBlockers.Distinct().OrderBy(x => x).ToList()));
                    }
                }
            }
        }

        return opportunities;
    }

    private static bool ExistsShapeWithSourceWithoutTarget(
        IEnumerable<EntityShapeFact> entityShapes,
        string sourceComponentFullName,
        string targetComponentFullName)
    {
        return entityShapes.Any(shape =>
            shape.ComponentFullNames.Contains(sourceComponentFullName) &&
            !shape.ComponentFullNames.Contains(targetComponentFullName));
    }

    private static bool IsEmptyTagComponent(TypeDefinition type)
    {
        return type != null && type.Fields.All(f => f.IsStatic || f.IsRuntimeSpecialName || f.IsSpecialName);
    }

    private static bool HasMaterialPropertyAttribute(TypeDefinition type, FieldDefinition field)
    {
        return HasMaterialPropertyAttribute(type?.CustomAttributes) ||
               HasMaterialPropertyAttribute(field?.CustomAttributes);
    }

    private static bool HasMaterialPropertyAttribute(IEnumerable<CustomAttribute> attributes)
    {
        if (attributes == null)
            return false;

        return attributes.Any(attr =>
            string.Equals(attr.AttributeType.FullName, "Unity.Rendering.MaterialPropertyAttribute", StringComparison.Ordinal) ||
            string.Equals(attr.AttributeType.Name, "MaterialPropertyAttribute", StringComparison.Ordinal));
    }

    private static bool IsCapabilitySupportedNow(RewriteCapability capability)
    {
        switch (capability)
        {
            case RewriteCapability.DirectFieldRedirect:
            case RewriteCapability.LookupGetItemRead:
            case RewriteCapability.EntityManagerGetComponentDataRedirect:
            case RewriteCapability.IJobEntityPeerInjection:
            case RewriteCapability.IJobEntityByValuePeerMaterialization:
            case RewriteCapability.IfePeerInjection:
            case RewriteCapability.LdfldaPeerRedirect:
            case RewriteCapability.LookupTryGetComponentRedirect:
            case RewriteCapability.LookupSetItemWholeStructWrite:
            case RewriteCapability.EntityManagerWholeStructWriteRedirect:
            case RewriteCapability.EcbWholeStructWriteRedirect:
            case RewriteCapability.TargetWholeStructWritePreserve:
                return true;
            default:
                return false;
        }
    }

    private static bool IsWriteLikeSourceAccess(FieldAccessSite site)
    {
        if (site == null)
            return false;

        return string.Equals(site.OpCodeName, "Stfld", StringComparison.Ordinal) ||
               string.Equals(site.OpCodeName, "Ldflda", StringComparison.Ordinal) ||
               site.Kind == AccessKind.AddressTakenField ||
               site.Kind == AccessKind.WholeStructInitStore ||
               site.Kind == AccessKind.EntityManagerWholeStructWrite ||
               site.Kind == AccessKind.EcbWholeStructWrite;
    }

    private static bool DirectRewriteRequiresPeerInMethod(FieldAccessSite site)
    {
        if (site == null || site.ExplicitEntityIndirection)
            return false;

        switch (site.Kind)
        {
            case AccessKind.DirectFieldUse:
            case AccessKind.AddressTakenField:
            case AccessKind.IJobEntityParameter:
            case AccessKind.IfeSingleComponent:
            case AccessKind.IfeTupleComponent:
            case AccessKind.UncheckedRefRwComponent:
                return true;
            default:
                return false;
        }
    }

    private static bool HasPeerSupportInMethod(
        FieldAccessSite site,
        string targetComponentFullName,
        IEnumerable<FieldAccessSite> allSites)
    {
        return site.RequiredEntityComponents.Contains(targetComponentFullName) ||
               site.AvailableUncheckedRefRwComponents.Contains(targetComponentFullName) ||
               site.AvailableUncheckedRefRoComponents.Contains(targetComponentFullName) ||
               MethodHasTargetComponentAccess(site.MethodFullName, targetComponentFullName, allSites);
    }

    private static bool HasWritableTargetComponentAccess(
        FieldAccessSite site,
        string targetComponentFullName,
        IEnumerable<FieldAccessSite> allSites)
    {
        return site.AvailableUncheckedRefRwComponents.Contains(targetComponentFullName) ||
               MethodHasWritableTargetAccess(site.MethodFullName, targetComponentFullName, allSites);
    }

    private static bool MethodHasTargetComponentAccess(
        string methodFullName,
        string targetComponentFullName,
        IEnumerable<FieldAccessSite> allSites)
    {
        return allSites.Any(site =>
            string.Equals(site.MethodFullName, methodFullName, StringComparison.Ordinal) &&
            string.Equals(site.SourceComponentFullName, targetComponentFullName, StringComparison.Ordinal));
    }

    private static bool MethodHasWritableTargetAccess(
        string methodFullName,
        string targetComponentFullName,
        IEnumerable<FieldAccessSite> allSites)
    {
        return allSites.Any(site =>
            string.Equals(site.MethodFullName, methodFullName, StringComparison.Ordinal) &&
            string.Equals(site.SourceComponentFullName, targetComponentFullName, StringComparison.Ordinal) &&
            (site.AvailableUncheckedRefRwComponents.Contains(targetComponentFullName) || IsWriteLikeSourceAccess(site)));
    }

    private static bool IsFieldAccessSiteRewriteSupported(
        FieldAccessSite site,
        string targetComponentFullName,
        IEnumerable<FieldAccessSite> allSites,
        IEnumerable<MethodDefinition> allMethods,
        out string blocker)
    {
        blocker = null;

        if (!DirectRewriteRequiresPeerInMethod(site))
        {
            if (site.Kind == AccessKind.EcbWholeStructWrite &&
                !MethodHasEcbValueAddComponentLocal(allMethods, site.MethodFullName, targetComponentFullName))
            {
                blocker = $"missing_peer_for_ecb_whole_struct_write:{site.Kind}";
                return false;
            }

            return true;
        }

        if (!HasPeerSupportInMethod(site, targetComponentFullName, allSites))
        {
            // Read-only SystemAPI.Query<RefRO<T>>() sites are supported either
            // through ComponentLookup<Target>[entity] when WithEntityAccess() is
            // present, or through a generated read-only peer accessor otherwise.
            if (!(site.Kind == AccessKind.IfeSingleComponent &&
                  !IsWriteLikeSourceAccess(site)))
            {
                blocker = $"missing_peer_for_direct_access:{site.Kind}";
                return false;
            }
        }

        // IJobEntity sites: the rewriter upgrades parameter access mode (in→ref),
        // so writable-peer pre-check is not needed — skip it.
        if (!site.RequiredCapabilities.Contains(RewriteCapability.IJobEntityPeerInjection) &&
            (IsWriteLikeSourceAccess(site) ||
             site.RequiredCapabilities.Contains(RewriteCapability.LdfldaPeerRedirect)) &&
            !HasWritableTargetComponentAccess(site, targetComponentFullName, allSites))
        {
            blocker = $"missing_writable_peer_for_direct_access:{site.Kind}";
            return false;
        }

        return true;
    }

    private static bool MethodHasEcbValueAddComponentLocal(
        IEnumerable<MethodDefinition> allMethods,
        string methodFullName,
        string targetComponentFullName)
    {
        var method = allMethods.FirstOrDefault(m =>
            m?.HasBody == true &&
            string.Equals(m.FullName, methodFullName, StringComparison.Ordinal));
        if (method == null)
            return false;

        foreach (var instr in method.Body.Instructions)
        {
            if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
                instr.Operand is not MethodReference mr)
                continue;

            if (mr.Name != "AddComponent" ||
                mr.DeclaringType == null ||
                (mr.DeclaringType.FullName != "Unity.Entities.EntityCommandBuffer" &&
                 mr.DeclaringType.FullName != "Unity.Entities.EntityCommandBuffer/ParallelWriter") ||
                mr is not GenericInstanceMethod gim ||
                gim.GenericArguments.Count != 1 ||
                !TypeRefFullNameEquals(gim.GenericArguments[0], targetComponentFullName))
                continue;

            var valueLoad = instr.Previous;
            var valueLocal = AccessPatternClassifier.GetLocalFromInstruction(valueLoad, method.Body);
            if (valueLocal != null && TypeRefFullNameEquals(valueLocal.VariableType, targetComponentFullName))
                return true;
        }

        return false;
    }

    private static MethodDefinition SafeResolve(MethodReference method)
    {
        try { return method?.Resolve(); }
        catch { return null; }
    }

    private static void AddTargetWholeStructWriteObligations(
        IEnumerable<MethodDefinition> allMethods,
        string targetComponentFullName,
        HashSet<RewriteCapability> requiredCapabilities,
        List<string> rewriteBlockers,
        ref bool rewriteSupportedNow)
    {
        foreach (var method in allMethods.Where(m => m?.HasBody == true))
        {
            foreach (var instr in method.Body.Instructions)
            {
                if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
                    instr.Operand is not MethodReference mr)
                    continue;

                if (mr.Name == "set_Item" &&
                    mr.DeclaringType is GenericInstanceType lookupGit &&
                    lookupGit.ElementType.Name.StartsWith("ComponentLookup", StringComparison.Ordinal) &&
                    lookupGit.GenericArguments.Count == 1 &&
                    TypeRefFullNameEquals(lookupGit.GenericArguments[0], targetComponentFullName))
                {
                    requiredCapabilities.Add(RewriteCapability.LookupSetItemWholeStructWrite);
                    continue;
                }

                if (mr is not GenericInstanceMethod gim ||
                    gim.GenericArguments.Count != 1 ||
                    !TypeRefFullNameEquals(gim.GenericArguments[0], targetComponentFullName))
                    continue;

                if (mr.Name == "SetComponentData" &&
                    mr.DeclaringType.FullName == "Unity.Entities.EntityManager")
                {
                    var valueLocal = AccessPatternClassifier.GetLocalFromInstruction(instr.Previous, method.Body);
                    if (valueLocal != null && TypeRefFullNameEquals(valueLocal.VariableType, targetComponentFullName))
                    {
                        requiredCapabilities.Add(RewriteCapability.TargetWholeStructWritePreserve);
                    }
                    else
                    {
                        rewriteSupportedNow = false;
                        rewriteBlockers.Add($"unsupported_target_whole_struct_write:entity_manager:{method.FullName}");
                    }
                    continue;
                }

                if (mr.Name == "SetComponent" &&
                    (mr.DeclaringType.FullName == "Unity.Entities.EntityCommandBuffer" ||
                     mr.DeclaringType.FullName == "Unity.Entities.EntityCommandBuffer/ParallelWriter"))
                {
                    if (CanSupportEcbTargetWholeStructWrite(method, instr, targetComponentFullName, allMethods))
                    {
                        requiredCapabilities.Add(RewriteCapability.TargetWholeStructWritePreserve);
                    }
                    else
                    {
                        requiredCapabilities.Add(RewriteCapability.EcbWholeStructWriteRedirect);
                        rewriteSupportedNow = false;
                        rewriteBlockers.Add($"unsupported_target_whole_struct_write:ecb:{method.FullName}");
                    }
                    continue;
                }
            }
        }
    }

    private static bool CanSupportEcbTargetWholeStructWrite(
        MethodDefinition method,
        Instruction setComponentCall,
        string targetComponentFullName,
        IEnumerable<MethodDefinition> allMethods)
    {
        if (method?.HasBody != true || setComponentCall == null)
            return false;

        var valueLocal = AccessPatternClassifier.GetLocalFromInstruction(setComponentCall.Previous, method.Body);
        if (valueLocal == null || !TypeRefFullNameEquals(valueLocal.VariableType, targetComponentFullName))
            return false;

        return HasByRefParameterOfType(method, targetComponentFullName);
    }

    private static bool HasByRefParameterOfType(MethodDefinition method, string typeFullName)
    {
        return method.Parameters.Any(p =>
            p.ParameterType is ByReferenceType br &&
            string.Equals(br.ElementType.FullName, typeFullName, StringComparison.Ordinal));
    }

    private static bool IsUserIjeExecuteCandidate(MethodDefinition method)
    {
        if (method?.HasBody != true ||
            method.Name != "Execute" ||
            method.Parameters.Count == 0 ||
            method.Parameters[0].ParameterType.Name.StartsWith("ArchetypeChunk", StringComparison.Ordinal))
            return false;

        return true;
    }
}
