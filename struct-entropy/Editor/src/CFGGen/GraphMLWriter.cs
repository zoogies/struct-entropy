using Mono.Cecil.Cil;
using System.Collections.Generic;
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

        var graph = new XElement(ns + "graph",
            new XAttribute("id", "StructEntropyRelocations"),
            new XAttribute("edgedefault", "directed"));

        foreach (var field in opportunities
                     .Select(o => o.SourceFieldFullName)
                     .Distinct()
                     .OrderBy(x => x, System.StringComparer.Ordinal))
        {
            graph.Add(new XElement(ns + "node",
                new XAttribute("id", BuildNodeId("field", field)),
                new XElement(ns + "data", new XAttribute("key", "nodeKind"), "field"),
                new XElement(ns + "data", new XAttribute("key", "displayName"), ShortFieldName(field)),
                new XElement(ns + "data", new XAttribute("key", "fullName"), field)));
        }

        foreach (var component in opportunities
                     .Select(o => o.TargetComponentFullName)
                     .Distinct()
                     .OrderBy(x => x, System.StringComparer.Ordinal))
        {
            graph.Add(new XElement(ns + "node",
                new XAttribute("id", BuildNodeId("component", component)),
                new XElement(ns + "data", new XAttribute("key", "nodeKind"), "component"),
                new XElement(ns + "data", new XAttribute("key", "displayName"), ShortTypeName(component)),
                new XElement(ns + "data", new XAttribute("key", "fullName"), component)));
        }

        int edgeId = 0;
        foreach (var edge in opportunities
                     .OrderBy(o => o.SourceFieldFullName, System.StringComparer.Ordinal)
                     .ThenBy(o => o.TargetComponentFullName, System.StringComparer.Ordinal))
        {
            graph.Add(new XElement(ns + "edge",
                new XAttribute("id", "e" + edgeId++),
                new XAttribute("source", BuildNodeId("field", edge.SourceFieldFullName)),
                new XAttribute("target", BuildNodeId("component", edge.TargetComponentFullName)),
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
        return new XElement(ns + "key",
            new XAttribute("id",        id),
            new XAttribute("for",       forTarget),
            new XAttribute("attr.name", id),
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
