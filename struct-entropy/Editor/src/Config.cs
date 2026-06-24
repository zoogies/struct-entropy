using System;
using System.Collections.Generic;

namespace StructEntropy.Runtime
{
    public enum CFGMode
    {
        WholeAssembly = 0,
        PerMethod = 1
    }

    public enum ExportTiming
    {
        Post = 0,
        Pre = 1,
        Both = 2
    }

    [Serializable]
    public sealed class StructEntropyConfig
    {
        public bool structEntropyEnabled = true;
        public int structEntropySeed = 0;
        public float structEntropyFieldRelocationProbability = 1.0f;
        public bool structEntropyVerboseLogging = false;
        public bool benchmarkLoggingEnabled = false;
        public bool cfgGenerationEnabled = false;
        public CFGMode cfgMode = CFGMode.WholeAssembly;
        public ExportTiming cfgTiming = ExportTiming.Post;
        public bool cgGenerationEnabled = false;
        public ExportTiming cgTiming = ExportTiming.Post;
        public bool ilDumpEnabled = false;
        public string ilDumpPath = "Logs/IL_Output";
        public ExportTiming ilDumpTiming = ExportTiming.Post;
        public bool structEntropyGraphExportEnabled = false;
        public List<string> targetAssemblies = new List<string>();
        public List<string> structEntropyAssemblyIncludePrefixes = new List<string>();
        public List<string> structEntropyAssemblyExcludePrefixes = new List<string>();
    }
}
