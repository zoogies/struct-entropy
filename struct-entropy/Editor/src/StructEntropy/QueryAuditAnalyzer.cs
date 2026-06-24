using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

public partial class StructEntropyCandidates
{
    private static void AnalyzeQueryAudit(
        List<CandidateGroup> phase4Candidates,
        List<MethodDefinition> allMethods,
        Dictionary<string, ComponentTypeInfo> possibleCandidateComponents,
        out List<CandidateAssessment> eligibleNow,
        out List<CandidateAssessment> eligibleWithRewrite)
    {
        // PruneUnsafeTupleQueryExposure was previously called here but was too coarse:
        // it removed TARGET components from groups when they appeared in partial IFE tuples,
        // eliminating entire groups. The correct check is directional — a SOURCE component
        // appearing in a partial tuple without the TARGET is the unsafe case, not the reverse.
        // Per-site blocking in ValidityAnalyzer (missing_peer_for_direct_access) and entity-shape
        // semantic checks (entity_shape_source_without_target) handle this correctly per-relocation.
        var phase4bCandidates = phase4Candidates;

        eligibleNow = new List<CandidateAssessment>();
        eligibleWithRewrite = new List<CandidateAssessment>();
        var phase5Seen = new HashSet<string>();

        foreach (var group in phase4bCandidates)
        {
            var originalGroup = new HashSet<string>(group.Components.Select(c => c.Type.FullName));
            var rewriteSites = originalGroup.ToDictionary(c => c, _ => new HashSet<string>());

            foreach (var method in allMethods)
            {
                foreach (var instr in method.Body.Instructions)
                {
                    if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
                        instr.Operand is not MethodReference mr)
                        continue;

                    if (!IsQueryAuditRelevantCall(mr))
                        continue;

                    var referencedGroupComponents = ExtractReferencedComponentTypesFromCall(mr, originalGroup);
                    if (referencedGroupComponents.Count == 0)
                        continue;

                    if (originalGroup.IsSubsetOf(referencedGroupComponents))
                        continue;

                    var siteLabel = $"{method.FullName}::{mr.DeclaringType.FullName}.{mr.Name}";
                    foreach (var component in referencedGroupComponents)
                    {
                        rewriteSites[component].Add(siteLabel);
                    }
                }
            }

            var componentsNeedingRewrite = rewriteSites
                .Where(kvp => kvp.Value.Count > 0)
                .Select(kvp => kvp.Key)
                .OrderBy(x => x)
                .ToList();

            var dedupeKey = $"{group.Source}:{BuildGroupKey(originalGroup)}";
            if (!phase5Seen.Add(dedupeKey))
                continue;

            var assessment = new CandidateAssessment(
                group,
                componentsNeedingRewrite.Count > 0,
                rewriteSites);

            if (assessment.RequiresQueryRewrite)
                eligibleWithRewrite.Add(assessment);
            else
                eligibleNow.Add(assessment);
        }
    }

    private static List<CandidateGroup> PruneUnsafeTupleQueryExposure(
        AssemblyDefinition assembly,
        List<CandidateGroup> phase4Candidates,
        Dictionary<string, ComponentTypeInfo> possibleCandidateComponents)
    {
        var phase4bCandidates = new List<CandidateGroup>();
        var phase4bSeen = new HashSet<string>();

        foreach (var group in phase4Candidates)
        {
            var originalGroup = new HashSet<string>(group.Components.Select(c => c.Type.FullName));
            var survivingComponents = new HashSet<string>(originalGroup);
            var unsafeTupleSites = FindUnsafeTupleQueryExposure(assembly, originalGroup);

            var removedByTupleExposure = unsafeTupleSites
                .Where(kvp => kvp.Value.Count > 0)
                .Select(kvp => kvp.Key)
                .OrderBy(x => x)
                .ToList();

            foreach (var componentName in removedByTupleExposure)
                survivingComponents.Remove(componentName);

            if (removedByTupleExposure.Count > 0)
            {
                StructEntropyLogger.Log($"  Phase 4B pruning details for source {group.Source}, group {{{BuildGroupKey(originalGroup)}}}:");
                foreach (var componentName in removedByTupleExposure)
                {
                    var shortSites = string.Join(", ", unsafeTupleSites[componentName].OrderBy(x => x));
                    StructEntropyLogger.Log($"    Removed (tuple query lacks peers): {componentName} via {shortSites}");
                }
            }

            if (survivingComponents.Count < 2)
                continue;

            var dedupeKey = $"{group.Source}:{BuildGroupKey(survivingComponents)}";
            if (!phase4bSeen.Add(dedupeKey))
                continue;

            var typedComponents = new HashSet<ComponentTypeInfo>(
                survivingComponents
                    .Where(n => possibleCandidateComponents.ContainsKey(n))
                    .Select(n => possibleCandidateComponents[n]));

            if (typedComponents.Count < 2)
                continue;

            phase4bCandidates.Add(new CandidateGroup(typedComponents, group.Source));
        }

        return phase4bCandidates;
    }

    private static bool IsQueryAuditRelevantCall(MethodReference mr)
    {
        if (mr == null || mr.DeclaringType == null)
            return false;

        var declaring = mr.DeclaringType.FullName;
        if (!declaring.StartsWith("Unity.Entities."))
            return false;

        var name = mr.Name;

        if (name == "Query" || name == "QueryBuilder")
            return true;

        if (name.StartsWith("With"))
            return true;

        if (name == "RequireForUpdate")
            return true;

        if (name.Contains("GetComponent") ||
            name.Contains("SetComponent") ||
            name.Contains("HasComponent"))
            return true;

        return false;
    }

    private static void CollectTypeFullNamesRecursive(TypeReference tr, HashSet<string> sink)
    {
        if (tr == null)
            return;

        sink.Add(tr.FullName);

        if (tr is GenericInstanceType git)
        {
            if (git.ElementType != null)
                CollectTypeFullNamesRecursive(git.ElementType, sink);

            foreach (var ga in git.GenericArguments)
                CollectTypeFullNamesRecursive(ga, sink);
            return;
        }

        if (tr is TypeSpecification ts && ts.ElementType != null)
            CollectTypeFullNamesRecursive(ts.ElementType, sink);
    }

    private static HashSet<string> ExtractReferencedComponentTypesFromCall(MethodReference mr, HashSet<string> originalGroup)
    {
        var referencedTypeNames = new HashSet<string>();

        if (mr is GenericInstanceMethod gim)
        {
            foreach (var ga in gim.GenericArguments)
                CollectTypeFullNamesRecursive(ga, referencedTypeNames);
        }

        if (mr.DeclaringType is GenericInstanceType dgit)
        {
            foreach (var ga in dgit.GenericArguments)
                CollectTypeFullNamesRecursive(ga, referencedTypeNames);
        }

        return new HashSet<string>(referencedTypeNames.Where(originalGroup.Contains));
    }

    private static Dictionary<string, HashSet<string>> FindUnsafeTupleQueryExposure(
        AssemblyDefinition assembly,
        HashSet<string> originalGroup)
    {
        var unsafeSitesByComponent = originalGroup.ToDictionary(c => c, _ => new HashSet<string>());

        foreach (var module in assembly.Modules)
        {
            foreach (var type in EnumerateTypesRecursive(module.Types))
            {
                if (!type.Name.StartsWith("IFE_", System.StringComparison.Ordinal))
                    continue;

                var enumeratorType = type.NestedTypes.FirstOrDefault(t => t.Name == "Enumerator");
                var currentMethod = enumeratorType?.Methods.FirstOrDefault(m => m.Name == "get_Current");
                if (currentMethod == null)
                    continue;

                if (currentMethod.ReturnType is not GenericInstanceType currentGit ||
                    !currentGit.ElementType.FullName.StartsWith("System.ValueTuple"))
                    continue;

                var tupleComponents = AccessPatternClassifier.ExtractGroupComponentsFromTupleReturn(currentMethod.ReturnType, originalGroup);
                if (tupleComponents.Count == 0 || tupleComponents.SetEquals(originalGroup))
                    continue;

                var siteLabel = $"{type.FullName}/Enumerator::get_Current";
                foreach (var component in tupleComponents)
                    unsafeSitesByComponent[component].Add(siteLabel);
            }
        }

        return unsafeSitesByComponent;
    }
}
