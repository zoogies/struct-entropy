using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using StructEntropy.Rewriter;
using static StructEntropy.Rewriter.ILInstructionHelpers;
using static StructEntropy.Rewriter.TypeResolutionHelpers;

/// <summary>
/// Executes planner-approved field relocations for struct entropy.
/// The planner supplies semantically valid, rewrite-ready moves; this rewriter applies the
/// supported IL transforms and fails if any planned source-field access remains unresolved.
///
/// Entry point: Rewrite(AssemblyDefinition, StructEntropyPlan)
/// </summary>
public static partial class StructEntropyRewriter
{
    private class TryGetPeerLocal
    {
        public MethodDefinition Method;
        public VariableDefinition SourceLocal;
        public TypeDefinition TargetType;
        public Instruction BranchInstruction;
        public VariableDefinition PeerLocal;
    }

    // --------------------------------------------------------------
    //  Entry point
    // --------------------------------------------------------------


    public static void Rewrite(AssemblyDefinition assembly, StructEntropyPlanner.StructEntropyPlan plan)
    {
        var module = assembly.MainModule;
        var relocations = BuildRelocations(module, plan);

        ExecuteTypeOwnerRelocations(module, relocations, "StructEntropyRewriter");
    }

    private static void ExecuteTypeOwnerRelocations(ModuleDefinition module, List<Relocation> relocations, string logPrefix)
    {

        if (relocations.Count == 0)
        {
            StructEntropyLogger.Log($"{logPrefix}: no approved relocations, skipping.");
            return;
        }

        int fieldsMoved = 0, refsRewritten = 0;

        foreach (var r in relocations)
        {
            if (r.Field is not FieldDefinition sourceFieldDef)
                throw new InvalidOperationException($"Type-owner relocation requires local source field definition: {r.SourceTypeFullName}::{r.Field?.Name}");

            var newField = new FieldDefinition(
                string.IsNullOrEmpty(r.TargetFieldName) ? r.Field.Name : r.TargetFieldName,
                sourceFieldDef.Attributes,
                module.ImportReference(r.Field.FieldType));
            CopyFieldMetadata(module, sourceFieldDef, newField);
            r.TargetType.Fields.Add(newField);
            r.SourceType.Fields.Remove(sourceFieldDef);
            r.NewField = newField;
            fieldsMoved++;
            StructEntropyLogger.Log(
                $"[SER]   Moved: {r.SourceType.Name}.{r.Field.Name} -> {r.TargetType.Name}");
        }

        foreach (var emptiedType in relocations
                     .Select(r => r.SourceType)
                     .Distinct()
                     .Where(t => !t.Fields.Any(f => !f.IsStatic)))
        {
            EnsureNonEmptyStructLayout(emptiedType);
        }

        var ecbMerges = new List<PendingEcbMerge>();
        refsRewritten += RewriteAllMethods(module, relocations, ecbMerges);
        refsRewritten += PreserveMovedFieldsOnTargetWholeStructWrites(module, relocations);

        foreach (var r in relocations)
            PatchChangedVersionFilters(module, r);

        if (ecbMerges.Count > 0)
        {
            var groupTypes = relocations.SelectMany(r => new[] { r.SourceType, r.TargetType }).Distinct().ToList();
            FixupEcbMerges(module, ecbMerges, groupTypes);
            FixupBakerMerges(module, ecbMerges, groupTypes);
        }

        NormalizeBranchMacros(module);
        RepairNullBranchOperands(module);
        ValidateNoPlannedOrphans(module, relocations);

        var touchedTypes = relocations
            .SelectMany(r => new[] { r.SourceType, r.TargetType })
            .Distinct()
            .OrderBy(t => t.FullName)
            .ToList();
        var layout = touchedTypes.Select(t => $"{t.Name} {{ {string.Join(", ", t.Fields.Where(f => !f.IsStatic).Select(f => $"{f.FieldType.Name} {f.Name}"))} }}");
        StructEntropyLogger.Log($"[SER]   Layout: {string.Join("  |  ", layout)}");
        StructEntropyLogger.Log($"{logPrefix}: planned {relocations.Count} relocation(s), moved {fieldsMoved} field(s), rewrote {refsRewritten} reference(s).");
    }

    private static List<Relocation> BuildRelocations(ModuleDefinition module, StructEntropyPlanner.StructEntropyPlan plan)
    {
        var relocations = new List<Relocation>();
        foreach (var move in plan.Moves)
        {
            var sourceType = FindTypeInModule(module, move.SourceComponentFullName);
            var targetType = FindTypeInModule(module, move.TargetComponentFullName);
            if (sourceType == null || targetType == null)
            {
                StructEntropyLogger.Log($"[SER] SKIP planned move: unresolved type {move.SourceComponentFullName} -> {move.TargetComponentFullName}");
                continue;
            }

            var sourceField = ResolveField(sourceType, move.SourceFieldFullName);
            if (sourceField == null)
            {
                StructEntropyLogger.Log($"[SER] SKIP planned move: unresolved field {move.SourceFieldFullName}");
                continue;
            }

            relocations.Add(new Relocation
            {
                Field = sourceField,
                SourceType = sourceType,
                SourceTypeFullName = sourceType.FullName,
                TargetType = targetType,
                TargetFieldName = move.TargetFieldName
            });
        }

        return relocations;
    }

    private static void EnsureNonEmptyStructLayout(TypeDefinition type)
    {
        if (type == null || !type.IsValueType)
            return;

        const string paddingFieldName = "__StructEntropy_nonzero_padding";
        if (!type.Fields.Any(f => !f.IsStatic))
        {
            var paddingField = new FieldDefinition(
                paddingFieldName,
                FieldAttributes.Private,
                type.Module.TypeSystem.Byte);
            type.Fields.Add(paddingField);
        }

        type.Attributes &= ~TypeAttributes.LayoutMask;
        type.Attributes |= TypeAttributes.SequentialLayout;
        type.PackingSize = 1;
        type.ClassSize = 1;

        StructEntropyLogger.Log($"[SER]   Applied non-zero padding to emptied struct {type.Name}");
    }

    private static void ValidateNoPlannedOrphans(ModuleDefinition module, List<Relocation> relocations)
    {
        var failures = new List<string>();
        foreach (var reloc in relocations)
        {
            foreach (var type in EnumerateAllTypes(module))
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var instr in method.Body.Instructions)
                {
                    if (instr.Operand != reloc.Field)
                        continue;

                    failures.Add($"{method.FullName}@{instr.OpCode.Code}:{reloc.SourceType.Name}.{reloc.Field.Name}");
                }
            }
        }

        if (failures.Count == 0)
            return;

        foreach (var failure in failures)
            StructEntropyLogger.Log($"[SER] ERROR: unsupported planned orphan {failure}");

        throw new InvalidOperationException(
            $"Struct Entropy planned rewrite left {failures.Count} unsupported field access(es).");
    }

    private static FieldDefinition ResolveField(TypeDefinition type, string fieldFullName)
    {
        if (type == null) return null;

        string expectedName = ExtractFieldName(fieldFullName);
        return type.Fields.FirstOrDefault(f =>
            $"{type.FullName}::{f.Name}" == fieldFullName ||
            f.FullName == fieldFullName ||
            f.Name == expectedName);
    }

    private static string ExtractFieldName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName))
            return fullName;

        int idx = fullName.LastIndexOf("::", StringComparison.Ordinal);
        return idx >= 0 ? fullName.Substring(idx + 2) : fullName;
    }

    private static bool TryFindSetItemForWholeStructFieldWrite(
        MethodDefinition method,
        Instruction sourceFieldWrite,
        Relocation reloc,
        FieldDefinition srcLookupField,
        out Instruction setItemCall,
        out List<Instruction> valueInstrs)
    {
        setItemCall = null;
        valueInstrs = null;

        if (method?.HasBody != true ||
            sourceFieldWrite?.OpCode != OpCodes.Stfld ||
            !IsFieldAccess(sourceFieldWrite, reloc.Field, reloc.SourceTypeFullName))
            return false;

        var valueEnd = sourceFieldWrite.Previous;
        if (valueEnd == null)
            return false;

        var valueStart = FindArgumentSequenceStart(valueEnd);
        if (valueStart == null)
            return false;

        var instanceLoad = valueStart.Previous;
        if (instanceLoad == null || !IsAddressLoad(instanceLoad))
            return false;

        var sourceLocal = GetLocalFromInstruction(instanceLoad, method.Body);
        if (sourceLocal == null || GetBaseTypeName(sourceLocal.VariableType) != reloc.SourceType.FullName)
            return false;

        valueInstrs = CloneInstructionList(EnumerateInstructionRange(valueStart, valueEnd));
        if (valueInstrs == null || valueInstrs.Count == 0)
            return false;

        int budget = 20;
        for (var scan = sourceFieldWrite.Next; scan != null && budget-- > 0; scan = scan.Next)
        {
            if ((scan.OpCode.Code != Code.Call && scan.OpCode.Code != Code.Callvirt) ||
                !IsSetItemOnComponentLookup(scan, reloc.SourceType))
                continue;

            if (FindFieldLoadForCall(scan, srcLookupField) == null)
                continue;

            var candidateValueLoad = scan.Previous;
            var candidateLocal = GetLocalFromInstruction(candidateValueLoad, method.Body);
            if (candidateLocal != sourceLocal)
                continue;

            setItemCall = scan;
            return true;
        }

        valueInstrs = null;
        return false;
    }


    // --------------------------------------------------------------
    //  Stage 2: per-method rewriting
    // --------------------------------------------------------------

    private static int RewriteAllMethods(
        ModuleDefinition module, List<Relocation> relocs,
        List<PendingEcbMerge> ecbMerges)
    {
        int total = 0;

        foreach (var type in EnumerateAllTypes(module))
        {
            foreach (var method in type.Methods.ToList())
            {
                if (!method.HasBody) continue;

                // IJobEntity pre-pass: user Execute with direct ref param of SourceType
                foreach (var r in relocs)
                {
                    if (IsUserIjeExecute(method, r.SourceType))
                        total += ApplyIJobEntityPeerInjection(module, method, r);
                }

                // General access site rewriting
                total += RewriteMethodAccessSites(module, method, relocs, ecbMerges);
            }
        }
        return total;
    }

    private static void NormalizeBranchMacros(ModuleDefinition module)
    {
        if (module == null)
            return;

        foreach (var type in EnumerateAllTypes(module))
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                method.Body.SimplifyMacros();
                method.Body.OptimizeMacros();
            }
        }
    }

    private static int RepairNullBranchOperands(ModuleDefinition module)
    {
        if (module == null)
            return 0;

        int repaired = 0;
        foreach (var type in EnumerateAllTypes(module))
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var branch in method.Body.Instructions)
                {
                    if (branch.OpCode.Code == Code.Switch)
                        continue;

                    if ((branch.OpCode.FlowControl != FlowControl.Branch &&
                         branch.OpCode.FlowControl != FlowControl.Cond_Branch) ||
                        !HasMissingBranchTarget(method, branch))
                        continue;

                    var target = FindForwardSwitchTarget(method, branch) ?? FindForwardRet(method, branch);
                    if (target == null)
                        throw new InvalidOperationException(
                            $"[SER] Cannot repair null branch target in {method.FullName} at IL_{branch.Offset:X4}.");

                    branch.Operand = target;
                    repaired++;
                    StructEntropyLogger.Log(
                        $"[SER]   Repaired null branch target in {method.DeclaringType.Name}.{method.Name} at IL_{branch.Offset:X4} -> IL_{target.Offset:X4}");
                }
            }
        }

        return repaired;
    }

    private static bool HasMissingBranchTarget(MethodDefinition method, Instruction branch)
    {
        if (branch.Operand == null)
            return true;

        if (branch.Operand is Instruction target)
            return !method.Body.Instructions.Contains(target);

        if (branch.Operand is Instruction[] targets)
            return targets.Any(target => target == null || !method.Body.Instructions.Contains(target));

        return false;
    }

    private static Instruction FindForwardSwitchTarget(MethodDefinition method, Instruction branch)
    {
        Instruction best = null;
        foreach (var instr in method.Body.Instructions)
        {
            if (instr.OpCode.Code != Code.Switch || instr.Operand is not Instruction[] targets)
                continue;

            foreach (var target in targets)
            {
                if (target == null || target.Offset <= branch.Offset)
                    continue;

                if (best == null || target.Offset < best.Offset)
                    best = target;
            }
        }

        return best;
    }

    private static Instruction FindForwardRet(MethodDefinition method, Instruction branch)
    {
        for (var scan = branch.Next; scan != null; scan = scan.Next)
        {
            if (scan.OpCode.Code == Code.Ret)
                return scan;
        }

        return null;
    }
    // Rewrites all ldfld/ldflda/stfld instructions in a method that reference moved fields.
    private static int RewriteMethodAccessSites(
        ModuleDefinition module, MethodDefinition method,
        List<Relocation> relocs, List<PendingEcbMerge> ecbMerges)
    {
        if (!method.HasBody) return 0;

        // Skip generated IJobChunk Execute; the IJobEntity peer injection pass patches the wrapper.
        if (IsGeneratedIjeExecute(method))
            return 0;

        // Skip user IJobEntity Execute bodies; those field accesses are redirected in the pre-pass.
        foreach (var r in relocs)
            if (IsUserIjeExecute(method, r.SourceType))
                return 0;

        int total = 0;
        var tryGetPeerLocals = new List<TryGetPeerLocal>();

        foreach (var reloc in relocs)
        {
            bool accessed = MethodAccessesField(method, reloc.Field, reloc.SourceTypeFullName);
            if (!accessed) continue;

            // ComponentLookupRewriter: declaring type has ComponentLookup<SourceType>.
            var srcLookup = FindComponentLookupField(method.DeclaringType, reloc.SourceType);
            if (srcLookup != null)
            {
                total += ApplyComponentLookup(module, method, reloc, srcLookup, tryGetPeerLocals);
                continue;
            }

            // EntityManagerGetRewriter: direct EntityManager.GetComponentData<SourceType>(entity).
            int s5 = ApplyEntityManagerGet(module, method, reloc);
            if (s5 > 0)
            {
                total += s5;
                continue;
            }

            int s6 = ApplyInlineForEach(module, method, reloc);
            if (s6 > 0)
            {
                total += s6;
                continue;
            }

            // ManagedRefRoForEachRewriter: managed (non-Burst) SystemAPI.Query<RefRO<T>>()
            // fallback copies that use Unity.Entities.RefRO<T> rather than the Burst
            // UncheckedRefRO enumerator handled by ApplyInlineForEach above.
            int sMref = ApplyManagedRefRoForEach(module, method, reloc);
            if (sMref > 0)
            {
                total += sMref;
                continue;
            }

            // GeneralFieldRedirectRewriter: value locals where the peer type is already in scope.
            int s3 = ApplyGeneralFieldRedirect(module, method, reloc, ecbMerges);
            total += s3;
        }

        return total;
    }
    private static bool HasInitObjForLocal(MethodDefinition method, VariableDefinition local)
    {
        foreach (var instr in method.Body.Instructions)
        {
            if (instr.OpCode != OpCodes.Initobj) continue;
            var prev = instr.Previous;
            if (prev == null) continue;
            var prevLocal = GetLocalFromInstruction(prev, method.Body);
            if (prevLocal != null && prevLocal == local) return true;
        }
        return false;
    }

    // --------------------------------------------------------------
    //  IL utility methods
    // --------------------------------------------------------------

    private static IEnumerable<TypeDefinition> EnumerateAllTypes(ModuleDefinition module)
    {
        foreach (var t in module.Types)
        {
            yield return t;
            foreach (var n in EnumerateNested(t)) yield return n;
        }
    }

    private static IEnumerable<TypeDefinition> EnumerateNested(TypeDefinition type)
    {
        foreach (var n in type.NestedTypes)
        {
            yield return n;
            foreach (var nn in EnumerateNested(n)) yield return nn;
        }
    }

}


