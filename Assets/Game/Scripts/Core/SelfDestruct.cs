using UnityEngine;

namespace DarkRealms.Core
{
    /// <summary>Destroys the GameObject after a delay. Used by one-shot visual effects.</summary>
    [DisallowMultipleComponent]
    public class SelfDestruct : MonoBehaviour
    {
        [SerializeField] private float lifetime = 0.3f;

        private float elapsed;

        private void Update()
        {
            elapsed += Time.deltaTime;
            if (elapsed >= lifetime)
                Destroy(gameObject);
        }
    }
}
