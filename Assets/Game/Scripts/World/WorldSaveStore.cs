using System;
using System.IO;
using UnityEngine;

namespace DarkRealms.World
{
    /// <summary>
    /// Versioned JSON persistence for <see cref="WorldProgress"/>.
    ///
    /// The project had no save system before this, so the design keeps to the smallest thing that is still
    /// safe: one slot, one file, an explicit schema version, an atomic replace, and one backup that a damaged
    /// save can fall back to. It is a single adapter - nothing else in the project writes progress to disk.
    ///
    /// Writes are atomic in the platform sense that matters here: the new file is written to a temporary path
    /// first and only then swapped in, so an interrupted or failed write leaves the previous save intact
    /// rather than truncating it.
    /// </summary>
    public static class WorldSaveStore
    {
        private const string FolderName = "DarkRealms";
        private const string FileName = "world_save.json";

        /// <summary>
        /// Overrides the storage folder. Verification uses a scratch folder so automated runs can never
        /// pollute (or read) the player's real save slot.
        /// </summary>
        public static string DirectoryOverride { get; set; }

        /// <summary>When false, <see cref="Save"/> is a no-op. Used by tests that exercise in-memory state.</summary>
        public static bool Enabled { get; set; } = true;

        public static string Directory
        {
            get
            {
                string root = string.IsNullOrEmpty(DirectoryOverride)
                    ? Path.Combine(Application.persistentDataPath, FolderName)
                    : DirectoryOverride;
                return root;
            }
        }

        public static string SavePath => Path.Combine(Directory, FileName);
        public static string BackupPath => SavePath + ".bak";
        public static string TempPath => SavePath + ".tmp";
        public static string CorruptPath => SavePath + ".corrupt";

        /// <summary>Number of successful writes this session. Diagnostics for the transition tests.</summary>
        public static int WriteCount { get; private set; }

        /// <summary>Number of failed writes this session. A non-zero value is reported, never hidden.</summary>
        public static int FailedWriteCount { get; private set; }

        // ------------------------------------------------------------------ load

        /// <summary>
        /// Loads a save into <paramref name="progress"/>. A missing save is a normal first run. A damaged save
        /// never throws at the caller: it is preserved next to the slot and the backup is used instead.
        /// </summary>
        public static WorldSaveLoadResult Load(WorldProgress progress)
        {
            if (progress == null)
                return new WorldSaveLoadResult(WorldSaveLoadOutcome.FreshStart, "no progress object");

            if (!File.Exists(SavePath))
            {
                if (File.Exists(BackupPath))
                    return TryLoadFrom(BackupPath, progress, WorldSaveLoadOutcome.LoadedFromBackup, "primary save missing");

                progress.Clear();
                return new WorldSaveLoadResult(WorldSaveLoadOutcome.FreshStart, "no save file");
            }

            string raw;
            try
            {
                raw = File.ReadAllText(SavePath);
            }
            catch (Exception ex)
            {
                return new WorldSaveLoadResult(WorldSaveLoadOutcome.RecoveredAfterCorruption, "unreadable primary: " + ex.Message);
            }

            if (!TryParse(raw, progress, out WorldSaveLoadResult parsed))
            {
                // A save from a newer version is not corruption: it must be refused as it stands, not replaced by
                // an older backup that would silently roll the player's progress backwards.
                return parsed.Outcome == WorldSaveLoadOutcome.RejectedNewerSchema
                    ? parsed
                    : RecoverFromCorruption(progress, parsed.Detail);
            }

            return parsed;
        }

        private static WorldSaveLoadResult TryLoadFrom(string path, WorldProgress progress, WorldSaveLoadOutcome outcome, string detail)
        {
            try
            {
                string raw = File.ReadAllText(path);
                if (TryParse(raw, progress, out WorldSaveLoadResult parsed))
                    return new WorldSaveLoadResult(outcome, detail + " (" + parsed.Detail + ")");

                if (parsed.Outcome == WorldSaveLoadOutcome.RejectedNewerSchema)
                    return parsed;
            }
            catch (Exception ex)
            {
                return new WorldSaveLoadResult(WorldSaveLoadOutcome.RecoveredAfterCorruption, path + ": " + ex.Message);
            }

            return RecoverFromCorruption(progress, "backup unusable");
        }

        private static bool TryParse(string raw, WorldProgress progress, out WorldSaveLoadResult result)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                result = new WorldSaveLoadResult(WorldSaveLoadOutcome.RecoveredAfterCorruption, "empty save file");
                return false;
            }

            WorldSaveData data;
            try
            {
                data = JsonUtility.FromJson<WorldSaveData>(raw);
            }
            catch (Exception ex)
            {
                result = new WorldSaveLoadResult(WorldSaveLoadOutcome.RecoveredAfterCorruption, "unparseable JSON: " + ex.Message);
                return false;
            }

            if (data == null)
            {
                result = new WorldSaveLoadResult(WorldSaveLoadOutcome.RecoveredAfterCorruption, "JSON produced no data");
                return false;
            }

            if (data.schemaVersion > WorldProgress.CurrentSchemaVersion)
            {
                // A newer save must not be silently downgraded: loading half of a future format would lose data.
                progress.Clear();
                result = new WorldSaveLoadResult(WorldSaveLoadOutcome.RejectedNewerSchema,
                    $"save schema {data.schemaVersion} is newer than supported {WorldProgress.CurrentSchemaVersion}");
                return false;
            }

            progress.LoadFrom(data);
            result = new WorldSaveLoadResult(WorldSaveLoadOutcome.Loaded, $"schema {data.schemaVersion}");
            return true;
        }

        private static WorldSaveLoadResult RecoverFromCorruption(WorldProgress progress, string detail)
        {
            TryPreserveCorruptFile();

            if (File.Exists(BackupPath))
                return TryLoadFrom(BackupPath, progress, WorldSaveLoadOutcome.RecoveredAfterCorruption, detail);

            progress.Clear();
            return new WorldSaveLoadResult(WorldSaveLoadOutcome.RecoveredAfterCorruption, detail + "; no backup, starting fresh");
        }

        private static void TryPreserveCorruptFile()
        {
            try
            {
                if (File.Exists(SavePath))
                    File.Copy(SavePath, CorruptPath, true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WorldSaveStore] Could not preserve the damaged save: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ save

        /// <summary>
        /// Writes the progress atomically. Returns false (with a Console warning) instead of throwing, because
        /// a full or locked disk must not take a running game down; the caller reports the failure.
        /// </summary>
        public static bool Save(WorldProgress progress)
        {
            if (!Enabled)
                return false;

            if (progress == null)
                return false;

            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                string json = JsonUtility.ToJson(progress.ToSaveData(), true);
                File.WriteAllText(TempPath, json);

                if (File.Exists(SavePath))
                {
                    // Replace keeps the previous file as the backup in one step; without a previous file the
                    // temp file simply becomes the save.
                    File.Replace(TempPath, SavePath, BackupPath, true);
                }
                else
                {
                    File.Move(TempPath, SavePath);
                }

                WriteCount++;
                return true;
            }
            catch (Exception ex)
            {
                FailedWriteCount++;
                Debug.LogWarning($"[WorldSaveStore] Save failed: {ex.Message}");
                TryDeleteTemp();
                return false;
            }
        }

        private static void TryDeleteTemp()
        {
            try
            {
                if (File.Exists(TempPath))
                    File.Delete(TempPath);
            }
            catch
            {
                // Nothing useful can be done; the next successful write replaces the temp file anyway.
            }
        }

        /// <summary>Deletes the slot, the backup and any quarantined file. Used by verification only.</summary>
        public static void DeleteAll()
        {
            foreach (string path in new[] { SavePath, BackupPath, TempPath, CorruptPath })
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[WorldSaveStore] Could not delete '{path}': {ex.Message}");
                }
            }

            WriteCount = 0;
            FailedWriteCount = 0;
        }
    }
}
