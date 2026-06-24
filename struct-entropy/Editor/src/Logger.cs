using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Unity.CompilationPipeline.Common.Diagnostics;

public sealed class StructEntropyLogContext : IDisposable
{
    private readonly List<DiagnosticMessage> diagnostics = new List<DiagnosticMessage>();
    private readonly StructEntropyLogContext previous;
    private bool disposed;

    internal StructEntropyLogContext(StructEntropyLogContext previous)
    {
        this.previous = previous;
    }

    internal void Add(string message)
    {
        lock (diagnostics)
        {
            diagnostics.Add(new DiagnosticMessage
            {
                DiagnosticType = DiagnosticType.Warning,
                MessageData = $"[StructEntropyILPP] {message}"
            });
        }
    }

    internal List<DiagnosticMessage> Snapshot()
    {
        lock (diagnostics)
        {
            return new List<DiagnosticMessage>(diagnostics);
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        StructEntropyLogger.Restore(previous);
    }
}

public static class StructEntropyLogger
{
    private static readonly AsyncLocal<StructEntropyLogContext> current = new AsyncLocal<StructEntropyLogContext>();

    public static StructEntropyLogContext BeginContext()
    {
        var context = new StructEntropyLogContext(current.Value);
        current.Value = context;
        return context;
    }

    internal static void Restore(StructEntropyLogContext previous)
    {
        current.Value = previous;
    }

    public static void Log(string message)
    {
        var context = current.Value;
        if (context != null)
            context.Add(message);

        AppendLine("StructEntropy.log", message);
    }

    public static void LogBenchmark(string message)
    {
        AppendLine("StructEntropy-benchmark.log", message);
        AppendLine("StructEntropy.log", $"[Benchmark] {message}");
    }

    private static readonly object fileLock = new object();

    private static void AppendLine(string fileName, string message)
    {
        try
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string logsDir = Path.Combine(Environment.CurrentDirectory, "Logs");
            string logPath = Path.Combine(logsDir, fileName);

            lock (fileLock)
            {
                Directory.CreateDirectory(logsDir);
                File.AppendAllText(logPath, $"[{timestamp}] {message}\n");
            }
        }
        catch
        {
            // Parallel ILPP runs may contend on the shared log file.
        }
    }

    public static List<DiagnosticMessage> GetDiagnosticsSnapshot()
    {
        return current.Value?.Snapshot() ?? new List<DiagnosticMessage>();
    }
}
