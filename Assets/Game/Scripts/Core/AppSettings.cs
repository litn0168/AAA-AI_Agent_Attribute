using System;
using UnityEngine;

namespace DarkRealms.Core
{
    /// <summary>
    /// Player preferences that must survive a restart. Nothing in the project persisted settings before, so this
    /// stays deliberately small: one clearly named PlayerPrefs key per option, a documented default used when the
    /// key is missing, and a Changed event so live interface elements follow an edit without a restart.
    /// </summary>
    public static class AppSettings
    {
        /// <summary>Key for "Escape opens the pause menu". Namespaced so it cannot collide with another key.</summary>
        public const string UseEscapeToPauseKey = "DarkRealms.Settings.UseEscapeToPause";

        /// <summary>Enabled on a first run, as specified.</summary>
        public const bool UseEscapeToPauseDefault = true;

        /// <summary>Raised after a setting is written, so open panels can follow the new value immediately.</summary>
        public static event Action Changed;

        private static bool useEscapeToPauseCache;
        private static bool useEscapeToPauseLoaded;

        /// <summary>
        /// When true, Escape opens the pause menu and pauses through <see cref="GameManager.SetPaused"/>. When
        /// false, Escape does nothing in gameplay - but it still resumes an already paused game, so the player can
        /// never be trapped behind a pause they cannot leave.
        /// </summary>
        public static bool UseEscapeToPause
        {
            get
            {
                if (!useEscapeToPauseLoaded)
                {
                    useEscapeToPauseCache = PlayerPrefs.GetInt(UseEscapeToPauseKey, UseEscapeToPauseDefault ? 1 : 0) != 0;
                    useEscapeToPauseLoaded = true;
                }

                return useEscapeToPauseCache;
            }
            set
            {
                bool previous = UseEscapeToPause;
                useEscapeToPauseCache = value;
                useEscapeToPauseLoaded = true;

                PlayerPrefs.SetInt(UseEscapeToPauseKey, value ? 1 : 0);
                PlayerPrefs.Save();

                if (previous != value)
                    Changed?.Invoke();
            }
        }

        /// <summary>True when the player already has a stored value, false on a first run.</summary>
        public static bool HasStoredUseEscapeToPause => PlayerPrefs.HasKey(UseEscapeToPauseKey);

        public static void ResetToDefaults() => UseEscapeToPause = UseEscapeToPauseDefault;

        /// <summary>
        /// Re-reads the stored value, discarding the in-memory copy. Used when a verification edits PlayerPrefs
        /// directly to check that a preference really is restored from disk.
        /// </summary>
        public static void Reload()
        {
            useEscapeToPauseLoaded = false;
        }
    }
}
