using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace StructEntropy.Editor
{
    /// <summary>
    /// Detects when any StructEntropy ILPP pass changes the compiled output and auto-triggers
    /// entity scene rebaking — without requiring an editor restart.
    ///
    /// Each pass writes a signature file to Library/StructEntropy-sig-{passName}.txt via
    /// StructEntropySignatureWriter. On every domain reload this watcher combines all signatures
    /// into a single hash, compares it to the last known value stored in EditorPrefs, and
    /// force-reimports all scenes if anything changed.
    /// </summary>
    [InitializeOnLoad]
    public static class StructEntropyCacheWatcher
    {
        private const string EditorPrefsKey = "StructEntropy.LastCombinedSignature";
        private const string SigFilePattern = "StructEntropy-sig-*.txt";

        static StructEntropyCacheWatcher()
        {
            CheckAndRebakeIfNeeded();
        }

        private static void CheckAndRebakeIfNeeded()
        {
            string combined = BuildCombinedSignature();
            string last = EditorPrefs.GetString(EditorPrefsKey, null);

            if (combined == last)
                return;

            EditorPrefs.SetString(EditorPrefsKey, combined);

            if (string.IsNullOrEmpty(last))
            {
                // First run — establish baseline without triggering a rebake.
                Debug.Log("[StructEntropy] Pass signatures established. Entity scenes will auto-rebake when any pass output changes.");
                return;
            }

            Debug.Log("[StructEntropy] Pass output changed since last compile — triggering entity scene rebake.");
            ForceRebakeAllScenes();
        }

        private static string BuildCombinedSignature()
        {
            string libPath = Path.Combine(Application.dataPath, "..", "Library");
            if (!Directory.Exists(libPath))
                return string.Empty;

            string[] files = Directory.GetFiles(libPath, SigFilePattern);
            Array.Sort(files, StringComparer.Ordinal); // deterministic order

            var sb = new StringBuilder();
            foreach (string f in files)
            {
                sb.AppendLine($"[{Path.GetFileNameWithoutExtension(f)}]");
                sb.AppendLine(File.ReadAllText(f));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Force-reimports all Unity scene files so the entity baker re-runs with the
        /// current component layout. No editor restart required.
        /// </summary>
        public static void ForceRebakeAllScenes()
        {
            string[] guids = AssetDatabase.FindAssets("t:Scene");
            int count = 0;
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                count++;
            }
            Debug.Log($"[StructEntropy] Triggered entity scene rebake for {count} scene(s).");
        }
    }
}
