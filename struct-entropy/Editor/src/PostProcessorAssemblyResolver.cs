using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Mono.Cecil;
using Unity.CompilationPipeline.Common.ILPostProcessing;

// Mostly written by Claude

/// <summary>
/// Assembly resolver for Unity IL post-processing.
/// Allows Mono.Cecil to find and load Unity assemblies during IL processing.
/// Optionally accepts a fallback directory (e.g. Library/ScriptAssemblies) for
/// editor-only assemblies that are not present in the runtime reference list.
/// </summary>
public class PostProcessorAssemblyResolver : IAssemblyResolver
{
    private readonly string[] _assemblyReferences;
    private readonly Dictionary<string, AssemblyDefinition> _cache = new Dictionary<string, AssemblyDefinition>();
    private readonly ICompiledAssembly _compiledAssembly;
    private readonly string _fallbackDirectory; // optional, null = no fallback

    public PostProcessorAssemblyResolver(ICompiledAssembly compiledAssembly, string fallbackDirectory = null)
    {
        _compiledAssembly = compiledAssembly;
        _assemblyReferences = compiledAssembly.References;
        _fallbackDirectory = fallbackDirectory;
    }

    public void Dispose()
    {
        foreach (var assembly in _cache.Values)
            assembly.Dispose();
        _cache.Clear();
    }

    public AssemblyDefinition Resolve(AssemblyNameReference name)
        => Resolve(name, new ReaderParameters());

    public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        if (_cache.TryGetValue(name.FullName, out var cached))
            return cached;

        // Primary: search the runtime reference list by assembly name
        var assemblyPath = _assemblyReferences
            .FirstOrDefault(r => Path.GetFileNameWithoutExtension(r) == name.Name);

        // Secondary: search the fallback directory if one was provided
        if (assemblyPath == null && _fallbackDirectory != null)
        {
            if (assemblyPath == null && _fallbackDirectory != null)
{
            // Handle legacy / mismatched assembly names
            var requestedName = name.Name;
            if (requestedName == "StructEntropyRuntime")
                requestedName = "StructEntropy.Runtime";

            var candidate = Path.Combine(_fallbackDirectory, requestedName + ".dll");
            StructEntropyLogger.Log($"[Resolver] Trying fallback candidate: {candidate}");

            if (File.Exists(candidate))
                assemblyPath = candidate;
        }
        }

        if (assemblyPath != null)
        {
            var assembly = AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters { AssemblyResolver = this });
            _cache[name.FullName] = assembly;
            return assembly;
        }

        throw new AssemblyResolutionException(name);
    }

    /// <summary>
    /// Attempts to locate and load Assembly-CSharp-Editor.dll from
    /// Library/ScriptAssemblies, which is not present in runtime references.
    /// Returns null (with a log message) instead of throwing if anything goes wrong.
    /// The returned AssemblyDefinition should be disposed by the caller.
    /// </summary>
    public static AssemblyDefinition TryLoadEditorAssembly(ICompiledAssembly compiledAssembly)
    {
        // ILPP runs with the project root as CWD � ScriptAssemblies is always here
        var scriptAssembliesDir = Path.GetFullPath(Path.Combine("Library", "ScriptAssemblies"));

        if (!Directory.Exists(scriptAssembliesDir))
        {
            StructEntropyLogger.Log($"Library/ScriptAssemblies not found at {scriptAssembliesDir} � skipping");
            return null;
        }

        var editorPath = Path.Combine(scriptAssembliesDir, "Assembly-CSharp-Editor.dll");
        if (!File.Exists(editorPath))
        {
            StructEntropyLogger.Log("Assembly-CSharp-Editor.dll not found (no editor code yet?) � skipping");
            return null;
        }

        var resolver = new PostProcessorAssemblyResolver(compiledAssembly, scriptAssembliesDir);
        try
        {
            return AssemblyDefinition.ReadAssembly(editorPath, new ReaderParameters
            {
                AssemblyResolver = resolver,
                ReadingMode = ReadingMode.Deferred
            });
        }
        catch (Exception ex)
        {
            StructEntropyLogger.Log($"Failed to load editor assembly � {ex.Message}");
            return null;
        }
    }
}