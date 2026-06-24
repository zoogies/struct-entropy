using System;
using System.Linq;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

public partial class StructEntropyCandidates
{
    private static List<EntityShapeFact> BuildInitializationShapeFacts(
        AssemblyDefinition assembly,
        Dictionary<string, ComponentTypeInfo> knownComponents)
    {
        var facts = new List<EntityShapeFact>();
        var seen = new HashSet<string>();

        foreach (var module in assembly.Modules)
        {
            foreach (var type in EnumerateTypesRecursive(module.Types))
            {
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    var entityLocalOrigins = new Dictionary<int, EntityOriginState>();
                    foreach (var local in method.Body.Variables)
                    {
                        if (IsEntityType(local.VariableType))
                            entityLocalOrigins[local.Index] = EntityOriginState.Unknown;
                    }

                    var createBuckets = new Dictionary<int, HashSet<string>>();

                    foreach (var instr in method.Body.Instructions)
                    {
                        if ((instr.OpCode.Code == Code.Call || instr.OpCode.Code == Code.Callvirt) &&
                            instr.Operand is MethodReference mr &&
                            mr.DeclaringType.FullName == "Unity.Entities.EntityManager" &&
                            mr.Name == "CreateArchetype")
                        {
                            var components = ExtractCreateArchetypeComponents(instr, knownComponents);
                            if (components.Count > 0)
                                AddShapeFact(facts, seen, GroupSource.CreateArchetype, components, $"{method.FullName}:CreateArchetype");
                        }

                        if (IsStoreLocalCode(instr.OpCode.Code) &&
                            TryGetLocalIndex(instr, out int storedSlot) &&
                            entityLocalOrigins.ContainsKey(storedSlot))
                        {
                            var prev = instr.Previous;
                            if (prev != null &&
                                (prev.OpCode.Code == Code.Call || prev.OpCode.Code == Code.Callvirt) &&
                                prev.Operand is MethodReference sourceMr)
                            {
                                if (sourceMr.ReturnType.FullName == "Unity.Entities.Entity" &&
                                    ((sourceMr.DeclaringType.FullName == "Unity.Entities.EntityManager" &&
                                      sourceMr.Name == "CreateEntity") ||
                                     (IsEntityCommandBufferType(sourceMr.DeclaringType.FullName) &&
                                      sourceMr.Name == "CreateEntity")))
                                {
                                    entityLocalOrigins[storedSlot] = EntityOriginState.CreatedByCreateEntity;
                                    if (!createBuckets.ContainsKey(storedSlot))
                                        createBuckets[storedSlot] = new HashSet<string>();
                                }
                                else if (sourceMr.ReturnType.FullName == "Unity.Entities.Entity" &&
                                         ((sourceMr.DeclaringType.FullName == "Unity.Entities.EntityManager" &&
                                           sourceMr.Name == "Instantiate") ||
                                          (IsEntityCommandBufferType(sourceMr.DeclaringType.FullName) &&
                                           sourceMr.Name == "Instantiate")))
                                {
                                    entityLocalOrigins[storedSlot] = EntityOriginState.CreatedByInstantiate;
                                    if (!createBuckets.ContainsKey(storedSlot))
                                        createBuckets[storedSlot] = new HashSet<string>();
                                }
                                else if (sourceMr.ReturnType.FullName == "Unity.Entities.Entity")
                                {
                                    entityLocalOrigins[storedSlot] = EntityOriginState.NonCreatedInMethod;
                                }
                                else
                                {
                                    entityLocalOrigins[storedSlot] = EntityOriginState.Unknown;
                                }
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

                        if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
                            instr.Operand is not MethodReference addMr)
                            continue;

                        string addedType = null;
                        bool isAdd =
                            TryGetValueAddComponentType(addMr, "Unity.Entities.EntityManager", knownComponents, out addedType) ||
                            IsEcbAddComponentCall(instr, knownComponents, out addedType);

                        if (!isAdd)
                            continue;

                        var evidence = ClassifyMutationEvidence(instr, method, entityLocalOrigins, out int slot);
                        if (evidence != MutationEvidence.Safe || slot < 0)
                            continue;

                        if (!entityLocalOrigins.TryGetValue(slot, out var origin) ||
                            (origin != EntityOriginState.CreatedByCreateEntity &&
                             origin != EntityOriginState.CreatedByInstantiate))
                            continue;

                        if (!createBuckets.TryGetValue(slot, out var bucket))
                            createBuckets[slot] = bucket = new HashSet<string>();
                        bucket.Add(addedType);
                    }

                    foreach (var kvp in createBuckets.Where(kvp => kvp.Value.Count > 0))
                    {
                        var origin = entityLocalOrigins.TryGetValue(kvp.Key, out var slotOrigin)
                            ? slotOrigin
                            : EntityOriginState.Unknown;
                        var source = origin == EntityOriginState.CreatedByInstantiate
                            ? GroupSource.EntityCommandBuffer
                            : GroupSource.InitializationBlock;
                        AddShapeFact(facts, seen, source, kvp.Value, $"{method.FullName}:slot:{kvp.Key}:{origin}");
                    }
                }
            }
        }

        foreach (var module in assembly.Modules)
        {
            foreach (var type in EnumerateTypesRecursive(module.Types))
            {
                if (!InheritsFromBaker(type))
                    continue;

                var bakeMethod = type.Methods.FirstOrDefault(m => m.Name == "Bake" && m.Parameters.Count == 1 && m.HasBody);
                if (bakeMethod == null)
                    continue;

                var componentsBySlot = new Dictionary<int, HashSet<string>>();
                var instrs = bakeMethod.Body.Instructions.ToList();

                for (int i = 0; i < instrs.Count; i++)
                {
                    var instr = instrs[i];
                    if (instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt)
                        continue;
                    if (instr.Operand is not MethodReference addMr || addMr.Name != "AddComponent")
                        continue;

                    var declaringBase = addMr.DeclaringType is GenericInstanceType dgit
                        ? dgit.ElementType.FullName
                        : addMr.DeclaringType.FullName;
                    if (declaringBase != "Unity.Entities.Baker`1" && declaringBase != "Unity.Entities.IBaker")
                        continue;

                    string typeName = null;
                    if (addMr is GenericInstanceMethod addGim && addGim.GenericArguments.Count == 1)
                        typeName = addGim.GenericArguments[0].FullName;
                    else if (addMr.Parameters.Count > 0)
                        typeName = addMr.Parameters[addMr.Parameters.Count - 1].ParameterType.FullName;

                    if (typeName == null || !knownComponents.ContainsKey(typeName))
                        continue;

                    int slot = -1;
                    var scan = instr.Previous;
                    int depth = 0;
                    while (scan != null && depth < 20)
                    {
                        if (TryGetLocalIndex(scan, out int candidateSlot) &&
                            (scan.OpCode.Code == Code.Ldloc || scan.OpCode.Code == Code.Ldloc_S ||
                             scan.OpCode.Code == Code.Ldloc_0 || scan.OpCode.Code == Code.Ldloc_1 ||
                             scan.OpCode.Code == Code.Ldloc_2 || scan.OpCode.Code == Code.Ldloc_3))
                        {
                            var locals = bakeMethod.Body.Variables;
                            if (candidateSlot < locals.Count &&
                                locals[candidateSlot].VariableType.FullName == "Unity.Entities.Entity")
                            {
                                slot = candidateSlot;
                                break;
                            }
                        }
                        scan = scan.Previous;
                        depth++;
                    }

                    if (slot == -1)
                        continue;

                    if (!componentsBySlot.TryGetValue(slot, out var bucket))
                        componentsBySlot[slot] = bucket = new HashSet<string>();
                    bucket.Add(typeName);
                }

                foreach (var kvp in componentsBySlot.Where(kvp => kvp.Value.Count > 0))
                    AddShapeFact(facts, seen, GroupSource.Baker, kvp.Value, $"{bakeMethod.FullName}:slot:{kvp.Key}");
            }
        }

        // Pass 3: field-stored entity origins (e.g. MonoBehaviour fields assigned from CreateEntity)
        // This catches patterns like: this.entityField = manager.CreateEntity();
        // followed by: manager.AddComponent<T>(this.entityField);
        foreach (var module in assembly.Modules)
        {
            foreach (var type in EnumerateTypesRecursive(module.Types))
            {
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    var entityFieldOrigins = new Dictionary<string, EntityOriginState>(StringComparer.Ordinal);
                    var fieldCreateBuckets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

                    foreach (var instr in method.Body.Instructions)
                    {
                        // Detect: stfld Entity-typed field preceded by CreateEntity() result
                        if (instr.OpCode.Code == Code.Stfld &&
                            instr.Operand is FieldReference storedField &&
                            IsEntityType(storedField.FieldType))
                        {
                            var prev = instr.Previous;
                            if (prev != null &&
                                (prev.OpCode.Code == Code.Call || prev.OpCode.Code == Code.Callvirt) &&
                                prev.Operand is MethodReference sourceMr &&
                                sourceMr.ReturnType.FullName == "Unity.Entities.Entity" &&
                                sourceMr.DeclaringType.FullName == "Unity.Entities.EntityManager" &&
                                sourceMr.Name == "CreateEntity")
                            {
                                var fkey = storedField.FullName;
                                entityFieldOrigins[fkey] = EntityOriginState.CreatedByCreateEntity;
                                if (!fieldCreateBuckets.ContainsKey(fkey))
                                    fieldCreateBuckets[fkey] = new HashSet<string>();
                            }
                        }

                        // Detect AddComponent/AddComponentData on field-stored entities
                        if ((instr.OpCode.Code == Code.Call || instr.OpCode.Code == Code.Callvirt) &&
                            instr.Operand is MethodReference addMr &&
                            TryGetValueAddComponentType(addMr, "Unity.Entities.EntityManager", knownComponents, out var addedType))
                        {
                            // Scan backward for ldfld of a tracked entity field
                            var scan = instr.Previous;
                            int depth = 0;
                            while (scan != null && depth < 20)
                            {
                                if (scan.OpCode.Code == Code.Ldfld &&
                                    scan.Operand is FieldReference loadedField &&
                                    entityFieldOrigins.TryGetValue(loadedField.FullName, out var origin) &&
                                    origin == EntityOriginState.CreatedByCreateEntity)
                                {
                                    if (!fieldCreateBuckets.TryGetValue(loadedField.FullName, out var bucket))
                                        fieldCreateBuckets[loadedField.FullName] = bucket = new HashSet<string>();
                                    bucket.Add(addedType);
                                    break;
                                }
                                scan = scan.Previous;
                                depth++;
                            }
                        }
                    }

                    foreach (var kvp in fieldCreateBuckets.Where(kvp => kvp.Value.Count > 0))
                    {
                        AddShapeFact(facts, seen, GroupSource.InitializationBlock, kvp.Value,
                            $"{method.FullName}:field:{kvp.Key}");
                    }
                }
            }
        }

        return facts;
    }

    private static void AddShapeFact(
        List<EntityShapeFact> facts,
        HashSet<string> seen,
        GroupSource source,
        HashSet<string> components,
        string provenance)
    {
        var key = $"{source}:{provenance}:{BuildGroupKey(components)}";
        if (!seen.Add(key))
            return;

        facts.Add(new EntityShapeFact(source, new HashSet<string>(components), provenance));
    }
}
