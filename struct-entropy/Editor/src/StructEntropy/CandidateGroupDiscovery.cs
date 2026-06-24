using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

public partial class StructEntropyCandidates
{
    private static Dictionary<string, ComponentTypeInfo> DiscoverPossibleCandidateComponents(AssemblyDefinition assembly)
    {
        var possibleCandidateComponents = new Dictionary<string, ComponentTypeInfo>();

        foreach (var module in assembly.Modules)
        {
            foreach (var type in EnumerateTypesRecursive(module.Types))
            {
                if (!type.IsValueType ||
                    !type.Interfaces.Any(i => i.InterfaceType.FullName == "Unity.Entities.IComponentData"))
                    continue;

                if (type.Interfaces.Any(i => i.InterfaceType.FullName == "Unity.Entities.ISharedComponentData") ||
                    type.Interfaces.Any(i => i.InterfaceType.FullName == "Unity.Entities.ICleanupComponentData"))
                    continue;

                var isEnableable = type.Interfaces.Any(i =>
                    i.InterfaceType.FullName == "Unity.Entities.IEnableableComponent");
                possibleCandidateComponents[type.FullName] = new ComponentTypeInfo(type, isEnableable);
            }
        }

        return possibleCandidateComponents;
    }

    private static List<CandidateGroup> DiscoverCandidateGroups(
        AssemblyDefinition assembly,
        Dictionary<string, ComponentTypeInfo> possibleCandidateComponents)
    {
        // BRING ME A CFG!
        var cfg = CFGGen.GenerateAssemblyCFG(assembly);
        var discoveredGroups = new List<DiscoveredGroup>();

        foreach (var block in cfg.blocks)
        {
            foreach (var instr in block.instructions)
            {
                if (instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) continue;
                if (instr.Operand is not MethodReference mr) continue;

                if (mr.DeclaringType.FullName == "Unity.Entities.EntityManager" &&
                    mr.Name == "CreateArchetype")
                {
                    var components = ExtractCreateArchetypeComponents(instr, possibleCandidateComponents);

                    if (components.Count > 1)
                        discoveredGroups.Add(new DiscoveredGroup(components, GroupSource.CreateArchetype));
                }
            }

            // Paper scope: group discovery is anchored to CreateEntity call sites.
            // Mid-lifetime co-add patterns are outside the evaluated claim.
            var instrs = block.instructions.ToList();
            for (int i = 0; i < instrs.Count; i++)
            {
                var instr = instrs[i];
                if (instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) continue;
                if (instr.Operand is not MethodReference createMr) continue;
                if (createMr.DeclaringType.FullName != "Unity.Entities.EntityManager") continue;
                if (createMr.Name != "CreateEntity") continue;

                // Paper scope: multi-entity return paths (NativeArray) are excluded from the evaluated claim.
                if (createMr.ReturnType.FullName != "Unity.Entities.Entity") continue;

                if (i + 1 >= instrs.Count) continue;
                if (!TryGetLocalIndex(instrs[i + 1], out int entitySlot)) continue;

                var groupComponents = new HashSet<string>();

                for (int j = i + 2; j < instrs.Count; j++)
                {
                    var fwd = instrs[j];
                    if (fwd.OpCode.Code != Code.Call && fwd.OpCode.Code != Code.Callvirt) continue;
                    if (fwd.Operand is not MethodReference addMr) continue;
                    if (!TryGetValueAddComponentType(addMr, "Unity.Entities.EntityManager", possibleCandidateComponents, out var typeName)) continue;

                    // Stack at call site: [..., entityManagerAddr, entity] -> call AddComponent<T>.
                    var entityPush = fwd.Previous;
                    if (entityPush == null) continue;
                    if (!TryGetLocalIndex(entityPush, out int pushedSlot)) continue;
                    if (pushedSlot != entitySlot) continue;

                    groupComponents.Add(typeName);
                }

                if (groupComponents.Count > 1)
                    discoveredGroups.Add(new DiscoveredGroup(groupComponents, GroupSource.InitializationBlock));
            }

            for (int i = 0; i < instrs.Count; i++)
            {
                var instr = instrs[i];
                if (instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) continue;
                if (instr.Operand is not MethodReference createMr) continue;
                if (!IsEcbEntityCreationMethod(createMr)) continue;

                if (i + 1 >= instrs.Count) continue;
                if (!TryGetLocalIndex(instrs[i + 1], out int entitySlot)) continue;

                var groupComponents = new HashSet<string>();

                for (int j = i + 2; j < instrs.Count; j++)
                {
                    var fwd = instrs[j];
                    if (!IsEcbAddComponentCall(fwd, possibleCandidateComponents, out var typeName)) continue;

                    // Stack shape differs across ECB overloads, so look for the created entity
                    // local among recent argument loads.
                    if (!HasEntitySlotLoadInRecentWindow(fwd, entitySlot)) continue;

                    groupComponents.Add(typeName);
                }

                if (groupComponents.Count > 1)
                    discoveredGroups.Add(new DiscoveredGroup(groupComponents, GroupSource.EntityCommandBuffer));
            }
        }

        foreach (var module in assembly.Modules)
        {
            foreach (var type in EnumerateTypesRecursive(module.Types))
            {
                if (!InheritsFromBaker(type)) continue;

                var bakeMethod = type.Methods.FirstOrDefault(m =>
                    m.Name == "Bake" && m.Parameters.Count == 1 && m.HasBody);
                if (bakeMethod == null) continue;

                var componentsBySlot = new Dictionary<int, HashSet<string>>();
                var instrs = bakeMethod.Body.Instructions.ToList();

                for (int i = 0; i < instrs.Count; i++)
                {
                    var instr = instrs[i];
                    if (instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) continue;
                    if (instr.Operand is not MethodReference addMr) continue;
                    if (addMr.Name != "AddComponent") continue;

                    var declaringBase = addMr.DeclaringType is GenericInstanceType dgit
                        ? dgit.ElementType.FullName
                        : addMr.DeclaringType.FullName;
                    if (declaringBase != "Unity.Entities.Baker`1" && declaringBase != "Unity.Entities.IBaker") continue;

                    string typeName = null;
                    if (addMr is GenericInstanceMethod addGim && addGim.GenericArguments.Count == 1)
                        typeName = addGim.GenericArguments[0].FullName;
                    else if (addMr.Parameters.Count > 0)
                        typeName = addMr.Parameters[addMr.Parameters.Count - 1].ParameterType.FullName;

                    if (typeName == null || !possibleCandidateComponents.ContainsKey(typeName)) continue;

                    int slot = -1;
                    var scan = instr.Previous;
                    var depth = 0;
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

                    if (slot == -1) continue;

                    if (!componentsBySlot.TryGetValue(slot, out var group))
                        componentsBySlot[slot] = group = new HashSet<string>();
                    group.Add(typeName);
                }

                foreach (var group in componentsBySlot.Values)
                {
                    if (group.Count > 1)
                        discoveredGroups.Add(new DiscoveredGroup(group, GroupSource.Baker));
                }
            }
        }

        var phase3Candidates = new List<CandidateGroup>();
        var phase3Seen = new HashSet<string>();

        foreach (var discovered in discoveredGroups)
        {
            if (discovered.ComponentFullNames.Count < 2)
                continue;

            var dedupeKey = $"{discovered.Source}:{BuildGroupKey(discovered.ComponentFullNames)}";
            if (!phase3Seen.Add(dedupeKey))
                continue;

            var typedComponents = new HashSet<ComponentTypeInfo>(
                discovered.ComponentFullNames
                    .Where(n => possibleCandidateComponents.ContainsKey(n))
                    .Select(n => possibleCandidateComponents[n]));

            if (typedComponents.Count < 2)
                continue;

            phase3Candidates.Add(new CandidateGroup(typedComponents, discovered.Source));
        }

        return phase3Candidates;
    }
}
