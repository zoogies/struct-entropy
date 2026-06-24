using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

public partial class StructEntropyCandidates
{
    private static List<CandidateGroup> PruneStructuralMutationCandidates(
        List<CandidateGroup> phase3Candidates,
        List<MethodDefinition> allMethods,
        Dictionary<string, ComponentTypeInfo> possibleCandidateComponents)
    {
        var phase4Candidates = new List<CandidateGroup>();
        var phase4Seen = new HashSet<string>();

        foreach (var group in phase3Candidates)
        {
            var originalGroup               = new HashSet<string>(group.Components.Select(c => c.Type.FullName));
            var survivingComponents         = new HashSet<string>(originalGroup);
            var hasSafeEvidence             = originalGroup.ToDictionary(c => c, _ => false);
            var hasUnknownEvidence          = originalGroup.ToDictionary(c => c, _ => false);
            var hasUnsafeEvidence           = originalGroup.ToDictionary(c => c, _ => false);
            var hasPeerSafeEvidence         = originalGroup.ToDictionary(c => c, _ => false);
            var hasAsymmetricSafeEvidence   = originalGroup.ToDictionary(c => c, _ => false);
            var safeAddBucketsAll           = new Dictionary<string, HashSet<string>>();
            var safeAddBucketsCreateOnly    = new Dictionary<string, HashSet<string>>();
            var safeRemoveBuckets           = new Dictionary<string, HashSet<string>>();

            foreach (var method in allMethods)
            {
                var entityLocalOrigins = new Dictionary<int, EntityOriginState>();
                foreach (var local in method.Body.Variables)
                {
                    if (IsEntityType(local.VariableType))
                        entityLocalOrigins[local.Index] = EntityOriginState.Unknown;
                }

                foreach (var instr in method.Body.Instructions)
                {
                    if (IsStoreLocalCode(instr.OpCode.Code) &&
                        TryGetLocalIndex(instr, out int storedSlot) &&
                        entityLocalOrigins.ContainsKey(storedSlot))
                    {
                        var prev = instr.Previous;
                        if (prev != null &&
                            (prev.OpCode.Code == Code.Call || prev.OpCode.Code == Code.Callvirt) &&
                            prev.Operand is MethodReference sourceMr)
                        {
                            if (IsInitializationCall(sourceMr) && sourceMr.ReturnType.FullName == "Unity.Entities.Entity")
                            {
                                entityLocalOrigins[storedSlot] =
                                    sourceMr.Name == "Instantiate"
                                        ? EntityOriginState.CreatedByInstantiate
                                        : EntityOriginState.CreatedByCreateEntity;
                            }
                            else if (sourceMr.ReturnType.FullName == "Unity.Entities.Entity")
                                entityLocalOrigins[storedSlot] = EntityOriginState.NonCreatedInMethod;
                            else
                                entityLocalOrigins[storedSlot] = EntityOriginState.Unknown;
                        }
                        else if (prev != null && IsEntityParameterLoad(prev, method))
                        {
                            entityLocalOrigins[storedSlot] = EntityOriginState.NonCreatedInMethod;
                        }
                        else if (prev != null &&
                                 TryGetLocalIndex(prev, out int sourceSlot) &&
                                 entityLocalOrigins.TryGetValue(sourceSlot, out var sourceOrigin))
                        {
                            entityLocalOrigins[storedSlot] = sourceOrigin;
                        }
                        else
                        {
                            entityLocalOrigins[storedSlot] = EntityOriginState.Unknown;
                        }
                    }

                    if (TryGetGenericStructuralMutation(instr, "AddComponent", out var addedType) &&
                        originalGroup.Contains(addedType))
                    {
                        var evidence = ClassifyMutationEvidence(instr, method, entityLocalOrigins, out int slot);
                        if (evidence == MutationEvidence.Safe) hasSafeEvidence[addedType] = true;
                        else if (evidence == MutationEvidence.Unsafe) hasUnsafeEvidence[addedType] = true;
                        else hasUnknownEvidence[addedType] = true;

                        if (evidence == MutationEvidence.Safe)
                        {
                            var bucketKey = $"{method.FullName}|slot:{slot}";
                            if (!safeAddBucketsAll.TryGetValue(bucketKey, out var allBucket))
                                safeAddBucketsAll[bucketKey] = allBucket = new HashSet<string>();
                            allBucket.Add(addedType);

                            if (slot >= 0 &&
                                entityLocalOrigins.TryGetValue(slot, out var originState) &&
                                originState == EntityOriginState.CreatedByCreateEntity)
                            {
                                if (!safeAddBucketsCreateOnly.TryGetValue(bucketKey, out var createBucket))
                                    safeAddBucketsCreateOnly[bucketKey] = createBucket = new HashSet<string>();
                                createBucket.Add(addedType);
                            }
                        }
                    }

                    if (TryGetGenericStructuralMutation(instr, "RemoveComponent", out var removedType) &&
                        originalGroup.Contains(removedType))
                    {
                        var evidence = ClassifyMutationEvidence(instr, method, entityLocalOrigins, out int slot);
                        if (evidence == MutationEvidence.Safe) hasSafeEvidence[removedType] = true;
                        else if (evidence == MutationEvidence.Unsafe) hasUnsafeEvidence[removedType] = true;
                        else hasUnknownEvidence[removedType] = true;

                        if (evidence == MutationEvidence.Safe)
                        {
                            var bucketKey = $"{method.FullName}|slot:{slot}";
                            if (!safeRemoveBuckets.TryGetValue(bucketKey, out var bucket))
                                safeRemoveBuckets[bucketKey] = bucket = new HashSet<string>();
                            bucket.Add(removedType);
                        }
                    }
                }
            }

            foreach (var bucket in safeAddBucketsAll.Values.Concat(safeRemoveBuckets.Values))
            {
                if (bucket.Count > 1)
                {
                    foreach (var component in bucket)
                    {
                        if (originalGroup.Contains(component))
                            hasPeerSafeEvidence[component] = true;
                    }
                }
            }

            foreach (var bucket in safeAddBucketsCreateOnly.Values.Concat(safeRemoveBuckets.Values))
            {
                if (bucket.Count > 0 && bucket.Count < originalGroup.Count)
                {
                    foreach (var component in bucket)
                    {
                        if (originalGroup.Contains(component))
                            hasAsymmetricSafeEvidence[component] = true;
                    }
                }
            }

            var removedByUnsafe = new List<string>();
            var removedByAsymmetry = new List<string>();
            var removedByUnknown = new List<string>();
            foreach (var componentName in originalGroup)
            {
                if (hasUnsafeEvidence[componentName])
                {
                    survivingComponents.Remove(componentName);
                    removedByUnsafe.Add(componentName);
                    continue;
                }

                if (hasAsymmetricSafeEvidence[componentName])
                {
                    survivingComponents.Remove(componentName);
                    removedByAsymmetry.Add(componentName);
                    continue;
                }

                if (hasUnknownEvidence[componentName] &&
                    !hasSafeEvidence[componentName] &&
                    !hasPeerSafeEvidence[componentName])
                {
                    survivingComponents.Remove(componentName);
                    removedByUnknown.Add(componentName);
                }
            }

            if (removedByUnsafe.Count > 0 || removedByAsymmetry.Count > 0 || removedByUnknown.Count > 0)
            {
                StructEntropyLogger.Log($"  Phase 4 pruning details for source {group.Source}, group {{{BuildGroupKey(originalGroup)}}}:");
                if (removedByUnsafe.Count > 0)
                    StructEntropyLogger.Log($"    Removed (unsafe mutation): {string.Join(", ", removedByUnsafe.OrderBy(x => x))}");
                if (removedByAsymmetry.Count > 0)
                    StructEntropyLogger.Log($"    Removed (asymmetric safe mutation): {string.Join(", ", removedByAsymmetry.OrderBy(x => x))}");
                if (removedByUnknown.Count > 0)
                    StructEntropyLogger.Log($"    Removed (unknown-only evidence): {string.Join(", ", removedByUnknown.OrderBy(x => x))}");
            }

            if (survivingComponents.Count < 2)
                continue;

            var dedupeKey = $"{group.Source}:{BuildGroupKey(survivingComponents)}";
            if (!phase4Seen.Add(dedupeKey))
                continue;

            var typedComponents = new HashSet<ComponentTypeInfo>(
                survivingComponents
                    .Where(n => possibleCandidateComponents.ContainsKey(n))
                    .Select(n => possibleCandidateComponents[n]));

            if (typedComponents.Count < 2)
                continue;

            phase4Candidates.Add(new CandidateGroup(typedComponents, group.Source));
        }

        return phase4Candidates;
    }
}
