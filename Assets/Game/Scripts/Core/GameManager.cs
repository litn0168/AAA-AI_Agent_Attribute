using UnityEngine;

namespace DarkRealms.Core
{
    /// <summary>
    /// Small central service for state that outlives a single actor: the active spawn
    /// point and global pause. Intentionally minimal so it does not become a god object.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }

        [SerializeField] private Transform defaultSpawnPoint;

        private Transform activeSpawn;

        public bool IsPaused { get; private set; }
        public Transform ActiveSpawn => activeSpawn != null ? activeSpawn : defaultSpawnPoint;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            activeSpawn = defaultSpawnPoint;
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        /// <summary>Sets the checkpoint used by respawn logic (e.g. when entering a new area).</summary>
        public void SetSpawnPoint(Transform spawn) => activeSpawn = spawn;

        public void SetPaused(bool paused)
        {
            IsPaused = paused;
            Time.timeScale = paused ? 0f : 1f;
        }

        public void TogglePause() => SetPaused(!IsPaused);
    }
}
