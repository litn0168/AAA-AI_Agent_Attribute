using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DarkRealms.EditorTools
{
    /// <summary>
    /// Non-destructive project setup: creates the Assets/Game folder tree and registers
    /// the game's sorting layers. Existing folders, layers and assets are preserved.
    /// </summary>
    public static class ProjectBootstrap
    {
        private const string Root = "Assets/Game";

        private static readonly string[] Folders =
        {
            Root,
            Root + "/Art",
            Root + "/Art/Characters",
            Root + "/Art/Environments",
            Root + "/Art/Props",
            Root + "/Art/Effects",
            Root + "/Art/UI",
            Root + "/Animations",
            Root + "/Audio",
            Root + "/Materials",
            Root + "/Prefabs",
            Root + "/Prefabs/Characters",
            Root + "/Prefabs/Environment",
            Root + "/Prefabs/Combat",
            Root + "/Prefabs/UI",
            Root + "/Scenes",
            Root + "/Scripts",
            Root + "/Scripts/Core",
            Root + "/Scripts/Player",
            Root + "/Scripts/Combat",
            Root + "/Scripts/Enemies",
            Root + "/Scripts/Environment",
            Root + "/Scripts/CameraSystem",
            Root + "/Scripts/UI",
            Root + "/Scripts/Editor",
            Root + "/Settings",
        };

        // Order matters: earlier entries render further back.
        private static readonly string[] SortingLayers =
        {
            "Sky",
            "ParallaxFar",
            "ParallaxMid",
            "ParallaxNear",
            "Ground",
            "Environment",
            "Characters",
            "Effects",
            "Foreground",
        };

        // Gameplay physics layers. Indices 0-7 are reserved by Unity.
        private static readonly string[] PhysicsLayers =
        {
            "Ground",
            "Player",
            "Enemy",
            "Attack",
        };

        [MenuItem("Dark Realms/Bootstrap Project")]
        public static void Run()
        {
            int foldersCreated = CreateFolders();
            int layersAdded = AddSortingLayers();
            int physicsLayersAdded = AddPhysicsLayers();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[DarkRealms] Bootstrap complete. Folders created: {foldersCreated}, sorting layers added: {layersAdded}, physics layers added: {physicsLayersAdded}.");
        }

        /// <summary>Returns the layer index for a project layer name, or 0 if missing.</summary>
        public static int LayerIndex(string layerName)
        {
            int index = LayerMask.NameToLayer(layerName);
            if (index < 0)
                Debug.LogWarning($"[DarkRealms] Layer '{layerName}' not found; defaulting to Default.");
            return index < 0 ? 0 : index;
        }

        public static int LayerMaskFor(string layerName) => 1 << LayerIndex(layerName);

        private static int AddPhysicsLayers()
        {
            var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (asset == null || asset.Length == 0)
                return 0;

            var tagManager = new SerializedObject(asset[0]);
            var layers = tagManager.FindProperty("layers");
            if (layers == null)
                return 0;

            int added = 0;
            int nextIndex = 8;

            foreach (string layerName in PhysicsLayers)
            {
                // Skip if the layer already exists anywhere in the list.
                bool exists = false;
                for (int i = 0; i < layers.arraySize; i++)
                {
                    if (layers.GetArrayElementAtIndex(i).stringValue == layerName)
                    {
                        exists = true;
                        break;
                    }
                }

                if (exists)
                    continue;

                // Find the next free user slot.
                while (nextIndex < layers.arraySize && !string.IsNullOrEmpty(layers.GetArrayElementAtIndex(nextIndex).stringValue))
                    nextIndex++;

                if (nextIndex >= layers.arraySize)
                    break;

                layers.GetArrayElementAtIndex(nextIndex).stringValue = layerName;
                added++;
                nextIndex++;
            }

            if (added > 0)
                tagManager.ApplyModifiedProperties();

            return added;
        }

        private static int CreateFolders()
        {
            int created = 0;
            foreach (string folder in Folders)
            {
                if (AssetDatabase.IsValidFolder(folder))
                    continue;

                string parent = System.IO.Path.GetDirectoryName(folder).Replace('\\', '/');
                string leaf = System.IO.Path.GetFileName(folder);
                if (!AssetDatabase.IsValidFolder(parent))
                    continue;

                AssetDatabase.CreateFolder(parent, leaf);
                created++;
            }
            return created;
        }

        private static int AddSortingLayers()
        {
            var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (asset == null || asset.Length == 0)
            {
                Debug.LogError("[DarkRealms] Could not open TagManager.asset; sorting layers were not added.");
                return 0;
            }

            var tagManager = new SerializedObject(asset[0]);
            var layers = tagManager.FindProperty("m_SortingLayers");
            if (layers == null)
            {
                Debug.LogError("[DarkRealms] m_SortingLayers not found.");
                return 0;
            }

            var existing = new HashSet<string>();
            for (int i = 0; i < layers.arraySize; i++)
            {
                var element = layers.GetArrayElementAtIndex(i);
                existing.Add(element.FindPropertyRelative("name").stringValue);
            }

            int added = 0;
            foreach (string layerName in SortingLayers)
            {
                if (existing.Contains(layerName))
                    continue;

                layers.InsertArrayElementAtIndex(layers.arraySize);
                var element = layers.GetArrayElementAtIndex(layers.arraySize - 1);
                element.FindPropertyRelative("name").stringValue = layerName;
                element.FindPropertyRelative("uniqueID").intValue = StableId(layerName);
                element.FindPropertyRelative("locked").boolValue = false;
                existing.Add(layerName);
                added++;
            }

            if (added > 0)
                tagManager.ApplyModifiedProperties();

            return added;
        }

        // Deterministic non-zero id so repeated runs are stable and never collide with Default (0).
        private static int StableId(string name)
        {
            unchecked
            {
                int hash = 17;
                foreach (char c in name)
                    hash = hash * 31 + c;
                hash &= 0x7FFFFFFF;
                return hash == 0 ? 1 : hash;
            }
        }
    }
}
