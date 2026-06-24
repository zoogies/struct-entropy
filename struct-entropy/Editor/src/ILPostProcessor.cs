using System;
using System.IO;
using System.Diagnostics;
using Unity.CompilationPipeline.Common.Diagnostics;
using Unity.CompilationPipeline.Common.ILPostProcessing;
using StructEntropy.Runtime;
using StructEntropy.Editor;

/// <summary>
/// Unity ILPP entry point. Pass order (each reads previous pass's output bytes):
///   1. Method Wrapping → 2. Symbol Mangling → 3. Struct Entropy →
///   4. Field Encoding → 5. Control Flow Obfuscation → 6. Exports → 7. Audit
/// Editor compiles skip IL modification (mapping-only mode).
/// </summary>
public sealed class StructEntropyILPostProcessor : ILPostProcessor
{
    private static StructEntropyConfig cachedWillProcessConfig;

    public override ILPostProcessor GetInstance() => this;

    public override bool WillProcess(ICompiledAssembly compiledAssembly)
    {
        if (cachedWillProcessConfig == null)
            cachedWillProcessConfig = StructEntropyConfigReader.LoadConfig("ProjectSettings/StructEntropyConfig.json");

        // If target assemblies are configured, use them
        if (cachedWillProcessConfig.targetAssemblies != null && cachedWillProcessConfig.targetAssemblies.Count > 0)
            return cachedWillProcessConfig.targetAssemblies.Contains(compiledAssembly.Name);

        // Fallback: original Assembly-CSharp behavior
        return compiledAssembly.Name.StartsWith("Assembly-CSharp") &&
            !compiledAssembly.Name.StartsWith("Assembly-CSharp-Editor");
    }


    public ILPostProcessResult ProcessUnsafe(ICompiledAssembly compiledAssembly) {
        StructEntropyLogger.Log($"Processing: {compiledAssembly.Name}");

        var config = LoadConfig();
        var totalTimer = Stopwatch.StartNew();
        StructEntropyLogger.Log($"Config - StructEntropy: {config.structEntropyEnabled} (seed: {config.structEntropySeed}, field relocation probability: {config.structEntropyFieldRelocationProbability:0.###}), CFGGen: {config.cfgGenerationEnabled}, CGGen: {config.cgGenerationEnabled}, BenchmarkLogging: {config.benchmarkLoggingEnabled}");

        byte[] assemblyData = compiledAssembly.InMemoryAssembly.PeData;
        byte[] pdbData = compiledAssembly.InMemoryAssembly.PdbData;

        byte[] currentAssemblyData = assemblyData;
        byte[] currentPdbData = pdbData;

        // Snapshot original bytes for Pre/Both exports before any passes mutate them
        bool needsPre = (config.cfgGenerationEnabled && config.cfgTiming != ExportTiming.Post)
                     || (config.cgGenerationEnabled  && config.cgTiming  != ExportTiming.Post)
                     || (config.ilDumpEnabled         && config.ilDumpTiming != ExportTiming.Post);

        if (needsPre)
            RunExports(config, compiledAssembly, currentAssemblyData, currentPdbData, "pre");

        // Struct entropy (must run before field encoding so IL patterns are clean)
        if (config.structEntropyEnabled)
        {
            (currentAssemblyData, currentPdbData) = MeasurePass(
                config,
                compiledAssembly.Name,
                "StructEntropy",
                currentAssemblyData,
                currentPdbData,
                () => StructEntropyPass.Process(currentAssemblyData, currentPdbData, compiledAssembly));
        }

        // Post-instrumentation exports
        bool needsPost = (config.cfgGenerationEnabled && config.cfgTiming != ExportTiming.Pre)
                      || (config.cgGenerationEnabled  && config.cgTiming  != ExportTiming.Pre)
                      || (config.ilDumpEnabled         && config.ilDumpTiming != ExportTiming.Pre);

        if (needsPost)
            RunExports(config, compiledAssembly, currentAssemblyData, currentPdbData, "post");

        totalTimer.Stop();
        if (config.benchmarkLoggingEnabled)
        {
            LogBenchmark(
                compiledAssembly.Name,
                "ILPostProcessor.Total",
                totalTimer.ElapsedMilliseconds,
                assemblyData.Length,
                currentAssemblyData.Length,
                pdbData?.Length ?? 0,
                currentPdbData?.Length ?? 0);
        }

        // return combined pass output:
        return new ILPostProcessResult(
            new InMemoryAssembly(currentAssemblyData, currentPdbData),
            StructEntropyLogger.GetDiagnosticsSnapshot()
        );
    }

    private static (byte[] assemblyData, byte[] pdbData) MeasurePass(
        StructEntropyConfig config,
        string assemblyName,
        string passName,
        byte[] beforeAssemblyData,
        byte[] beforePdbData,
        Func<(byte[] assemblyData, byte[] pdbData)> action)
    {
        var timer = Stopwatch.StartNew();
        var result = action();
        timer.Stop();

        if (config.benchmarkLoggingEnabled)
        {
            LogBenchmark(
                assemblyName,
                passName,
                timer.ElapsedMilliseconds,
                beforeAssemblyData?.Length ?? 0,
                result.assemblyData?.Length ?? 0,
                beforePdbData?.Length ?? 0,
                result.pdbData?.Length ?? 0);
        }

        return result;
    }

    private static void LogBenchmark(
        string assemblyName,
        string passName,
        long elapsedMs,
        int beforeAssemblyBytes,
        int afterAssemblyBytes,
        int beforePdbBytes,
        int afterPdbBytes)
    {
        int assemblyDelta = afterAssemblyBytes - beforeAssemblyBytes;
        int pdbDelta = afterPdbBytes - beforePdbBytes;

        StructEntropyLogger.LogBenchmark(
            $"{assemblyName} :: {passName} :: {elapsedMs} ms :: " +
            $"AssemblyBytes {beforeAssemblyBytes} -> {afterAssemblyBytes} ({assemblyDelta:+#;-#;0}), " +
            $"PdbBytes {beforePdbBytes} -> {afterPdbBytes} ({pdbDelta:+#;-#;0})");
    }

    /// <summary>
    /// Runs enabled static analysis exports (CFG, CG, IL dump) against the given assembly bytes.
    /// <paramref name="slot"/> is "pre" or "post" and is used as a subfolder in each export's output path.
    /// Each export is only run if its timing includes this slot.
    /// </summary>
    private void RunExports(StructEntropyConfig config, ICompiledAssembly compiledAssembly,
        byte[] assemblyData, byte[] pdbData, string slot)
    {
        bool runCfg = config.cfgGenerationEnabled && TimingIncludes(config.cfgTiming, slot);
        bool runCg  = config.cgGenerationEnabled  && TimingIncludes(config.cgTiming,  slot);
        bool runIl  = config.ilDumpEnabled         && TimingIncludes(config.ilDumpTiming, slot);

        if (!runCfg && !runCg && !runIl) return;

        var processor = new StructEntropyAssemblyProcessor(assemblyData, pdbData, compiledAssembly);
        var (assembly, resolver) = processor.LoadAssembly();

        if (runCfg)
            CFGGen.ProcessAndWriteCFG(assembly, compiledAssembly, config, slot);

        if (runCg)
            CGGen.ProcessAndWriteCG(assembly, compiledAssembly, slot);

        if (runIl)
        {
            string outPath = Path.Combine(
                Directory.GetCurrentDirectory(),
                config.ilDumpPath,
                compiledAssembly.Name,
                slot + ".il");
            string dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(outPath, ILDumper.FormatAssembly(assembly.MainModule));
            StructEntropyLogger.Log($"IL dump ({slot}) written to {outPath}");
        }
    }

    private static bool TimingIncludes(ExportTiming timing, string slot)
        => timing == ExportTiming.Both
        || (timing == ExportTiming.Pre  && slot == "pre")
        || (timing == ExportTiming.Post && slot == "post");

    public override ILPostProcessResult Process(ICompiledAssembly compiledAssembly)
    {
        using var logContext = StructEntropyLogger.BeginContext();

        try {
            return ProcessUnsafe(compiledAssembly);
        }
        catch (Exception e) {
            StructEntropyLogger.Log($"Failed to process assembly: {e}");

            // error, return unmodified
            return new ILPostProcessResult(compiledAssembly.InMemoryAssembly, StructEntropyLogger.GetDiagnosticsSnapshot());
        }
    }

    private StructEntropyConfig LoadConfig()
    {
        const string configPath = "ProjectSettings/StructEntropyConfig.json";
        return StructEntropyConfigReader.LoadConfig(configPath);
    }
}
