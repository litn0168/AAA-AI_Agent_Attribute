#if UNITY_EDITOR
using System.Collections.Generic;
using DarkRealms.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DarkRealms.EditorTools
{
    /// <summary>
    /// Adds the world-map runtime to a scene: the atlas runtime, this scene's map identity, its arrival ports and
    /// its travel boundaries.
    ///
    /// It is separate from the prototype builder on purpose. The builder creates prototype maps from nothing;
    /// this installer only adds what is missing to a scene that already exists, so the hand-built prototype scene
    /// is extended rather than rebuilt.
    /// </summary>
    public static class WorldMapInstaller
    {
        [MenuItem("Dark Realms/World Map/Install World Map Into Open Scene")]
        public static void InstallIntoOpenScene()
        {
            Scene scene = SceneManager.GetActiveScene();

            WorldAtlasDefinition atlas = WorldPrototypeBuilder.LoadAtlas();
            if (atlas == null)
            {
                Debug.LogError($"[WorldMapInstaller] No atlas at {WorldPrototypeBuilder.AtlasPath}. " +
                               "Run 'Dark Realms/World Map/Build Prototype World' first.");
                return;
            }

            MapDefinition map = atlas.GetMapByScenePath(scene.path);
            if (map == null)
            {
                Debug.LogError($"[WorldMapInstaller] Scene '{scene.path}' is not registered as a map in the atlas, " +
                               "so there is nothing to install. Add it to the atlas first.");
                return;
            }

            // A scene hierarchy that is already installed is left alone, apart from re-linking the atlas.
            WorldAtlasRuntime runtime = Object.FindAnyObjectByType<WorldAtlasRuntime>();
            if (runtime == null)
            {
                var runtimeObject = new GameObject("WorldAtlasRuntime");
                Undo.RegisterCreatedObjectUndo(runtimeObject, "Install World Map Runtime");
                runtime = runtimeObject.AddComponent<WorldAtlasRuntime>();
            }

            EditorUtility.SetDirty(runtime);
            SceneBuilder.SetObject(runtime, "atlas", atlas);

            MapIdentity identity = Object.FindAnyObjectByType<MapIdentity>();
            if (identity == null)
            {
                var identityObject = new GameObject("MapIdentity");
                Undo.RegisterCreatedObjectUndo(identityObject, "Install Map Identity");
                identity = identityObject.AddComponent<MapIdentity>();
            }

            identity.EditorConfigure(map.id, scene.name);
            EditorUtility.SetDirty(identity);

            Transform ports = FindOrCreateGroup("EntryPorts");
            int created = 0;
            foreach (MapAnchorDefinition anchor in map.anchors)
            {
                if (anchor == null || MapEntryPoint.Find(scene, anchor.id) != null)
                    continue;

                var portObject = new GameObject("Port_" + anchor.id);
                Undo.RegisterCreatedObjectUndo(portObject, "Create Map Entry Port");
                portObject.transform.SetParent(ports, false);
                portObject.transform.position = new Vector3(anchor.position.x, anchor.position.y, 0f);

                var port = portObject.AddComponent<MapEntryPoint>();
                port.EditorConfigure(anchor.id, string.Equals(anchor.id, map.defaultEntryAnchorId));
                created++;
            }

            Transform exits = FindOrCreateGroup("Exits");
            int triggers = 0;
            for (int i = 0; i < atlas.connections.Length; i++)
            {
                MapConnectionDefinition connection = atlas.connections[i];
                if (connection == null || !string.Equals(connection.fromMapId, map.id, System.StringComparison.Ordinal))
                    continue;

                MapAnchorDefinition anchor = map.FindAnchor(connection.fromAnchorId);
                if (anchor == null || HasTrigger(exits, connection.id))
                    continue;

                var triggerObject = new GameObject("Exit_" + connection.id);
                Undo.RegisterCreatedObjectUndo(triggerObject, "Create Map Travel Boundary");
                triggerObject.transform.SetParent(exits, false);
                triggerObject.transform.position = new Vector3(anchor.position.x, anchor.position.y, 0f);

                var collider = triggerObject.AddComponent<BoxCollider2D>();
                collider.isTrigger = true;
                collider.size = new Vector2(2f, 4f);

                var trigger = triggerObject.AddComponent<MapTravelTrigger>();
                trigger.EditorConfigure(connection.id);
                triggers++;
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Selection.activeGameObject = identity.gameObject;

            Debug.Log($"[WorldMapInstaller] '{scene.name}' installed as map '{map.id}': " +
                      $"{created} port(s) created, {triggers} travel boundary(ies) created. " +
                      "Press Play and press M for the world map.");
        }

        /// <summary>A scene whose runtime has no atlas cannot travel or plan a route when played on its own.</summary>
        private static bool HasAssignedAtlas()
        {
            WorldAtlasRuntime runtime = Object.FindAnyObjectByType<WorldAtlasRuntime>();
            return runtime != null && runtime.Atlas != null;
        }

        private static Transform FindOrCreateGroup(string name)
        {
            GameObject existing = GameObject.Find(name);
            if (existing != null)
                return existing.transform;

            var group = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(group, "Create " + name);
            return group.transform;
        }

        private static bool HasTrigger(Transform parent, string connectionId)
        {
            MapTravelTrigger[] triggers = parent.GetComponentsInChildren<MapTravelTrigger>(true);
            for (int i = 0; i < triggers.Length; i++)
                if (string.Equals(triggers[i].ConnectionId, connectionId, System.StringComparison.Ordinal))
                    return true;

            return false;
        }

        /// <summary>Reports what the open scene still needs, without changing anything.</summary>
        [MenuItem("Dark Realms/World Map/Report Open Scene Setup")]
        public static void ReportOpenSceneSetup()
        {
            Scene scene = SceneManager.GetActiveScene();
            WorldAtlasDefinition atlas = WorldPrototypeBuilder.LoadAtlas();
            MapDefinition map = atlas != null ? atlas.GetMapByScenePath(scene.path) : null;

            var lines = new List<string>
            {
                $"scene: {scene.path}",
                $"atlas: {(atlas != null ? atlas.atlasId : "<none>")}",
                $"registered map: {(map != null ? map.id : "<none>")}",
                $"runtime present: {Object.FindAnyObjectByType<WorldAtlasRuntime>() != null}",
                $"runtime atlas assigned: {HasAssignedAtlas()}",
                $"identity present: {Object.FindAnyObjectByType<MapIdentity>() != null}",
                $"entry ports: {Object.FindObjectsByType<MapEntryPoint>(FindObjectsInactive.Include).Length}",
                $"travel boundaries: {Object.FindObjectsByType<MapTravelTrigger>(FindObjectsInactive.Include).Length}"
            };

            Debug.Log("[WorldMapInstaller] " + string.Join(" | ", lines));
        }
    }
}
#endif
