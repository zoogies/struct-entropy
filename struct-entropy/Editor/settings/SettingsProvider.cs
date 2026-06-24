using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using StructEntropy.Runtime;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace StructEntropy.Editor
{
    public class StructEntropySettingsProvider : SettingsProvider
    {
        private StructEntropyConfig config;
        private bool hasPendingChanges = false;

        // Assembly UI state
        private bool packageFoldout = false;
        private Assembly[] cachedAssemblies;
        private List<string> projectAssemblyNames;
        private List<string> packageAssemblyNames;

        public StructEntropySettingsProvider(string path, SettingsScope scopes)
            : base(path, scopes) { }

        public override void OnActivate(string searchContext, UnityEngine.UIElements.VisualElement rootElement)
        {
            config = StructEntropySettings.GetConfig();
            hasPendingChanges = false;
            RefreshAssemblyLists();
        }

        public override void OnDeactivate()
        {
            FlushPendingChanges();
        }

        private void RefreshAssemblyLists()
        {
            cachedAssemblies = CompilationPipeline.GetAssemblies();
            projectAssemblyNames = new List<string>();
            packageAssemblyNames = new List<string>();

            foreach (var asm in cachedAssemblies)
            {
                // Skip editor assemblies
                if ((asm.flags & AssemblyFlags.EditorAssembly) != 0)
                    continue;

                string name = asm.name;

                // Skip Unity engine modules
                if (name.StartsWith("UnityEngine.") || name.StartsWith("UnityEditor.") ||
                    name.StartsWith("com.unity.modules.") || name == "UnityEngine" || name == "UnityEditor")
                    continue;

                // Categorize: project assemblies have source files under Assets/
                bool isProject = asm.sourceFiles.Any(f =>
                    f.Replace('\\', '/').StartsWith("Assets/") &&
                    !f.Replace('\\', '/').StartsWith("Assets/_Recovery/"));

                if (isProject)
                    projectAssemblyNames.Add(name);
                else
                    packageAssemblyNames.Add(name);
            }

            projectAssemblyNames.Sort();
            packageAssemblyNames.Sort();
        }

        public override void OnGUI(string searchContext)
        {
            EditorGUILayout.LabelField("StructEntropy", EditorStyles.largeLabel);
            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("Compiler Passes", EditorStyles.boldLabel);
            config.structEntropyEnabled = EditorGUILayout.ToggleLeft("Struct Entropy", config.structEntropyEnabled);
            if (config.structEntropyEnabled)
            {
                EditorGUI.indentLevel++;
                config.structEntropySeed = EditorGUILayout.IntField("Planner Seed", config.structEntropySeed);
                config.structEntropyFieldRelocationProbability = EditorGUILayout.Slider(
                    "Relocation Chance",
                    config.structEntropyFieldRelocationProbability,
                    0.0f,
                    1.0f);
                config.structEntropyVerboseLogging = EditorGUILayout.ToggleLeft("Verbose Logging", config.structEntropyVerboseLogging);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Benchmarking", EditorStyles.boldLabel);
            config.benchmarkLoggingEnabled = EditorGUILayout.ToggleLeft("Per-Pass Benchmark Logging", config.benchmarkLoggingEnabled);
            if (config.benchmarkLoggingEnabled)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField("→ Logs/StructEntropy-benchmark.log", EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Logs per-assembly pass time and byte deltas for StructEntropy instrumentation.", EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
            }

            bool compilerPassChanged = EditorGUI.EndChangeCheck();
            if (compilerPassChanged)
            {
                MarkDirty();
            }

            // Target Assemblies section
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Target Assemblies", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Select which assemblies StructEntropy processes. If none are selected, the post-processor falls back to Assembly-CSharp.",
                MessageType.Info);

            if (projectAssemblyNames == null)
                RefreshAssemblyLists();

            // Project assemblies
            EditorGUILayout.LabelField("Project Assemblies", EditorStyles.miniBoldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Select All Project", EditorStyles.miniButtonLeft))
            {
                foreach (var name in projectAssemblyNames)
                {
                    if (!config.targetAssemblies.Contains(name))
                        config.targetAssemblies.Add(name);
                }
                MarkDirty();
            }
            if (GUILayout.Button("Deselect All", EditorStyles.miniButtonRight))
            {
                foreach (var name in projectAssemblyNames)
                    config.targetAssemblies.Remove(name);
                MarkDirty();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel++;
            foreach (var name in projectAssemblyNames)
            {
                bool wasSelected = config.targetAssemblies.Contains(name);
                bool isSelected = EditorGUILayout.ToggleLeft(name, wasSelected);
                if (isSelected != wasSelected)
                {
                    if (isSelected)
                        config.targetAssemblies.Add(name);
                    else
                        config.targetAssemblies.Remove(name);
                    MarkDirty();
                }
            }
            EditorGUI.indentLevel--;

            // Package assemblies (foldout)
            if (packageAssemblyNames.Count > 0)
            {
                packageFoldout = EditorGUILayout.Foldout(packageFoldout, $"Package & Unity Assemblies ({packageAssemblyNames.Count})", true);
                if (packageFoldout)
                {
                    EditorGUI.indentLevel++;
                    foreach (var name in packageAssemblyNames)
                    {
                        bool wasSelected = config.targetAssemblies.Contains(name);
                        bool isSelected = EditorGUILayout.ToggleLeft(name, wasSelected);
                        if (isSelected != wasSelected)
                        {
                            if (isSelected)
                                config.targetAssemblies.Add(name);
                            else
                                config.targetAssemblies.Remove(name);
                            MarkDirty();
                        }
                    }
                    EditorGUI.indentLevel--;
                }
            }

            // Static Analysis Export section
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Static Analysis Export", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Export metadata and IL from instrumented assemblies after all passes run. " +
                "Output is written to the path below, relative to the Unity project root.",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();

            config.cfgGenerationEnabled = EditorGUILayout.ToggleLeft("CFG Generation", config.cfgGenerationEnabled);
            if (config.cfgGenerationEnabled)
            {
                EditorGUI.indentLevel++;
                config.cfgMode = (CFGMode)EditorGUILayout.EnumPopup("CFG Mode", config.cfgMode);
                config.cfgTiming = (ExportTiming)EditorGUILayout.EnumPopup("Timing", config.cfgTiming);
                EditorGUILayout.LabelField(
                    "→ Logs/CFG_Output/<Assembly>/pre|post/",
                    EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
            }

            config.cgGenerationEnabled = EditorGUILayout.ToggleLeft("CG Generation", config.cgGenerationEnabled);
            if (config.cgGenerationEnabled)
            {
                EditorGUI.indentLevel++;
                config.cgTiming = (ExportTiming)EditorGUILayout.EnumPopup("Timing", config.cgTiming);
                EditorGUILayout.LabelField(
                    "→ Logs/CG_Output/<Assembly>/pre|post/",
                    EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
            }

            config.ilDumpEnabled = EditorGUILayout.ToggleLeft("IL Dump", config.ilDumpEnabled);
            if (config.ilDumpEnabled)
            {
                EditorGUI.indentLevel++;
                config.ilDumpPath = EditorGUILayout.TextField("Output Directory", config.ilDumpPath);
                config.ilDumpTiming = (ExportTiming)EditorGUILayout.EnumPopup("Timing", config.ilDumpTiming);
                EditorGUILayout.LabelField(
                    $"→ {config.ilDumpPath}/<Assembly>/pre|post.il",
                    EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
            }

            if (config.structEntropyEnabled)
            {
                config.structEntropyGraphExportEnabled = EditorGUILayout.ToggleLeft(
                    "Struct Entropy GraphML",
                    config.structEntropyGraphExportEnabled);
                if (config.structEntropyGraphExportEnabled)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.LabelField(
                        "-> Logs/StructEntropy_Output/<Assembly>/relocation-opportunities.graphml",
                        EditorStyles.miniLabel);
                    EditorGUI.indentLevel--;
                }

            }

            if (EditorGUI.EndChangeCheck())
                MarkDirty();

            // Pending changes indicator and apply button
            if (hasPendingChanges)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(
                    "You have unsaved changes. They will be applied when you navigate away, or click Apply now.",
                    MessageType.Warning);
                if (GUILayout.Button("Apply Changes & Recompile", GUILayout.Height(25)))
                {
                    FlushPendingChanges();
                }
            }

            // Cache management section
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Cache Management", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Try these options in descending order if something isn't working (often stale caching).",
                MessageType.Info
            );

            if (GUILayout.Button("Recompile Editor Assemblies", GUILayout.Height(30)))
            {
                FlushPendingChanges();
                CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
            }

            if (GUILayout.Button("Rebake Entity Scenes", GUILayout.Height(30)))
            {
                StructEntropyCacheWatcher.ForceRebakeAllScenes();
            }

            if (GUILayout.Button("Clear Entity Cache & Restart Unity", GUILayout.Height(30)))
            {
                if (EditorUtility.DisplayDialog(
                    "Clear Entity Cache",
                    "This will:\n" +
                    "• Delete Library/Artifacts\n" +
                    "• Delete Library/StateCache\n" +
                    "• Delete Library/PlayerDataCache\n" +
                    "• Close Unity automatically\n\n" +
                    "After restarting Unity, all assets will reimport. Continue?",
                    "Clear & Close Unity",
                    "Cancel"))
                {
                    FlushPendingChanges();
                    ClearEntityCaches();
                }
            }
        }

        private void MarkDirty()
        {
            hasPendingChanges = true;
        }

        private void FlushPendingChanges()
        {
            if (!hasPendingChanges)
                return;

            hasPendingChanges = false;
            StructEntropySettings.SaveConfig(config);
            CompilationPipeline.RequestScriptCompilation();
        }

        private void ClearEntityCaches()
        {
            var projectPath = Directory.GetCurrentDirectory();
            var libraryPath = Path.Combine(projectPath, "Library");

            var cachePaths = new[]
            {
                Path.Combine(libraryPath, "Artifacts"),
                Path.Combine(libraryPath, "StateCache"),
                Path.Combine(libraryPath, "PlayerDataCache")
            };

            int clearedCount = 0;
            foreach (var path in cachePaths)
            {
                if (Directory.Exists(path))
                {
                    try
                    {
                        Directory.Delete(path, true);
                        Debug.Log($"[StructEntropy] Deleted cache: {path}");
                        clearedCount++;
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogError($"[StructEntropy] Failed to delete {path}: {e.Message}");
                    }
                }
            }

            if (clearedCount > 0)
            {
                Debug.Log($"[StructEntropy] Successfully cleared {clearedCount} cache director{(clearedCount == 1 ? "y" : "ies")}. Unity will now close.");
                Debug.Log("[StructEntropy] After restarting Unity, all assets will reimport and entity scenes will rebake with current ILPP settings.");

                EditorApplication.delayCall += () => EditorApplication.Exit(0);
            }
            else
            {
                EditorUtility.DisplayDialog("No Caches Found", "No cache directories found to clear.", "OK");
            }
        }

        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            return new StructEntropySettingsProvider("Project/StructEntropy", SettingsScope.Project)
            {
                keywords = new[] { "StructEntropy", "Anticheat", "Obfuscation", "Compiler", "Assembly" }
            };
        }
    }
}
