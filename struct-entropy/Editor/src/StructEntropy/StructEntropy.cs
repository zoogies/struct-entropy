using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using Unity.CompilationPipeline.Common.ILPostProcessing;
using StructEntropy.Editor;

/// <summary>
/// Struct Entropy obfuscation pass with seeded relocation planning.
/// Identifies ECS component structs and performs field relocation to obscure memory layout.
/// Emits relocation summaries and applies planner-approved IL rewrites.
/// </summary>
public class StructEntropyPass
{
    public static (byte[] assemblyData, byte[] pdbData) Process(
        byte[] assemblyData,
        byte[] pdbData,
        ICompiledAssembly compiledAssembly)
    {
        var processor = new StructEntropyAssemblyProcessor(assemblyData, pdbData, compiledAssembly);

        return processor.ProcessAssembly(assembly =>
        {
            // 1. Load config
            var config = StructEntropyConfigReader.LoadConfig("ProjectSettings/StructEntropyConfig.json");
            if (!config.structEntropyEnabled)
            {
                StructEntropyLogger.Log("Struct Entropy disabled in config, skipping.");
                return;
            }

            StructEntropyLogger.Log("Running Struct Entropy pass.");

            // 2. Collect local candidates
            var localAnalysis = StructEntropyCandidates.GetCandidates(assembly, compiledAssembly);
            var allGroups = localAnalysis.EligibleNow.Concat(localAnalysis.EligibleWithRewrite).ToList();

            // 3. Build plan
            var plan = StructEntropyPlanner.BuildPlan(
                assembly,
                localAnalysis.RelocationOpportunities,
                config.structEntropySeed,
                config.structEntropyFieldRelocationProbability);

            // 4. Log and emit local summary artifacts
            if (allGroups.Count == 0)
            {
                StructEntropyLogger.Log("Struct Entropy: no candidate groups found, skipping.");
                return;
            }

            StructEntropyLogger.Log($"Struct Entropy: {allGroups.Count} candidate group(s)");
            foreach (var assessment in allGroups)
            {
                var typeNames = string.Join(", ", assessment.Group.Components.OrderBy(c => c.Type.FullName).Select(c => c.Type.Name));
                StructEntropyLogger.Log($"  [{assessment.Group.Source}] {typeNames}");
            }

            StructEntropyLogger.Log(
                $"Struct Entropy graph: {localAnalysis.EntityShapes.Count} shape fact(s), " +
                $"{localAnalysis.FieldAccessSites.Count} field access site(s), " +
                $"{localAnalysis.RelocationOpportunities.Count} relocation opportunity edge(s)");

            WriteOpportunitySummary(localAnalysis.RelocationOpportunities, verboseLogging: config.structEntropyVerboseLogging);
            WritePlanSummary(plan);
            WriteGraphExportIfEnabled(compiledAssembly, localAnalysis);

            // 5. Rewrite
            StructEntropyRewriter.Rewrite(assembly, plan);

            // 6. Write artifacts
            WritePlanArtifact(compiledAssembly, plan);
            WriteEntropySignature(plan);
        });
    }

    private static void WriteEntropySignature(StructEntropyPlanner.StructEntropyPlan plan)
    {
        var lines = plan.Moves
            .SelectMany(m => new[] { m.SourceComponentFullName, m.TargetComponentFullName, m.SourceFieldFullName })
            .OrderBy(n => n, StringComparer.Ordinal);

        string signature = string.Join("\n", lines);
        StructEntropySignatureWriter.Write("struct-entropy", signature);
    }

    private static void WritePlanArtifact(
        ICompiledAssembly compiledAssembly,
        StructEntropyPlanner.StructEntropyPlan plan)
    {
        string outPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "Logs",
            "StructEntropy_Output",
            compiledAssembly.Name,
            "selected-relocations.json");

        string dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(outPath, BuildPlanJson(compiledAssembly, plan), new UTF8Encoding(false));
        StructEntropyLogger.Log($"Struct Entropy selected relocations written to {outPath}");
    }

    private static string BuildPlanJson(
        ICompiledAssembly compiledAssembly,
        StructEntropyPlanner.StructEntropyPlan plan)
    {
        var moves = (plan?.Moves ?? new List<StructEntropyPlanner.ApprovedRelocation>())
            .OrderBy(m => m.SourceComponentFullName, StringComparer.Ordinal)
            .ThenBy(m => m.SourceFieldFullName, StringComparer.Ordinal)
            .ThenBy(m => m.TargetComponentFullName, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.Append("  \"assembly\": ").Append(JsonString(compiledAssembly.Name ?? string.Empty)).AppendLine(",");
        sb.Append("  \"seed\": ").Append(plan?.Seed ?? 0).AppendLine(",");
        sb.Append("  \"moveCount\": ").Append(moves.Count).AppendLine(",");
        sb.AppendLine("  \"moves\": [");

        for (int i = 0; i < moves.Count; i++)
        {
            var move = moves[i];
            var capabilities = (move.RequiredCapabilities ?? new HashSet<RewriteCapability>())
                .OrderBy(c => c.ToString(), StringComparer.Ordinal)
                .Select(c => c.ToString())
                .ToList();

            sb.AppendLine("    {");
            sb.Append("      \"sourceComponent\": ").Append(JsonString(move.SourceComponentFullName ?? string.Empty)).AppendLine(",");
            sb.Append("      \"sourceField\": ").Append(JsonString(move.SourceFieldFullName ?? string.Empty)).AppendLine(",");
            sb.Append("      \"targetComponent\": ").Append(JsonString(move.TargetComponentFullName ?? string.Empty)).AppendLine(",");
            sb.Append("      \"sourceFieldShort\": ").Append(JsonString(ShortFieldName(move.SourceFieldFullName))).AppendLine(",");
            sb.Append("      \"targetComponentShort\": ").Append(JsonString(ShortTypeName(move.TargetComponentFullName))).AppendLine(",");
            sb.Append("      \"requiredCapabilities\": [");
            for (int j = 0; j < capabilities.Count; j++)
            {
                if (j > 0)
                    sb.Append(", ");
                sb.Append(JsonString(capabilities[j]));
            }
            sb.AppendLine("]");
            sb.Append("    }");
            if (i < moves.Count - 1)
                sb.Append(",");
            sb.AppendLine();
        }

        sb.AppendLine("  ]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string JsonString(string value)
    {
        if (value == null)
            return "null";

        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (ch < ' ')
                        sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static void WriteOpportunitySummary(
        List<RelocationOpportunity> opportunities,
        int maxEdgesToPrint = int.MaxValue,
        bool verboseLogging = false)
    {
        int semanticAllow = opportunities.Count(o => o.SemanticAllowed);
        int semanticReject = opportunities.Count - semanticAllow;
        int rewriteAllow = opportunities.Count(o => o.SemanticAllowed && o.RewriteSupportedNow);
        int rewriteBlocked = opportunities.Count(o => o.SemanticAllowed && !o.RewriteSupportedNow);
        StructEntropyLogger.Log(
            $"Struct Entropy opportunities: {semanticAllow} semantic-allow, {semanticReject} semantic-reject, " +
            $"{rewriteAllow} rewrite-ready, {rewriteBlocked} rewrite-blocked");

        var semanticReasons = opportunities
            .Where(o => !o.SemanticAllowed)
            .SelectMany(o => o.SemanticReasons)
            .GroupBy(r => r)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal);

        foreach (var reason in semanticReasons)
            StructEntropyLogger.Log($"  semantic-reason[{reason.Key}] = {reason.Count()}");

        var rewriteReasons = opportunities
            .Where(o => o.SemanticAllowed && !o.RewriteSupportedNow)
            .SelectMany(o => o.RewriteBlockers)
            .GroupBy(r => r)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal);

        foreach (var reason in rewriteReasons)
            StructEntropyLogger.Log($"  rewrite-blocker[{reason.Key}] = {reason.Count()}");

        if (verboseLogging)
        {
            var orderedEdges = opportunities
                         .OrderBy(o => o.SourceComponentFullName, StringComparer.Ordinal)
                         .ThenBy(o => o.SourceFieldFullName, StringComparer.Ordinal)
                         .ThenBy(o => o.TargetComponentFullName, StringComparer.Ordinal)
                         .ToList();

            foreach (var edge in orderedEdges.Take(maxEdgesToPrint))
            {
                var shortField = ShortFieldName(edge.SourceFieldFullName);
                var shortTarget = ShortTypeName(edge.TargetComponentFullName);
                var sourceAssemblyText = string.IsNullOrEmpty(edge.SourceAssemblyName) ? "?" : edge.SourceAssemblyName;
                var targetAssemblyText = string.IsNullOrEmpty(edge.TargetAssemblyName) ? "?" : edge.TargetAssemblyName;
                var accessAssemblyText = edge.AccessAssemblyNames == null || edge.AccessAssemblyNames.Count == 0
                    ? "none"
                    : string.Join(", ", edge.AccessAssemblyNames.OrderBy(x => x, StringComparer.Ordinal));
                var capabilityText = edge.RequiredCapabilities.Count == 0
                    ? "none"
                    : string.Join(", ", edge.RequiredCapabilities.OrderBy(c => c.ToString(), StringComparer.Ordinal));
                var semanticReasonText = edge.SemanticReasons.Count == 0
                    ? "none"
                    : string.Join(", ", edge.SemanticReasons.OrderBy(r => r, StringComparer.Ordinal));
                var blockerText = edge.RewriteBlockers.Count == 0
                    ? "none"
                    : string.Join(", ", edge.RewriteBlockers.OrderBy(r => r, StringComparer.Ordinal));
                var status = edge.SemanticAllowed
                    ? (edge.RewriteSupportedNow ? "ALLOW" : "BLOCK")
                    : "REJECT";

                StructEntropyLogger.Log(
                    $"  edge [{status}] {shortField} -> {shortTarget} | srcAsm: {sourceAssemblyText} | tgtAsm: {targetAssemblyText} | accessAsm: {accessAssemblyText} | caps: {capabilityText} | semantic: {semanticReasonText} | blockers: {blockerText}");
            }

            if (orderedEdges.Count > maxEdgesToPrint)
                StructEntropyLogger.Log($"  ... {orderedEdges.Count - maxEdgesToPrint} more edge(s) omitted");
        }
    }

    private static void WriteGraphExportIfEnabled(
        ICompiledAssembly compiledAssembly,
        CandidateAnalysisResult analysis)
    {
        var config = StructEntropyConfigReader.LoadConfig("ProjectSettings/StructEntropyConfig.json");
        if (!config.structEntropyEnabled || !config.structEntropyGraphExportEnabled)
            return;

        string outPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "Logs",
            "StructEntropy_Output",
            compiledAssembly.Name,
            "relocation-opportunities.graphml");

        string dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (GraphMLWriter.WriteFile(analysis.RelocationOpportunities, outPath))
            StructEntropyLogger.Log($"Struct Entropy GraphML written to {outPath}");
        else
            StructEntropyLogger.Log($"WARNING Struct Entropy GraphML export failed: {outPath}");
    }

    private static void WritePlanSummary(StructEntropyPlanner.StructEntropyPlan plan)
    {
        StructEntropyLogger.Log($"Struct Entropy plan: {plan.Moves.Count} approved relocation(s) (seed: {plan.Seed})");
        foreach (var move in plan.Moves
                     .OrderBy(m => m.SourceComponentFullName, StringComparer.Ordinal)
                     .ThenBy(m => m.SourceFieldFullName, StringComparer.Ordinal)
                     .ThenBy(m => m.TargetComponentFullName, StringComparer.Ordinal))
        {
            var caps = move.RequiredCapabilities.Count == 0
                ? "none"
                : string.Join(", ", move.RequiredCapabilities.OrderBy(c => c.ToString(), StringComparer.Ordinal));
            StructEntropyLogger.Log(
                $"  plan {ShortFieldName(move.SourceFieldFullName)} -> {ShortTypeName(move.TargetComponentFullName)} | caps: {caps}");
        }
    }

    private static string ShortTypeName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName))
            return fullName;

        int idx = fullName.LastIndexOf('.');
        return idx >= 0 ? fullName.Substring(idx + 1) : fullName;
    }

    private static string ShortFieldName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName))
            return fullName;

        int idx = fullName.LastIndexOf("::", StringComparison.Ordinal);
        return idx >= 0 ? fullName.Substring(idx + 2) : fullName;
    }
}
