using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using StructEntropy.Runtime;

namespace StructEntropy.Editor
{
    /// <summary>
    /// Robust JSON config reader for ILPP context (no external dependencies).
    /// Handles whitespace, formatting variations, and missing fields gracefully.
    /// </summary>
    public static class StructEntropyConfigReader
    {
        public static StructEntropyConfig LoadConfig(string configPath)
        {
            if (!File.Exists(configPath))
            {
                StructEntropyLogger.Log($"Config file not found at {configPath}, using defaults");
                return new StructEntropyConfig();
            }

            try
            {
                string json = File.ReadAllText(configPath);
                var config = new StructEntropyConfig();

                // Parse each field with robust regex patterns that handle whitespace
                config.structEntropyEnabled = ParseBool(json, "structEntropyEnabled", config.structEntropyEnabled);
                config.cfgGenerationEnabled = ParseBool(json, "cfgGenerationEnabled", config.cfgGenerationEnabled);
                config.cgGenerationEnabled = ParseBool(json, "cgGenerationEnabled", config.cgGenerationEnabled);
                config.benchmarkLoggingEnabled = ParseBool(json, "benchmarkLoggingEnabled", config.benchmarkLoggingEnabled);
                config.ilDumpEnabled = ParseBool(json, "ilDumpEnabled", config.ilDumpEnabled);
                config.ilDumpPath = ParseString(json, "ilDumpPath", config.ilDumpPath);
                config.structEntropyGraphExportEnabled = ParseBool(json, "structEntropyGraphExportEnabled", config.structEntropyGraphExportEnabled);
                config.structEntropyVerboseLogging = ParseBool(json, "structEntropyVerboseLogging", config.structEntropyVerboseLogging);
                config.structEntropySeed = ParseInt(json, "structEntropySeed", config.structEntropySeed);
                config.structEntropyFieldRelocationProbability = Clamp01(ParseFloat(
                    json,
                    "structEntropyFieldRelocationProbability",
                    config.structEntropyFieldRelocationProbability));

                int cfgModeInt = ParseInt(json, "cfgMode", (int)config.cfgMode);
                config.cfgMode = cfgModeInt == 1 ? CFGMode.PerMethod : CFGMode.WholeAssembly;

                config.cfgTiming = ParseExportTiming(json, "cfgTiming", config.cfgTiming);
                config.cgTiming = ParseExportTiming(json, "cgTiming", config.cgTiming);
                config.ilDumpTiming = ParseExportTiming(json, "ilDumpTiming", config.ilDumpTiming);

                config.targetAssemblies = ParseStringArray(json, "targetAssemblies");
                config.structEntropyAssemblyIncludePrefixes = ParseStringArray(json, "structEntropyAssemblyIncludePrefixes");
                config.structEntropyAssemblyExcludePrefixes = ParseStringArray(json, "structEntropyAssemblyExcludePrefixes");

                return config;
            }
            catch (Exception e)
            {
                StructEntropyLogger.Log($"Failed to parse config, using defaults: {e.Message}");
                return new StructEntropyConfig();
            }
        }

        /// <summary>
        /// Parse a boolean field from JSON, handling any amount of whitespace.
        /// Pattern: "key" : true/false (with optional spaces/tabs)
        /// </summary>
        private static bool ParseBool(string json, string key, bool defaultValue)
        {
            try
            {
                // Match "key"\s*:\s*(true|false) - allows any whitespace around colon
                string pattern = $"\"{key}\"\\s*:\\s*(true|false)";
                Match match = Regex.Match(json, pattern, RegexOptions.IgnoreCase);

                if (match.Success)
                {
                    return bool.Parse(match.Groups[1].Value);
                }
            }
            catch (Exception e)
            {
                StructEntropyLogger.Log($"Failed to parse bool field '{key}': {e.Message}");
            }

            return defaultValue;
        }

        /// <summary>
        /// Parse an integer field from JSON, handling any amount of whitespace.
        /// Pattern: "key" : 123 (with optional spaces/tabs)
        /// </summary>
        private static int ParseInt(string json, string key, int defaultValue)
        {
            try
            {
                // Match "key"\s*:\s*(\d+) - allows any whitespace around colon
                string pattern = $"\"{key}\"\\s*:\\s*(\\d+)";
                Match match = Regex.Match(json, pattern);

                if (match.Success)
                {
                    return int.Parse(match.Groups[1].Value);
                }
            }
            catch (Exception e)
            {
                StructEntropyLogger.Log($"Failed to parse int field '{key}': {e.Message}");
            }

            return defaultValue;
        }

        private static float ParseFloat(string json, string key, float defaultValue)
        {
            try
            {
                string pattern = $"\"{key}\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?)";
                Match match = Regex.Match(json, pattern);

                if (match.Success)
                {
                    return float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                }
            }
            catch (Exception e)
            {
                StructEntropyLogger.Log($"Failed to parse float field '{key}': {e.Message}");
            }

            return defaultValue;
        }

        private static float Clamp01(float value)
        {
            if (value < 0.0f) return 0.0f;
            if (value > 1.0f) return 1.0f;
            return value;
        }

        private static string ParseString(string json, string key, string defaultValue)
        {
            try
            {
                string pattern = $"\"{key}\"\\s*:\\s*\"([^\"]*)\"";
                Match match = Regex.Match(json, pattern);
                if (match.Success)
                    return match.Groups[1].Value;
            }
            catch (Exception e)
            {
                StructEntropyLogger.Log($"Failed to parse string field '{key}': {e.Message}");
            }
            return defaultValue;
        }

        private static ExportTiming ParseExportTiming(string json, string key, ExportTiming defaultValue)
        {
            try
            {
                string pattern = $"\"{key}\"\\s*:\\s*(\\d+)";
                Match match = Regex.Match(json, pattern);
                if (match.Success)
                {
                    int val = int.Parse(match.Groups[1].Value);
                    if (val == 1) return ExportTiming.Pre;
                    if (val == 2) return ExportTiming.Both;
                    return ExportTiming.Post;
                }
            }
            catch (Exception e)
            {
                StructEntropyLogger.Log($"Failed to parse ExportTiming field '{key}': {e.Message}");
            }
            return defaultValue;
        }

        private static List<string> ParseStringArray(string json, string key)
        {
            var result = new List<string>();
            try
            {
                // Match "key"\s*:\s*\[...\] - captures the array contents
                string pattern = $"\"{key}\"\\s*:\\s*\\[([^\\]]*)\\]";
                Match match = Regex.Match(json, pattern, RegexOptions.Singleline);

                if (match.Success)
                {
                    string arrayContent = match.Groups[1].Value;
                    // Extract each quoted string value
                    var itemPattern = new Regex("\"([^\"]+)\"");
                    foreach (Match item in itemPattern.Matches(arrayContent))
                    {
                        result.Add(item.Groups[1].Value);
                    }
                }
            }
            catch (Exception e)
            {
                StructEntropyLogger.Log($"Failed to parse string array field '{key}': {e.Message}");
            }

            return result;
        }
    }
}
