using System;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Pdb;
using Unity.CompilationPipeline.Common.ILPostProcessing;

/// <summary>
/// Reusable assembly processing infrastructure for IL post-processing.
/// Handles assembly loading with symbols and resolver, and writing with regenerated symbols.
/// </summary>
public class StructEntropyAssemblyProcessor
{
    private readonly ICompiledAssembly _compiledAssembly;
    private readonly byte[] _assemblyData;
    private readonly byte[] _pdbData;
    private readonly bool _hasSymbols;

    public StructEntropyAssemblyProcessor(byte[] assemblyData, byte[] pdbData, ICompiledAssembly compiledAssembly)
    {
        _assemblyData = assemblyData;
        _pdbData = pdbData;
        _compiledAssembly = compiledAssembly;
        _hasSymbols = pdbData != null && pdbData.Length > 0;
    }

    /// <summary>
    /// Loads the assembly with proper resolver and symbol support.
    /// The caller is responsible for disposing the returned AssemblyDefinition and resolver.
    /// </summary>
    public (AssemblyDefinition assembly, PostProcessorAssemblyResolver resolver) LoadAssembly()
    {
        var scriptAssembliesDir = Path.Combine(Directory.GetCurrentDirectory(), "Library", "ScriptAssemblies");
        var resolver = new PostProcessorAssemblyResolver(_compiledAssembly, scriptAssembliesDir);

        var readerParameters = new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadSymbols = _hasSymbols,
            SymbolStream = _hasSymbols ? new MemoryStream(_pdbData) : null,
            SymbolReaderProvider = _hasSymbols ? new PortablePdbReaderProvider() : null
        };

        var memoryStream = new MemoryStream(_assemblyData);
        var assemblyDefinition = AssemblyDefinition.ReadAssembly(memoryStream, readerParameters);

        return (assemblyDefinition, resolver);
    }

    /// <summary>
    /// Writes the modified assembly with regenerated symbols.
    /// </summary>
    public (byte[] assemblyData, byte[] pdbData) WriteAssembly(AssemblyDefinition assemblyDefinition)
    {
        using (var assemblyStream = new MemoryStream())
        using (var symbolWriteStream = new MemoryStream())
        {
            var writerParameters = new WriterParameters
            {
                WriteSymbols = _hasSymbols,
                SymbolStream = symbolWriteStream,
                SymbolWriterProvider = _hasSymbols ? new PortablePdbWriterProvider() : null
            };

            assemblyDefinition.Write(assemblyStream, writerParameters);

            return (assemblyStream.ToArray(), _hasSymbols ? symbolWriteStream.ToArray() : _pdbData);
        }
    }

    /// <summary>
    /// Helper method to process an assembly with a transformation action.
    /// Handles loading, transformation, and writing automatically.
    /// </summary>
    public (byte[] assemblyData, byte[] pdbData) ProcessAssembly(Action<AssemblyDefinition> transformAction)
    {
        var (assembly, resolver) = LoadAssembly();

        try
        {
            transformAction(assembly);
            return WriteAssembly(assembly);
        }
        finally
        {
            assembly?.Dispose();
            resolver?.Dispose();
        }
    }
}
