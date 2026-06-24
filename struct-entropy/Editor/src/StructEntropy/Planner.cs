using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

public static class StructEntropyPlanner
{
    public class ApprovedRelocation
    {
        public string SourceComponentFullName;
        public string SourceFieldFullName;
        public string TargetComponentFullName;
        public string TargetFieldName; // renamed field on target (null = keep original name)
        public HashSet<RewriteCapability> RequiredCapabilities;

        public ApprovedRelocation(
            string sourceComponentFullName,
            string sourceFieldFullName,
            string targetComponentFullName,
            HashSet<RewriteCapability> requiredCapabilities,
            string targetFieldName = null)
        {
            SourceComponentFullName = sourceComponentFullName;
            SourceFieldFullName = sourceFieldFullName;
            TargetComponentFullName = targetComponentFullName;
            TargetFieldName = targetFieldName;
            RequiredCapabilities = requiredCapabilities;
        }
    }

    public class StructEntropyPlan
    {
        public int Seed;
        public List<ApprovedRelocation> Moves;

        public StructEntropyPlan(int seed, List<ApprovedRelocation> moves)
        {
            Seed = seed;
            Moves = moves;
        }
    }

    public static StructEntropyPlan BuildPlan(
        AssemblyDefinition assembly,
        CandidateAnalysisResult analysis,
        int seed)
    {
        return BuildPlan(assembly, analysis, seed, 1.0f);
    }

    public static StructEntropyPlan BuildPlan(
        AssemblyDefinition assembly,
        CandidateAnalysisResult analysis,
        int seed,
        float fieldRelocationProbability)
    {
        return BuildPlan(
            assembly,
            analysis?.RelocationOpportunities ?? Enumerable.Empty<RelocationOpportunity>(),
            seed,
            fieldRelocationProbability);
    }

    public static StructEntropyPlan BuildPlan(
        AssemblyDefinition assembly,
        IEnumerable<RelocationOpportunity> opportunities,
        int seed)
    {
        return BuildPlan(assembly, opportunities, seed, 1.0f);
    }

    public static StructEntropyPlan BuildPlan(
        AssemblyDefinition assembly,
        IEnumerable<RelocationOpportunity> opportunities,
        int seed,
        float fieldRelocationProbability)
    {
        fieldRelocationProbability = Clamp01(fieldRelocationProbability);

        var eligible = (opportunities ?? Enumerable.Empty<RelocationOpportunity>())
            .Where(o => o.SemanticAllowed && o.RewriteSupportedNow)
            .Distinct(new OpportunityComparer())
            .ToList();

        if (eligible.Count == 0 || fieldRelocationProbability <= 0.0f)
            return new StructEntropyPlan(seed, new List<ApprovedRelocation>());

        var sourceDegree = eligible
            .GroupBy(o => o.SourceComponentFullName)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var targetDegree = eligible
            .GroupBy(o => o.TargetComponentFullName)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var rng = new Random(seed);
        var randomized = eligible
            .OrderBy(o => sourceDegree[o.SourceComponentFullName])
            .ThenBy(o => targetDegree[o.TargetComponentFullName])
            .ThenBy(_ => rng.Next())
            .ToList();

        var usedSourceFields = new HashSet<string>(StringComparer.Ordinal);
        var enabledSourceFields = BuildEnabledSourceFieldSet(eligible, seed, fieldRelocationProbability);
        // Track directed pairs to allow multiple fields from the same source→target,
        // but prevent circular dependencies (A→B and B→A in the same plan).
        var usedDirectedPairs = new HashSet<string>(StringComparer.Ordinal);
        var moves = new List<ApprovedRelocation>();

        foreach (var edge in randomized)
        {
            string forwardKey = edge.SourceComponentFullName + "->" + edge.TargetComponentFullName;
            string reverseKey = edge.TargetComponentFullName + "->" + edge.SourceComponentFullName;
            if (usedSourceFields.Contains(edge.SourceFieldFullName) ||
                !enabledSourceFields.Contains(edge.SourceFieldFullName) ||
                usedDirectedPairs.Contains(reverseKey))
                continue;

            var sourceType = FindType(assembly.MainModule, edge.SourceComponentFullName);
            var targetType = FindType(assembly.MainModule, edge.TargetComponentFullName);
            var sourceField = FindField(sourceType, edge.SourceFieldFullName);
            if (sourceType == null)
            {
                StructEntropyLogger.Log($"[SEP] Skip edge {ShortField(edge.SourceFieldFullName)} -> {ShortType(edge.TargetComponentFullName)}: unresolved source type '{edge.SourceComponentFullName}'");
                continue;
            }
            if (targetType == null)
            {
                StructEntropyLogger.Log($"[SEP] Skip edge {ShortField(edge.SourceFieldFullName)} -> {ShortType(edge.TargetComponentFullName)}: unresolved target type '{edge.TargetComponentFullName}'");
                continue;
            }
            if (sourceField == null)
            {
                StructEntropyLogger.Log($"[SEP] Skip edge {ShortField(edge.SourceFieldFullName)} -> {ShortType(edge.TargetComponentFullName)}: unresolved source field '{edge.SourceFieldFullName}'");
                continue;
            }
            if (sourceField.IsStatic)
            {
                StructEntropyLogger.Log($"[SEP] Skip edge {ShortField(edge.SourceFieldFullName)} -> {ShortType(edge.TargetComponentFullName)}: source field is static");
                continue;
            }

            // If the target already has a field with the same name, generate a
            // unique name prefixed with the source type to avoid collision.
            string targetFieldName = null;
            if (targetType.Fields.Any(f => !f.IsStatic && f.Name == sourceField.Name))
            {
                var sourceShort = ShortType(edge.SourceComponentFullName);
                targetFieldName = $"__zd_{sourceShort}_{sourceField.Name}";
                StructEntropyLogger.Log(
                    $"[SEP] Renaming relocated field: {ShortField(edge.SourceFieldFullName)} -> {ShortType(edge.TargetComponentFullName)}::{targetFieldName} (collision with existing '{sourceField.Name}')");
            }

            moves.Add(new ApprovedRelocation(
                edge.SourceComponentFullName,
                edge.SourceFieldFullName,
                edge.TargetComponentFullName,
                new HashSet<RewriteCapability>(edge.RequiredCapabilities),
                targetFieldName));

            usedSourceFields.Add(edge.SourceFieldFullName);
            usedDirectedPairs.Add(forwardKey);
        }

        return new StructEntropyPlan(seed, moves);
    }

    private static HashSet<string> BuildEnabledSourceFieldSet(
        IEnumerable<RelocationOpportunity> eligible,
        int seed,
        float fieldRelocationProbability)
    {
        var sourceFields = eligible
            .Select(o => o.SourceFieldFullName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        if (fieldRelocationProbability >= 1.0f)
            return new HashSet<string>(sourceFields, StringComparer.Ordinal);

        var rng = new Random(seed ^ unchecked((int)0x9E3779B9));
        var enabled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceField in sourceFields)
        {
            if (rng.NextDouble() <= fieldRelocationProbability)
                enabled.Add(sourceField);
        }

        return enabled;
    }

    private static float Clamp01(float value)
    {
        if (value < 0.0f) return 0.0f;
        if (value > 1.0f) return 1.0f;
        return value;
    }

    private static TypeDefinition FindType(ModuleDefinition module, string fullName)
    {
        return module.GetTypes().FirstOrDefault(t => t.FullName == fullName);
    }

    private static FieldDefinition FindField(TypeDefinition type, string fieldFullName)
    {
        if (type == null) return null;
        string expectedName = ExtractFieldName(fieldFullName);
        return type.Fields.FirstOrDefault(f =>
            $"{type.FullName}::{f.Name}" == fieldFullName ||
            f.FullName == fieldFullName ||
            f.Name == expectedName);
    }

    private static string ShortType(string fullName)
    {
        int idx = fullName.LastIndexOf('.');
        return idx >= 0 ? fullName.Substring(idx + 1) : fullName;
    }

    private static string ShortField(string fullName)
    {
        return ExtractFieldName(fullName);
    }

    private static string ExtractFieldName(string fullName)
    {
        int idx = fullName.LastIndexOf("::", StringComparison.Ordinal);
        return idx >= 0 ? fullName.Substring(idx + 2) : fullName;
    }

    private sealed class OpportunityComparer : IEqualityComparer<RelocationOpportunity>
    {
        public bool Equals(RelocationOpportunity x, RelocationOpportunity y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x == null || y == null) return false;
            return x.SourceComponentFullName == y.SourceComponentFullName &&
                   x.SourceFieldFullName == y.SourceFieldFullName &&
                   x.TargetComponentFullName == y.TargetComponentFullName;
        }

        public int GetHashCode(RelocationOpportunity obj)
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + (obj.SourceComponentFullName != null ? StringComparer.Ordinal.GetHashCode(obj.SourceComponentFullName) : 0);
                hash = (hash * 31) + (obj.SourceFieldFullName != null ? StringComparer.Ordinal.GetHashCode(obj.SourceFieldFullName) : 0);
                hash = (hash * 31) + (obj.TargetComponentFullName != null ? StringComparer.Ordinal.GetHashCode(obj.TargetComponentFullName) : 0);
                return hash;
            }
        }
    }
}
