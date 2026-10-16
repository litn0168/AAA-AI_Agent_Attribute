using UnityEngine;

namespace DarkRealms.World
{
    /// <summary>
    /// Placed once per playable scene: it states which atlas map this scene is. The scene is not the authority -
    /// the atlas is - so an id that is not registered is reported and does not change world state.
    ///
    /// It also makes a prototype scene playable on its own: pressing Play straight into a map scene still sets
    /// the current map, so travel and route planning work without starting from the hub.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MapIdentity : MonoBehaviour
    {
        [Tooltip("Stable atlas map id, e.g. map.proto.grassland. Must match a MapDefinition in the atlas.")]
        [SerializeField] private string mapId = string.Empty;

        [Tooltip("Optional scene name override used only for logging.")]
        [SerializeField] private string sceneLabel = string.Empty;

        public string MapId => mapId;
        public string SceneLabel => string.IsNullOrEmpty(sceneLabel) ? gameObject.scene.name : sceneLabel;

        private void Awake()
        {
            if (WorldAtlasRuntime.Instance != null)
                WorldAtlasRuntime.Instance.NotifySceneMapEntered(this);
        }

#if UNITY_EDITOR
        /// <summary>Authoring entry point used by the prototype scene builder.</summary>
        public void EditorConfigure(string id, string label)
        {
            mapId = id;
            sceneLabel = label;
        }
#endif
    }
}
