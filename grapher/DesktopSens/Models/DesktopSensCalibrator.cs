using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;


namespace grapher.DesktopSens.Models
{
    /// <summary>
    /// Per-cell observation accumulator. Each cell collects every (speed, ratio)
    /// sample that lands in its (angle, distance) bucket, plus the legacy
    /// signed-error and rotation-error lists used by the v4 single-factor model.
    /// </summary>
    public class ZoneData
    {
        public int AngleZoneIndex;
        public int DistanceZoneIndex;
        public List<double> SignedErrors = new List<double>();
        public List<double> TargetDistances = new List<double>();
        public List<DesktopSensTrial> Trials = new List<DesktopSensTrial>();
        public List<double> BaseTargetDistances = new List<double>();
        public List<double> BaseSignedErrors = new List<double>();
        public List<double> ExcessTargetDistances = new List<double>();
        public List<double> ExcessSignedErrors = new List<double>();
        public List<double> RotationErrors = new List<double>();
        // NEW (v5): (input speed, observed sensitivity ratio) pairs for curve fitting.
        public List<SensitivitySample> CurveSamples = new List<SensitivitySample>();
        public double MeanError => SignedErrors.Count > 0 ? SignedErrors.Average() : 0.0;
        public double MeanRotationError
        {
            get
            {
                if (RotationErrors.Count == 0) return 0.0;
                double sin = 0.0;
                double cos = 0.0;
                foreach (double angle in RotationErrors)
                {
                    double radians = angle * Math.PI / 180.0;
                    sin += Math.Sin(radians);
                    cos += Math.Cos(radians);
                }
                return Math.Atan2(sin, cos) * 180.0 / Math.PI;
            }
        }
        public double Variance => SignedErrors.Count > 1
            ? SignedErrors.Select(e => Math.Pow(e - MeanError, 2)).Average()
            : 0.0;

        // FIX #3: angular-error accumulator. Used to weight trials by
        // how on-axis they were. A trial that landed 12° off the target
        // axis contributes less to the cell's factor than a trial that
        // landed dead-on, because the off-axis component is mostly
        // perpendicular noise rather than along-axis calibration error.
        public List<double> AngularErrors = new List<double>();
    }

    public static class DesktopSensCalibrator
    {
        // FIX #9: trial-rejection thresholds, kept as named constants
        // so the trainer can override them if needed.
        public const int MinRawSamplesForValidTrial = 3;
        public const double MinFlickDurationMs = 40.0;
        public const double MaxFlickDurationMs = 4500.0;
        public const double MaxAngularErrorDegrees = 180.0;

        public static void Calibrate(DesktopSensProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (profile.Trials == null) profile.Trials = new List<DesktopSensTrial>();

            // The distance-zone count comes from TrainingGeometry — the trainer
            // is the central authority.
            int distanceZoneCount = TrainingGeometry.Current.DistanceZoneCount;
            int cellCount = DesktopSensConstants.AngularZoneCount * distanceZoneCount;
            CalibrationResidualCache.PruneToCellCount(profile, cellCount);
            CalibrationCellResultCache.PruneToCellCount(profile, cellCount);

            EnsureArray(ref profile.CellSensitivityFactor, cellCount, 1.0);
            EnsureArray(ref profile.CellSensitivityConfidence, cellCount, 0.0);
            EnsureArray(ref profile.CellAccelerationFactor, cellCount, 1.0);
            EnsureArray(ref profile.CellAccelerationConfidence, cellCount, 0.0);
            EnsureArray(ref profile.CellRotationOffsetDegrees, cellCount, 0.0);
            EnsureArray(ref profile.CellRotationConfidence, cellCount, 0.0);
            EnsureArray(ref profile.SensitivityFactor, DesktopSensConstants.AngularZoneCount, 1.0);
            EnsureArray(ref profile.SensitivityConfidence, DesktopSensConstants.AngularZoneCount, 0.0);
            EnsureArray(ref profile.AccelerationFactor, distanceZoneCount, 1.0);
            EnsureArray(ref profile.AccelerationConfidence, distanceZoneCount, 0.0);

            // v5 curve storage arrays.
            EnsureArray(ref profile.CellCurveMode, cellCount, (int)AccelCurveMode.NoAccel);
            EnsureArray(ref profile.CellCurveParameters, cellCount * DesktopSensConstants.MaxCurveParameters, 0.0);
            EnsureArray(ref profile.CellCurveRmse, cellCount, 0.0);
            EnsureArray(ref profile.CellCurveAic, cellCount, 0.0);
            EnsureArray(ref profile.CellCurveSampleCount, cellCount, 0);

            // Recompute from the current trial set. Never leave stale factors in cells whose
            // observations were removed or are no longer valid.
            ResetArray(profile.CellSensitivityFactor, 1.0);
            ResetArray(profile.CellSensitivityConfidence, 0.0);
            ResetArray(profile.CellAccelerationFactor, 1.0);
            ResetArray(profile.CellAccelerationConfidence, 0.0);
            ResetArray(profile.CellRotationOffsetDegrees, 0.0);
            ResetArray(profile.CellRotationConfidence, 0.0);
            ResetArray(profile.SensitivityFactor, 1.0);
            ResetArray(profile.SensitivityConfidence, 0.0);
            ResetArray(profile.AccelerationFactor, 1.0);
            ResetArray(profile.AccelerationConfidence, 0.0);
            ResetIntArray(profile.CellCurveMode, (int)AccelCurveMode.NoAccel);
            ResetArray(profile.CellCurveParameters, 0.0);
            ResetArray(profile.CellCurveRmse, 0.0);
            ResetArray(profile.CellCurveAic, 0.0);
            ResetIntArray(profile.CellCurveSampleCount, 0);

            // FIX #9: validate every trial before using it. Rejected
            // trials stay in profile.Trials (for diagnostic display) but
            // are skipped by the cell-aggregation loop below.
            double noiseFloor = profile.NoiseFloorCm;
            foreach (var trial in profile.Trials)
            {
                if (trial == null || trial.IsEvaluationOnly || trial.IsCalibrationExcluded) continue;
                trial.Validate(
                    noiseFloorCm: noiseFloor,
                    minRawSamples: MinRawSamplesForValidTrial,
                    minDurationMs: MinFlickDurationMs,
                    maxDurationMs: MaxFlickDurationMs,
                    maxAngularErrorDegrees: MaxAngularErrorDegrees);
            }

            var sessionBiasLogGain = BuildSessionBiases(profile.Trials);
            var cells = new Dictionary<int, ZoneData>();
            foreach (var trial in profile.Trials)
            {
                if (trial == null || trial.IsEvaluationOnly || trial.IsSessionAnchor ||
                    trial.IsCalibrationExcluded || trial.WasRejected || !trial.HasMovementMeasurement ||
                    trial.AngleZoneIndex < 0 || trial.AngleZoneIndex >= DesktopSensConstants.AngularZoneCount ||
                    trial.DistanceZoneIndex < 0 || trial.DistanceZoneIndex >= distanceZoneCount ||
                    !DesktopSensTrainingState.IsCalibrationDistanceZone(trial.DistanceZoneIndex) ||
                    !IsFinite(trial.TargetDistanceCm) ||
                    trial.TargetDistanceCm <= DesktopSensConstants.BaseZoneEndCm)
                    continue;

                int cellIndex = DesktopSensProfile.GetCellIndex(trial.AngleZoneIndex, trial.DistanceZoneIndex);
                if (!cells.TryGetValue(cellIndex, out ZoneData zone))
                {
                    zone = new ZoneData
                    {
                        AngleZoneIndex = trial.AngleZoneIndex,
                        DistanceZoneIndex = trial.DistanceZoneIndex
                    };
                    cells.Add(cellIndex, zone);
                }

                // Chain calibration is reconstructed in the ordered pass below.
                // Cardinal cells consume factors from earlier distance zones only
                // when those factors have positive confidence in the current pass.
                // Non-cardinal cells and distance zone 0 have no preceding factor.
                double signedError = trial.SignedErrorCm;
                if (Math.Abs(signedError) < 0.000001 &&
                    Math.Abs(trial.ProjectedDistanceCm) < 0.000001 &&
                    trial.FinalErrorCm > 0.0)
                    signedError = trial.IsUnderFlick ? -trial.FinalErrorCm : trial.FinalErrorCm;
                if (!IsFinite(signedError)) continue;

                // Retain the raw trial. Chain residuals are rebuilt in one
                // deterministic pass below using only factors already estimated
                // for earlier verified zones in this same calibration run.
                zone.Trials.Add(trial);
                zone.SignedErrors.Add(signedError);
                zone.TargetDistances.Add(trial.TargetDistanceCm);

                if (trial.HasRotationMeasurement && IsFinite(trial.SignedRotationErrorDegrees))
                    zone.RotationErrors.Add(trial.SignedRotationErrorDegrees);

                // FIX #3: angular-error accumulation.
                if (trial.HasAngularMeasurement && IsFinite(trial.AngularErrorDegrees))
                    zone.AngularErrors.Add(trial.AngularErrorDegrees);

                // COMBINED FIX (GPT 0002): collect a curve sample for
                // this exact cell. If ASDW was used, accept the sample
                // only when the final post-ASDW landing is within this
                // cell's existing adaptive tolerance. The ratio still
                // describes the raw mouse response (FlickDistanceCm /
                // TargetDistanceCm); the manual correction supplies an
                // independent confirmation that the user reached the
                // target with ASDW, and is never folded into SignedErrorCm.
                //
                // Trials WITHOUT ASDW (HasManualAsdwCorrection = false)
                // always pass this validation — they rely on the existing
                // trial.Validate() rejection path. This is the
                // documented asymmetric behaviour: a trial with ASDW is
                // stricter because the user explicitly confirmed the
                // landing; a trial without ASDW has no such confirmation
                // and falls back to the existing validation.
                double curveToleranceCm = DesktopSensConstants.DefaultToleranceCm;
                if (profile.CellTrainingStates != null &&
                    profile.CellTrainingStates.TryGetValue(cellIndex, out TrainingCellState curveState) &&
                    curveState != null &&
                    IsFinite(curveState.EffectiveToleranceCm) &&
                    curveState.EffectiveToleranceCm > 0.0)
                    curveToleranceCm = curveState.EffectiveToleranceCm;

                double sessionBias = GetSessionBias(sessionBiasLogGain, trial.SessionId);
                SensitivitySample? sample = TryBuildCurveSample(
                    trial, sessionBias, curveToleranceCm);
                if (sample.HasValue)
                    zone.CurveSamples.Add(sample.Value);
            }

            // A malformed/non-finite trial can create a cell before its error is rejected.
            // Remove empty cells before averaging so calibration cannot throw on Average().
            foreach (int emptyCell in cells
                .Where(pair => pair.Value.SignedErrors.Count == 0 || pair.Value.TargetDistances.Count == 0)
                .Select(pair => pair.Key)
                .ToList())
            {
                cells.Remove(emptyCell);
            }

            var angleErrorTotals = new double[DesktopSensConstants.AngularZoneCount];
            var angleCounts = new int[DesktopSensConstants.AngularZoneCount];
            var angleConfidenceTotals = new double[DesktopSensConstants.AngularZoneCount];
            var distanceConfidenceTotals = new double[distanceZoneCount];
            int precisionStableCells = 0;
            double overallConfidenceTotal = 0.0;
            int overallConfidenceCount = 0;
            var distanceFactorTotals = new double[distanceZoneCount];
            var distanceCounts = new int[distanceZoneCount];
            int populatedCells = 0;

            foreach (var pair in cells
                .OrderBy(p => p.Value.AngleZoneIndex)
                .ThenBy(p => p.Value.DistanceZoneIndex))
            {
                int cellIndex = pair.Key;
                ZoneData zone = pair.Value;

                // Reconstruct every residual from raw trial data and the
                // exact active LUT factor captured with that trial. The
                // factor arrays are reset at the start of Calibrate(), and
                // no earlier cell factor is replayed on top of the active LUT.
                zone.SignedErrors.Clear();
                zone.TargetDistances.Clear();
                zone.BaseTargetDistances.Clear();
                zone.BaseSignedErrors.Clear();
                zone.ExcessTargetDistances.Clear();
                zone.ExcessSignedErrors.Clear();

                // The dependency key includes exact raw inputs, per-trial
                // active LUT factors, session bias and geometry. Each cell
                // remains independent of unrelated previously calibrated cells.
                byte[] residualDependencyKey = BuildResidualCacheKey(
                    profile, zone, sessionBiasLogGain);
                double[] cachedResiduals;
                if (!CalibrationResidualCache.TryGet(
                    profile, cellIndex, residualDependencyKey, out cachedResiduals))
                {
                    cachedResiduals = new double[zone.Trials.Count];
                    for (int trialIndex = 0; trialIndex < zone.Trials.Count; trialIndex++)
                    {
                        DesktopSensTrial trial = zone.Trials[trialIndex];
                        cachedResiduals[trialIndex] = double.NaN;

                        double rawError = trial.SignedErrorCm;
                        if (Math.Abs(rawError) < 0.000001 &&
                            Math.Abs(trial.ProjectedDistanceCm) < 0.000001 &&
                            trial.FinalErrorCm > 0.0)
                            rawError = trial.IsUnderFlick ? -trial.FinalErrorCm : trial.FinalErrorCm;
                        if (!IsFinite(rawError)) continue;

                        double residual = ComputeResidualSignedError(trial, rawError);
                        if (!IsFinite(residual)) continue;
                        double sessionBias = GetSessionBias(sessionBiasLogGain, trial.SessionId);
                        if (sessionBias != 0.0)
                        {
                            double observed = trial.TargetDistanceCm + residual;
                            if (!IsFinite(observed) || observed <= 0.05) continue;
                            residual = observed * Math.Exp(-sessionBias) - trial.TargetDistanceCm;
                        }
                        if (IsFinite(residual))
                            cachedResiduals[trialIndex] = residual;
                    }

                    CalibrationResidualCache.Store(
                        profile, cellIndex, residualDependencyKey, cachedResiduals);
                }

                for (int trialIndex = 0; trialIndex < zone.Trials.Count; trialIndex++)
                {
                    DesktopSensTrial trial = zone.Trials[trialIndex];
                    double residual = cachedResiduals[trialIndex];
                    if (!IsFinite(residual)) continue;

                    // NEW (Model v6, ASDW consistency): use the same effective
                    // target that ComputeResidualSignedError used to derive the
                    // residual. When the trial carries a NeededDistanceCm (ASDW
                    // correction landed inside tolerance), the residual was
                    // computed against NeededDistanceCm, so the paired target
                    // stored in the zone must also be NeededDistanceCm —
                    // otherwise the ratio signedError/target would no longer
                    // represent the raw mouse response and the gain estimator
                    // would receive inconsistent (target, error) pairs. Trials
                    // without a valid NeededDistanceCm fall back to
                    // TargetDistanceCm (legacy behaviour).
                    double effectiveTargetCm = IsFinite(trial.NeededDistanceCm) &&
                                               trial.NeededDistanceCm > DesktopSensConstants.MinimumDenominatorCm
                        ? trial.NeededDistanceCm
                        : trial.TargetDistanceCm;
                    if (!IsFinite(effectiveTargetCm) || effectiveTargetCm <= 0.0)
                        effectiveTargetCm = trial.TargetDistanceCm;

                    zone.SignedErrors.Add(residual);
                    zone.TargetDistances.Add(effectiveTargetCm);
                    if (effectiveTargetCm <= DesktopSensConstants.BaseZoneEndCm)
                    {
                        zone.BaseTargetDistances.Add(effectiveTargetCm);
                        zone.BaseSignedErrors.Add(residual);
                    }
                    else
                    {
                        zone.ExcessTargetDistances.Add(effectiveTargetCm - DesktopSensConstants.BaseZoneEndCm);
                        zone.ExcessSignedErrors.Add(residual);
                    }
                }
                if (zone.SignedErrors.Count == 0 || zone.TargetDistances.Count == 0)
                    continue;

                // Count only cells that still have at least one finite residual
                // after chain reconstruction and session-bias adjustment.
                // The original dictionary can contain cells whose residuals
                // were all discarded, so cells.Count would overstate coverage.
                populatedCells++;

                byte[] cellResultKey = BuildCellResultCacheKey(zone, cellIndex);
                CalibrationCellResult cachedCellResult;
                if (CalibrationCellResultCache.TryGet(profile, cellIndex, cellResultKey, out cachedCellResult))
                {
                    profile.CellSensitivityFactor[cellIndex] = cachedCellResult.SensitivityFactor;
                    profile.CellSensitivityConfidence[cellIndex] = cachedCellResult.SensitivityConfidence;
                    profile.CellAccelerationFactor[cellIndex] = cachedCellResult.AccelerationFactor;
                    profile.CellAccelerationConfidence[cellIndex] = cachedCellResult.AccelerationConfidence;
                    profile.CellRotationOffsetDegrees[cellIndex] = cachedCellResult.RotationOffsetDegrees;
                    profile.CellRotationConfidence[cellIndex] = cachedCellResult.RotationConfidence;
                    if (cachedCellResult.PrecisionStable) precisionStableCells++;

                    int weight = cachedCellResult.SampleWeight;
                    if (cachedCellResult.IsBaseDimension)
                    {
                        angleErrorTotals[zone.AngleZoneIndex] += cachedCellResult.SensitivityFactor * weight;
                        angleCounts[zone.AngleZoneIndex] += weight;
                        angleConfidenceTotals[zone.AngleZoneIndex] += cachedCellResult.SensitivityConfidence * weight;
                    }
                    else if (cachedCellResult.IncludeDistanceAggregate)
                    {
                        distanceFactorTotals[zone.DistanceZoneIndex] += cachedCellResult.AccelerationFactor * weight;
                        distanceCounts[zone.DistanceZoneIndex] += weight;
                        distanceConfidenceTotals[zone.DistanceZoneIndex] += cachedCellResult.AccelerationConfidence * weight;
                    }
                    overallConfidenceTotal += cachedCellResult.OverallConfidence;
                    overallConfidenceCount++;

                    if (zone.CurveSamples.Count >= DesktopSensConstants.CurveFittingMinSamples)
                    {
                        CurveFitResult cachedFit = CalibrationCurveCache.GetOrFit(profile, cellIndex, zone.CurveSamples);
                        if (cachedFit.IsValid)
                        {
                            profile.CellCurveMode[cellIndex] = (int)cachedFit.Mode;
                            profile.SetCellCurveParameters(cellIndex, cachedFit.Parameters);
                            profile.CellCurveRmse[cellIndex] = cachedFit.Rmse;
                            profile.CellCurveAic[cellIndex] = cachedFit.Aic;
                            profile.CellCurveSampleCount[cellIndex] = cachedFit.SampleCount;
                        }
                    }
                    continue;
                }

                // Robust single-factor estimate using Huber mean instead of plain Average.
                // Huber rejects outliers automatically, so a single bad flick can no
                // longer pull the cell factor by 20%.
                double robustSignedError = RobustStatistics.HuberMean(zone.SignedErrors);
                double targetCm = RobustStatistics.TrimmedMean(zone.TargetDistances, 0.2);

                // PATCH 3 from review: classification MUST match the rule
                // used by ComputeResidualSignedError and
                // DesktopSensProfileMerger, which both use the zone CENTER,
                // not the trial-median target distance. Otherwise the
                // calibrator stores a factor in one array while the merger
                // reads the other (orphaned factor for boundary cells).
                double zoneCenterCmForCell = TrainingGeometry.Current.ZoneCenterCm(zone.DistanceZoneIndex);
                bool isBaseZoneForCell = zoneCenterCmForCell <= DesktopSensConstants.BaseZoneEndCm;

                // Angular deviation remains diagnostic data and does not
                // suppress a cell's calibration or confidence.
                const double angularConfidenceWeight = 1.0;

                // Rotation offset: robust circular mean.
                if (zone.RotationErrors.Count > 0)
                {
                    profile.CellRotationOffsetDegrees[cellIndex] =
                        RobustStatistics.HuberCircularMean(zone.RotationErrors);
                    profile.CellRotationConfidence[cellIndex] = Confidence(zone.RotationErrors.Count) * angularConfidenceWeight;
                }

                double observedCm = targetCm + robustSignedError;
                double factor;
                bool isPrecisionStable = false;
                if (isBaseZoneForCell)  // PATCH 3 from review (was: targetCm <= BaseZoneEndCm)
                {
                    double logGain, logSe, logHalfWidth;
                    int logSamples;
                    bool hasLogGain = RobustStatistics.TryLogGainConfidence(
                        zone.BaseTargetDistances, zone.BaseSignedErrors,
                        out logGain, out logSe, out logHalfWidth, out logSamples);
                    factor = hasLogGain
                        ? Math.Exp(-logGain)
                        : SafeRatio(targetCm, observedCm);
                    if (!IsFinite(factor) || factor <= 0.0) continue;

                    // NEW (Model v10, magnitude-aware slope boost): apply the
                    // same sign-aware, magnitude-aware boost to base-dimension
                    // cells.
                    factor = ApplyMagnitudeSlopeBoost(factor, zone);

                    // NEW (Model v10, adaptive convergence): same adaptive
                    // interpolation as the excess-dimension path.
                    factor = ApplyAdaptiveConvergence(profile, cellIndex, factor, zone);

                    profile.CellSensitivityFactor[cellIndex] = factor;
                    double precisionConfidence = hasLogGain
                        ? TrainingPrecisionConfidence(logHalfWidth)
                        : Confidence(zone.BaseSignedErrors.Count);
                    profile.CellSensitivityConfidence[cellIndex] =
                        precisionConfidence * angularConfidenceWeight;
                    isPrecisionStable = hasLogGain &&
                        logSamples >= DesktopSensTrainingState.MinSuccessfulAttemptsForFirstRound &&
                        logHalfWidth <= DesktopSensTrainingState.PrecisionToleranceLog &&
                        angularConfidenceWeight > 0.0;
                    if (isPrecisionStable) precisionStableCells++;

                }
                else
                {
                    // With BaseZoneEndCm = 0.0 the "excess" is the full target
                    // distance, so this branch now handles EVERY cell including
                    // micro-flick zones (the previous "first 3 cm" special
                    // case is gone). Target and signed error remain paired per
                    // trial, so the estimator cannot accidentally mix
                    // observations across trials.
                    double desiredExcess = RobustStatistics.TrimmedMean(zone.ExcessTargetDistances, 0.2);
                    double excessError = RobustStatistics.HuberMean(zone.ExcessSignedErrors);
                    double observedExcess = desiredExcess + excessError;
                    double excessLogGain, excessLogSe, excessHalfWidth;
                    int excessSamples;
                    bool hasExcessLogGain = RobustStatistics.TryLogGainConfidence(
                        zone.ExcessTargetDistances, zone.ExcessSignedErrors,
                        out excessLogGain, out excessLogSe, out excessHalfWidth, out excessSamples);
                    if (hasExcessLogGain || observedExcess > DesktopSensConstants.MinimumDenominatorCm)
                    {
                        factor = hasExcessLogGain
                            ? Math.Exp(-excessLogGain)
                            : SafeRatio(desiredExcess, observedExcess);
                        if (!IsFinite(factor) || factor <= 0.0) continue;

                        // NEW (Model v10, magnitude-aware slope boost): multiply
                        // the gain-estimator factor by a sign-aware boost
                        // derived from the cell's ABSOLUTE under/over-flick
                        // magnitude (zone.ExcessSignedErrors holds the residual
                        // signed errors for this cell). This is the user's
                        // directive: a 1-cm under-flick should produce a small
                        // correction, but a 5-cm under-flick should produce a
                        // much larger correction. The boost is NON-LINEAR
                        // (MagnitudeSlopePower > 1) so larger under-flicks
                        // produce disproportionately larger corrections. The
                        // boost is sign-aware: over-flick produces the same
                        // magnitude of correction as under-flick, just in the
                        // opposite direction.
                        // Zone index is NOT used — only the under-flick
                        // magnitude matters (a 4-cm under-flick in zone 2
                        // produces the same boost as a 4-cm under-flick in
                        // zone 8).
                        factor = ApplyMagnitudeSlopeBoost(factor, zone);

                        // NEW (Model v10, adaptive convergence): instead of
                        // storing the freshly-computed factor directly, blend
                        // it with the cell's previously stored factor. The
                        // blend rate (alpha) grows with trial count, so:
                        //   * Train 1: factor moves 30% of the way toward the
                        //     new estimate (cautious — the prior is still 1.0).
                        //   * Train 5: factor moves ~50% toward the new estimate.
                        //   * Train 10+: factor moves 70% toward the new estimate.
                        //
                        // This gives the user's directive: "ضریب باید در طول
                        // چند train به عدد دقیق برسه" — the factor converges
                        // gradually over multiple trains rather than jumping
                        // to the final value on the first train.
                        //
                        // Bidirectional property: when the user over-flicks,
                        // the new estimate is < the prior, so the adaptive
                        // factor DECREASES. When the user under-flicks, the
                        // new estimate is > the prior, so the adaptive factor
                        // INCREASES. This is what the user asked for: zone N
                        // might converge to 1.23 (under-flick) while zone N+1
                        // converges to 0.24 (over-flick) at the same time.
                        factor = ApplyAdaptiveConvergence(profile, cellIndex, factor, zone);

                        profile.CellAccelerationFactor[cellIndex] = factor;
                        double precisionConfidence = hasExcessLogGain
                            ? TrainingPrecisionConfidence(excessHalfWidth)
                            : Confidence(zone.ExcessSignedErrors.Count);
                        profile.CellAccelerationConfidence[cellIndex] =
                            precisionConfidence * angularConfidenceWeight;
                        if (hasExcessLogGain && excessSamples >= DesktopSensTrainingState.MinSuccessfulAttemptsForFirstRound &&
                            excessHalfWidth <= DesktopSensTrainingState.PrecisionToleranceLog &&
                            angularConfidenceWeight > 0.0)
                            isPrecisionStable = true;
                        if (isPrecisionStable) precisionStableCells++;
                    }
                }

                bool isBaseDimension = isBaseZoneForCell;  // PATCH 3 from review (was: targetCm <= BaseZoneEndCm)
                bool includeDistanceAggregate = !isBaseDimension &&
                    observedCm - DesktopSensConstants.BaseZoneEndCm > DesktopSensConstants.MinimumDenominatorCm &&
                    profile.CellAccelerationConfidence[cellIndex] > 0.0;
                double overallCellConfidence = isBaseDimension
                    ? profile.CellSensitivityConfidence[cellIndex]
                    : profile.CellAccelerationConfidence[cellIndex];
                CalibrationCellResultCache.Store(profile, cellIndex, cellResultKey, new CalibrationCellResult
                {
                    SensitivityFactor = profile.CellSensitivityFactor[cellIndex],
                    SensitivityConfidence = profile.CellSensitivityConfidence[cellIndex],
                    AccelerationFactor = profile.CellAccelerationFactor[cellIndex],
                    AccelerationConfidence = profile.CellAccelerationConfidence[cellIndex],
                    RotationOffsetDegrees = profile.CellRotationOffsetDegrees[cellIndex],
                    RotationConfidence = profile.CellRotationConfidence[cellIndex],
                    PrecisionStable = isPrecisionStable,
                    IsBaseDimension = isBaseDimension,
                    IncludeDistanceAggregate = includeDistanceAggregate,
                    SampleWeight = zone.SignedErrors.Count,
                    OverallConfidence = overallCellConfidence
                });

                // Only observations for the relevant correction dimension contribute to legacy summaries.
                if (isBaseZoneForCell)  // PATCH 3 from review (was: targetCm <= BaseZoneEndCm)
                {
                    angleErrorTotals[zone.AngleZoneIndex] += profile.CellSensitivityFactor[cellIndex] * zone.SignedErrors.Count;
                    angleCounts[zone.AngleZoneIndex] += zone.SignedErrors.Count;
                    angleConfidenceTotals[zone.AngleZoneIndex] +=
                        profile.CellSensitivityConfidence[cellIndex] * zone.SignedErrors.Count;
                }
                else if (observedCm - DesktopSensConstants.BaseZoneEndCm > DesktopSensConstants.MinimumDenominatorCm &&
                    profile.CellAccelerationConfidence[cellIndex] > 0.0)
                {
                    distanceFactorTotals[zone.DistanceZoneIndex] += profile.CellAccelerationFactor[cellIndex] * zone.SignedErrors.Count;
                    distanceCounts[zone.DistanceZoneIndex] += zone.SignedErrors.Count;
                    distanceConfidenceTotals[zone.DistanceZoneIndex] +=
                        profile.CellAccelerationConfidence[cellIndex] * zone.SignedErrors.Count;
                }

                double cellConfidence = isBaseZoneForCell  // PATCH 3 from review (was: targetCm <= BaseZoneEndCm)
                    ? profile.CellSensitivityConfidence[cellIndex]
                    : profile.CellAccelerationConfidence[cellIndex];
                overallConfidenceTotal += cellConfidence;
                overallConfidenceCount++;

                // ─────────────────────────────────────────────────────────────────
                // NEW (v5): fit multi-model acceleration curve for this cell.
                // ─────────────────────────────────────────────────────────────────
                if (zone.CurveSamples.Count >= DesktopSensConstants.CurveFittingMinSamples)
                {
                    CurveFitResult fit = CalibrationCurveCache.GetOrFit(profile, cellIndex, zone.CurveSamples);
                    if (fit.IsValid)
                    {
                        profile.CellCurveMode[cellIndex] = (int)fit.Mode;
                        profile.SetCellCurveParameters(cellIndex, fit.Parameters);
                        profile.CellCurveRmse[cellIndex] = fit.Rmse;
                        profile.CellCurveAic[cellIndex] = fit.Aic;
                        profile.CellCurveSampleCount[cellIndex] = fit.SampleCount;
                        // Curve-fit quality is diagnostic only. It must not
                        // promote a cell past the measurement CI precision gate.
                    }
                }
            }

            for (int angle = 0; angle < DesktopSensConstants.AngularZoneCount; angle++)
            {
                if (angleCounts[angle] > 0)
                {
                    profile.SensitivityFactor[angle] = angleErrorTotals[angle] / angleCounts[angle];
                    profile.SensitivityConfidence[angle] = angleConfidenceTotals[angle] / angleCounts[angle];
                }
            }
            for (int distance = 0; distance < distanceZoneCount; distance++)
            {
                if (distanceCounts[distance] > 0)
                {
                    profile.AccelerationFactor[distance] = distanceFactorTotals[distance] / distanceCounts[distance];
                    profile.AccelerationConfidence[distance] = distanceConfidenceTotals[distance] / distanceCounts[distance];
                }
            }

            int eligibleDistanceZones = 0;
            for (int d = 0; d < distanceZoneCount; d++)
                if (DesktopSensTrainingState.IsCalibrationDistanceZone(d))
                    eligibleDistanceZones++;
            int eligibleCellCount = DesktopSensConstants.AngularZoneCount * eligibleDistanceZones;
            profile.CellsInsufficientData = Math.Max(0, eligibleCellCount - populatedCells);
            // FIX #4: use the polling-aware sample threshold.
            profile.CellsStable = precisionStableCells;
            profile.CellsLowConfidence = populatedCells - profile.CellsStable;
            profile.OverallCoverage = eligibleCellCount > 0
                ? Math.Min(1.0, (double)populatedCells / eligibleCellCount)
                : 0.0;
            profile.OverallConfidence = overallConfidenceCount == 0 ? 0.0 :
                overallConfidenceTotal / overallConfidenceCount;
            profile.ModelVersion = DesktopSensConstants.CurrentModelVersion;
            profile.ModifiedTimestamp = DateTime.UtcNow;
        }

        // ─────────────────────────────────────────────────────────────────────
        // ─────────────────────────────────────────────────────────────────────
        // Residual reconstruction.
        //
        // MouseWatcher captures the raw input stream before the app-side
        // normalized stream. A trial stores the directional base multiplier
        // and speed-LUT factor that were active when it was captured. Apply
        // those factors exactly once to the raw observation. Do not replay
        // previous distance-cell factors: the active speed LUT already
        // represents the committed calibration.
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Computes the current residual for the trainer's immediate hit check.
        /// Raw input is transformed only by the active RawAccel LUT factor
        /// captured on this trial; prior cell factors are not replayed again.
        /// </summary>
        public static double ComputeChainAdjustedResidual(
            DesktopSensProfile profile,
            DesktopSensTrial trial,
            double rawSignedError)
        {
            if (profile == null || trial == null || trial.IsEvaluationOnly)
                return rawSignedError;

            return ComputeResidualSignedError(trial, rawSignedError);
        }

        private static byte[] BuildCellResultCacheKey(ZoneData zone, int cellIndex)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                // Schema version 6 (Model v10): zone target aggregation now uses
                // the ASDW-confirmed effective target (NeededDistanceCm) instead
                // of the raw TargetDistanceCm. The aggregated (target, residual)
                // pairs differ from any cache entry written under schema 5, so
                // those entries must not be reused.
                writer.Write(6);
                writer.Write(cellIndex);
                writer.Write(zone.AngleZoneIndex);
                writer.Write(zone.DistanceZoneIndex);
                int distanceZoneCount = TrainingGeometry.Current.DistanceZoneCount;
                writer.Write(distanceZoneCount);
                writer.Write(TrainingGeometry.Current.EffectiveMaxDistanceCm);
                for (int distance = 0; distance < distanceZoneCount; distance++)
                    writer.Write(TrainingGeometry.Current.ZoneCenterCm(distance));
                writer.Write(DesktopSensConstants.BaseZoneEndCm);
                writer.Write(DesktopSensConstants.MinimumDenominatorCm);
                writer.Write(DesktopSensConstants.MinimumCalibrationFactor);
                writer.Write(DesktopSensConstants.MaximumCalibrationFactor);
                writer.Write(DesktopSensTrainingState.MinSuccessfulAttemptsForFirstRound);
                writer.Write(DesktopSensTrainingState.PrecisionToleranceLog);
                writer.Write(MaxAngularErrorDegrees);

                WriteExactDoubles(writer, zone.SignedErrors);
                WriteExactDoubles(writer, zone.TargetDistances);
                WriteExactDoubles(writer, zone.BaseTargetDistances);
                WriteExactDoubles(writer, zone.BaseSignedErrors);
                WriteExactDoubles(writer, zone.ExcessTargetDistances);
                WriteExactDoubles(writer, zone.ExcessSignedErrors);
                WriteExactDoubles(writer, zone.AngularErrors);
                WriteExactDoubles(writer, zone.RotationErrors);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static void WriteExactDoubles(BinaryWriter writer, IList<double> values)
        {
            writer.Write(values != null ? values.Count : -1);
            if (values == null) return;
            foreach (double value in values)
                writer.Write(BitConverter.DoubleToInt64Bits(value));
        }

        private static byte[] BuildResidualCacheKey(
            DesktopSensProfile profile,
            ZoneData zone,
            IDictionary<string, double> sessionBiases)
        {
            // Binary encoding keeps the exact dependency snapshot compact even
            // when trials contain hundreds of raw mouse samples.
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                int distanceZoneCount = TrainingGeometry.Current.DistanceZoneCount;
                writer.Write(distanceZoneCount);
                writer.Write(TrainingGeometry.Current.EffectiveMaxDistanceCm);
                writer.Write(DesktopSensConstants.BaseZoneEndCm);

                // Zone centers determine the approximate-path segment boundaries.
                for (int d = 0; d < distanceZoneCount; d++)
                    writer.Write(TrainingGeometry.Current.ZoneCenterCm(d));

                // Residuals depend on the active LUT factor stored on each
                // trial, not on earlier per-cell arrays. Each trial's exact
                // LUT factor is serialized below.

                writer.Write(zone.Trials.Count);
                foreach (DesktopSensTrial trial in zone.Trials)
                {
                    writer.Write(trial.SessionId != null);
                    if (trial.SessionId != null) writer.Write(trial.SessionId);
                    writer.Write(GetSessionBias(sessionBiases, trial.SessionId));
                    writer.Write(trial.AngleZoneIndex);
                    writer.Write(trial.DistanceZoneIndex);
                    writer.Write(trial.SignedErrorCm);
                    writer.Write(BitConverter.DoubleToInt64Bits(trial.NeededDistanceCm));
                    writer.Write(trial.FlickDistanceCm);
                    writer.Write(trial.DurationMs);
                    writer.Write(trial.AppliedBaseFactor);
                    writer.Write(trial.HasAppliedLutFactor);
                    writer.Write(trial.AppliedLutFactor);
                    writer.Write(trial.ProjectedDistanceCm);
                    writer.Write(trial.FinalErrorCm);
                    writer.Write(trial.IsUnderFlick);
                    writer.Write(trial.TargetDistanceCm);
                    writer.Write(trial.TargetAngleDegrees);
                    writer.Write(trial.AppliedRotationCorrectionDegrees);
                    writer.Write(trial.EffectiveCpi);
                    writer.Write(trial.MouseDPI);

                    int sampleCount = trial.RawSamples != null ? trial.RawSamples.Count : -1;
                    writer.Write(sampleCount);
                    if (trial.RawSamples != null)
                    {
                        foreach (var sample in trial.RawSamples)
                        {
                            writer.Write((object)sample != null);
                            if ((object)sample == null) continue;
                            writer.Write((double)sample.DX);
                            writer.Write((double)sample.DY);
                        }
                    }
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        private static double ComputeResidualSignedError(
            DesktopSensTrial trial,
            double rawSignedError)
        {
            // MouseWatcher.RawMouseSampleReceived is raised directly from
            // WM_INPUT before the application-side normalized stream. The
            // trial distance is therefore the pre-driver physical input.
            // RawAccel's active speed LUT is the only prior correction that
            // belongs in this observation: apply it exactly once here.
            // Do not replay earlier distance-cell factors as well; those
            // factors are already represented by the active speed LUT.
            if (trial == null)
                return rawSignedError;

            // NEW (Model v6): the target reference for the residual is
            // NeededDistanceCm when the ASDW correction confirmed the target was
            // reached. rawObserved stays the physical flick (target + rawError)
            // because that is what the driver actually scaled. Subtracting
            // effectiveTarget from the scaled flick yields exactly the
            // "flick + nudge should have become target" residual.
            double effectiveTargetCm = IsFinite(trial.NeededDistanceCm) &&
                                       trial.NeededDistanceCm > DesktopSensConstants.MinimumDenominatorCm
                ? trial.NeededDistanceCm
                : trial.TargetDistanceCm;

            double appliedBaseFactor = IsFinite(trial.AppliedBaseFactor) &&
                trial.AppliedBaseFactor > 0.0 ? trial.AppliedBaseFactor : 1.0;
            double appliedLutFactor = trial.HasAppliedLutFactor &&
                IsFinite(trial.AppliedLutFactor) && trial.AppliedLutFactor > 0.0
                    ? trial.AppliedLutFactor : 1.0;
            double appliedOutputFactor = appliedBaseFactor * appliedLutFactor;
            if (!IsFinite(appliedOutputFactor) || appliedOutputFactor <= 0.0 ||
                Math.Abs(appliedOutputFactor - 1.0) < 1e-12)
                return (trial.TargetDistanceCm + rawSignedError) - effectiveTargetCm;

            double rawObserved = trial.TargetDistanceCm + rawSignedError;
            if (!IsFinite(rawObserved) || rawObserved <= 0.0)
                return rawSignedError;

            double driverObserved = rawObserved * appliedOutputFactor;
            if (!IsFinite(driverObserved))
                return rawSignedError;

            return driverObserved - effectiveTargetCm;
        }

        /// <summary>
        /// Build a (speed, ratio) sample from a trial. Returns null if the trial
        /// lacks duration or has a degenerate distance.
        /// </summary>
        private static Dictionary<string, double> BuildSessionBiases(IList<DesktopSensTrial> trials)
        {
            var grouped = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            if (trials == null) return new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (DesktopSensTrial trial in trials)
            {
                if (trial == null || !trial.IsSessionAnchor || trial.WasRejected || trial.IsEvaluationOnly ||
                    string.IsNullOrEmpty(trial.SessionId) || !IsFinite(trial.TargetDistanceCm) ||
                    trial.TargetDistanceCm <= DesktopSensConstants.BaseZoneEndCm ||
                    Math.Abs(trial.TargetDistanceCm - 2.0) > 0.25 || !IsFinite(trial.SignedErrorCm)) continue;
                double observed = trial.TargetDistanceCm + trial.SignedErrorCm;
                if (!IsFinite(observed) || observed <= 0.05) continue;
                double value = Math.Log(observed / trial.TargetDistanceCm);
                if (!IsFinite(value)) continue;
                if (!grouped.TryGetValue(trial.SessionId, out List<double> values))
                {
                    values = new List<double>();
                    grouped.Add(trial.SessionId, values);
                }
                values.Add(value);
            }
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var pair in grouped)
                if (pair.Value.Count >= 3) result[pair.Key] = RobustStatistics.Median(pair.Value);
            return result;
        }

        public static IDictionary<string, double> GetSessionBiasLogGains(IList<DesktopSensTrial> trials)
        {
            return BuildSessionBiases(trials);
        }

        public static bool TryGetSessionBiasLogGain(
            IList<DesktopSensTrial> trials,
            string sessionId,
            out double logBias)
        {
            logBias = 0.0;
            if (string.IsNullOrEmpty(sessionId)) return false;
            Dictionary<string, double> biases = BuildSessionBiases(trials);
            return biases.TryGetValue(sessionId, out logBias) && IsFinite(logBias);
        }

        private static double GetSessionBias(IDictionary<string, double> biases, string sessionId)
        {
            if (biases == null || string.IsNullOrEmpty(sessionId)) return 0.0;
            return biases.TryGetValue(sessionId, out double value) && IsFinite(value) ? value : 0.0;
        }

        private static SensitivitySample? TryBuildCurveSample(
            DesktopSensTrial trial,
            double sessionBiasLogGain,
            double manualCorrectionToleranceCm)
        {
            if (!IsFinite(trial.TargetDistanceCm) ||
                trial.TargetDistanceCm <= DesktopSensConstants.BaseZoneEndCm) return null;
            if (!IsFinite(trial.FlickDistanceCm) ||
                trial.FlickDistanceCm <= DesktopSensConstants.BaseZoneEndCm) return null;
            if (!IsFinite(trial.DurationMs) || trial.DurationMs <= 0.0) return null;

            // Input speed always comes from raw mouse movement. ASDW is
            // not counted as mouse input or extra flick speed.
            // NEW (Model v6): the target reference for curve fitting is
            // NeededDistanceCm when the ASDW correction landed inside tolerance —
            // meaning the user's flick + nudge genuinely reached the target.
            // Otherwise fall back to the raw TargetDistanceCm (legacy behaviour).
            double effectiveTargetCm = IsFinite(trial.NeededDistanceCm) &&
                                       trial.NeededDistanceCm > DesktopSensConstants.MinimumDenominatorCm
                ? trial.NeededDistanceCm
                : trial.TargetDistanceCm;

            double targetExcessCm = effectiveTargetCm - DesktopSensConstants.BaseZoneEndCm;
            double flickExcessCm = trial.FlickDistanceCm - DesktopSensConstants.BaseZoneEndCm;
            if (targetExcessCm <= DesktopSensConstants.MinimumDenominatorCm ||
                flickExcessCm <= DesktopSensConstants.MinimumDenominatorCm) return null;

            double speedCmPerSec = flickExcessCm / (trial.DurationMs / 1000.0);
            if (!IsFinite(speedCmPerSec) || speedCmPerSec <= 0.0) return null;

            // COMBINED FIX (GPT 0002): ASDW confirms that the final
            // landing reached this cell's existing adaptive tolerance.
            // The target marker remains the reference distance; manual
            // movement is never counted as mouse output or used to
            // change the required correction ratio. If ASDW was used
            // but the final residual is not within tolerance, reject
            // this trial's curve sample for this cell.
            //
            // Trials WITHOUT ASDW (HasManualAsdwCorrection = false)
            // always pass this check — they rely on trial.Validate()
            // for rejection. See the comment in Calibrate() for the
            // documented asymmetric behaviour.
            if (trial.HasManualAsdwCorrection &&
                (!IsFinite(trial.ManualAsdwCorrectionCm) ||
                 !IsFinite(trial.ManualAsdwFinalErrorCm) ||
                 !IsFinite(manualCorrectionToleranceCm) ||
                 manualCorrectionToleranceCm <= 0.0 ||
                 trial.ManualAsdwFinalErrorCm > manualCorrectionToleranceCm))
                return null;

            double ratio = flickExcessCm / targetExcessCm;
            if (IsFinite(sessionBiasLogGain)) ratio *= Math.Exp(-sessionBiasLogGain);
            if (!IsFinite(ratio) || ratio <= 0.0) return null;
            return new SensitivitySample
            {
                InputSpeedCmPerSec = speedCmPerSec,
                ObservedRatio = ratio,
                Weight = 1.0
            };
        }

        private static void ResetArray(double[] values, double defaultValue)
        {
            if (values == null) return;
            for (int i = 0; i < values.Length; i++) values[i] = defaultValue;
        }

        private static void ResetIntArray(int[] values, int defaultValue)
        {
            if (values == null) return;
            for (int i = 0; i < values.Length; i++) values[i] = defaultValue;
        }

        private static void EnsureArray(ref double[] values, int length, double defaultValue)
        {
            if (values == null || values.Length != length)
            {
                var replacement = new double[length];
                for (int i = 0; i < replacement.Length; i++) replacement[i] = defaultValue;
                if (values != null)
                    Array.Copy(values, replacement, Math.Min(values.Length, replacement.Length));
                values = replacement;
            }
        }

        private static void EnsureArray(ref int[] values, int length, int defaultValue)
        {
            if (values == null || values.Length != length)
            {
                var replacement = new int[length];
                for (int i = 0; i < replacement.Length; i++) replacement[i] = defaultValue;
                if (values != null)
                    Array.Copy(values, replacement, Math.Min(values.Length, replacement.Length));
                values = replacement;
            }
        }

        private static double SafeRatio(double numerator, double denominator)
        {
            if (Math.Abs(denominator) < DesktopSensConstants.MinimumDenominatorCm)
                denominator = denominator < 0 ? -DesktopSensConstants.MinimumDenominatorCm : DesktopSensConstants.MinimumDenominatorCm;
            return numerator / denominator;
        }

        private static double TrainingPrecisionConfidence(double halfWidth95)
        {
            if (!IsFinite(halfWidth95) || halfWidth95 < 0.0) return 0.0;
            return DesktopSensTrainingState.PrecisionToleranceLog /
                (DesktopSensTrainingState.PrecisionToleranceLog + halfWidth95);
        }

        private static double Confidence(int sampleCount)
        {
            return Math.Min(1.0, sampleCount / 10.0);
        }

        /// <summary>
        /// NEW (Model v10, magnitude-aware slope boost): multiply the
        /// calibrator's base factor by a sign-aware, magnitude-aware boost
        /// derived from the cell's ABSOLUTE under/over-flick magnitude.
        ///
        /// The boost is:
        ///     boost = -sign(meanSignedError) * K * |meanSignedError|^P
        ///     factor = baseFactor * (1 + boost)
        ///
        /// Sign convention (signedError = flick - effectiveTarget):
        ///   * under-flick  → signedError &lt; 0 → boost &gt; 0 (factor increases)
        ///   * over-flick   → signedError &gt; 0 → boost &lt; 0 (factor decreases)
        ///
        /// The negative sign on `boost = -sign(error) * ...` inverts the
        /// natural sign of the signed error so that under-flick (negative
        /// error) INCREASES the factor (positive boost) and over-flick
        /// (positive error) DECREASES it.
        ///
        /// P (MagnitudeSlopePower) controls the non-linearity:
        ///   P=1 → linear: each extra cm of under-flick adds the same boost
        ///   P>1 → larger under-flicks produce disproportionately larger
        ///         boosts (the user's intent: 5-cm under-flick needs MORE
        ///         than 5× the correction of a 1-cm under-flick)
        ///
        /// Zone index is intentionally NOT used — the user explicitly said
        /// the slope must depend on the under-flick magnitude, not on which
        /// zone the trial landed in.
        ///
        /// The boost magnitude is clamped to MagnitudeSlopeMaxBoost so a
        /// single bad trial with a very large under-flick cannot explode
        /// the factor.
        /// </summary>
        private static double ApplyMagnitudeSlopeBoost(double baseFactor, ZoneData zone)
        {
            if (zone == null || zone.ExcessSignedErrors == null || zone.ExcessSignedErrors.Count == 0)
                return baseFactor;
            if (!IsFinite(baseFactor) || baseFactor <= 0.0)
                return baseFactor;

            // Use the robust Huber mean of the cell's signed errors so a
            // single outlier trial does not dominate the boost.
            double meanSignedError = RobustStatistics.HuberMean(zone.ExcessSignedErrors);
            if (!IsFinite(meanSignedError) || Math.Abs(meanSignedError) < 1e-6)
                return baseFactor;

            // Dead-zone: small under-flicks (below MagnitudeSlopeDeadZoneCm)
            // are typically sensor noise rather than a real scaling error.
            // Suppress the boost entirely so high-precision calibration
            // tests are not contaminated by sub-cm noise.
            double magnitude = Math.Abs(meanSignedError);
            if (magnitude < DesktopSensConstants.MagnitudeSlopeDeadZoneCm)
                return baseFactor;

            // Magnitude-aware, sign-aware, non-linear boost.
            // boost = -sign(error) * K * |error|^P
            //   under-flick (error<0): sign=-1, boost = +K * |error|^P  → factor up
            //   over-flick  (error>0): sign=+1, boost = -K * |error|^P  → factor down
            double coefficient = DesktopSensConstants.MagnitudeSlopeCoefficient;
            double power = DesktopSensConstants.MagnitudeSlopePower;
            double magnitudeTerm = coefficient * Math.Pow(magnitude, power);
            double boost = -Math.Sign(meanSignedError) * magnitudeTerm;

            // Clamp the boost magnitude to the configured maximum.
            double maxBoost = DesktopSensConstants.MagnitudeSlopeMaxBoost;
            if (boost > maxBoost) boost = maxBoost;
            if (boost < -maxBoost) boost = -maxBoost;
            if (!IsFinite(boost))
                return baseFactor;

            double boosted = baseFactor * (1.0 + boost);
            if (!IsFinite(boosted) || boosted <= 0.0)
                return baseFactor;
            return boosted;
        }

        /// <summary>
        /// NEW (Model v10, adaptive convergence): blend the freshly-computed
        /// factor with the cell's previously stored factor. The blend rate
        /// (alpha) grows with trial count so the factor converges gradually
        /// over multiple trains rather than jumping to the final value on
        /// the first train.
        ///
        /// The user's directive:
        ///   "seri aval 1.5 zarib dad bazam 2 sant to taraine badi kam ovord
        ///    bayad shibo bishtaro bishtar koneh ta moghei ke dige noghteh
        ///    sabz beshe"
        ///
        /// Implementation:
        ///   adaptiveFactor = prior + alpha * (newEstimate - prior)
        ///   alpha = lerp(Min, Max, min(1.0, trialCount / FullTrialCount))
        ///
        /// Per-cell: each cellIndex keeps its own prior via
        /// CellAccelerationFactor[cellIndex] (or CellSensitivityFactor for
        /// base-dimension cells). The dimension is detected from
        /// zone.DistanceZoneIndex: cells in zone 0 use SensitivityFactor,
        /// others use AccelerationFactor. (Matches the rest of the
        /// calibrator's isBaseDimension branch.)
        /// </summary>
        private static double ApplyAdaptiveConvergence(
            DesktopSensProfile profile,
            int cellIndex,
            double newEstimate,
            ZoneData zone)
        {
            if (profile == null || !IsFinite(newEstimate) || newEstimate <= 0.0)
                return newEstimate;
            if (zone == null)
                return newEstimate;

            // Read the previously stored factor for this cell. Use the
            // isBaseDimension branch (same condition as the caller) to pick
            // the right array — SensitivityFactor for base zones,
            // AccelerationFactor for excess zones.
            bool isBaseDimension = TrainingGeometry.Current.ZoneCenterCm(zone.DistanceZoneIndex)
                <= DesktopSensConstants.BaseZoneEndCm;
            double prior = isBaseDimension
                ? (profile.CellSensitivityFactor != null &&
                   cellIndex >= 0 && cellIndex < profile.CellSensitivityFactor.Length
                    ? profile.CellSensitivityFactor[cellIndex] : DesktopSensConstants.AdaptiveInitialFactor)
                : (profile.CellAccelerationFactor != null &&
                   cellIndex >= 0 && cellIndex < profile.CellAccelerationFactor.Length
                    ? profile.CellAccelerationFactor[cellIndex] : DesktopSensConstants.AdaptiveInitialFactor);

            // If the prior is not finite or non-positive, start neutral.
            if (!IsFinite(prior) || prior <= 0.0)
                prior = DesktopSensConstants.AdaptiveInitialFactor;

            // Trial count for this cell — both base and excess signed-error
            // lists are accumulated per cell.
            int trialCount = isBaseDimension
                ? (zone.BaseSignedErrors != null ? zone.BaseSignedErrors.Count : 0)
                : (zone.ExcessSignedErrors != null ? zone.ExcessSignedErrors.Count : 0);
            if (trialCount <= 0)
                trialCount = 1;

            // alpha = lerp(Min, Max, min(1.0, trialCount / FullTrialCount))
            double alphaT = (double)trialCount /
                (double)System.Math.Max(1, DesktopSensConstants.AdaptiveLearningRateFullTrialCount);
            if (alphaT > 1.0) alphaT = 1.0;
            double alpha = DesktopSensConstants.AdaptiveLearningRateMin +
                alphaT * (DesktopSensConstants.AdaptiveLearningRateMax -
                          DesktopSensConstants.AdaptiveLearningRateMin);

            // Interpolate toward the new estimate.
            double adaptiveFactor = prior + alpha * (newEstimate - prior);
            if (!IsFinite(adaptiveFactor) || adaptiveFactor <= 0.0)
                return newEstimate;

            // NEW (crash-safety): hard cap on the per-cell factor. This
            // prevents a single trial with a very large under-flick (e.g.
            // flick=1, target=20 → raw ratio=20) from blowing up the cell's
            // factor on the first train. The cap is generous (3.0 = 3× the
            // raw input) but safe — anything above that means the calibrator
            // saw a degenerate input and should be re-evaluated on the next
            // train rather than committed to the LUT.
            //
            // The user's directive: "noghteh aval calib nemishehey shibo
            // tondtar mikoneh ta jai ke barnameh crash mikoneh" — the first
            // point must NOT crash the program. The cap makes the first
            // train safe; subsequent trains refine the factor inside the cap.
            if (adaptiveFactor > DesktopSensConstants.AdaptiveFactorMax)
                adaptiveFactor = DesktopSensConstants.AdaptiveFactorMax;
            if (adaptiveFactor < DesktopSensConstants.AdaptiveFactorMin)
                adaptiveFactor = DesktopSensConstants.AdaptiveFactorMin;
            return adaptiveFactor;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
