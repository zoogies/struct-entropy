using System;
using System.IO;

/// <summary>
/// Utility for ILPP passes to record their "effective configuration" signature after each compile.
/// StructEntropyCacheWatcher reads all signatures on domain reload and triggers entity scene rebake
/// when any pass's output changes — without requiring an editor restart.
///
/// Usage (from any ILPP pass):
///   StructEntropySignatureWriter.Write("field-encoding", $"seed={seed}\n{sortedFieldNames}");
///   StructEntropySignatureWriter.Write("struct-entropy", sortedTypeNames);
/// </summary>
public static class StructEntropySignatureWriter
{
    private const string FilePrefix = "StructEntropy-sig-";

    /// <summary>
    /// Writes the pass signature to Library/StructEntropy-sig-{passName}.txt.
    /// If the pass produces no output (disabled or nothing to do), delete any stale file.
    /// </summary>
    public static void Write(string passName, string signature)
    {
        try
        {
            string path = GetPath(passName);
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            if (string.IsNullOrEmpty(signature))
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            else
            {
                File.WriteAllText(path, signature);
            }
        }
        catch (Exception e)
        {
            StructEntropyLogger.Log($"[StructEntropySignatureWriter] Failed to write '{passName}' signature: {e.Message}");
        }
    }

    /// <summary>
    /// Removes the signature file for a pass (call when a pass is disabled).
    /// </summary>
    public static void Clear(string passName)
    {
        try
        {
            string path = GetPath(passName);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private static string GetPath(string passName) =>
        Path.Combine(Environment.CurrentDirectory, "Library", $"{FilePrefix}{passName}.txt");
}
