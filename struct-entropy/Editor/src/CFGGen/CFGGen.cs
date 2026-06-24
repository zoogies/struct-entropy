using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Unity.CompilationPipeline.Common.ILPostProcessing;

public enum EdgeKind
{
    Fallthrough,
    BranchTaken,
    SwitchCase,
    Exception
}

public sealed class CFG
{
    public List<BasicBlock> blocks = new List<BasicBlock>();
    public List<Edge> edges = new List<Edge>();
}

public sealed class Edge
{
    public EdgeKind transitionType;

    public BasicBlock parent;
    public BasicBlock child;
}

// Abstraction, subset of method instructions (isolated out control flow branches)
public sealed class BasicBlock
{
    public string id;
    public Collection<Instruction> instructions = new();

    public string methodShortName  = "";   // e.g. "OnUpdate"  — for Gephi partitioning
    public string declaringTypeName = "";  // e.g. "MySystem"  — for Gephi partitioning
    public bool isEntry;                   // true for the first block of its method

    public BasicBlock(string id)
    {
        this.id = id;
    }
}

public sealed class CFGGen
{
    private static int _idCounter = 0;

    public static CFG GenerateMethodCFG(MethodDefinition method)
    {
        _idCounter = 0;

        var CFG = new CFG();

        // instructions which begin new blocks
        HashSet<Instruction> leaders = new();

        if (!method.HasBody) return CFG; // some sort of extern, has no IL to generate blocks from
        if (method.Body.Instructions.Count == 0) return CFG;
        // TODO: can we throw something here? return tuple with status? (maybe empty CFG is enough)

        leaders.Add(method.Body.Instructions[0]); // first instruction is always a leader

        foreach (var instruction in method.Body.Instructions)
        {
            switch (instruction.OpCode.FlowControl)
            {
                // add branch targets as leaders
                case FlowControl.Branch:
                    if (instruction.Operand is Instruction brTarget)
                        leaders.Add(brTarget);

                    if (instruction.Next != null)
                        leaders.Add(instruction.Next);

                    break;
                case FlowControl.Cond_Branch:
                    if (instruction.OpCode.Code != Code.Switch)
                    {
                        if (instruction.Operand is Instruction condTarget)
                            leaders.Add(condTarget); // if/elif
                    }
                    else // switches have many targets
                    {
                        foreach (var target in (Instruction[])instruction.Operand)
                        {
                            leaders.Add(target);
                        }
                    }

                    if (instruction.Next != null)
                        leaders.Add(instruction.Next);

                    break;

                // terminal instructions
                case FlowControl.Return:
                case FlowControl.Throw:
                    break;

                // leave/leave.s instructions
                case FlowControl.Meta:
                    if (instruction.OpCode.Code == Code.Leave || instruction.OpCode.Code == Code.Leave_S)
                    {
                        if (instruction.Operand is Instruction leaveTarget)
                            leaders.Add(leaveTarget);
                        if (instruction.Next != null)
                            leaders.Add(instruction.Next);
                    }
                    break;

                // Instructions that do not generate leaders
                case FlowControl.Next:
                case FlowControl.Call:
                case FlowControl.Phi:
                case FlowControl.Break:
                    break;
                default:
                    break;
            }
        }

        // exception handler starts are always targets
        foreach (var handler in method.Body.ExceptionHandlers)
        {
            if (handler.HandlerStart != null)
                leaders.Add(handler.HandlerStart);
            if (handler.TryStart != null)
                leaders.Add(handler.TryStart);
            if (handler.FilterStart != null)
                leaders.Add(handler.FilterStart);
        }

        Dictionary<Instruction, BasicBlock> map = new(); // O(1) lookup instruction -> BB

        BasicBlock current = null;
        bool firstBlock = true;

        // build map of instructions to blocks
        foreach (var instruction in method.Body.Instructions)
        {
            if (leaders.Contains(instruction))
            {
                current = new($"{method.FullName}.{_idCounter++}")
                {
                    methodShortName   = method.Name,
                    declaringTypeName = method.DeclaringType?.Name ?? "",
                    isEntry           = firstBlock
                };
                CFG.blocks.Add(current);
                firstBlock = false;
            }
            map[instruction] = current;
            current.instructions.Add(instruction);
        }

        // build edges
        foreach (var block in CFG.blocks)
        {
            if (block.instructions.Count == 0)
                continue;

            var last = block.instructions.Last();

            switch (last.OpCode.FlowControl)
            {
                // unconditional branch, including leave
                case FlowControl.Branch:
                    if (last.Operand is Instruction brEdgeTarget && map.ContainsKey(brEdgeTarget))
                        CFG.edges.Add(new Edge
                        {
                            parent = block,
                            child = map[brEdgeTarget],
                            transitionType = EdgeKind.BranchTaken
                        });
                    break;

                // conditional branch, switch
                case FlowControl.Cond_Branch:
                    if (last.OpCode.Code == Code.Switch)
                    {
                        if (last.Operand is Instruction[] switchTargets)
                            foreach (var target in switchTargets)
                                if (target != null && map.ContainsKey(target))
                                    CFG.edges.Add(new Edge
                                    {
                                        parent = block,
                                        child = map[target],
                                        transitionType = EdgeKind.SwitchCase
                                    });

                        // fallthrough to next instruction
                        if (last.Next != null && map.ContainsKey(last.Next))
                            CFG.edges.Add(new Edge
                            {
                                parent = block,
                                child = map[last.Next],
                                transitionType = EdgeKind.Fallthrough
                            });
                    }
                    else
                    {
                        // branch taken
                        if (last.Operand is Instruction condEdgeTarget && map.ContainsKey(condEdgeTarget))
                            CFG.edges.Add(new Edge
                            {
                                parent = block,
                                child = map[condEdgeTarget],
                                transitionType = EdgeKind.BranchTaken
                            });

                        // fallthrough
                        if (last.Next != null && map.ContainsKey(last.Next))
                            CFG.edges.Add(new Edge
                            {
                                parent = block,
                                child = map[last.Next],
                                transitionType = EdgeKind.Fallthrough
                            });
                    }
                    break;

                /*
                 *   WARNING: TODO:
                 *   This implementation is treating leave/leave.s as a simple branch.
                 *   
                 *   Full leave semantics are hard because control must pass through all
                 *   enclosing finally blocks before reaching the target.
                 */
                case FlowControl.Meta: // leave is FlowControl.Meta in Cecil
                    if (last.OpCode.Code == Code.Leave || last.OpCode.Code == Code.Leave_S)
                    {
                        if (last.Operand is Instruction leaveEdgeTarget && map.ContainsKey(leaveEdgeTarget))
                            CFG.edges.Add(new Edge
                            {
                                parent = block,
                                child = map[leaveEdgeTarget],
                                transitionType = EdgeKind.BranchTaken
                            });
                    }
                    break;

                // terminal instructions
                case FlowControl.Return:
                case FlowControl.Throw:
                    break;

                // everything else: linear fallthrough
                default:
                    if (last.Next != null)
                        CFG.edges.Add(new Edge
                        {
                            parent = block,
                            child = map[last.Next],
                            transitionType = EdgeKind.Fallthrough
                        });
                    break;
            }
        }

        // handle exception edges (conservative approach: add edges from every block in region to handler)
        foreach (var handler in method.Body.ExceptionHandlers)
        {
            if (handler.HandlerStart == null)
                continue;

            var handlerBlock = map[handler.HandlerStart];
            HashSet<BasicBlock> processedBlocks = new HashSet<BasicBlock>();

            // try/catch
            if (handler.TryStart != null && handler.TryEnd != null)
            {
                var tryStartIdx = method.Body.Instructions.IndexOf(handler.TryStart);
                var tryEndIdx = method.Body.Instructions.IndexOf(handler.TryEnd);

                for (int i = tryStartIdx; i < tryEndIdx && i < method.Body.Instructions.Count; i++)
                {
                    var block = map[method.Body.Instructions[i]];
                    
                    if (!processedBlocks.Contains(block))
                    {
                        CFG.edges.Add(new Edge
                        {
                            parent = block,
                            child = handlerBlock,
                            transitionType = EdgeKind.Exception
                        });
                        processedBlocks.Add(block);
                    }
                }
            }

            // filter
            if (handler.FilterStart != null)
            {
                var filterBlock = map[handler.FilterStart];
                CFG.edges.Add(new Edge
                {
                    parent = filterBlock,
                    child = handlerBlock,
                    transitionType = EdgeKind.Exception
                });
            }
        }

        return CFG;
    }

    private static void ProcessTypeAndNestedTypes(TypeDefinition type, CFG assemblyCFG)
    {
        foreach (var method in type.Methods)
        {
            var methodCFG = GenerateMethodCFG(method);
            assemblyCFG.blocks.AddRange(methodCFG.blocks);
            assemblyCFG.edges.AddRange(methodCFG.edges);
        }

        foreach (var nestedType in type.NestedTypes)
        {
            ProcessTypeAndNestedTypes(nestedType, assemblyCFG);
        }
    }

    public static CFG GenerateAssemblyCFG(AssemblyDefinition assembly)
    {
        var CFG = new CFG();

        foreach (var module in assembly.Modules)
        {
            foreach (var type in module.Types)
            {
                ProcessTypeAndNestedTypes(type, CFG);
            }
        }

        return CFG;
    }

    public static CFG GenerateCFG(byte[] assemblyData, byte[] pdbData, ICompiledAssembly compiledAssembly)
    {
        var processor = new StructEntropyAssemblyProcessor(assemblyData, pdbData, compiledAssembly);
        var (assembly, resolver) = processor.LoadAssembly();
        try
        {
            return GenerateAssemblyCFG(assembly);
        }
        finally
        {
            resolver?.Dispose();
        }
    }

    public static void ProcessAndWriteCFG(AssemblyDefinition assembly, ICompiledAssembly compiledAssembly, StructEntropy.Runtime.StructEntropyConfig config, string slot = "post")
    {
        string outputDir = System.IO.Path.Combine("Logs", "CFG_Output", compiledAssembly.Name, slot);
        System.IO.Directory.CreateDirectory(outputDir);

        if (config.cfgMode == StructEntropy.Runtime.CFGMode.WholeAssembly)
        {
            var cfg = GenerateAssemblyCFG(assembly);
            string outputPath = System.IO.Path.Combine(outputDir, "out.graphml");
            GraphMLWriter.WriteFile(cfg, outputPath);
            StructEntropyLogger.Log($"Assembly CFG ({slot}) written to: {outputPath}");
        }
        else
        {
            foreach (var module in assembly.Modules)
            {
                foreach (var type in module.Types)
                {
                    foreach (var method in type.Methods)
                    {
                        if (method.HasBody)
                        {
                            var cfg = GenerateMethodCFG(method);
                            string fileName = $"{method.FullName.Replace(" ", "_").Replace("<", "_").Replace(">", "_").Replace("/", "_").Replace("::", "_")}.graphml";
                            string outputPath = System.IO.Path.Combine(outputDir, fileName);
                            GraphMLWriter.WriteFile(cfg, outputPath);
                        }
                    }
                }
            }
            StructEntropyLogger.Log($"Per-method CFGs ({slot}) written to: {outputDir}");
        }
    }
}
