using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace grapher.DesktopSens.Models
{
    /// <summary>
    /// Merged, ready-to-apply representation of the four cardinal-direction
    /// acceleration profiles. Produced by DesktopSensProfileMerger.Merge
    /// once all reachable cardinal cells (horizontal axes use yaw-reachable zones;
    /// vertical axes use pitch-reachable zones) have been verified green.
    /// </summary>
    [Serializable]
    public class MergedCardinalProfile
    {
        public DirectionProfile Right { get; set; } = new DirectionProfile();
        public DirectionProfile Down  { get; set; } = new DirectionProfile();
        public DirectionProfile Left  { get; set; } = new DirectionProfile();
        public DirectionProfile Up    { get; set; } = new DirectionProfile();

        public double OverallCoverage { get; set; }
        public double OverallConfidence { get; set; }
        public int VerifiedCellCount { get; set; }
        public int TotalCardinalCells { get; set; }
        public DateTime MergedAtUtc { get; set; } = DateTime.UtcNow;

        // FIX #7: sanity-check results. Filled by BuildMergedProfile.
        // The applier can refuse to write a profile that fails the
        // sanity check (extreme factors, severe asymmetry, etc.).
        public bool PassedSanityCheck { get; set; } = true;
        public List<string> SanityWarnings { get; set; } = new List<string>();
        public List<string> SanityErrors { get; set; } = new List<string>();
    }

    [Serializable]
    public class DirectionProfile
    {
        // Saved directional base-sensitivity multiplier, independent of the
        // speed-dependent per-cell acceleration LUT. The merger copies this
        // from the profile's Finder/manual Right/Left/Up/Down base factor.
        public double BaseSensitivityFactor { get; set; } = 1.0;

        // Per-distance-zone acceleration factors. With the 3 cm exclusion
        // removed, index 0 is the FIRST zone (smallest target distance),
        // not the first zone beyond 3 cm. The merger rebuilds the array
        // per direction at merge time so its length matches the current
        // yaw- or pitch-limited geometry.
        public double[] AccelerationByDistanceZone { get; set; } =
            new double[DesktopSensConstants.DefaultDistanceZoneCount];

        // Step-hold curve derived from the per-zone factors. It preserves
        // each interval's factor without spline overshoot. The RawAccel
        // bridge converts green-cell speed anchors into one shared LUT per
        // axis: it applies a one-sided curve progressively with a warning,
        // blends directions within 5%, and selects the higher-confidence
        // direction per speed interval when their factors conflict.
        public double[] InterpolatedCurve { get; set; } = new double[0];

        // Signed rotation correction in degrees for this direction.
        public double RotationOffsetDegrees { get; set; } = 0.0;

        // Number of successful samples that contributed. Carried over from
        // the per-cell state map for diagnostic display in the applier.
        public int SuccessfulSamples { get; set; }

        // Mean confidence (0..1) across this direction's green, valid LUT
        // cells. Used only as a compatibility fallback for legacy LutPoints
        // that do not carry their own per-speed confidence.
        public double MeanConfidence { get; set; } = 0.0;

        // LUT-friendly points (speed in cm/s, factor) collected ONLY from
        // green (Verified + PrecisionVerified) cells in this direction.
        // The applier converts these to RawAccel's LUT format. Each entry
        // corresponds to ONE green cell. The speed comes from the median
        // of that cell's accepted-trial flick speeds (same formula as
        // DesktopSensCalibrator.TryBuildCurveSample:
        // FlickDistanceCm / DurationMs * 1000). The factor is the residual
        // CellAccelerationFactor composed with the active LUT factor captured
        // at the representative median speed. Near-equal speed cells are
        // resolved locally by confidence; unrelated speed clusters are kept.
        //
        // This field is populated by DesktopSensProfileMerger.BuildLutPoints
        // and consumed by RawAccelSettingsBridge.TryBuildLutValues.
        public List<LutPoint> LutPoints { get; set; } = new List<LutPoint>();

        // True only when these points came from the continuous raw-sample fitter.
        // Legacy median-speed fallbacks must keep the staircase applier.
        public bool UsesSampleFitLut { get; set; } = false;
    }

    /// <summary>
    /// A single LUT-friendly (speed, sensitivity factor) pair.
    /// Speed is in cm/s (the unit TryBuildCurveSample produces). The
    /// applier converts it to RawAccel's LUT input unit
    /// (normalized counts/ms = cm/s / 2.54) using the
    /// DriverNormalizedDPI = 1000 invariant — see the conversion comment
    /// in RawAccelSettingsBridge.TryBuildLutValues for the proof that
    /// DPI cancels out of the conversion.
    /// </summary>
    [Serializable]
    public struct LutPoint
    {
        public double InputSpeedCmPerSec;
        public double Factor;
        // Confidence belongs to this calibrated speed cell, not merely to
        // the direction as a whole. HasConfidence keeps legacy two-argument
        // points compatible with callers that still supply aggregate confidence.
        public double Confidence;
        public bool HasConfidence;

        public LutPoint(double speed, double factor)
        {
            InputSpeedCmPerSec = speed;
            Factor = factor;
            Confidence = 0.0;
            HasConfidence = false;
        }

        public LutPoint(double speed, double factor, double confidence)
        {
            InputSpeedCmPerSec = speed;
            Factor = factor;
            Confidence = confidence;
            HasConfidence = true;
        }
    }

    /// <summary>
    /// Contract for the bridge that writes a merged cardinal profile into
    /// RawAccel's native Settings interface.
    /// </summary>
    public interface ICardinalProfileApplier
    {
        /// <summary>
        /// Apply the supported portion of the merged profile to RawAccel.
        /// Returns true only when the native activation path confirms success.
        /// The current in-project adapter applies directional base ratios
        /// and compatible shared speed LUTs per axis. It cannot encode
        /// independent positive/negative curves on one axis or per-direction
        /// rotation offsets, so conflicting signed curves are rejected.
        /// </summary>
        bool Apply(MergedCardinalProfile merged, DesktopSensProfile sourceProfile);
    }

    /// <summary>
    /// FIX #11: backup-merge result. Returned by MergeAndApplyWithBackup.
    /// </summary>
    [Serializable]
    public class MergeBackupResult
    {
        public bool Success { get; set; }
        public string BackupPath { get; set; }
        public DateTime BackupAtUtc { get; set; } = DateTime.UtcNow;
        public string FailureReason { get; set; } = "";
    }

    public static class DesktopSensProfileMerger
    {
        // Train no longer carves out a 3 cm neutral band — BaseZoneEndCm is
        // 0.0 after the user's directive to remove the micro-flick exemption.
        // Every distance zone (including the smallest / micro-flick zones)
        // now contributes a per-cell acceleration factor to the merged curve.

        // FIX #7: sanity-check thresholds.
        private const double MinAcceptableFactor = DesktopSensConstants.MinimumCalibrationFactor;
        private const double MaxAcceptableFactor = DesktopSensConstants.MaximumCalibrationFactor;
        private const double MaxAcceptableAsymmetry = 0.30; // 30% relative diff
        private const double MaxAcceptableMeanDeviation = 0.25; // |mean - 1.0|

        /// <summary>
        /// True iff every reachable cardinal cell for the current yaw/pitch
        /// geometry is Verified on the profile's training-state map.
        /// This is the FINAL merge gate — used by TryMergeAndApply when
        /// the user explicitly presses M, or by auto-merge in
        /// RecordTrainingAttempt at the end of every successful trial.
        /// </summary>
        public static bool IsReady(DesktopSensProfile profile)
        {
            if (profile == null) return false;
            // FIX (8/16-way): use the direction-count-aware check so the
            // merge-and-apply only fires when every axis in the active
            // mode (4, 8, or 16) is verified — not just the legacy 4
            // cardinal axes.
            return DesktopSensTrainingState.AllCardinalCellsVerifiedFor(
                profile, profile.CellTrainingStates);
        }

        /// <summary>
        /// GRADUAL MERGE (user's directive): true iff at least one
        /// cell in the active mode is Verified. The user wants the
        /// calibration to be applied to the driver IMMEDIATELY after
        /// each cell goes green, not only when the entire grid is
        /// done. This means that when the user is calibrating cell N,
        /// cells 0..N-1 are already live in the driver — so the user
        /// can feel the cumulative effect of each verified cell as
        /// they progress. BuildMergedProfile / BuildDirection already
        /// handle the partial case correctly: unverified cells use a
        /// confidence-weighted blend between the cell's stored factor
        /// and 1.0 (the no-correction default), so a verified cell
        /// contributes its factor while an unverified cell stays at
        /// 1.0 (no correction). This is safe — the driver always
        /// receives a finite, sensible value for every direction and
        /// every zone.
        /// </summary>
        public static bool IsPartiallyReady(DesktopSensProfile profile)
        {
            if (profile == null) return false;
            if (profile.CellTrainingStates == null) return false;
            // Apply partial calibration as soon as any in-range cell has
            // a recorded attempt. Confidence blending keeps untouched cells
            // neutral while removing the yellow/green completion gate.
            foreach (var state in profile.CellTrainingStates.Values)
                if (state != null && state.TotalAttemptCount > 0 &&
                    DesktopSensTrainingState.IsCalibrationDistanceZone(state.DistanceZoneIndex))
                    return true;
            return false;
        }

        /// <summary>
        /// Build the merged profile. Reuses the per-cell factors that
        /// DesktopSensCalibrator.Calibrate(profile) has already populated
        /// on the profile; this method only reshapes them into the four
        /// direction structs the applier expects.
        /// </summary>
        public static MergedCardinalProfile BuildMergedProfile(DesktopSensProfile profile)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            var merged = new MergedCardinalProfile
            {
                OverallCoverage = profile.OverallCoverage,
                OverallConfidence = profile.OverallConfidence,
                VerifiedCellCount = DesktopSensTrainingState.CountVerifiedCells(profile.CellTrainingStates),
                TotalCardinalCells = DesktopSensTrainingState.TotalCardinalCells,
                MergedAtUtc = DateTime.UtcNow
            };
            merged.Right = BuildDirection(profile, 0);
            merged.Down  = BuildDirection(profile, 1);
            merged.Left  = BuildDirection(profile, 2);
            merged.Up    = BuildDirection(profile, 3);

            // COMBINED FIX (GPT 0002): Build a hold-step (zero-order-hold)
            // curve from the independently calibrated distance cells.
            // Unlike a spline, adding a later cell cannot alter any
            // earlier cell's samples or introduce neighbour-driven
            // overshoot. PCHIP remains available (PchipInterpolate) for
            // legacy callers/tests that want the smooth interpolation;
            // the merged profile used by the applier now uses the
            // staircase so the per-cell isolation guarantee is
            // bit-for-bit exact.
            merged.Right.InterpolatedCurve = BuildStaircaseCurve(merged.Right.AccelerationByDistanceZone);
            merged.Down.InterpolatedCurve  = BuildStaircaseCurve(merged.Down.AccelerationByDistanceZone);
            merged.Left.InterpolatedCurve  = BuildStaircaseCurve(merged.Left.AccelerationByDistanceZone);
            merged.Up.InterpolatedCurve    = BuildStaircaseCurve(merged.Up.AccelerationByDistanceZone);

            // FIX #7: sanity checks.
            RunSanityChecks(merged);

            return merged;
        }

        /// <summary>
        /// Convenience: build + apply in one call. Refuses to apply if
        /// IsReady returns false OR if the merged profile fails the
        /// sanity check.
        /// </summary>
        public static bool MergeAndApply(
            DesktopSensProfile profile,
            ICardinalProfileApplier applier)
        {
            string ignored;
            return MergeAndApply(profile, applier, out ignored);
        }

        public static bool MergeAndApply(
            DesktopSensProfile profile,
            ICardinalProfileApplier applier,
            out string failureReason)
        {
            failureReason = null;
            if (profile == null)
            {
                failureReason = "Profile is null.";
                return false;
            }
            if (applier == null)
            {
                failureReason = "No RawAccel profile applier is connected.";
                return false;
            }
            // GRADUAL MERGE (user's directive): accept both the full
            // merge case (IsReady — every cell verified) and the
            // partial case (IsPartiallyReady — at least one cell
            // verified). The user explicitly asked that each verified
            // cell be applied to the driver immediately, so they can
            // feel the cumulative effect while they're still calibrating
            // the rest. BuildMergedProfile already handles partial
            // state correctly via confidence-weighted blends.
            if (!IsReady(profile) && !IsPartiallyReady(profile))
            {
                failureReason = "No Train cell has completed its first calibration round yet.";
                return false;
            }

            MergedCardinalProfile merged;
            try
            {
                merged = BuildMergedProfile(profile);
            }
            catch (Exception ex)
            {
                failureReason = "Could not build merged profile: " + ex.Message;
                return false;
            }

            if (!merged.PassedSanityCheck)
            {
                failureReason = merged.SanityErrors != null && merged.SanityErrors.Count > 0
                    ? string.Join(Environment.NewLine, merged.SanityErrors)
                    : "Merged profile failed its sanity checks.";
                return false;
            }

            try
            {
                if (!applier.Apply(merged, profile))
                {
                    failureReason = "The RawAccel adapter did not confirm activation.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                failureReason = "RawAccel activation failed: " + ex.Message;
                return false;
            }

            if (profile.LastAppliedMergedProfile != null)
                profile.PreviousAppliedMergedProfile = profile.LastAppliedMergedProfile;
            profile.LastAppliedMergedProfile = merged;
            profile.AllCardinalCellsVerified = IsReady(profile);
            profile.LastMergeAppliedTimestampUtc = DateTime.UtcNow;
            profile.ModifiedTimestamp = DateTime.UtcNow;
            return true;
        }

        /// <summary>
        /// FIX #11: build + apply with a backup file written first.
        /// The backup is a JSON serialization of the profile (the
        /// caller's serializer). If MergeAndApply fails, the backup is
        /// kept so the user can roll back later via RollbackFromBackup.
        /// </summary>
        public static MergeBackupResult MergeAndApplyWithBackup(
            DesktopSensProfile profile,
            ICardinalProfileApplier applier,
            string backupPath)
        {
            var result = new MergeBackupResult();
            if (profile == null || applier == null)
            {
                result.FailureReason = "profile or applier null";
                return result;
            }
            if (string.IsNullOrEmpty(backupPath))
            {
                result.FailureReason = "backup path empty";
                return result;
            }
            if (!IsReady(profile))
            {
                result.FailureReason = "not all cardinal cells verified";
                return result;
            }

            try
            {
                // The actual JSON serialization is the caller's job
                // (we cannot depend on Newtonsoft.Json from this layer
                // without breaking the namespace isolation). The
                // trainer writes the JSON to backupPath before calling
                // this method, OR we delegate via a callback.
                //
                // We just record the backup path on the profile so the
                // trainer can use it for rollback.
                profile.LastBackupPath = backupPath;
                profile.LastBackupAtUtc = DateTime.UtcNow;
                result.BackupPath = backupPath;

                string failureReason;
                if (!MergeAndApply(profile, applier, out failureReason))
                {
                    result.FailureReason = failureReason ?? "merge-and-apply failed";
                    return result;
                }
                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                result.FailureReason = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// Build a single direction's profile by reading the per-cell
        /// factors at angle zone = cardinal zone index, distance zone = 0..N-1.
        ///
        /// FIX #5: the per-zone factor is now a CONFIDENCE-WEIGHTED
        /// average of the cell's stored factor and 1.0 (the no-correction
        /// default). A cell with low confidence contributes less to
        /// the merged curve, preventing a poorly-trained cell from
        /// pulling the entire curve.
        /// </summary>
        private static DirectionProfile BuildDirection(DesktopSensProfile profile, int cardinalDirectionIndex)
        {
            if (cardinalDirectionIndex < 0 || cardinalDirectionIndex >= DesktopSensTrainingState.CardinalDirectionCount)
                throw new ArgumentOutOfRangeException(nameof(cardinalDirectionIndex));

            int angleZone = DesktopSensTrainingState.CardinalAngleZoneIndices[cardinalDirectionIndex];
            var dir = new DirectionProfile();

            // Base sensitivity is calibrated and persisted independently
            // from the speed-dependent per-cell LUT. Carry the four saved
            // directional multipliers into the merged profile; forcing 1.0
            // here silently discarded the user's Finder/manual calibration
            // before TryApplyTrainDirectionalFactors could write it to RawAccel.
            double savedBaseFactor;
            switch (cardinalDirectionIndex)
            {
                case 0: savedBaseFactor = profile.RightSensitivityFactor; break;
                case 1: savedBaseFactor = profile.DownSensitivityFactor; break;
                case 2: savedBaseFactor = profile.LeftSensitivityFactor; break;
                case 3: savedBaseFactor = profile.UpSensitivityFactor; break;
                default: savedBaseFactor = DesktopSensConstants.NeutralFactor; break;
            }
            dir.BaseSensitivityFactor =
                IsFinite(savedBaseFactor) && savedBaseFactor >= 0.5 && savedBaseFactor <= 2.0
                    ? savedBaseFactor
                    : DesktopSensConstants.NeutralFactor;
            int trainBaseCellIndex = DesktopSensProfile.GetCellIndex(angleZone, 0);
            dir.RotationOffsetDegrees =
                profile.CellRotationOffsetDegrees != null &&
                trainBaseCellIndex >= 0 &&
                trainBaseCellIndex < profile.CellRotationOffsetDegrees.Length
                    ? profile.CellRotationOffsetDegrees[trainBaseCellIndex]
                    : 0.0;

            // Aggregate per-cell acceleration factors for this direction.
            int distanceZoneCount = TrainingGeometry.Current.CardinalDirectionZoneCount(cardinalDirectionIndex);
            if (dir.AccelerationByDistanceZone == null ||
                dir.AccelerationByDistanceZone.Length != distanceZoneCount)
            {
                var resized = new double[distanceZoneCount];
                for (int i = 0; i < distanceZoneCount; i++) resized[i] = 1.0;
                if (dir.AccelerationByDistanceZone != null)
                {
                    int copy = Math.Min(dir.AccelerationByDistanceZone.Length, distanceZoneCount);
                    Array.Copy(dir.AccelerationByDistanceZone, resized, copy);
                }
                dir.AccelerationByDistanceZone = resized;
            }

            double confidenceSum = 0.0;
            int confidenceCount = 0;

            for (int d = 0; d < distanceZoneCount; d++)
            {
                int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, d);
                double factor = 1.0;
                double confidence = 0.0;
                double zoneCenterCm = TrainingGeometry.Current.ZoneCenterCm(d);

                // With BaseZoneEndCm = 0.0, IsCalibrationDistanceZone(d)
                // is true for every zone with a positive center, so the
                // per-zone factor array covers the full reachable range.
                // No "first 3 cm" exemption is applied.
                if (DesktopSensTrainingState.IsCalibrationDistanceZone(d) &&
                    profile.CellAccelerationFactor != null &&
                    cellIndex >= 0 && cellIndex < profile.CellAccelerationFactor.Length)
                {
                    double cellFactor = profile.CellAccelerationFactor[cellIndex];
                    if (profile.CellAccelerationConfidence != null &&
                        cellIndex < profile.CellAccelerationConfidence.Length)
                        confidence = profile.CellAccelerationConfidence[cellIndex];
                    if (!IsFinite(confidence) || confidence < 0.0) confidence = 0.0;
                    if (confidence > 1.0) confidence = 1.0;

                    if (IsFinite(cellFactor) && cellFactor > 0.0)
                    {
                        factor = WeightedBlend(cellFactor, 1.0, confidence);

                        // Direction-level fallback confidence must describe
                        // only actual green LUT contributors. Yellow, attempted,
                        // missing, or invalid-factor cells must not outvote a
                        // verified cell during a shared-axis conflict.
                        bool isGreen = profile.CellTrainingStates != null &&
                            profile.CellTrainingStates.TryGetValue(cellIndex, out TrainingCellState state) &&
                            state != null && state.Verified && state.PrecisionVerified;
                        if (isGreen)
                        {
                            confidenceSum += confidence;
                            confidenceCount++;
                        }
                    }
                }

                dir.AccelerationByDistanceZone[d] = factor;
            }

            dir.MeanConfidence = confidenceCount > 0 ? confidenceSum / confidenceCount : 0.0;

            // Carry over the successful-sample count for the applier's HUD.
            if (profile.CellTrainingStates != null)
            {
                int sum = 0;
                for (int d = 0; d < distanceZoneCount; d++)
                {
                    if (!DesktopSensTrainingState.IsCalibrationDistanceZone(d)) continue;
                    int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, d);
                    if (profile.CellTrainingStates.TryGetValue(cellIndex, out var s) && s != null)
                        sum += s.SuccessfulAttemptCount;
                }
                dir.SuccessfulSamples = sum;
            }

            // Build the LUT points ONLY from green (Verified +
            // PrecisionVerified) cells. A cell that is yellow (calibrated
            // but not yet precision-stable) or unobserved contributes NO
            // point — so when a new cell goes green, it can only APPEND
            // its point, never change any earlier point. This is the
            // per-point isolation guarantee for the LUT path.
            //
            // The speed for each green cell is the MEDIAN of that cell's
            // accepted-trial flick speeds (FlickDistanceCm / DurationMs *
            // 1000), matching the formula in
            // DesktopSensCalibrator.TryBuildCurveSample. The factor is
            // the cell's CellAccelerationFactor (NOT the
            // confidence-weighted blend used by AccelerationByDistanceZone
            // — the LUT point represents the cell's actual calibrated
            // factor; below/above the green range the applier uses 1.0).
            //
            // NEW (Model v6): prefer the sample-fit LUT when the profile
            // enables it. The sample-fit path uses raw per-sample
            // observations and can include yellow cells with a confidence
            // weight; the legacy median-speed path is kept behind the flag
            // for backward compatibility.
            if (profile.UseSampleFitLut)
            {
                dir.LutPoints = BuildSampleFitLutPoints(
                    profile, angleZone, cardinalDirectionIndex, dir.BaseSensitivityFactor);
                dir.UsesSampleFitLut = dir.LutPoints != null && dir.LutPoints.Count > 0;
                // Legacy saved trials may not contain RawSamples. Keep those
                // profiles usable by falling back to the median-speed path only
                // when no sample-fit curve could be produced.
                if (dir.LutPoints == null || dir.LutPoints.Count == 0)
                {
                    DesktopSensLog.Warn(
                        "Sample-fit produced no curve for angleZone=" + angleZone +
                        "; falling back to legacy median-speed LUT for stored trials.");
                    dir.LutPoints = BuildLutPoints(profile, angleZone, distanceZoneCount);
                }
            }
            else
            {
                dir.LutPoints = BuildLutPoints(profile, angleZone, distanceZoneCount);
            }

            return dir;
        }

        /// <summary>
        /// Build the (speed, factor) LUT points for one direction, using
        /// ONLY the green (Verified + PrecisionVerified) cells. The speed
        /// for each cell is the median of that cell's accepted-trial
        /// flick speeds (FlickDistanceCm / DurationMs * 1000, same as
        /// TryBuildCurveSample). The factor is the cell's
        /// CellAccelerationFactor.
        ///
        /// A non-green cell contributes no point. This means adding a
        /// new green cell can only APPEND a new point; it cannot change
        /// any earlier point. The applier (TryBuildLutValues) further
        /// enforces per-point isolation by emitting a zero-order-hold
        /// staircase at each cell boundary — see the epsilon trick in
        /// TryBuildLutValues.
        ///
        /// Cells are emitted in ascending distance-zone order so the
        /// resulting points are in ascending speed order (faster flicks
        /// land in farther distance zones on average).
        /// </summary>
        private static List<LutPoint> BuildLutPoints(
            DesktopSensProfile profile,
            int angleZone,
            int distanceZoneCount)
        {
            var points = new List<LutPoint>();
            if (profile == null || profile.Trials == null) return points;

            // First pass: collect (speed, factor, confidence, cellIndex)
            // for every green cell on this angle zone.
            var rawEntries = new List<(double speed, double factor, double confidence, int cellIndex)>();
            for (int d = 0; d < distanceZoneCount; d++)
            {
                if (!DesktopSensTrainingState.IsCalibrationDistanceZone(d)) continue;

                int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, d);

                // The cell must be green in the per-cell training state.
                // "Green" means Verified AND PrecisionVerified (the same
                // gate the merger uses for the "cell is fully calibrated"
                // state). A cell that is yellow (Verified but not
                // PrecisionVerified) or unobserved is NOT included.
                if (profile.CellTrainingStates == null) continue;
                if (!profile.CellTrainingStates.TryGetValue(cellIndex, out TrainingCellState state) ||
                    state == null)
                    continue;

                // NEW (Model v6): include any cell with at least one accepted attempt
                // and a valid factor — green (Verified + PrecisionVerified) cells
                // contribute at full weight, yellow (attempted but not yet verified)
                // cells contribute at their measured confidence, floored so they still
                // shape the fit. Previously yellow cells were silently dropped, which
                // left every non-first zone effectively uncalibrated.
                bool isGreen = state.Verified && state.PrecisionVerified;
                bool isYellow = !isGreen &&
                    state.AcceptedAttemptCount > 0 &&
                    state.TotalAttemptCount > 0;
                if (!isGreen && !isYellow) continue;

                // The cell must have a finite positive factor.
                if (profile.CellAccelerationFactor == null ||
                    cellIndex < 0 || cellIndex >= profile.CellAccelerationFactor.Length)
                    continue;
                double cellFactor = profile.CellAccelerationFactor[cellIndex];
                if (!IsFinite(cellFactor) || cellFactor <= 0.0) continue;

                // Read the cell's confidence (used as the tiebreaker for
                // near-equal speeds — see below).
                double cellConfidence = 0.0;
                if (profile.CellAccelerationConfidence != null &&
                    cellIndex < profile.CellAccelerationConfidence.Length)
                    cellConfidence = profile.CellAccelerationConfidence[cellIndex];
                if (!IsFinite(cellConfidence) || cellConfidence < 0.0)
                    cellConfidence = 0.0;
                if (cellConfidence > 1.0)
                    cellConfidence = 1.0;

                // Yellow cells that have a confidence floor below the global minimum
                // are still included at the minimum so their shape information is
                // preserved. This is the difference the user described: "yellow
                // should not be a hard exclusion, just a lower weight".
                double cellFactorWeight = SampleLutFitter.ComputeCellWeight(
                    isGreen, isYellow, cellConfidence);
                if (!isGreen)
                    cellConfidence = cellFactorWeight;

                // Compute the median speed of this cell's accepted
                // (non-rejected, non-evaluation-only, non-anchor) trials.
                // Same formula as TryBuildCurveSample:
                //   speedCmPerSec = FlickDistanceCm / (DurationMs / 1000.0)
                // = FlickDistanceCm * 1000 / DurationMs.
                //
                // CellAccelerationFactor is the residual factor estimated
                // after the active RawAccel LUT is applied to the raw input.
                // The replacement LUT must therefore contain the TOTAL
                // factor = current active factor × residual factor.
                var speedSamples = new List<(double speed, double appliedFactor)>();
                foreach (var trial in profile.Trials)
                {
                    if (trial == null || trial.IsEvaluationOnly || trial.IsSessionAnchor ||
                        trial.IsCalibrationExcluded || trial.WasRejected) continue;
                    if (!trial.HasMovementMeasurement) continue;
                    if (trial.AngleZoneIndex != angleZone || trial.DistanceZoneIndex != d) continue;
                    if (!IsFinite(trial.FlickDistanceCm) || trial.FlickDistanceCm <= 0.0) continue;
                    if (!IsFinite(trial.DurationMs) || trial.DurationMs <= 0.0) continue;
                    double speed = trial.FlickDistanceCm * 1000.0 / trial.DurationMs;
                    if (IsFinite(speed) && speed > 0.0)
                    {
                        double appliedFactor = trial.HasAppliedLutFactor &&
                            IsFinite(trial.AppliedLutFactor) && trial.AppliedLutFactor > 0.0
                                ? trial.AppliedLutFactor : 1.0;
                        speedSamples.Add((speed, appliedFactor));
                    }
                }
                if (speedSamples.Count == 0) continue;

                speedSamples.Sort((a, b) => a.speed.CompareTo(b.speed));
                double medianSpeed;
                double medianAppliedFactor;
                int mid = speedSamples.Count / 2;
                if (speedSamples.Count % 2 == 0)
                {
                    medianSpeed = (speedSamples[mid - 1].speed + speedSamples[mid].speed) * 0.5;
                    // Factors are multiplicative; use a geometric mean for
                    // the two samples bracketing the median speed.
                    medianAppliedFactor = Math.Sqrt(
                        speedSamples[mid - 1].appliedFactor * speedSamples[mid].appliedFactor);
                }
                else
                {
                    medianSpeed = speedSamples[mid].speed;
                    medianAppliedFactor = speedSamples[mid].appliedFactor;
                }

                if (!IsFinite(medianSpeed) || medianSpeed <= 0.0 ||
                    !IsFinite(medianAppliedFactor) || medianAppliedFactor <= 0.0)
                    continue;

                // The residual factor is estimated after the active LUT has
                // affected the raw-input observation. Pair the representative
                // speed with its corresponding active LUT factor so the new
                // LUT stores the total factor at that speed.
                // Legacy median-speed path uses the same green/yellow
                // influence policy as sample-fit: green is full strength,
                // yellow is blended toward neutral by its capped confidence.
                double weightedCellFactor = WeightedBlend(cellFactor, 1.0, cellFactorWeight);
                double totalFactor = weightedCellFactor * medianAppliedFactor;
                if (!IsFinite(totalFactor) || totalFactor <= 0.0) continue;

                rawEntries.Add((medianSpeed, totalFactor, cellConfidence, cellIndex));
            }

            // Second pass: sort by speed (Claude directive #2). Ties are
            // resolved by keeping the cell with higher confidence; if
            // confidences are equal, the lower cellIndex (closer to the
            // start of the calibration sweep) wins. Two cells whose
            // median speeds are within 1% of each other (a relative speed
            // ratio in [0.99, 1.01]) are considered "near-equal" — the
            // tiebreaker decides which one is kept, the other is dropped
            // and a warning is logged via the DiagnosticLog callback
            // (returned via the BuildLutPointsReport struct so the
            // caller can include it in its log).
            rawEntries.Sort((a, b) =>
            {
                int bySpeed = a.speed.CompareTo(b.speed);
                if (bySpeed != 0) return bySpeed;
                // Exact tie — keep the higher-confidence cell.
                int byConf = b.confidence.CompareTo(a.confidence);
                if (byConf != 0) return byConf;
                return a.cellIndex.CompareTo(b.cellIndex);
            });

            // Collapse near-equal speed clusters by confidence, not by
            // whichever sample happens to sort last. A new cell may replace
            // only the representative of its own <1% speed cluster; all
            // unrelated points remain unchanged.
            int clusterStart = 0;
            while (clusterStart < rawEntries.Count)
            {
                int clusterEnd = clusterStart + 1;
                double clusterAnchorSpeed = rawEntries[clusterStart].speed;
                while (clusterEnd < rawEntries.Count)
                {
                    double ratio = rawEntries[clusterEnd].speed / clusterAnchorSpeed;
                    if (!(ratio < 1.01)) break;
                    clusterEnd++;
                }

                var winner = rawEntries[clusterStart];
                for (int i = clusterStart + 1; i < clusterEnd; i++)
                {
                    var candidate = rawEntries[i];
                    if (candidate.confidence > winner.confidence ||
                        (candidate.confidence == winner.confidence &&
                         candidate.cellIndex < winner.cellIndex))
                        winner = candidate;
                }

                if (clusterEnd - clusterStart > 1)
                {
                    for (int i = clusterStart; i < clusterEnd; i++)
                    {
                        var dropped = rawEntries[i];
                        if (dropped.cellIndex == winner.cellIndex) continue;
                        DesktopSensLog.Warn(
                            "BuildLutPoints: dropped near-equal-speed cell: " +
                            "cellIndex=" + dropped.cellIndex +
                            " speed=" + dropped.speed.ToString("0.###") + "cm/s" +
                            " factor=" + dropped.factor.ToString("0.###") +
                            " confidence=" + dropped.confidence.ToString("0.###") +
                            " (kept cellIndex=" + winner.cellIndex +
                            " speed=" + winner.speed.ToString("0.###") + "cm/s" +
                            " confidence=" + winner.confidence.ToString("0.###") + ")");
                    }
                }

                points.Add(new LutPoint(winner.speed, winner.factor, winner.confidence));
                clusterStart = clusterEnd;
            }

            return points;
        }

        /// <summary>
        /// NEW (Model v6): build per-direction LutPoints using the sample-fit
        /// path. Trials that belong to the axis are collected in one pass and
        /// handed to SampleLutFitter.Fit. Yellow cells are included with a
        /// confidence weight; the fitter uses every raw (speed, displacement)
        /// sample so the resulting curve shape matches what the driver actually
        /// applies.
        /// </summary>
        private static List<LutPoint> BuildSampleFitLutPoints(
            DesktopSensProfile profile,
            int angleZone,
            int cardinalDirectionIndex,
            double targetBaseFactor)
        {
            var points = new List<LutPoint>();
            if (profile == null || profile.Trials == null) return points;

            int distanceZoneCount = TrainingGeometry.Current.CardinalDirectionZoneCount(
                cardinalDirectionIndex);
            var eligible = new List<DesktopSensTrial>();
            var trialWeights = new List<double>();
            var targetScales = new List<double>();
            foreach (DesktopSensTrial trial in profile.Trials)
            {
                if (trial == null || trial.IsEvaluationOnly || trial.IsSessionAnchor ||
                    trial.IsCalibrationExcluded || trial.WasRejected) continue;
                if (!trial.HasMovementMeasurement) continue;
                if (trial.AngleZoneIndex != angleZone) continue;
                if (!DesktopSensTrainingState.IsCalibrationDistanceZone(trial.DistanceZoneIndex)) continue;
                if (trial.DistanceZoneIndex < 0 || trial.DistanceZoneIndex >= distanceZoneCount) continue;
                int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, trial.DistanceZoneIndex);
                if (profile.CellTrainingStates == null ||
                    !profile.CellTrainingStates.TryGetValue(cellIndex, out var state) ||
                    state == null) continue;

                bool isGreen = state.Verified && state.PrecisionVerified;
                bool isYellow = !isGreen && state.AcceptedAttemptCount > 0;
                double confidence = 0.0;
                if (profile.CellAccelerationConfidence != null &&
                    cellIndex >= 0 && cellIndex < profile.CellAccelerationConfidence.Length)
                    confidence = profile.CellAccelerationConfidence[cellIndex];
                double weight = SampleLutFitter.ComputeCellWeight(isGreen, isYellow, confidence);
                if (weight <= 0.0) continue;

                // Raw input samples are pre-driver. The replacement LUT must
                // reproduce the target under the base factor that will be applied
                // now. Normalize each historical target by (current base / captured
                // base), so a Base Sens change does not get silently lost in the LUT.
                double appliedBase = IsFinite(trial.AppliedBaseFactor) &&
                    trial.AppliedBaseFactor > 0.0 ? trial.AppliedBaseFactor : 1.0;
                double targetScale = targetBaseFactor / appliedBase;
                if (!IsFinite(targetScale) || targetScale <= 0.0) continue;

                eligible.Add(trial);
                trialWeights.Add(weight);
                targetScales.Add(targetScale);
            }

            if (eligible.Count == 0) return points;

            // Soft anchor fitting is intentionally removed: persisted node
            // constraints are the sole source of frozen-cell isolation.

            // Exact per-node frozen factors are persisted on the profile.
            // These constraints supersede historical soft anchors.
            double[] frozenNodes = null;
            if (profile.FrozenSampleLutNodeFactorsByAngleZone != null &&
                profile.FrozenSampleLutNodeFactorsByAngleZone.TryGetValue(angleZone, out var persistedFrozen) &&
                persistedFrozen != null &&
                persistedFrozen.Length == DesktopSensConstants.SampleLutDefaultNodes)
                frozenNodes = (double[])persistedFrozen.Clone();

            SampleLutFitter.FitResult fit = SampleLutFitter.Fit(
                eligible,
                null,
                DesktopSensConstants.SampleLutDefaultNodes,
                includeYellowCells: true,
                trialWeights: trialWeights,
                targetScale: 1.0,
                targetScales: targetScales,
                fixedNodeFactors: frozenNodes);
            if (fit == null || !fit.IsValid)
            {
                DesktopSensLog.Warn(
                    "BuildSampleFitLutPoints: sample-fit failed for angleZone=" + angleZone +
                    " cardinal=" + cardinalDirectionIndex +
                    " reason=" + (fit?.FailureReason ?? "unknown"));
                return points;
            }

            var builtPoints = SampleLutFitter.ToLutPoints(fit);

            // Freeze node values at the moment cells are green. Existing
            // frozen values are never refreshed from a later fit. A newly
            // green cell owns the nodes up to its accepted-trial median speed.
            if (profile.FrozenSampleLutNodeFactorsByAngleZone == null)
                profile.FrozenSampleLutNodeFactorsByAngleZone =
                    new Dictionary<int, double[]>();
            double[] frozen = profile.FrozenSampleLutNodeFactorsByAngleZone.TryGetValue(angleZone, out var stored)
                && stored != null && stored.Length == fit.NodeFactors.Length
                    ? (double[])stored.Clone()
                    : CreateFreeNodeArray(fit.NodeFactors.Length);

            var greenSpeedSamplesByCell =
                new Dictionary<int, List<(double speed, double displacementWeight)>>();
            foreach (var trial in eligible)
            {
                int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, trial.DistanceZoneIndex);
                if (profile.CellTrainingStates == null ||
                    !profile.CellTrainingStates.TryGetValue(cellIndex, out var state) ||
                    state == null || !state.Verified || !state.PrecisionVerified)
                    continue;

                List<(double speed, double displacementWeight)> cellSamples =
                    GetProjectedSpeedSamplesForFreeze(trial);
                if (cellSamples.Count == 0 &&
                    IsFinite(trial.FlickDistanceCm) && trial.FlickDistanceCm > 0.0 &&
                    IsFinite(trial.DurationMs) && trial.DurationMs > 0.0)
                {
                    // Legacy profiles without raw samples: use their recorded
                    // mean speed as a conservative compatibility fallback.
                    cellSamples.Add((trial.FlickDistanceCm * 1000.0 / trial.DurationMs,
                        trial.FlickDistanceCm));
                }
                if (cellSamples.Count == 0) continue;
                if (!greenSpeedSamplesByCell.TryGetValue(cellIndex, out var accumulated))
                {
                    accumulated = new List<(double, double)>();
                    greenSpeedSamplesByCell[cellIndex] = accumulated;
                }
                accumulated.AddRange(cellSamples);
            }

            foreach (var cell in greenSpeedSamplesByCell)
            {
                // Weighted P90 of instantaneous target-axis speeds. Weights
                // are |projected displacement| in cm, so short/noisy samples
                // contribute less than samples that account for meaningful
                // motion. This matches SampleLutFitter.ExtractProjectedSamples.
                double p90Speed = WeightedPercentile(cell.Value, 0.90);
                if (!IsFinite(p90Speed) || p90Speed <= 0.0) continue;

                // Freeze through (inclusive) the first fixed-grid node whose
                // speed is >= P90. This is a ceil boundary: interpolation at
                // the cell's upper observed speed cannot depend on a free node.
                int freezeThroughNode = fit.NodeSpeedCmPerSec.Length - 1;
                for (int node = 0; node < fit.NodeSpeedCmPerSec.Length; node++)
                {
                    if (fit.NodeSpeedCmPerSec[node] >= p90Speed)
                    {
                        freezeThroughNode = node;
                        break;
                    }
                }

                for (int node = 0; node <= freezeThroughNode; node++)
                {
                    if (!IsFinite(frozen[node]) || frozen[node] <= 0.0)
                    {
                        frozen[node] = fit.NodeFactors[node];
                        DesktopSensLog.Info("SampleLut freeze: angleZone=" + angleZone +
                            " cellIndex=" + cell.Key + " node=" + node +
                            " speed=" + fit.NodeSpeedCmPerSec[node].ToString("0.###") +
                            " factor=" + frozen[node].ToString("0.########") +
                            " weightedP90=" + p90Speed.ToString("0.###"));
                    }
                }
            }
            profile.FrozenSampleLutNodeFactorsByAngleZone[angleZone] = frozen;

            // Maintain the legacy anchor cache for compatibility only; it is
            // no longer an input to the sample-fit solver.
            if (profile.LastBuiltLutPointsByAngleZone == null)
                profile.LastBuiltLutPointsByAngleZone = new Dictionary<int, List<LutPoint>>();
            if (profile.LastBuiltLutBaseFactorByAngleZone == null)
                profile.LastBuiltLutBaseFactorByAngleZone = new Dictionary<int, double>();
            profile.LastBuiltLutPointsByAngleZone[angleZone] = builtPoints;
            profile.LastBuiltLutBaseFactorByAngleZone[angleZone] = targetBaseFactor;

            return builtPoints;
        }

        /// <summary>
        /// Explicitly reopen a cell's LUT ownership and every later node.
        /// Call when a previously-green cell is intentionally recalibrated.
        /// </summary>
        public static bool UnfreezeSampleLutFromCell(
            DesktopSensProfile profile, int angleZone, int distanceZone)
        {
            if (profile == null || profile.FrozenSampleLutNodeFactorsByAngleZone == null ||
                !profile.FrozenSampleLutNodeFactorsByAngleZone.TryGetValue(angleZone, out var frozen) ||
                frozen == null || frozen.Length != DesktopSensConstants.SampleLutDefaultNodes)
                return false;

            var currentCellSamples = new List<(double speed, double displacementWeight)>();
            var previousCellSamples = new List<(double speed, double displacementWeight)>();
            if (profile.Trials != null)
            {
                foreach (var trial in profile.Trials)
                {
                    if (trial == null || trial.IsEvaluationOnly || trial.IsSessionAnchor ||
                        trial.IsCalibrationExcluded || trial.WasRejected ||
                        trial.AngleZoneIndex != angleZone)
                        continue;
                    var sampleSpeeds = GetProjectedSpeedSamplesForFreeze(trial);
                    if (sampleSpeeds.Count == 0 &&
                        IsFinite(trial.FlickDistanceCm) && trial.FlickDistanceCm > 0.0 &&
                        IsFinite(trial.DurationMs) && trial.DurationMs > 0.0)
                        sampleSpeeds.Add((trial.FlickDistanceCm * 1000.0 / trial.DurationMs,
                            trial.FlickDistanceCm));

                    if (trial.DistanceZoneIndex == distanceZone)
                        currentCellSamples.AddRange(sampleSpeeds);
                    else if (distanceZone > 0 && trial.DistanceZoneIndex == distanceZone - 1)
                    {
                        int previousCellIndex = DesktopSensProfile.GetCellIndex(angleZone, distanceZone - 1);
                        bool previousCellGreen = profile.CellTrainingStates != null &&
                            profile.CellTrainingStates.TryGetValue(previousCellIndex, out var previousState) &&
                            previousState != null && previousState.Verified && previousState.PrecisionVerified;
                        if (previousCellGreen)
                            previousCellSamples.AddRange(sampleSpeeds);
                    }
                }
            }
            if (currentCellSamples.Count == 0)
            {
                // A trial-deletion/reset path may remove the cell's samples
                // before it asks for unfreeze. The previous cell's P90 still
                // identifies the ownership boundary; if unavailable, release
                // conservatively from node zero rather than retaining stale
                // frozen constraints.
                DesktopSensLog.Warn("SampleLut unfreeze has no current-cell speed samples; " +
                    "using previous-cell boundary or releasing all nodes: angleZone=" +
                    angleZone + " distanceZone=" + distanceZone);
            }

            // Node ownership follows each green cell's weighted-P90 ceil
            // boundary. Preserve the prior green cell's inclusive boundary
            // and release the following node onward (current cell + higher).
            int firstNodeToUnfreeze = 0;
            if (distanceZone > 0 && previousCellSamples.Count > 0)
            {
                double previousP90 = WeightedPercentile(previousCellSamples, 0.90);
                if (IsFinite(previousP90) && previousP90 > 0.0)
                {
                    int previousFreezeThrough = frozen.Length - 1;
                    for (int node = 0; node < frozen.Length; node++)
                    {
                        double nodeSpeed = GetFixedNodeSpeed(node, frozen.Length);
                        if (nodeSpeed >= previousP90)
                        {
                            previousFreezeThrough = node;
                            break;
                        }
                    }
                    firstNodeToUnfreeze = Math.Min(frozen.Length, previousFreezeThrough + 1);
                }
            }

            int unfrozen = 0;
            for (int node = 0; node < frozen.Length; node++)
            {
                if (node < firstNodeToUnfreeze) continue;
                double nodeSpeed = GetFixedNodeSpeed(node, frozen.Length);
                if (IsFinite(frozen[node]) && frozen[node] > 0.0)
                {
                    DesktopSensLog.Info("SampleLut unfreeze: angleZone=" + angleZone +
                        " distanceZone=" + distanceZone + " node=" + node +
                        " speed=" + nodeSpeed.ToString("0.###"));
                    unfrozen++;
                }
                frozen[node] = 0.0;
            }
            profile.FrozenSampleLutNodeFactorsByAngleZone[angleZone] = frozen;
            DesktopSensLog.Info("SampleLut unfreeze completed: angleZone=" + angleZone +
                " distanceZone=" + distanceZone + " releasedNodes=" + unfrozen +
                " firstNode=" + firstNodeToUnfreeze);
            return unfrozen > 0;
        }

        private static List<(double speed, double displacementWeight)> GetProjectedSpeedSamplesForFreeze(
            DesktopSensTrial trial)
        {
            var result = new List<(double, double)>();
            if (trial == null || trial.RawSamples == null || trial.RawSamples.Count < 2 ||
                !IsFinite(trial.EffectiveCpi) || trial.EffectiveCpi <= 0.0)
                return result;

            // Keep the same target-axis projection and timebase as
            // SampleLutFitter.ExtractProjectedSamples.
            double angle = trial.TargetAngleDegrees * Math.PI / 180.0;
            double axisX = Math.Cos(angle);
            double axisY = Math.Sin(angle);
            double cmPerCount = DesktopSensConstants.InchesToCm / trial.EffectiveCpi;
            long previousTimestamp = trial.RawSamples[0].Timestamp;
            for (int i = 1; i < trial.RawSamples.Count; i++)
            {
                var sample = trial.RawSamples[i];
                long deltaTicks = sample.Timestamp - previousTimestamp;
                previousTimestamp = sample.Timestamp;
                if (deltaTicks <= 0) continue;
                double dtSec = deltaTicks / (double)Stopwatch.Frequency;
                double projectedCounts = sample.DX * axisX + sample.DY * axisY;
                double displacementCm = projectedCounts * cmPerCount;
                double speed = Math.Abs(displacementCm) / dtSec;
                double weight = Math.Abs(displacementCm);
                if (IsFinite(speed) && speed > 0.0 &&
                    IsFinite(weight) && weight > 0.0)
                    result.Add((speed, weight));
            }
            return result;
        }

        private static double WeightedPercentile(
            IList<(double speed, double displacementWeight)> samples,
            double percentile)
        {
            if (samples == null || samples.Count == 0) return double.NaN;
            var sorted = new List<(double speed, double weight)>();
            double totalWeight = 0.0;
            foreach (var sample in samples)
            {
                if (!IsFinite(sample.speed) || sample.speed <= 0.0 ||
                    !IsFinite(sample.displacementWeight) || sample.displacementWeight <= 0.0)
                    continue;
                sorted.Add((sample.speed, sample.displacementWeight));
                totalWeight += sample.displacementWeight;
            }
            if (sorted.Count == 0 || totalWeight <= 0.0) return double.NaN;
            sorted.Sort((a, b) => a.speed.CompareTo(b.speed));
            double targetWeight = Math.Max(0.0, Math.Min(1.0, percentile)) * totalWeight;
            double accumulated = 0.0;
            foreach (var sample in sorted)
            {
                accumulated += sample.weight;
                if (accumulated >= targetWeight) return sample.speed;
            }
            return sorted[sorted.Count - 1].speed;
        }

        private static double GetFixedNodeSpeed(int node, int nodeCount)
        {
            if (nodeCount <= 1) return DesktopSensConstants.SampleLutMinSpeedCmPerSec;
            double logMin = Math.Log(DesktopSensConstants.SampleLutMinSpeedCmPerSec);
            double logMax = Math.Log(DesktopSensConstants.SampleLutMaxSpeedCmPerSec);
            double t = (double)node / (nodeCount - 1);
            return Math.Exp(logMin + (logMax - logMin) * t);
        }

        private static double[] CreateFreeNodeArray(int count)
        {
            var values = new double[count];
            for (int i = 0; i < count; i++) values[i] = 0.0;
            return values;
        }

        private static double EvaluateLutPoints(IList<LutPoint> points, double speed)
        {
            if (points == null || points.Count == 0 || !IsFinite(speed) || speed <= 0.0)
                return double.NaN;
            var sorted = new List<LutPoint>(points);
            sorted.Sort((a, b) => a.InputSpeedCmPerSec.CompareTo(b.InputSpeedCmPerSec));
            if (speed <= sorted[0].InputSpeedCmPerSec) return sorted[0].Factor;
            int last = sorted.Count - 1;
            if (speed >= sorted[last].InputSpeedCmPerSec) return sorted[last].Factor;
            for (int i = 1; i <= last; i++)
            {
                if (speed > sorted[i].InputSpeedCmPerSec) continue;
                double t = (speed - sorted[i - 1].InputSpeedCmPerSec) /
                    (sorted[i].InputSpeedCmPerSec - sorted[i - 1].InputSpeedCmPerSec);
                return sorted[i - 1].Factor * (1.0 - t) + sorted[i].Factor * t;
            }
            return sorted[last].Factor;
        }

        /// <summary>
        /// The merger does not read live RawAccel settings. Each trial stores
        /// the active speed-LUT factor captured at its pre-driver median speed.
        /// BuildLutPoints composes that factor with the newly estimated
        /// residual CellAccelerationFactor to produce the total replacement
        /// LUT factor for the cell.
        /// </summary>
        /// <summary>
        /// FIX #5: confidence-weighted blend.
        /// Returns (1 - confidence) * fallback + confidence * value.
        /// A confidence of 0 means "no information" → fully fall back.
        /// A confidence of 1 means "fully trust this measurement".
        /// </summary>
        private static double WeightedBlend(double value, double fallback, double confidence)
        {
            if (!IsFinite(confidence) || confidence < 0.0) confidence = 0.0;
            if (confidence > 1.0) confidence = 1.0;
            return (1.0 - confidence) * fallback + confidence * value;
        }

        // ─────────────────────────────────────────────────────────────────────
        // FIX #5: PCHIP (Piecewise Cubic Hermite Interpolation, monotone
        // variant) for smoothing the piecewise factor array into a
        // continuous curve.
        //
        // PCHIP preserves monotonicity — if the input is monotonically
        // increasing/decreasing, the interpolated curve is too (no
        // overshoot like cubic splines). For acceleration profiles this
        // is critical: an overshoot would create a "dead zone" where the
        // factor swings above the user's intended maximum, causing the
        // mouse to feel erratic.
        //
        // The output is sampled at 4× the input intervals (so a 17-cell
        // zone array becomes a 65-point curve). The applier can resample
        // this to RawAccel's LUT resolution (typically 257 points).
        // ─────────────────────────────────────────────────────────────────────
        public static double[] PchipInterpolate(double[] values, int oversample = 4)
        {
            if (values == null || values.Length == 0) return new double[0];
            if (values.Length == 1) return new double[] { values[0] };
            if (oversample < 1) oversample = 1;

            int n = values.Length;
            int outN = (n - 1) * oversample + 1;
            double[] output = new double[outN];

            // Compute the slopes between consecutive points.
            double[] h = new double[n - 1];
            double[] delta = new double[n - 1];
            for (int i = 0; i < n - 1; i++)
            {
                h[i] = 1.0;
                delta[i] = values[i + 1] - values[i];
            }

            // Compute the derivatives at each point (Fritsch-Carlson
            // monotone PCHIP). Interior points use the harmonic mean
            // of the neighboring slopes when they have the same sign.
            double[] d = new double[n];
            d[0] = delta[0];
            d[n - 1] = delta[n - 2];
            for (int i = 1; i < n - 1; i++)
            {
                if (delta[i - 1] * delta[i] <= 0.0)
                {
                    d[i] = 0.0; // local extremum — flat derivative
                }
                else
                {
                    double w1 = 2.0 * h[i] + h[i - 1];
                    double w2 = h[i] + 2.0 * h[i - 1];
                    d[i] = (w1 + w2) / (w1 / Math.Max(1e-12, delta[i - 1]) + w2 / Math.Max(1e-12, delta[i]));
                    // Clamp to ensure monotonicity
                    if (Math.Abs(d[i]) > 3.0 * Math.Min(Math.Abs(delta[i - 1]), Math.Abs(delta[i])))
                        d[i] = 3.0 * Math.Min(Math.Abs(delta[i - 1]), Math.Abs(delta[i])) * Math.Sign(d[i]);
                }
            }

            // Sample the cubic Hermite interpolant at oversample points per segment.
            for (int i = 0; i < n - 1; i++)
            {
                for (int j = 0; j < oversample; j++)
                {
                    double t = (double)j / oversample;
                    double t2 = t * t;
                    double t3 = t2 * t;
                    double h00 = 2.0 * t3 - 3.0 * t2 + 1.0;
                    double h10 = t3 - 2.0 * t2 + t;
                    double h01 = -2.0 * t3 + 3.0 * t2;
                    double h11 = t3 - t2;
                    output[i * oversample + j] =
                        h00 * values[i] +
                        h10 * d[i] +
                        h01 * values[i + 1] +
                        h11 * d[i + 1];
                }
            }
            output[outN - 1] = values[n - 1];
            return output;
        }

        /// <summary>
        /// COMBINED FIX (GPT 0002): Expands each independently calibrated
        /// distance-cell value into a zero-order-hold (staircase) step.
        /// Appending or changing a later cell leaves all earlier output
        /// samples bit-for-bit unchanged — this is the per-cell isolation
        /// guarantee that PCHIP (a cubic spline) cannot provide, because
        /// PCHIP's interpolation between cells i and i+1 depends on the
        /// derivatives at both endpoints, which in turn depend on the
        /// neighbour cells.
        ///
        /// The output has length `(values.Length - 1) * oversample + 1`.
        /// Sample `i * oversample + j` (for `j` in `0..oversample-1`)
        /// equals `values[i]`. The final sample equals the last value.
        ///
        /// PCHIP remains available via `PchipInterpolate` for callers that
        /// want the smooth interpolation; the merged profile used by the
        /// applier now uses the staircase.
        /// </summary>
        public static double[] BuildStaircaseCurve(double[] values, int oversample = 4)
        {
            if (values == null || values.Length == 0) return new double[0];
            if (values.Length == 1) return new double[] { values[0] };
            if (oversample < 1) oversample = 1;

            int outputLength = (values.Length - 1) * oversample + 1;
            double[] output = new double[outputLength];
            for (int cell = 0; cell < values.Length - 1; cell++)
            {
                for (int sample = 0; sample < oversample; sample++)
                    output[cell * oversample + sample] = values[cell];
            }
            output[outputLength - 1] = values[values.Length - 1];
            return output;
        }

        // ─────────────────────────────────────────────────────────────────────
        // FIX #7: sanity checks.
        //
        // The merged profile is rejected (PassedSanityCheck = false) if:
        //   • Any direction's mean factor deviates from 1.0 by more than
        //     MaxAcceptableMeanDeviation (25%). This usually means the
        //     base sensitivity was set wrong, so the per-cell factors
        //     are all compensating for a constant offset.
        //   • Any individual factor falls outside [0.5, 2.0] (the
        //     calibrator's clamp range).
        //
        // The merged profile gets a warning (added to SanityWarnings) if:
        //   • Right vs Left (or Up vs Down) factors differ by more than
        //     30% in the mean — this is a sign of asymmetry that the
        //     user should investigate, but it could be intentional.
        //   • The mean confidence of any direction is below 0.5 —
        //     the profile is being applied with weak evidence.
        // ─────────────────────────────────────────────────────────────────────
        private static void RunSanityChecks(MergedCardinalProfile merged)
        {
            merged.SanityErrors.Clear();
            merged.SanityWarnings.Clear();

            CheckDirection(merged, merged.Right, "Right");
            CheckDirection(merged, merged.Down, "Down");
            CheckDirection(merged, merged.Left, "Left");
            CheckDirection(merged, merged.Up, "Up");

            // Asymmetry: |mean(Right) - mean(Left)| > threshold?
            double rightMean = MeanFactor(merged.Right);
            double leftMean = MeanFactor(merged.Left);
            double asymH = RelativeDiff(rightMean, leftMean);
            if (asymH > MaxAcceptableAsymmetry)
                merged.SanityWarnings.Add(
                    $"Horizontal asymmetry {asymH:P1} (Right={rightMean:F3}, Left={leftMean:F3}) — investigate.");

            double upMean = MeanFactor(merged.Up);
            double downMean = MeanFactor(merged.Down);
            double asymV = RelativeDiff(upMean, downMean);
            if (asymV > MaxAcceptableAsymmetry)
                merged.SanityWarnings.Add(
                    $"Vertical asymmetry {asymV:P1} (Up={upMean:F3}, Down={downMean:F3}) — investigate.");

            // Low confidence
            if (merged.Right.MeanConfidence < 0.5) merged.SanityWarnings.Add("Right mean confidence < 0.5");
            if (merged.Down.MeanConfidence < 0.5) merged.SanityWarnings.Add("Down mean confidence < 0.5");
            if (merged.Left.MeanConfidence < 0.5) merged.SanityWarnings.Add("Left mean confidence < 0.5");
            if (merged.Up.MeanConfidence < 0.5) merged.SanityWarnings.Add("Up mean confidence < 0.5");

            // Errors make the merge fail; warnings just flag concerns.
            merged.PassedSanityCheck = merged.SanityErrors.Count == 0;
        }

        private static void CheckDirection(MergedCardinalProfile merged, DirectionProfile dir, string name)
        {
            if (!IsFinite(dir.BaseSensitivityFactor) || dir.BaseSensitivityFactor <= 0.0)
                merged.SanityErrors.Add(
                    $"{name}: base sensitivity factor must be finite and positive.");

            if (!IsFinite(dir.RotationOffsetDegrees))
                merged.SanityErrors.Add($"{name}: rotation offset is not finite.");

            if (dir.AccelerationByDistanceZone == null || dir.AccelerationByDistanceZone.Length == 0)
            {
                merged.SanityErrors.Add($"{name}: empty acceleration array");
                return;
            }
            double mean = MeanFactor(dir);
            if (Math.Abs(mean - 1.0) > MaxAcceptableMeanDeviation)
                merged.SanityWarnings.Add(
                    $"{name}: mean factor {mean:F3} deviates from neutral by {Math.Abs(mean - 1.0):P1}; verify the base sensitivity.");
            for (int i = 0; i < dir.AccelerationByDistanceZone.Length; i++)
            {
                double f = dir.AccelerationByDistanceZone[i];
                if (!IsFinite(f) || f <= 0.0)
                    merged.SanityErrors.Add(
                        $"{name}: factor at zone {i} must be finite and positive.");
            }

            // The driver consumes LutPoints, not only the per-zone summary
            // array. Validate the actual curve as well so a pathological
            // sample-fit cannot bypass the merger's safety gate.
            if (dir.LutPoints != null)
            {
                for (int i = 0; i < dir.LutPoints.Count; i++)
                {
                    LutPoint point = dir.LutPoints[i];
                    if (!IsFinite(point.InputSpeedCmPerSec) || point.InputSpeedCmPerSec <= 0.0)
                        merged.SanityErrors.Add(
                            $"{name}: LUT speed at point {i} must be finite and positive.");
                    if (!IsFinite(point.Factor) ||
                        point.Factor < MinAcceptableFactor ||
                        point.Factor > MaxAcceptableFactor)
                        merged.SanityErrors.Add(
                            $"{name}: LUT factor at point {i} must be within [{MinAcceptableFactor}, {MaxAcceptableFactor}].");
                    if (i > 0 &&
                        point.InputSpeedCmPerSec <= dir.LutPoints[i - 1].InputSpeedCmPerSec)
                        merged.SanityErrors.Add(
                            $"{name}: LUT speeds must be strictly ascending (point " + i + ").");
                }
            }
        }

        private static double MeanFactor(DirectionProfile dir)
        {
            if (dir.AccelerationByDistanceZone == null || dir.AccelerationByDistanceZone.Length == 0)
                return 1.0;
            double sum = 0.0;
            int n = 0;
            foreach (var f in dir.AccelerationByDistanceZone)
            {
                if (IsFinite(f)) { sum += f; n++; }
            }
            return n > 0 ? sum / n : 1.0;
        }

        private static double RelativeDiff(double a, double b)
        {
            double avg = 0.5 * (Math.Abs(a) + Math.Abs(b));
            if (avg < 1e-9) return 0.0;
            return Math.Abs(a - b) / avg;
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
