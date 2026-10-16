using UnityEngine;

namespace DarkRealms.World
{
    /// <summary>
    /// An arrival port inside a scene. Cross-map travel resolves its placement through these, matching the
    /// atlas anchor id of the connection. Placing the player anywhere else - the map centre, the atlas node,
    /// the origin - would put them somewhere the level was never authored to receive them.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MapEntryPoint : MonoBehaviour
    {
        [Tooltip("Atlas anchor id this port serves, e.g. port.east_pass. Must match a MapAnchorDefinition.")]
        [SerializeField] private string anchorId = string.Empty;

        [Tooltip("Port used when a map is entered without a specific arrival anchor.")]
        [SerializeField] private bool isDefaultEntry;

        public string AnchorId => anchorId;
        public bool IsDefaultEntry => isDefaultEntry;

        public Vector2 Position2D => transform.position;

        /// <summary>Finds the port for an anchor id inside a loaded scene. Returns null when the scene has none,
        /// which the transition service reports instead of silently spawning at the wrong place.</summary>
        public static MapEntryPoint Find(UnityEngine.SceneManagement.Scene scene, string targetAnchorId)
        {
            if (string.IsNullOrEmpty(targetAnchorId))
                return null;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                MapEntryPoint[] ports = root.GetComponentsInChildren<MapEntryPoint>(true);
                for (int i = 0; i < ports.Length; i++)
                    if (string.Equals(ports[i].anchorId, targetAnchorId, System.StringComparison.Ordinal))
                        return ports[i];
            }

            return null;
        }

        /// <summary>Finds the port a map should default to, used when the requested anchor has no scene port.</summary>
        public static MapEntryPoint FindDefault(UnityEngine.SceneManagement.Scene scene)
        {
            MapEntryPoint first = null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                MapEntryPoint[] ports = root.GetComponentsInChildren<MapEntryPoint>(true);
                for (int i = 0; i < ports.Length; i++)
                {
                    if (ports[i].isDefaultEntry)
                        return ports[i];

                    if (first == null)
                        first = ports[i];
                }
            }

            return first;
        }

#if UNITY_EDITOR
        /// <summary>Authoring entry point used by the prototype scene builder.</summary>
        public void EditorConfigure(string id, bool isDefault)
        {
            anchorId = id;
            isDefaultEntry = isDefault;
        }
#endif
    }
}
