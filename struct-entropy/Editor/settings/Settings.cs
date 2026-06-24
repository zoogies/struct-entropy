using System.IO;
using UnityEngine;
using StructEntropy.Runtime;

namespace StructEntropy.Editor
{
    public static class StructEntropySettings
    {
        private const string ConfigPath = "ProjectSettings/StructEntropyConfig.json";
        private static StructEntropyConfig cachedConfig;

        public static StructEntropyConfig GetConfig()
        {
            if (cachedConfig != null)
                return cachedConfig;

            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                cachedConfig = JsonUtility.FromJson<StructEntropyConfig>(json);
            }
            else
            {
                cachedConfig = new StructEntropyConfig();
                SaveConfig(cachedConfig);
            }

            return cachedConfig;
        }

        public static void SaveConfig(StructEntropyConfig config)
        {
            cachedConfig = config;
            string json = JsonUtility.ToJson(config, true);
            File.WriteAllText(ConfigPath, json);
        }

        public static void InvalidateCache()
        {
            cachedConfig = null;
        }
    }
}
