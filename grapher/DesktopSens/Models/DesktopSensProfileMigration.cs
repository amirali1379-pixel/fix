using System;
using System.Collections.Generic;

namespace grapher.DesktopSens.Models
{
    /// <summary>Versioned profile migrations testable without constructing WinForms.</summary>
    public static class DesktopSensProfileMigration
    {
        public sealed class MigrationResult
        {
            public int CellsMarkedNeedsReview;
            public int TrialsMarkedRejected;
            public int TrialsWithoutRawSamples;
        }

        /// <summary>
        /// Model v13 replaces the 12-node LUT grid. Old frozen-node arrays
        /// are index-incompatible and are cleared. Green states are retained
        /// but marked NeedsReview. Historical trials are never deleted;
        /// raw-sample trials are rechecked with the live training gate.
        /// </summary>
        public static MigrationResult MigrateToVersion13(DesktopSensProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            var result = new MigrationResult();
            if (profile.ModelVersion >= 13) return result;

            if (profile.FrozenSampleLutNodeFactorsByAngleZone == null)
                profile.FrozenSampleLutNodeFactorsByAngleZone = new Dictionary<int, double[]>();
            else
                profile.FrozenSampleLutNodeFactorsByAngleZone.Clear();

            if (profile.LastBuiltLutPointsByAngleZone != null)
                profile.LastBuiltLutPointsByAngleZone.Clear();
            if (profile.LastBuiltLutBaseFactorByAngleZone != null)
                profile.LastBuiltLutBaseFactorByAngleZone.Clear();

            if (profile.CellTrainingStates != null)
            {
                foreach (TrainingCellState state in profile.CellTrainingStates.Values)
                {
                    if (state == null || !state.Verified || !state.PrecisionVerified) continue;
                    state.NeedsReview = true;
                    result.CellsMarkedNeedsReview++;
                }
            }

            if (profile.Trials != null)
            {
                foreach (DesktopSensTrial trial in profile.Trials)
                {
                    if (trial == null || trial.WasRejected || trial.IsCalibrationExcluded ||
                        trial.IsEvaluationOnly || trial.IsSessionAnchor || !trial.HasMovementMeasurement)
                        continue;

                    if (trial.RawSamples == null || trial.RawSamples.Count < 2 ||
                        !IsFinite(trial.EffectiveCpi) || trial.EffectiveCpi <= 0.0)
                    {
                        // Preserve legacy trials that lack the data required by the gate.
                        result.TrialsWithoutRawSamples++;
                        continue;
                    }

                    int distanceZone = Math.Max(0, trial.DistanceZoneIndex);
                    double zoneStartCm = distanceZone > 0
                        ? TrainingGeometry.Current.ZoneCenterCm(distanceZone - 1)
                        : 0.0;
                    DesktopSensTrialGateResult gate = DesktopSensTrialGate.Evaluate(
                        trial, zoneStartCm, distanceZone == 0);
                    if (gate.Accepted) continue;

                    trial.WasRejected = true;
                    trial.RejectionReason = gate.Reason;
                    result.TrialsMarkedRejected++;
                }
            }

            CalibrationResidualCache.Clear(profile);
            profile.ModelVersion = 13;
            profile.ModifiedTimestamp = DateTime.UtcNow;
            DesktopSensLog.Warn(
                "DesktopSens model v13 migration: old frozen LUT nodes cleared; " +
                result.CellsMarkedNeedsReview + " green cells marked NeedsReview; " +
                result.TrialsMarkedRejected + " trials marked rejected; " +
                result.TrialsWithoutRawSamples + " legacy trials lacked raw samples and were retained.");
            return result;
        }

        /// <summary>Model v14 maps legacy cells to the matching End cell in the paired Mid/End grid.</summary>
        public static MigrationResult MigrateToVersion14(DesktopSensProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            var result = new MigrationResult();
            if (profile.ModelVersion >= 14 || !TrainingGeometry.Current.UseMidEndPoints) return result;
            if (profile.ModelVersion < 13) MigrateToVersion13(profile);

            int oldCount = TrainingGeometry.Current.DistanceZoneCount / 2;
            var mapped = new Dictionary<int, TrainingCellState>();
            if (profile.CellTrainingStates != null)
            {
                foreach (TrainingCellState state in profile.CellTrainingStates.Values)
                {
                    if (state == null || state.AngleZoneIndex < 0 ||
                        state.AngleZoneIndex >= DesktopSensConstants.AngularZoneCount ||
                        state.DistanceZoneIndex < 0 || state.DistanceZoneIndex >= oldCount) continue;
                    state.DistanceZoneIndex = state.DistanceZoneIndex * 2 + 1;
                    if (state.Verified && state.PrecisionVerified)
                    {
                        state.NeedsReview = true;
                        result.CellsMarkedNeedsReview++;
                    }
                    mapped[DesktopSensProfile.GetCellIndex(state.AngleZoneIndex, state.DistanceZoneIndex)] = state;
                }
            }
            profile.CellTrainingStates = mapped;
            if (profile.Trials != null)
                foreach (DesktopSensTrial trial in profile.Trials)
                    if (trial != null && trial.DistanceZoneIndex >= 0 && trial.DistanceZoneIndex < oldCount)
                        trial.DistanceZoneIndex = trial.DistanceZoneIndex * 2 + 1;

            if (profile.FrozenSampleLutNodeFactorsByAngleZone == null)
                profile.FrozenSampleLutNodeFactorsByAngleZone = new Dictionary<int, double[]>();
            else profile.FrozenSampleLutNodeFactorsByAngleZone.Clear();
            if (profile.LastBuiltLutPointsByAngleZone != null) profile.LastBuiltLutPointsByAngleZone.Clear();
            if (profile.LastBuiltLutBaseFactorByAngleZone != null) profile.LastBuiltLutBaseFactorByAngleZone.Clear();
            CalibrationResidualCache.Clear(profile);
            profile.ModelVersion = 14;
            profile.ModifiedTimestamp = DateTime.UtcNow;
            DesktopSensLog.Warn("DesktopSens model v14 migration: legacy cells/trials mapped to End cells; " +
                result.CellsMarkedNeedsReview + " green cells marked NeedsReview; frozen LUT nodes cleared.");
            return result;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}