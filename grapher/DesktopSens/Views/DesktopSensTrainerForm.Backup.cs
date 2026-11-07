using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using OpenTK;
using grapher.DesktopSens.Models;
using grapher.Models.Mouse;

namespace grapher.DesktopSens.Views
{
    public partial class DesktopSensTrainerForm
    {
        // FIX #11: backup before merge + rollback.
        private string GetBackupDirectory()
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RawAccel", "DesktopSens", "backups");
                System.IO.Directory.CreateDirectory(dir);
                return dir;
            }
            catch (Exception ex)
            {
                DesktopSensLog.Warn("Could not create the profile backup directory; using the temporary directory", ex);
                return System.IO.Path.GetTempPath();
            }
        }

        private void CleanupOldBackups(string directory, int maxCount = 50)
        {
            if (string.IsNullOrEmpty(directory)) return;
            string expectedDirectory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RawAccel", "DesktopSens", "backups");
            if (!string.Equals(
                System.IO.Path.GetFullPath(directory).TrimEnd(System.IO.Path.DirectorySeparatorChar),
                System.IO.Path.GetFullPath(expectedDirectory).TrimEnd(System.IO.Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                string[] files = System.IO.Directory.GetFiles(directory, "profile_backup_*.json");
                Array.Sort(files, StringComparer.Ordinal);
                int remaining = files.Length;
                for (int i = 0; i < files.Length && remaining > maxCount; i++)
                {
                    if (string.Equals(files[i], _profile != null ? _profile.LastBackupPath : null,
                        StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        System.IO.File.Delete(files[i]);
                        remaining--;
                    }
                    catch (Exception ex)
                    {
                        DesktopSensLog.Warn("Could not rotate an old profile backup", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                DesktopSensLog.Warn("Could not enumerate profile backups for rotation", ex);
            }
        }

        private bool WriteBackupBeforeMerge()
        {
            if (_profile == null) return false;
            string path = null;
            string previousPath = _profile.LastBackupPath;
            DateTime? previousBackupAt = _profile.LastBackupAtUtc;
            try
            {
                string dir = GetBackupDirectory();
                string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
                path = System.IO.Path.Combine(dir, "profile_backup_" + timestamp + ".json");
                _profile.LastBackupPath = path;
                _profile.LastBackupAtUtc = DateTime.UtcNow;
                SerializeProfileToFile(_profile, path);
                if (!System.IO.File.Exists(path))
                    throw new System.IO.IOException("Backup file was not created.");

                // Read the backup back before allowing a destructive merge.
                DesktopSensProfile verifiedBackup = DeserializeProfileFromFile(path);
                int expectedTrialCount = _profile.Trials != null ? _profile.Trials.Count : 0;
                int actualTrialCount = verifiedBackup != null && verifiedBackup.Trials != null
                    ? verifiedBackup.Trials.Count : -1;
                int expectedStateCount = _profile.CellTrainingStates != null ? _profile.CellTrainingStates.Count : 0;
                int actualStateCount = verifiedBackup != null && verifiedBackup.CellTrainingStates != null
                    ? verifiedBackup.CellTrainingStates.Count : -1;
                if (verifiedBackup == null ||
                    actualTrialCount != expectedTrialCount ||
                    actualStateCount != expectedStateCount ||
                    Math.Abs(verifiedBackup.MouseDPI - _profile.MouseDPI) > 0.0001 ||
                    Math.Abs(verifiedBackup.MeasuredCpi - _profile.MeasuredCpi) > 0.0001)
                    throw new System.IO.IOException("Backup verification failed; serialized profile did not round-trip.");

                CleanupOldBackups(dir, 50);
                UpdateFixesHud();
                return true;
            }
            catch (Exception ex)
            {
                _profile.LastBackupPath = previousPath;
                _profile.LastBackupAtUtc = previousBackupAt;
                try { if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch (Exception cleanupEx) { DesktopSensLog.Warn("Could not remove an incomplete backup file", cleanupEx); }
                DesktopSensLog.Error("Profile backup failed", ex);
                return false;
            }
        }

        private void RollbackProfileFromBackup()
        {
            if (_profile == null) return;
            if (string.IsNullOrEmpty(_profile.LastBackupPath)) return;
            string path = _profile.LastBackupPath;
            if (!System.IO.File.Exists(path)) return;
            try
            {
                var backup = DeserializeProfileFromFile(path);
                if (backup == null) return;
                string retainedBackupPath = _profile.LastBackupPath;
                backup.CurrentSessionId = _profile.CurrentSessionId;
                // Restore every public profile field onto the shared instance.
                CopyProfileFields(backup, _profile);
                _profile.LastBackupPath = retainedBackupPath;

                string applyError = null;
                bool reapplied = DesktopSensProfileMerger.IsReady(_profile) &&
                    _cardinalApplier != null &&
                    DesktopSensProfileMerger.MergeAndApply(_profile, _cardinalApplier, out applyError);

                if (_arena3D != null && !_arena3D.IsDisposed)
                {
                    _arena3D.TrainingStates = _profile.CellTrainingStates;
                    _arena3D.Invalidate();
                }
                UpdateTrainingArenaProperties();
                UpdateTrainingHud();
                UpdateFixesHud();
                OnCellVerifiedPersistProfile();

                if (!reapplied)
                    MessageBox.Show(this,
                        "The saved profile was restored, but RawAccel could not be reactivated. " +
                        (applyError ?? "The backup does not contain a complete verified cardinal profile."),
                        "Rollback partially completed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                DesktopSensLog.Error("Profile rollback failed", ex);
            }
        }

        /// <summary>
        /// Copy the calibration-relevant fields from `source` onto `target`.
        /// Used by RollbackProfileFromBackup since _profile is readonly.
        /// </summary>
        private static void CopyProfileFields(DesktopSensProfile source, DesktopSensProfile target)
        {
            if (source == null || target == null) return;
            DesktopSensProfileCopy.CopyPublicMembers(source, target);
        }

        private void SerializeProfileToFile(DesktopSensProfile profile, string path)
        {
            try
            {
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(
                    profile, Newtonsoft.Json.Formatting.Indented);
                System.IO.File.WriteAllText(path, json);
                return;
            }
            catch (Exception ex)
            {
                DesktopSensLog.Warn("Newtonsoft profile serialization failed; trying DataContract fallback", ex);
            }
            var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(
                typeof(DesktopSensProfile));
            using (var stream = System.IO.File.Create(path))
                serializer.WriteObject(stream, profile);
        }

        private DesktopSensProfile DeserializeProfileFromFile(string path)
        {
            try
            {
                var json = System.IO.File.ReadAllText(path);
                return Newtonsoft.Json.JsonConvert.DeserializeObject<DesktopSensProfile>(json);
            }
            catch (Exception ex)
            {
                DesktopSensLog.Warn("Newtonsoft profile deserialization failed; trying DataContract fallback", ex);
            }
            try
            {
                var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(
                    typeof(DesktopSensProfile));
                using (var stream = System.IO.File.OpenRead(path))
                    return (DesktopSensProfile)serializer.ReadObject(stream);
            }
            catch (Exception ex)
            {
                DesktopSensLog.Error("DataContract profile deserialization failed", ex);
                return null;
            }
        }
    }
}
