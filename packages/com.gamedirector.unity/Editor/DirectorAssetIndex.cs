using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GameDirector.Unity.Editor
{
    [Serializable] public sealed class AssetEntry
    {
        public string id, name, kind, path, dependencyHash;
        public double duration;
    }
    [Serializable] public sealed class AssetInventory
    {
        public int version = 1;
        public string engine = "Unity", engineVersion = Application.unityVersion;
        public List<AssetEntry> assets = new List<AssetEntry>();
    }
    // Read-only inventory. Stable GUID + local ID distinguishes imported sub-assets.
    // This deliberately makes no claim about rig compatibility or dramatic meaning.
    public static class DirectorAssetIndex
    {
        public static IEnumerable<string> Paths(string folder)
        {
            if (!AssetDatabase.IsValidFolder(folder) || !(folder == "Assets" || folder.StartsWith("Assets/", StringComparison.Ordinal)))
                throw new ArgumentException("Select a folder inside Assets.");
            return new[] { "t:Prefab", "t:Model", "t:AnimationClip", "t:AudioClip", "t:Scene" }
                .SelectMany(type => AssetDatabase.FindAssets(type, new[] { folder })).Distinct().Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, StringComparer.Ordinal);
        }
        public static IEnumerable<AssetEntry> Read(string path)
        {
            var hash = AssetDatabase.GetAssetDependencyHash(path).ToString();
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (!(asset is AnimationClip) && !(asset is AudioClip) && !(asset is SceneAsset) && !(asset is GameObject && AssetDatabase.IsMainAsset(asset))) continue;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long localId)) continue;
                yield return new AssetEntry { id = "unity:" + guid + ":" + localId, name = asset.name, kind = asset.GetType().Name, path = path, dependencyHash = hash,
                    duration = asset is AnimationClip clip ? clip.length : asset is AudioClip audio ? audio.length : 0 };
            }
        }
    }
}
