using UnityEngine;

namespace DarkRealms.Core
{
    /// <summary>A named sequence of sprites played back at a fixed rate.</summary>
    [System.Serializable]
    public class SpriteClip
    {
        public string name = "Idle";
        public Sprite[] frames;
        public float fps = 10f;
        public bool loop = true;
    }

    /// <summary>
    /// Minimal frame-based sprite animator. Deliberately avoids Animator/AnimatorController
    /// assets so animation data can be authored and swapped entirely from code/inspector.
    /// </summary>
    [RequireComponent(typeof(SpriteRenderer))]
    [DisallowMultipleComponent]
    public class FrameAnimator : MonoBehaviour
    {
        [SerializeField] private SpriteRenderer target;
        [SerializeField] private SpriteClip[] clips;
        [SerializeField] private string defaultClip = "Idle";

        private SpriteClip current;
        private float timer;
        private int index;
        private float speed = 1f;

        public string CurrentClip => current != null ? current.name : null;

        private void Awake()
        {
            if (target == null)
                target = GetComponent<SpriteRenderer>();
        }

        private void Start()
        {
            if (!string.IsNullOrEmpty(defaultClip))
                Play(defaultClip);
        }

        public void Play(string clipName, float speedMultiplier = 1f, bool restart = false)
        {
            var clip = Find(clipName);
            if (clip == null)
                return;

            speed = speedMultiplier;

            if (current == clip && !restart)
                return;

            current = clip;
            index = 0;
            timer = 0f;
            ApplyCurrentFrame();
        }

        /// <summary>Plays the clip only if it is not already the active one. Cheap to call every frame.</summary>
        public void PlayIfNot(string clipName, float speedMultiplier = 1f)
        {
            if (current == null || current.name != clipName)
                Play(clipName, speedMultiplier);
        }

        public bool HasClip(string clipName) => Find(clipName) != null;

        private SpriteClip Find(string clipName)
        {
            if (clips == null)
                return null;

            foreach (var clip in clips)
            {
                if (clip != null && clip.name == clipName)
                    return clip;
            }

            return null;
        }

        private void Update()
        {
            if (current == null || current.frames == null || current.frames.Length == 0)
                return;

            if (current.frames.Length == 1 || current.fps <= 0f)
            {
                ApplyCurrentFrame();
                return;
            }

            timer += Time.deltaTime * current.fps * speed;

            while (timer >= 1f)
            {
                timer -= 1f;
                index++;

                if (index >= current.frames.Length)
                    index = current.loop ? 0 : current.frames.Length - 1;
            }

            ApplyCurrentFrame();
        }

        private void ApplyCurrentFrame()
        {
            if (target == null || current == null || current.frames.Length == 0)
                return;

            target.sprite = current.frames[Mathf.Clamp(index, 0, current.frames.Length - 1)];
        }
    }
}
