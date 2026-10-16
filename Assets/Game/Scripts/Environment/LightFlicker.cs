using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace DarkRealms.Environment
{
    /// <summary>
    /// Flickers a URP 2D Light2D to sell torch flame. Falls back to a no-op when the
    /// component is missing so it is safe to attach to any prop.
    /// </summary>
    [DisallowMultipleComponent]
    public class LightFlicker : MonoBehaviour
    {
        [SerializeField] private Light2D targetLight;
        [SerializeField] private float baseIntensity = 1.1f;
        [SerializeField] private float amplitude = 0.28f;
        [SerializeField] private float speed = 7f;
        [SerializeField] private float radiusJitter = 0.06f;

        private float seed;
        private float baseRadius;

        private void Awake()
        {
            if (targetLight == null)
                targetLight = GetComponent<Light2D>();

            if (targetLight == null)
                return;

            seed = Random.value * 100f;
            baseRadius = targetLight.pointLightOuterRadius;
        }

        private void Update()
        {
            if (targetLight == null)
                return;

            float noise = Mathf.PerlinNoise(seed, Time.time * speed) - 0.5f;
            targetLight.intensity = Mathf.Max(0f, baseIntensity + noise * 2f * amplitude);
            targetLight.pointLightOuterRadius = baseRadius * (1f + noise * 2f * radiusJitter);
        }
    }
}
