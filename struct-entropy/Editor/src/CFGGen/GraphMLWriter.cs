using Mono.Cecil.Cil;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

public sealed class GraphMLWriter
{
    public static bool WriteFile(CFG Graph, string path)
    {
        XNamespace ns = "http://graphml.graphdrawing.org/xmlns";

        var graphml = new XElement(ns + "graphml");

        // node keys
        graphml.Add(MakeKey(ns, "instructionCount", "node", "int"));
        graphml.Add(MakeKey(ns, "methodName",       "node", "string"));
        graphml.Add(MakeKey(ns, "declaringType",    "node", "string"));
        graphml.Add(MakeKey(ns, "firstOpcode",      "node", "string"));
        graphml.Add(MakeKey(ns, "lastOpcode",       "node", "string"));
        graphml.Add(MakeKey(ns, "isEntry",          "node", "boolean"));
        graphml.Add(MakeKey(ns, "isExit",           "node", "boolean"));
        graphml.Add(MakeKey(ns, "isDeadBlock",      "node", "boolean"));

        // edge keys
        graphml.Add(MakeKey(ns, "edgeKind", "edge", "string"));

        var graph = new XElement(ns + "graph",
            new XAttribute("id", "G"),
            new XAttribute("edgedefault", "directed"));

        // nodes
        foreach (var block in Graph.blocks)
        {
            // Short label: "MethodName.N (X IL)"
            int lastDot = block.id.LastIndexOf('.');
            string blockIdx = lastDot >= 0 ? block.id.Substring(lastDot + 1) : "0";
            string label = $"{block.methodShortName}.{blockIdx} ({block.instructions.Count} IL)";

            var first = block.instructions.Count > 0 ? block.instructions[0] : null;
            var last  = block.instructions.Count > 0 ? block.instructions[block.instructions.Count - 1] : null;

            string firstOpcode = first?.OpCode.Code.ToString() ?? "";
            string lastOpcode  = last?.OpCode.Code.ToString()  ?? "";

            bool isExit = last != null &&
                (last.OpCode.FlowControl == FlowControl.Return ||
                 last.OpCode.FlowControl == FlowControl.Throw);

            // Dead block detection: unreachable blocks inserted by OpaquePredicate.
            // Matches all dead block shapes (4-5 instructions, ends with br,
            // only operates on locals + constants — no calls or field access).
            bool isDeadBlock = block.instructions.Count >= 4
                && block.instructions.Count <= 5
                && last != null && last.OpCode.FlowControl == FlowControl.Branch
                && IsLocalArithmeticOnly(block.instructions);

            var node = new XElement(ns + "node",
                new XAttribute("id",    block.id),
                new XAttribute("label", label));

            node.Add(Data(ns, "instructionCount", block.instructions.Count));
            node.Add(Data(ns, "methodName",       block.methodShortName));
            node.Add(Data(ns, "declaringType",    block.declaringTypeName));
            node.Add(Data(ns, "firstOpcode",      firstOpcode));
            node.Add(Data(ns, "lastOpcode",       lastOpcode));
            node.Add(Data(ns, "isEntry",          block.isEntry.ToString().ToLower()));
            node.Add(Data(ns, "isExit",           isExit.ToString().ToLower()));
            node.Add(Data(ns, "isDeadBlock",      isDeadBlock.ToString().ToLower()));

            graph.Add(node);
        }

        // edges
        int edgeId = 0;
        foreach (var e in Graph.edges)
        {
            var edgeElem = new XElement(ns + "edge",
                new XAttribute("id",     "e" + edgeId++),
                new XAttribute("source", e.parent.id),
                new XAttribute("target", e.child.id));

            edgeElem.Add(Data(ns, "edgeKind", e.transitionType.ToString()));

            graph.Add(edgeElem);
        }

        graphml.Add(graph);

        try
        {
            var doc = new XDocument(graphml);
            doc.Save(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool WriteFile(CG Graph, string path)
    {
        XNamespace ns = "http://graphml.graphdrawing.org/xmlns";

        var graphml = new XElement(ns + "graphml");

        // node keys
        graphml.Add(MakeKey(ns, "isExternal", "node", "boolean"));
        graphml.Add(MakeKey(ns, "typeName",   "node", "string"));
        graphml.Add(MakeKey(ns, "typeId",     "node", "int"));

        // edge keys
        graphml.Add(MakeKey(ns, "callOpcode", "edge", "string"));
        graphml.Add(MakeKey(ns, "ilOffset",   "edge", "int"));

        var graph = new XElement(ns + "graph",
            new XAttribute("id", "G"),
            new XAttribute("edgedefault", "directed"));

        // build typeId lookup: assign a unique int per distinct typeName
        var typeIdMap = new Dictionary<string, int>();
        foreach (var method in Graph.methods)
        {
            string tn = method.method?.DeclaringType?.Name ?? "";
            if (!typeIdMap.ContainsKey(tn))
                typeIdMap[tn] = typeIdMap.Count;
        }

        // nodes
        foreach (var method in Graph.methods)
        {
            // Short label: "TypeName::MethodName" for internal, full id for external stubs
            string label = method.method != null
                ? $"{method.method.DeclaringType?.Name}::{method.method.Name}"
                : method.id;

            string typeName = method.method?.DeclaringType?.Name ?? "";

            var node = new XElement(ns + "node",
                new XAttribute("id",    method.id),
                new XAttribute("label", label));

            node.Add(Data(ns, "isExternal", method.isExternal.ToString().ToLower()));
            node.Add(Data(ns, "typeName",   typeName));
            node.Add(Data(ns, "typeId",     typeIdMap[typeName]));

            graph.Add(node);
        }

        // edges
        int edgeId = 0;
        foreach (var e in Graph.edges)
        {
            var edgeElem = new XElement(ns + "edge",
                new XAttribute("id",     "e" + edgeId++),
                new XAttribute("source", e.parent.id),
                new XAttribute("target", e.child.id));

            edgeElem.Add(Data(ns, "callOpcode", e.callOpcode.ToString()));
            edgeElem.Add(Data(ns, "ilOffset",   e.ilOffset));

            graph.Add(edgeElem);
        }

        graphml.Add(graph);

        try
        {
            var doc = new XDocument(graphml);
            doc.Save(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool WriteFile(List<RelocationOpportunity> opportunities, string path)
    {
        XNamespace ns = "http://graphml.graphdrawing.org/xmlns";

        var graphml = new XElement(ns + "graphml");

        graphml.Add(MakeNamedKey(ns, "nodeLabel", "node", "label", "string"));
        graphml.Add(MakeNamedKey(ns, "nodeColor", "node", "color", "string"));
        graphml.Add(MakeKey(ns, "size", "node", "double"));
        graphml.Add(MakeKey(ns, "x", "node", "double"));
        graphml.Add(MakeKey(ns, "y", "node", "double"));

        graphml.Add(new XElement(ns + "key",
            new XAttribute("id", "nodeKind"),
            new XAttribute("for", "node"),
            new XAttribute("attr.name", "nodeKind"),
            new XAttribute("attr.type", "string")));

        graphml.Add(new XElement(ns + "key",
            new XAttribute("id", "displayName"),
            new XAttribute("for", "node"),
            new XAttribute("attr.name", "displayName"),
            new XAttribute("attr.type", "string")));

        graphml.Add(new XElement(ns + "key",
            new XAttribute("id", "fullName"),
            new XAttribute("for", "node"),
            new XAttribute("attr.name", "fullName"),
            new XAttribute("attr.type", "string")));

        graphml.Add(new XElement(ns + "key",
            new XAttribute("id", "semanticAllowed"),
            new XAttribute("for", "edge"),
            new XAttribute("attr.name", "semanticAllowed"),
            new XAttribute("attr.type", "boolean")));

        graphml.Add(new XElement(ns + "key",
            new XAttribute("id", "rewriteSupportedNow"),
            new XAttribute("for", "edge"),
            new XAttribute("attr.name", "rewriteSupportedNow"),
            new XAttribute("attr.type", "boolean")));

        graphml.Add(new XElement(ns + "key",
            new XAttribute("id", "requiredCapabilities"),
            new XAttribute("for", "edge"),
            new XAttribute("attr.name", "requiredCapabilities"),
            new XAttribute("attr.type", "string")));

        graphml.Add(new XElement(ns + "key",
            new XAttribute("id", "semanticReasons"),
            new XAttribute("for", "edge"),
            new XAttribute("attr.name", "semanticReasons"),
            new XAttribute("attr.type", "string")));

        graphml.Add(new XElement(ns + "key",
            new XAttribute("id", "rewriteBlockers"),
            new XAttribute("for", "edge"),
            new XAttribute("attr.name", "rewriteBlockers"),
            new XAttribute("attr.type", "string")));

        graphml.Add(new XElement(ns + "key",
            new XAttribute("id", "sourceAssembly"),
            new XAttribute("for", "edge"),
            new XAttribute("attr.name", "sourceAssembly"),
            new XAttribute("attr.type", "string")));

        graphml.Add(new XElement(ns + "key",
            new XAttribute("id", "targetAssembly"),
            new XAttribute("for", "edge"),
            new XAttribute("attr.name", "targetAssembly"),
            new XAttribute("attr.type", "string")));

        graphml.Add(new XElement(ns + "key",
            new XAttribute("id", "accessAssemblies"),
            new XAttribute("for", "edge"),
            new XAttribute("attr.name", "accessAssemblies"),
            new XAttribute("attr.type", "string")));

        graphml.Add(MakeNamedKey(ns, "edgeLabel", "edge", "label", "string"));
        graphml.Add(MakeKey(ns, "weight", "edge", "double"));
        graphml.Add(MakeNamedKey(ns, "edgeColor", "edge", "color", "string"));
        graphml.Add(MakeKey(ns, "approvedCategory", "edge", "string"));
        graphml.Add(MakeKey(ns, "relocationStatus", "edge", "string"));

        var graph = new XElement(ns + "graph",
            new XAttribute("id", "StructEntropyRelocations"),
            new XAttribute("edgedefault", "directed"));

        var fields = opportunities
            .Select(o => o.SourceFieldFullName)
            .Distinct()
            .OrderBy(x => x, System.StringComparer.Ordinal)
            .ToList();

        var components = opportunities
            .Select(o => o.TargetComponentFullName)
            .Distinct()
            .OrderBy(x => x, System.StringComparer.Ordinal)
            .ToList();

        for (int i = 0; i < fields.Count; i++)
        {
            string field = fields[i];
            string label = ShortFieldName(field);

            graph.Add(new XElement(ns + "node",
                new XAttribute("id", BuildNodeId("field", field)),
                Data(ns, "nodeLabel", label),
                Data(ns, "nodeColor", "#d6604d"),
                Data(ns, "size", "18"),
                Data(ns, "x", "-400"),
                Data(ns, "y", GraphLayoutY(i, fields.Count)),
                new XElement(ns + "data", new XAttribute("key", "nodeKind"), "field"),
                new XElement(ns + "data", new XAttribute("key", "displayName"), label),
                new XElement(ns + "data", new XAttribute("key", "fullName"), field)));
        }

        for (int i = 0; i < components.Count; i++)
        {
            string component = components[i];
            string label = ShortTypeName(component);

            graph.Add(new XElement(ns + "node",
                new XAttribute("id", BuildNodeId("component", component)),
                Data(ns, "nodeLabel", label),
                Data(ns, "nodeColor", "#4393c3"),
                Data(ns, "size", "22"),
                Data(ns, "x", "400"),
                Data(ns, "y", GraphLayoutY(i, components.Count)),
                new XElement(ns + "data", new XAttribute("key", "nodeKind"), "component"),
                new XElement(ns + "data", new XAttribute("key", "displayName"), label),
                new XElement(ns + "data", new XAttribute("key", "fullName"), component)));
        }

        int edgeId = 0;
        foreach (var edge in opportunities
                     .OrderBy(o => o.SourceFieldFullName, System.StringComparer.Ordinal)
                     .ThenBy(o => o.TargetComponentFullName, System.StringComparer.Ordinal))
        {
            string edgeLabel = EdgeLabel(edge);

            graph.Add(new XElement(ns + "edge",
                new XAttribute("id", "e" + edgeId++),
                new XAttribute("source", BuildNodeId("field", edge.SourceFieldFullName)),
                new XAttribute("target", BuildNodeId("component", edge.TargetComponentFullName)),
                Data(ns, "edgeLabel", edgeLabel),
                Data(ns, "weight", edge.SemanticAllowed && edge.RewriteSupportedNow ? "4" : "1.5"),
                Data(ns, "edgeColor", EdgeColor(edge)),
                Data(ns, "approvedCategory", edge.SemanticAllowed && edge.RewriteSupportedNow ? "approved" : "blocked"),
                Data(ns, "relocationStatus", RelocationStatus(edge)),
                new XElement(ns + "data", new XAttribute("key", "semanticAllowed"), edge.SemanticAllowed.ToString().ToLowerInvariant()),
                new XElement(ns + "data", new XAttribute("key", "rewriteSupportedNow"), edge.RewriteSupportedNow.ToString().ToLowerInvariant()),
                new XElement(ns + "data", new XAttribute("key", "requiredCapabilities"),
                    string.Join("|", edge.RequiredCapabilities.OrderBy(c => c.ToString(), System.StringComparer.Ordinal))),
                new XElement(ns + "data", new XAttribute("key", "semanticReasons"),
                    string.Join("|", edge.SemanticReasons.OrderBy(r => r, System.StringComparer.Ordinal))),
                new XElement(ns + "data", new XAttribute("key", "rewriteBlockers"),
                    string.Join("|", edge.RewriteBlockers.OrderBy(r => r, System.StringComparer.Ordinal))),
                new XElement(ns + "data", new XAttribute("key", "sourceAssembly"), edge.SourceAssemblyName ?? string.Empty),
                new XElement(ns + "data", new XAttribute("key", "targetAssembly"), edge.TargetAssemblyName ?? string.Empty),
                new XElement(ns + "data", new XAttribute("key", "accessAssemblies"),
                    string.Join("|", (edge.AccessAssemblyNames ?? new HashSet<string>()).OrderBy(x => x, System.StringComparer.Ordinal)))));
        }

        graphml.Add(graph);

        try
        {
            new XDocument(graphml).Save(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GraphLayoutY(int index, int count)
    {
        double centered = index - ((count - 1) / 2.0);
        return (centered * 90.0).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string EdgeLabel(RelocationOpportunity edge)
    {
        if (edge.SemanticAllowed && edge.RewriteSupportedNow)
            return "allowed";
        if (edge.SemanticAllowed)
            return "rewrite blocked";
        return "semantic blocked";
    }

    private static string RelocationStatus(RelocationOpportunity edge)
    {
        if (edge.SemanticAllowed && edge.RewriteSupportedNow)
            return "approved";
        if (edge.SemanticAllowed)
            return "rewrite_blocked";
        return "semantic_blocked";
    }

    private static string EdgeColor(RelocationOpportunity edge)
    {
        if (edge.SemanticAllowed && edge.RewriteSupportedNow)
            return "#1a9850";
        if (edge.SemanticAllowed)
            return "#fdae61";
        return "#d73027";
    }

    private static string BuildNodeId(string prefix, string value)
    {
        using (var sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
            var builder = new StringBuilder(prefix.Length + 1 + (hash.Length * 2));
            builder.Append(prefix);
            builder.Append('_');
            foreach (byte b in hash)
                builder.Append(b.ToString("x2"));
            return builder.ToString();
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

        int idx = fullName.LastIndexOf("::", System.StringComparison.Ordinal);
        return idx >= 0 ? fullName.Substring(idx + 2) : fullName;
    }

    private static XElement MakeKey(XNamespace ns, string id, string forTarget, string attrType)
    {
        return MakeNamedKey(ns, id, forTarget, id, attrType);
    }

    private static XElement MakeNamedKey(XNamespace ns, string id, string forTarget, string attrName, string attrType)
    {
        return new XElement(ns + "key",
            new XAttribute("id", id),
            new XAttribute("for", forTarget),
            new XAttribute("attr.name", attrName),
            new XAttribute("attr.type", attrType));
    }

    private static XElement Data(XNamespace ns, string key, object value)
    {
        return new XElement(ns + "data",
            new XAttribute("key", key),
            value);
    }

    /// <summary>
    /// Returns true if every instruction in the block (except the final branch)
    /// is a local variable load/store, constant load, or arithmetic/bitwise op.
    /// This identifies dead blocks inserted by OpaquePredicate regardless of shape.
    /// </summary>
    private static bool IsLocalArithmeticOnly(IList<Instruction> instructions)
    {
        // Check all instructions except the last (which is the br we already verified)
        for (int i = 0; i < instructions.Count - 1; i++)
        {
            var code = instructions[i].OpCode.Code;
            switch (code)
            {
                // local loads/stores
                case Code.Ldloc:
                case Code.Ldloc_S:
                case Code.Ldloc_0:
                case Code.Ldloc_1:
                case Code.Ldloc_2:
                case Code.Ldloc_3:
                case Code.Stloc:
                case Code.Stloc_S:
                case Code.Stloc_0:
                case Code.Stloc_1:
                case Code.Stloc_2:
                case Code.Stloc_3:
                // constant loads
                case Code.Ldc_I4:
                case Code.Ldc_I4_S:
                case Code.Ldc_I4_0:
                case Code.Ldc_I4_1:
                case Code.Ldc_I4_2:
                case Code.Ldc_I4_3:
                case Code.Ldc_I4_4:
                case Code.Ldc_I4_5:
                case Code.Ldc_I4_6:
                case Code.Ldc_I4_7:
                case Code.Ldc_I4_8:
                case Code.Ldc_I4_M1:
                // arithmetic / bitwise
                case Code.Add:
                case Code.Sub:
                case Code.Mul:
                case Code.Xor:
                case Code.Or:
                case Code.And:
                case Code.Neg:
                case Code.Not:
                case Code.Shl:
                case Code.Shr:
                case Code.Shr_Un:
                    break;
                default:
                    return false;
            }
        }
        return true;
    }
}
