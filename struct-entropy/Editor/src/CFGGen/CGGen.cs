using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Collections.Generic;
using System.Linq;
using Unity.CompilationPipeline.Common.ILPostProcessing;

public sealed class CG
{
    public List<CGNode> methods = new List<CGNode>();
    public List<CGEdge> edges = new List<CGEdge>();
}

public sealed class CGEdge
{
    public Code callOpcode;
    public int ilOffset;
    public List<TypeReference> genericArgs;
    public CGNode parent;
    public CGNode child;
}

public sealed class CGNode
{
    public string id;
    public MethodDefinition method; // null if external
    public bool isExternal;
    public CGNode(string id, MethodDefinition method, bool isExternal = false)
    {
        this.id = id;
        this.method = method;
        this.isExternal = isExternal;
    }
}

public sealed class CGGen
{
    public static CG GenerateAssemblyCG(AssemblyDefinition assembly)
    {
        var cg = new CG();
        var dict = new Dictionary<string, CGNode>();

        // nodes (all types including nested)
        foreach (var module in assembly.Modules)
            foreach (var type in GetAllTypes(module))
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    var node = new CGNode(method.FullName, method, isExternal: false);
                    cg.methods.Add(node);
                    dict[method.FullName] = node;
                }

        // edges
        foreach (var module in assembly.Modules)
            foreach (var type in GetAllTypes(module))
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    if (!dict.TryGetValue(method.FullName, out var callerNode)) continue;

                    foreach (var instruction in method.Body.Instructions)
                    {
                        switch (instruction.OpCode.Code)
                        {
                            case Code.Call:
                            case Code.Callvirt:
                            case Code.Newobj:
                                var operandMethod = instruction.Operand as MethodReference;
                                if (operandMethod == null) continue;

                                string fullName = operandMethod.FullName;

                                if (!dict.TryGetValue(fullName, out var calleeNode))
                                {
                                    // external — resolve if possible, create stub node
                                    // TODO: can we resolve inter-dependent assemblies?
                                    //       - might not be useful
                                    MethodDefinition resolved = null;
                                    try { resolved = operandMethod.Resolve(); } catch { }
                                    calleeNode = new CGNode(fullName, resolved, isExternal: true);
                                    dict[fullName] = calleeNode;
                                    cg.methods.Add(calleeNode);
                                }

                                var edge = new CGEdge
                                {
                                    callOpcode = instruction.OpCode.Code,
                                    ilOffset = instruction.Offset,
                                    parent = callerNode,
                                    child = calleeNode,
                                    genericArgs = null
                                };

                                if (operandMethod is GenericInstanceMethod gim && gim.GenericArguments.Count > 0)
                                    edge.genericArgs = gim.GenericArguments.ToList();

                                cg.edges.Add(edge);
                                break;
                        }
                    }
                }

        StructEntropyLogger.Log($"Generated CG with {cg.methods.Count} nodes and {cg.edges.Count} edges");
        return cg;
    }

    private static IEnumerable<TypeDefinition> GetAllTypes(ModuleDefinition module)
    {
        foreach (var type in module.Types)
            foreach (var t in Flatten(type))
                yield return t;
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes)
            foreach (var t in Flatten(nested))
                yield return t;
    }

    public static void ProcessAndWriteCG(AssemblyDefinition assembly, ICompiledAssembly compiledAssembly, string slot = "post")
    {
        string outputDir = System.IO.Path.Combine("Logs", "CG_Output", compiledAssembly.Name, slot);
        System.IO.Directory.CreateDirectory(outputDir);
        var cg = GenerateAssemblyCG(assembly);
        string outputPath = System.IO.Path.Combine(outputDir, "out.graphml");
        GraphMLWriter.WriteFile(cg, outputPath);
        StructEntropyLogger.Log($"Assembly CG ({slot}) written to: {outputPath}");
    }
}