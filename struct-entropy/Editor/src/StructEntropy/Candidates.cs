using System;
using System.Linq;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Unity.CompilationPipeline.Common.ILPostProcessing;

public partial class StructEntropyCandidates
{
    private enum EntityOriginState
    {
        Unknown,
        CreatedByCreateEntity,
        CreatedByInstantiate,
        NonCreatedInMethod
    }

    private enum MutationEvidence
    {
        Safe,
        Unsafe,
        Unknown
    }

    private static IEnumerable<TypeDefinition> EnumerateTypesRecursive(IEnumerable<TypeDefinition> rootTypes)
    {
        foreach (var type in rootTypes)
        {
            yield return type;
            foreach (var nested in EnumerateTypesRecursive(type.NestedTypes))
                yield return nested;
        }
    }

    // represents a set of candidates that are initialized together
    public class DiscoveredGroup
    {
        public HashSet<string> ComponentFullNames;
        public GroupSource Source;

        public DiscoveredGroup(HashSet<string> componentFullNames, GroupSource source)
        {
            ComponentFullNames = componentFullNames;
            Source = source;
        }
    }

    private static HashSet<string> ExtractCreateArchetypeComponents(Instruction createArchetypeCall, Dictionary<string, ComponentTypeInfo> knownComponents)
    {
        var result = new HashSet<string>();

        // walk back to find the newarr allocation for CreateArchetype args
        var instr = createArchetypeCall.Previous;
        while (instr != null)
        {
            if (instr.OpCode.Code == Code.Newarr &&
                instr.Operand is TypeReference tr &&
                (tr.FullName == "Unity.Entities.ComponentType" || tr.FullName == "System.Type"))
            {
                // Walk forward from allocation, collecting both ComponentType.ReadWrite<T>()
                // and EntityManager.CreateArchetype(typeof(T), ...) argument forms.
                var forward = instr.Next;
                while (forward != null && forward != createArchetypeCall)
                {
                    if (forward.OpCode.Code == Code.Call &&
                        forward.Operand is GenericInstanceMethod gim &&
                        gim.DeclaringType.FullName == "Unity.Entities.ComponentType" &&
                        (gim.Name == "ReadWrite" || gim.Name == "ReadOnly"))
                    {
                        var typeName = gim.GenericArguments[0].FullName;
                        if (knownComponents.ContainsKey(typeName))
                            result.Add(typeName);
                    }

                    if (TryGetTypeFromHandleTypeName(forward, out var typeFromHandleName) &&
                        knownComponents.ContainsKey(typeFromHandleName))
                    {
                        result.Add(typeFromHandleName);
                    }

                    forward = forward.Next;
                }
                break;
            }
            instr = instr.Previous;
        }

        return result;
    }

    private static bool TryGetTypeFromHandleTypeName(Instruction instr, out string typeName)
    {
        typeName = null;

        if (instr == null ||
            instr.OpCode.Code != Code.Call ||
            instr.Operand is not MethodReference mr ||
            mr.Name != "GetTypeFromHandle" ||
            mr.DeclaringType.FullName != "System.Type")
            return false;

        var previous = instr.Previous;
        if (previous?.OpCode.Code != Code.Ldtoken ||
            previous.Operand is not TypeReference typeReference)
            return false;

        typeName = typeReference.FullName;
        return !string.IsNullOrEmpty(typeName);
    }

    // identify local variable slots for the CreateEntity result
    private static bool TryGetLocalIndex(Instruction instr, out int index)
    {
        index = -1;
        switch (instr.OpCode.Code)
        {
            case Code.Stloc_0: case Code.Ldloc_0: index = 0; return true;
            case Code.Stloc_1: case Code.Ldloc_1: index = 1; return true;
            case Code.Stloc_2: case Code.Ldloc_2: index = 2; return true;
            case Code.Stloc_3: case Code.Ldloc_3: index = 3; return true;
            case Code.Stloc_S:
            case Code.Ldloc_S:
            case Code.Stloc:
            case Code.Ldloc:
                index = ((VariableReference)instr.Operand).Index; return true;
            default: return false;
        }
    }

    // Checks whether a type derives from Unity.Entities.Baker<T>.
    private static bool InheritsFromBaker(TypeDefinition type)
    {
        var current = type.BaseType;
        while (current != null)
        {
            var elementType = current is GenericInstanceType git ? git.ElementType : current;
            if (elementType.FullName == "Unity.Entities.Baker`1")
                return true;
            try { current = current.Resolve()?.BaseType; }
            catch { break; }  // unresolvable reference
        }
        return false;
    }

    private static bool IsEntityCommandBufferType(string fullName)
    {
        return fullName == "Unity.Entities.EntityCommandBuffer"
            || fullName == "Unity.Entities.EntityCommandBuffer/ParallelWriter";
    }

    private static bool IsEcbEntityCreationMethod(MethodReference mr)
    {
        if (!IsEntityCommandBufferType(mr.DeclaringType.FullName))
            return false;

        if (mr.ReturnType.FullName != "Unity.Entities.Entity")
            return false;

        return mr.Name == "CreateEntity" || mr.Name == "Instantiate";
    }

    private static bool IsEcbAddComponentCall(
        Instruction instr,
        Dictionary<string, ComponentTypeInfo> knownComponents,
        out string componentTypeFullName)
    {
        componentTypeFullName = null;

        if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
            instr.Operand is not MethodReference mr)
            return false;

        if (!IsEntityCommandBufferType(mr.DeclaringType.FullName))
            return false;

        if (mr.Name != "AddComponent")
            return false;

        if (mr is GenericInstanceMethod gim && gim.GenericArguments.Count == 1)
        {
            componentTypeFullName = gim.GenericArguments[0].FullName;
            return true;
        }

        if (mr.Parameters.Count > 0)
        {
            var lastParam = mr.Parameters[mr.Parameters.Count - 1].ParameterType;
            if (knownComponents.ContainsKey(lastParam.FullName))
            {
                componentTypeFullName = lastParam.FullName;
                return true;
            }
        }

        return false;
    }

    private static bool HasEntitySlotLoadInRecentWindow(Instruction callInstr, int entitySlot, int maxDepth = 24)
    {
        var scan = callInstr.Previous;
        int depth = 0;
        while (scan != null && depth < maxDepth)
        {
            if (TryGetLocalIndex(scan, out int slot) &&
                (scan.OpCode.Code == Code.Ldloc || scan.OpCode.Code == Code.Ldloc_S ||
                 scan.OpCode.Code == Code.Ldloc_0 || scan.OpCode.Code == Code.Ldloc_1 ||
                 scan.OpCode.Code == Code.Ldloc_2 || scan.OpCode.Code == Code.Ldloc_3) &&
                slot == entitySlot)
            {
                return true;
            }

            scan = scan.Previous;
            depth++;
        }

        return false;
    }

    private static bool TryGetRecentLoadedEntityLocalSlot(
        Instruction callInstr,
        Dictionary<int, EntityOriginState> entityLocalOrigins,
        out int slot,
        int maxDepth = 48)
    {
        slot = -1;
        var scan = callInstr.Previous;
        int depth = 0;
        while (scan != null && depth < maxDepth)
        {
            if (TryGetLocalIndex(scan, out int candidateSlot) &&
                (scan.OpCode.Code == Code.Ldloc || scan.OpCode.Code == Code.Ldloc_S ||
                 scan.OpCode.Code == Code.Ldloc_0 || scan.OpCode.Code == Code.Ldloc_1 ||
                 scan.OpCode.Code == Code.Ldloc_2 || scan.OpCode.Code == Code.Ldloc_3) &&
                entityLocalOrigins.ContainsKey(candidateSlot))
            {
                slot = candidateSlot;
                return true;
            }

            scan = scan.Previous;
            depth++;
        }

        return false;
    }

    private static string BuildGroupKey(IEnumerable<string> components)
    {
        return string.Join("|", components.OrderBy(c => c));
    }

    private static bool IsStructuralMutationCarrier(string fullName)
    {
        return fullName == "Unity.Entities.EntityManager"
            || fullName == "Unity.Entities.EntityCommandBuffer"
            || fullName == "Unity.Entities.EntityCommandBuffer/ParallelWriter";
    }

    private static bool IsInitializationCall(MethodReference mr)
    {
        if (mr.DeclaringType.FullName == "Unity.Entities.EntityManager")
            return mr.Name == "CreateEntity" || mr.Name == "CreateArchetype" || mr.Name == "Instantiate";

        if (IsEntityCommandBufferType(mr.DeclaringType.FullName))
            return mr.Name == "CreateEntity" || mr.Name == "Instantiate";

        return false;
    }

    private static bool TryGetGenericStructuralMutation(
        Instruction instr,
        string methodName,
        out string componentTypeFullName)
    {
        componentTypeFullName = null;

        if ((instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) ||
            instr.Operand is not GenericInstanceMethod gim)
            return false;

        if (!IsStructuralMutationCarrier(gim.DeclaringType.FullName))
            return false;

        if (gim.Name != methodName || gim.GenericArguments.Count != 1)
            return false;

        componentTypeFullName = gim.GenericArguments[0].FullName;
        return true;
    }

    private static IEnumerable<MethodDefinition> EnumerateMethodsRecursive(IEnumerable<TypeDefinition> rootTypes)
    {
        foreach (var type in rootTypes)
        {
            foreach (var method in type.Methods)
                yield return method;

            foreach (var nestedMethod in EnumerateMethodsRecursive(type.NestedTypes))
                yield return nestedMethod;
        }
    }

    private static bool IsStoreLocalCode(Code code)
    {
        return code == Code.Stloc || code == Code.Stloc_S ||
               code == Code.Stloc_0 || code == Code.Stloc_1 ||
               code == Code.Stloc_2 || code == Code.Stloc_3;
    }

    private static bool IsLoadArgCode(Code code)
    {
        return code == Code.Ldarg || code == Code.Ldarg_S ||
               code == Code.Ldarg_0 || code == Code.Ldarg_1 ||
               code == Code.Ldarg_2 || code == Code.Ldarg_3;
    }

    private static bool TryGetArgIndex(Instruction instr, out int index)
    {
        index = -1;
        switch (instr.OpCode.Code)
        {
            case Code.Ldarg_0: index = 0; return true;
            case Code.Ldarg_1: index = 1; return true;
            case Code.Ldarg_2: index = 2; return true;
            case Code.Ldarg_3: index = 3; return true;
            case Code.Ldarg_S:
            case Code.Ldarg:
                if (instr.Operand is ParameterDefinition pd)
                {
                    index = pd.Index;
                    return true;
                }
                if (instr.Operand is byte b)
                {
                    index = b;
                    return true;
                }
                if (instr.Operand is int i)
                {
                    index = i;
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static bool IsEntityType(TypeReference tr)
    {
        return tr != null && tr.FullName == "Unity.Entities.Entity";
    }

    private static bool IsEntityParameterLoad(Instruction instr, MethodDefinition method)
    {
        if (!TryGetArgIndex(instr, out int argIndex))
            return false;

        if (method.HasThis)
        {
            if (argIndex == 0)
                return false; // ldarg.0 == this
            argIndex -= 1;
        }

        if (argIndex < 0 || argIndex >= method.Parameters.Count)
            return false;

        return IsEntityType(method.Parameters[argIndex].ParameterType);
    }

    private static MutationEvidence ClassifyMutationEvidence(
        Instruction instr,
        MethodDefinition method,
        Dictionary<int, EntityOriginState> entityLocalOrigins,
        out int slot)
    {
        slot = -1;
        if (TryGetRecentLoadedEntityLocalSlot(instr, entityLocalOrigins, out int candidateSlot))
        {
            slot = candidateSlot;

            if (entityLocalOrigins.TryGetValue(candidateSlot, out var origin))
            {
                if (origin == EntityOriginState.CreatedByCreateEntity ||
                    origin == EntityOriginState.CreatedByInstantiate)
                    return MutationEvidence.Safe;
                if (origin == EntityOriginState.NonCreatedInMethod)
                    return MutationEvidence.Unsafe;
            }
        }

        // Fallback: if entity is passed directly from method args or returned from a call,
        // classify conservatively using local method context.
        var scan = instr.Previous;
        int depth = 0;
        while (scan != null && depth < 48)
        {
            if (IsLoadArgCode(scan.OpCode.Code) && IsEntityParameterLoad(scan, method))
                return MutationEvidence.Unsafe;

            if ((scan.OpCode.Code == Code.Call || scan.OpCode.Code == Code.Callvirt) &&
                scan.Operand is MethodReference mr &&
                IsEntityType(mr.ReturnType))
            {
                return IsInitializationCall(mr) ? MutationEvidence.Safe : MutationEvidence.Unsafe;
            }

            scan = scan.Previous;
            depth++;
        }

        return MutationEvidence.Unknown;
    }

    private static bool TryGetValueAddComponentType(
        MethodReference mr,
        string expectedCarrierFullName,
        Dictionary<string, ComponentTypeInfo> knownComponents,
        out string componentTypeFullName)
    {
        componentTypeFullName = null;

        if (mr == null || mr.DeclaringType == null ||
            (mr.Name != "AddComponent" && mr.Name != "AddComponentData"))
            return false;

        if (mr.DeclaringType.FullName != expectedCarrierFullName)
            return false;

        if (mr is GenericInstanceMethod gim && gim.GenericArguments.Count == 1)
        {
            componentTypeFullName = gim.GenericArguments[0].FullName;
            return knownComponents.ContainsKey(componentTypeFullName);
        }

        if (mr.Parameters.Count == 0)
            return false;

        var lastParam = mr.Parameters[mr.Parameters.Count - 1].ParameterType;
        if (!knownComponents.ContainsKey(lastParam.FullName))
            return false;

        componentTypeFullName = lastParam.FullName;
        return true;
    }

    public static CandidateAnalysisResult GetCandidates(AssemblyDefinition assembly, ICompiledAssembly compiledAssembly) {

        var possibleCandidateComponents = DiscoverPossibleCandidateComponents(assembly);
        var phase3Candidates = DiscoverCandidateGroups(assembly, possibleCandidateComponents);


        // PHASE 4 - STRUCTURAL MUTATION ANALYSIS
        // For each surviving group G, scan all reachable methods:
        //   DISQUALIFY component X from G if:
        //     RemoveComponent<X> exists with no corresponding removal of all other G members
        //     in same BB or same ComponentTypeSet(S)
        //   DISQUALIFY component X from G if:
        //     AddComponent<X> in a BB that is NOT an initialization BB for a new entity
        //     (post-creation addition = mid-lifetime archetype transition)
        //   SAFE: RemoveComponent(ComponentTypeSet(S)) where G is a subset of S
        //   SAFE: DestroyEntity(*) - symmetric exit
        //   Apply to EntityManager AND EntityCommandBuffer AND ECB.ParallelWriter
        //
        // NOTE: This pass currently models generic Add/Remove calls.
        //       Non-generic ComponentTypeSet overloads are treated as unknown (not disqualifying yet).
        var allMethods = new List<MethodDefinition>();
        foreach (var module in assembly.Modules)
            allMethods.AddRange(EnumerateMethodsRecursive(module.Types).Where(m => m.HasBody));

        var phase4Candidates = PruneStructuralMutationCandidates(
            phase3Candidates,
            allMethods,
            possibleCandidateComponents);

        var entityShapeFacts = BuildInitializationShapeFacts(assembly, possibleCandidateComponents);


        AnalyzeQueryAudit(
            phase4Candidates,
            allMethods,
            possibleCandidateComponents,
            out var eligibleNow,
            out var eligibleWithRewrite);


        var candidateGroupsForGraph = eligibleNow.Concat(eligibleWithRewrite).Select(a => a.Group).ToList();
        var fieldAccessSites = AccessPatternClassifier.BuildFieldAccessSites(allMethods, candidateGroupsForGraph, possibleCandidateComponents);
        var relocationOpportunities = ValidityAnalyzer.BuildRelocationOpportunities(candidateGroupsForGraph, entityShapeFacts, fieldAccessSites, allMethods);

        return new CandidateAnalysisResult(
            eligibleNow,
            eligibleWithRewrite,
            entityShapeFacts,
            fieldAccessSites,
            relocationOpportunities);
    }
}
