using System;
using System.Collections.Generic;
using grapher.DesktopSens.Models;

namespace grapher.DesktopSens.Models
{
    /// <summary>
    /// Calibrates the four cardinal directions used by RawAccel's native X/Y sign ratios.
    ///
    /// Direction is inferred from the raw mouse deltas (not from TargetAngleDegrees),
    /// because purely vertical flicks produce no yaw change and were previously
    /// classified as "Right", leaving Up/Down factors stuck at 1.0 forever.
    /// Rotation is accumulated per direction as a circular mean and collapsed into
    /// a single weighted value for RawAccel's scalar rotation field.
    ///
    /// v5 upgrade: each direction now stores its own multi-model acceleration curve
    /// fit (Linear/Natural/Power/Synchronous, selected by AIC) in addition to the
    /// legacy single-factor ratio. ApplyDirectionalProfile reads the curve and
    /// maps it to the matching RawAccel acceleration mode (classic / natural /
    /// power / synchronous) instead of forcing every direction into a flat
    /// sensitivity multiplier.
    ///
    /// FIX (jitter + base-sens): the previous estimator used a plain trimmed-mean
    /// ratio (target/observed). For short Sens-Finder flicks (0-2 cm) the mouse's
    /// sensor noise is a large fraction of the signal, so the trimmed mean
    /// averaged the noise back to ~1.0 and the user saw "base sens doesn't
    /// change". We now use the same log-gain estimator as the per-cell
    /// calibrator (RobustStatistics.TryLogGain) — it works in log-space where
    /// additive noise becomes multiplicative, and a robust Huber mean in log
    /// space rejects the worst 20% of samples rather than averaging them in.
    /// We also subtract the user's measured noise floor (profile.NoiseFloorCm)
    /// from each observation's absolute distance so sub-noise-floor motion no
    /// longer biases the factor toward 1.0.
    /// </summary>
    public static class DirectionalCalibrator
    {
        public static void Calibrate(DesktopSensProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (profile.Trials == null) profile.Trials = new List<DesktopSensTrial>();
            Calibrate(profile, profile.Trials);
        }

        /// <summary>
        /// Rebuilds the four directional estimates from exactly the supplied
        /// observations. Sensitivity Finder passes its own history here so
        /// Train trials can never leak into or overwrite Finder estimates.
        /// </summary>
        public static void Calibrate(
            DesktopSensProfile profile,
            IList<DesktopSensTrial> trials,
            double noiseFloorOverride = double.NaN,
            bool preserveUnobservedDirections = false)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (trials == null) trials = new List<DesktopSensTrial>();

            // FIX (8/16-way): the four RawAccel output-DPI-ratio fields
            // can only carry the four cardinal factors (Right/Down/Left/
            // Up), but the user can still benefit from 8/16-way training:
            // the per-cell calibrator (DesktopSensCalibrator.Calibrate)
            // stores per-angle-zone factors in the 32×N cell arrays, which
            // a future LUT-capable bridge can apply as a full directional
            // map. For the legacy 4-output bridge, we collapse the active
            // mode's trials into the four cardinal buckets by projecting
            // each trial's raw delta onto the dominant cardinal axis:
            //
            //   • 4-way:  every trial stays in its own bucket (no change).
            //   • 8-way:  a DownRight trial's dominant axis is whichever
            //             of Right/Down has |sumDelta| ≥ the other; it
            //             contributes its full signed error to that
            //             bucket so the cardinal factor absorbs the
            //             diagonal-axis bias.
            //   • 16-way: same projection rule, finer-grained source.
            //
            // This means the four output factors remain meaningful in
            // every mode, but the user must understand that 8/16-way is
            // primarily a *visualization and per-cell* feature: a
            // LUT-capable RawAccel bridge is required to actually apply
            // independent per-axis curves for the diagonals. See
            // FIXES_README.md (Limitations section) for the full
            // explanation.
            int activeDirectionCount = DesktopSensTrainingState.ResolveDirectionCount(profile);

            // Per-direction accumulators (always 4 — RawAccel's driver
            // has exactly four output-DPI-ratio slots).
            var targetTotals = new double[4];
            var observedTotals = new double[4];
            var counts = new int[4];

            // Per-direction (speed, ratio) samples for v5 curve fitting.
            var curveSamples = new List<SensitivitySample>[4];
            for (int i = 0; i < 4; i++) curveSamples[i] = new List<SensitivitySample>();

            // Rotation accumulators per direction (circular, so accumulate sin/cos).
            var rotSin = new double[4];
            var rotCos = new double[4];
            var rotCount = new int[4];
            IDictionary<string, double> sessionBiases =
                DesktopSensCalibrator.GetSessionBiasLogGains(trials);

            // Noise floor: drop any trial whose |signed error| is smaller
            // than the noise floor (the trainer's N key populates this).
            // Until the user has measured a noise floor this stays at 0
            // and the calibrator keeps every trial (legacy behaviour).
            double noiseFloorCm = IsFinite(noiseFloorOverride)
                ? Math.Max(0.0, noiseFloorOverride)
                : (profile.NoiseFloorCm > 0.0 ? profile.NoiseFloorCm : 0.0);

            // FIX (8/16-way): use DirectionFromSamplesMulti(trial, mode)
            // to classify each trial into one of the active mode's axes,
            // then project that trial onto the one or two cardinal
            // buckets whose axis(s) the trial's motion touches. For pure
            // cardinal trials the projection is trivial (weight = 1.0 on
            // the matching bucket). For diagonal trials the weight is the
            // cosine of the angle between the trial's motion and that
            // cardinal axis, so a 45° DownRight flick contributes 0.707
            // of its signed error to Right and 0.707 to Down. The four
            // cardinal factors therefore converge to the user's true
            // directional gain without a separate 8/16-way output
            // channel, and the per-cell arrays (which a LUT-capable
            // bridge can read separately) still store the per-axis
            // diagonal data.

            // FIX (noise floor): the previous implementation subtracted
            // noiseFloorCm from |observed| linearly, which biased the
            // estimator when the user's signed error was smaller than
            // the noise floor. We now drop a trial entirely when its
            // |signed error| is below the noise floor (i.e. when the
            // observation is statistically indistinguishable from a
            // noise hit). Trials whose signed error exceeds the noise
            // floor keep their observed value unchanged — this is a
            // clean rejection filter, not a magnitude rescaling, and
            // it cannot introduce the systematic bias the reviewer
            // flagged.

            // Per-trial paired lists for the log-gain estimator. We
            // accumulate target+observed CLEAN pairs per cardinal
            // direction in one pass, so the fallback path no longer
            // needs to re-scan profile.Trials four times (O(N) total,
            // not O(4N)).
            var perTrialPairsByDir = new List<(double target, double observed, double signedError)>[4];
            for (int i = 0; i < 4; i++)
                perTrialPairsByDir[i] = new List<(double, double, double)>();

            foreach (DesktopSensTrial trial in trials)
            {
                if (trial == null || trial.IsEvaluationOnly || trial.IsSessionAnchor ||
                    trial.IsCalibrationExcluded || trial.WasRejected) continue;
                if (!IsFinite(trial.TargetDistanceCm) || trial.TargetDistanceCm <= 0.0) continue;
                double sessionBias = 0.0;
                if (!string.IsNullOrEmpty(trial.SessionId))
                    sessionBiases.TryGetValue(trial.SessionId, out sessionBias);

                // Direction MUST come from RawSamples, not from
                // TargetAngleDegrees, because a purely vertical flick
                // produces no yaw change and would otherwise be
                // misclassified as Right. In 8/16-way mode this also
                // ensures the trial lands in its true diagonal bucket.
                int axisIndex = DirectionFromSamplesMulti(trial, activeDirectionCount);
                if (axisIndex < 0) continue;

                // Compute the four-bucket projection weights for this
                // trial. A pure cardinal axis (in any mode) has weight
                // 1.0 on its bucket and 0 on the others; a diagonal
                // axis splits its weight between the two cardinals
                // that bracket it.
                double[] cardinalWeights = ComputeCardinalProjectionWeights(
                    axisIndex, activeDirectionCount);
                if (cardinalWeights == null) continue;

                // Rotation is accumulated for every trial with a valid
                // measurement, regardless of distance, weighted by the
                // same projection so a DownRight flick's rotation error
                // is shared between the Right and Down rotation means.
                if (trial.HasRotationMeasurement && IsFinite(trial.SignedRotationErrorDegrees))
                {
                    double rad = trial.SignedRotationErrorDegrees * Math.PI / 180.0;
                    double sin = Math.Sin(rad);
                    double cos = Math.Cos(rad);
                    for (int d = 0; d < 4; d++)
                    {
                        if (cardinalWeights[d] <= 0.0) continue;
                        rotSin[d] += sin * cardinalWeights[d];
                        rotCos[d] += cos * cardinalWeights[d];
                        rotCount[d]++;
                    }
                }

                // Sensitivity Finder: base sensitivity is learned only from
                // trials inside the Finder's 0-3 cm micro-zone. This is the
                // Finder mode's OWN target scope (MicroZoneMaxCm, a legacy
                // constant still set to 3.0) and is independent of
                // BaseZoneEndCm — which is now 0.0 after the user's directive
                // to remove the Train-calibration 3 cm exemption. The two
                // mechanisms cover different concerns: Finder learns the
                // directional base ratio from short flicks; Train learns the
                // per-cell acceleration factor across the FULL range.
                if (!trial.HasMovementMeasurement || trial.TargetDistanceCm > DesktopSensConstants.MicroZoneMaxCm)
                {
                    for (int d = 0; d < 4; d++)
                        if (cardinalWeights[d] > 0.0)
                            AddCurveSample(curveSamples[d], trial, sessionBias);
                    continue;
                }

                double error = trial.SignedErrorCm;
                if (Math.Abs(error) < 1e-6 && Math.Abs(trial.ProjectedDistanceCm) < 1e-6 && trial.FinalErrorCm > 0)
                    error = trial.IsUnderFlick ? -trial.FinalErrorCm : trial.FinalErrorCm;
                double observed = trial.TargetDistanceCm + error;
                if (sessionBias != 0.0 && IsFinite(observed) && observed > DesktopSensConstants.MinimumObservedDistanceCm)
                    observed *= Math.Exp(-sessionBias);
                error = observed - trial.TargetDistanceCm;
                if (!IsFinite(error) || !IsFinite(observed) || observed <= DesktopSensConstants.MinimumObservedDistanceCm)
                    continue;

                // Noise-floor rejection: drop the trial entirely when
                // its signed error is smaller than the noise floor.
                // This is a *rejection filter*, not a magnitude
                // subtraction — it preserves the estimator's unbiased
                // behaviour while discarding observations that are
                // statistically indistinguishable from sensor noise.
                if (noiseFloorCm > 0.0 && Math.Abs(error) < noiseFloorCm)
                    continue;

                // Distribute the trial's contribution across the
                // cardinal buckets it touches, weighted by the
                // projection weights computed above. The signed error
                // and observed distance are the same for every copy;
                // the projection is purely a weighting scheme.
                for (int d = 0; d < 4; d++)
                {
                    double w = cardinalWeights[d];
                    if (w <= 0.0) continue;
                    targetTotals[d] += trial.TargetDistanceCm * w;
                    observedTotals[d] += observed * w;
                    counts[d]++;
                    perTrialPairsByDir[d].Add((trial.TargetDistanceCm, observed, error));

                    // The curve sampler consumes the trial's speed/ratio
                    // pair, which is also a property of the trial itself
                    // (not of its bucket); we add one copy per bucket
                    // it touches so the per-direction curve fits see the
                    // diagonal motion too.
                    AddCurveSample(curveSamples[d], trial, sessionBias);
                }
            }

            // Build the per-direction paired lists for the log-gain
            // estimator from the projection above. We already collected
            // (target, observed, signedError) triples per bucket, so
            // the fallback path no longer needs to re-scan
            // profile.Trials four times.
            //
            // FIX (PATCH 1 from review): TryLogGainConfidence expects
            // (target, signedError) pairs — NOT (target, observed). The
            // estimator internally reconstructs observed = target +
            // signedError. Passing observed distances here made the
            // estimator compute log((target + observed) / target)
            // instead of log(observed / target), which clamped almost
            // every factor to MinimumCalibrationFactor = 0.5 and made
            // the Sensitivity Finder useless for under-flicks. We now
            // pass the signed error.
            var logTargetLists = new List<double>[4];
            var logErrorLists = new List<double>[4];
            for (int i = 0; i < 4; i++)
            {
                logTargetLists[i] = new List<double>(perTrialPairsByDir[i].Count);
                logErrorLists[i] = new List<double>(perTrialPairsByDir[i].Count);
                foreach (var pair in perTrialPairsByDir[i])
                {
                    logTargetLists[i].Add(pair.target);
                    // TryLogGainConfidence expects SIGNED ERRORS
                    // (observed - target), not observed distances. The
                    // estimator internally reconstructs
                    // observed = target + signedError.
                    logErrorLists[i].Add(pair.signedError);
                }
            }

            double[] factors =
            {
                profile.RightSensitivityFactor,
                profile.DownSensitivityFactor,
                profile.LeftSensitivityFactor,
                profile.UpSensitivityFactor
            };
            double[] confidence =
            {
                profile.RightSensitivityConfidence,
                profile.DownSensitivityConfidence,
                profile.LeftSensitivityConfidence,
                profile.UpSensitivityConfidence
            };

            for (int d = 0; d < 4; d++)
            {
                if (counts[d] == 0 || observedTotals[d] <= DesktopSensConstants.MinimumObservedDistanceCm)
                {
                    // Finder can run incrementally, one direction at a time.
                    // Preserve a direction with no Finder observations so it
                    // cannot erase a saved/manual factor from another direction.
                    // Explicit reset still uses the default false behavior.
                    if (!preserveUnobservedDirections)
                    {
                        factors[d] = DesktopSensConstants.NeutralFactor;
                        confidence[d] = 0.0;
                    }
                }
                else
                {
                    // FIX (jitter + base-sens): prefer the log-gain estimator.
                    // TryLogGain returns false if there are not enough paired
                    // samples or if all samples are degenerate; in that case
                    // we fall back to the trimmed-mean ratio so a tiny sample
                    // set still produces a finite, non-1.0 factor.
                    double logGain, logSe, logHalfWidth;
                    int logSamples;
                    // FIX (PATCH 1 from review): pass the signed-error
                    // list, not the observed-distances list.
                    bool hasLogGain = RobustStatistics.TryLogGainConfidence(
                        logTargetLists[d], logErrorLists[d],
                        out logGain, out logSe, out logHalfWidth, out logSamples);

                    if (hasLogGain)
                    {
                        // factor = exp(-logGain): if the user over-flicks
                        // (observed > target), logGain > 0, so factor < 1.0;
                        // if the user under-flicks, factor > 1.0. This is the
                        // exact same convention used by the per-cell calibrator.
                        factors[d] = Clamp(Math.Exp(-logGain),
                            DesktopSensConstants.MinimumCalibrationFactor,
                            DesktopSensConstants.MaximumCalibrationFactor);
                        // Confidence comes from the CI half-width: a tight CI
                        // means we trust the estimate, a wide CI means we don't.
                        // Map half-width to confidence in [0, 1] via 1 / (1 + w).
                        if (IsFinite(logHalfWidth) && logHalfWidth > 0.0)
                            confidence[d] = Math.Max(0.0, Math.Min(1.0, 1.0 / (1.0 + logHalfWidth)));
                        else
                            confidence[d] = Math.Min(1.0, counts[d] / 10.0);
                    }
                    else
                    {
                        // Fallback: trimmed-mean ratio from the pairs we
                        // already collected in the single pass above. No
                        // re-scan of profile.Trials needed.
                        var targetList = new List<double>(perTrialPairsByDir[d].Count);
                        var observedList = new List<double>(perTrialPairsByDir[d].Count);
                        foreach (var pair in perTrialPairsByDir[d])
                        {
                            targetList.Add(pair.target);
                            observedList.Add(pair.observed);
                        }
                        if (targetList.Count > 0 && observedList.Count > 0)
                        {
                            double targetMean = RobustStatistics.TrimmedMean(targetList, 0.2);
                            double observedMean = RobustStatistics.TrimmedMean(observedList, 0.2);
                            factors[d] = Clamp(targetMean / Math.Max(DesktopSensConstants.MinimumObservedDistanceCm, observedMean),
                                DesktopSensConstants.MinimumCalibrationFactor, DesktopSensConstants.MaximumCalibrationFactor);
                        }
                        else
                        {
                            factors[d] = Clamp(targetTotals[d] / Math.Max(DesktopSensConstants.MinimumObservedDistanceCm, observedTotals[d]),
                                DesktopSensConstants.MinimumCalibrationFactor, DesktopSensConstants.MaximumCalibrationFactor);
                        }
                        confidence[d] = Math.Min(1.0, counts[d] / 10.0);
                    }
                }

                // ───────────────────────────────────────────────────────────────
                // v5: fit multi-model curve for this direction.
                // ───────────────────────────────────────────────────────────────
                if (curveSamples[d].Count >= DesktopSensConstants.CurveFittingMinSamples)
                {
                    CurveFitResult fit = AccelCurveFitter.FitBest(curveSamples[d]);
                    if (fit.IsValid)
                    {
                        profile.SetDirectionalCurveMode(d, fit.Mode);
                        profile.SetDirectionalCurveParameters(d, fit.Parameters);
                        profile.DirectionalCurveRmse[d] = fit.Rmse;
                        profile.DirectionalCurveAic[d] = fit.Aic;
                        profile.DirectionalCurveSampleCount[d] = fit.SampleCount;
                        // Boost confidence from the curve quality.
                        double scale = Math.Max(0.1, Math.Abs(targetTotals[d] / Math.Max(1, counts[d])));
                        double curveConf = RobustStatistics.RobustConfidence(
                            fit.SampleCount, fit.Rmse * scale, scale);
                        confidence[d] = Math.Max(confidence[d], curveConf);

                        // DIAGNOSTIC: log the fitted mode and parameters so the
                        // user can see (in the DesktopSensLog ring buffer) why the
                        // driver sees or doesn't see an acceleration curve for
                        // this direction. The most common reason the mode ends
                        // up as "Linear with accel=0" is that the user's trials
                        // all have similar ratios at different speeds — i.e.
                        // the user's sensitivity does not actually vary with
                        // speed, so no acceleration curve can be fitted.
                        DesktopSensLog.Warn(
                            "Direction " + d + " curve fit: mode=" + fit.Mode +
                            ", samples=" + fit.SampleCount +
                            ", rmse=" + fit.Rmse.ToString("0.0000") +
                            ", params=[" + FormatParams(fit.Parameters) + "]");
                    }
                    else
                    {
                        profile.SetDirectionalCurveMode(d, AccelCurveMode.NoAccel);
                        profile.SetDirectionalCurveParameters(d, new double[0]);
                        profile.DirectionalCurveRmse[d] = 0.0;
                        profile.DirectionalCurveAic[d] = 0.0;
                        profile.DirectionalCurveSampleCount[d] = 0;
                        // DIAGNOSTIC: FitBest returned invalid — typically
                        // because every per-model fit failed (singular matrix
                        // in LM, or AICc undefined because sample count ≤ k+1).
                        DesktopSensLog.Warn(
                            "Direction " + d + " curve fit FAILED (FitBest returned invalid). " +
                            "samples=" + curveSamples[d].Count);
                    }
                }
                else if (!preserveUnobservedDirections || counts[d] > 0 || curveSamples[d].Count > 0)
                {
                    // Not enough samples for a curve fit; legacy factor still applies.
                    profile.SetDirectionalCurveMode(d, AccelCurveMode.NoAccel);
                    profile.SetDirectionalCurveParameters(d, new double[0]);
                    profile.DirectionalCurveRmse[d] = 0.0;
                    profile.DirectionalCurveAic[d] = 0.0;
                    profile.DirectionalCurveSampleCount[d] = 0;
                    // DIAGNOSTIC: log when we have too few samples so the user
                    // can tell that the trainer just needs more trials, not a
                    // bug.
                    DesktopSensLog.Warn(
                        "Direction " + d + " curve fit SKIPPED (too few samples: " +
                        curveSamples[d].Count + "/" + DesktopSensConstants.CurveFittingMinSamples + ").");
                }
            }

            profile.RightSensitivityFactor = factors[0];
            profile.DownSensitivityFactor  = factors[1];
            profile.LeftSensitivityFactor  = factors[2];
            profile.UpSensitivityFactor    = factors[3];
            profile.RightSensitivityConfidence = confidence[0];
            profile.DownSensitivityConfidence  = confidence[1];
            profile.LeftSensitivityConfidence  = confidence[2];
            profile.UpSensitivityConfidence    = confidence[3];

            // DIAGNOSTIC: log the final cardinal factors so the user can
            // see — via "Show Diag Log" — exactly what the calibrator
            // produced. If these are all 1.000 it means either:
            //   (a) the trial set had fewer than 3 valid trials per
            //       direction (TryLogGainConfidence returns false with
            //       < 3 samples, so the estimator falls back to the
            //       initial 1.0);
            //   (b) every trial was rejected by the noise-floor filter
            //       (|signedError| < profile.NoiseFloorCm — run N to
            //       estimate the noise floor first, or the filter is
            //       disabled when NoiseFloorCm = 0);
            //   (c) every trial was a long-flick (target > 3 cm) and
            //       so was routed only to the curve-fit path, not to
            //       the per-direction factor estimate;
            //   (d) the user's flicks were so accurate that logGain
            //       came out as 0 → factor = exp(0) = 1.0.
            DesktopSensLog.Warn(
                "Calibrator: per-direction counts R=" + counts[0] +
                " D=" + counts[1] + " L=" + counts[2] + " U=" + counts[3] +
                " | final factors R=" + factors[0].ToString("0.000") +
                " D=" + factors[1].ToString("0.000") +
                " L=" + factors[2].ToString("0.000") +
                " U=" + factors[3].ToString("0.000") +
                " | confidence R=" + confidence[0].ToString("0.000") +
                " D=" + confidence[1].ToString("0.000") +
                " L=" + confidence[2].ToString("0.000") +
                " U=" + confidence[3].ToString("0.000"));

            // Store the per-quadrant rotation means on the profile so that
            // ApplyDirectionalProfile can collapse them into a single scalar for
            // the driver via ComputeWeightedRotation().
            profile.RightRotationOffsetDegrees = CircularMean(rotSin[0], rotCos[0], rotCount[0]);
            profile.DownRotationOffsetDegrees  = CircularMean(rotSin[1], rotCos[1], rotCount[1]);
            profile.LeftRotationOffsetDegrees  = CircularMean(rotSin[2], rotCos[2], rotCount[2]);
            profile.UpRotationOffsetDegrees    = CircularMean(rotSin[3], rotCos[3], rotCount[3]);

            profile.ModelVersion = DesktopSensConstants.CurrentModelVersion;
            profile.ModifiedTimestamp = DateTime.UtcNow;
        }

        /// <summary>
        /// FIX (8/16-way): compute the four-cardinal-bucket projection
        /// weights for an axis in the active direction-count mode.
        ///
        /// Returns an array of length 4 (Right/Down/Left/Up), where each
        /// entry is in [0, 1] and the sum is 1.0 (subject to FP rounding).
        /// A pure cardinal axis (Right, Down, Left, Up in any mode) puts
        /// weight 1.0 on its bucket and 0 on the others. A diagonal axis
        /// (8-way: DownRight, DownLeft, UpLeft, UpRight; 16-way: the eight
        /// half-step directions) splits its weight between the two
        /// cardinals that bracket it, weighted by the cosine of the
        /// angular distance between the axis and each cardinal.
        ///
        /// For example, in 8-way mode, axis 1 (DownRight at 45°) has:
        ///   weight[Right] = cos(45°) = 0.707
        ///   weight[Down]  = sin(45°) = 0.707
        ///   weight[Left]  = 0
        ///   weight[Up]    = 0
        ///
        /// In 16-way mode, axis 1 (Right/DownRight at 22.5°) has:
        ///   weight[Right] = cos(22.5°) ≈ 0.924
        ///   weight[Down]  = sin(22.5°) ≈ 0.383
        ///
        /// Returns null if `axisIndex` is out of range for the mode.
        /// </summary>
        public static double[] ComputeCardinalProjectionWeights(int axisIndex, int directionCount)
        {
            int[] zones = DesktopSensTrainingState.AngleZoneIndicesForCount(directionCount);
            if (axisIndex < 0 || axisIndex >= zones.Length) return null;
            int zone = zones[axisIndex];
            // Each zone is 11.25° wide; the axis sits at zone * 11.25°.
            double axisAngleDeg = zone * (360.0 / DesktopSensConstants.AngularZoneCount);
            // Wrap to [0, 360).
            axisAngleDeg = ((axisAngleDeg % 360.0) + 360.0) % 360.0;

            // Cardinal buckets sit at 0° (Right), 90° (Down), 180° (Left),
            // 270° (Up). The projection weight on each bucket is the
            // non-negative part of the cosine of the angular distance to
            // that bucket, normalized so that the four weights sum to 1.
            // For an axis exactly on a cardinal, the weight on that
            // cardinal is 1.0 and 0 on the others. For an axis exactly
            // between two cardinals (e.g. 45° for 8-way DownRight), the
            // weights are 0.707 each on the two bracketing cardinals.
            double[] raw = new double[4];
            double[] cardinalAngles = { 0.0, 90.0, 180.0, 270.0 };
            double totalRaw = 0.0;
            for (int d = 0; d < 4; d++)
            {
                // Signed angular distance to this cardinal, normalized
                // to [-180, 180] so cos is symmetric.
                double delta = axisAngleDeg - cardinalAngles[d];
                while (delta > 180.0)  delta -= 360.0;
                while (delta < -180.0)  delta += 360.0;
                // Weight = max(0, cos(|delta|)). At 0° weight = 1, at 90°
                // weight = 0, at 180° weight = -1 (clamped to 0). This
                // naturally handles the case where an axis is on the
                // opposite side of the compass from a cardinal.
                double w = Math.Cos(delta * Math.PI / 180.0);
                if (w < 0.0) w = 0.0;
                raw[d] = w;
                totalRaw += w;
            }

            // Normalize so the four weights sum to 1.0 (subject to FP
            // rounding). For a pure cardinal axis only one weight is
            // non-zero, so it ends up at 1.0. For a 45° diagonal two
            // weights are 0.707 each; their sum is 1.414, so after
            // normalization each is 0.5 — which means each cardinal
            // bucket gets half of the trial's contribution. That matches
            // the user's mental model: a 45° flick is "half Right, half
            // Down".
            double[] weights = new double[4];
            if (totalRaw > 1e-9)
            {
                for (int d = 0; d < 4; d++)
                    weights[d] = raw[d] / totalRaw;
            }
            else
            {
                // Degenerate — axisIndex was somehow invalid. Fall back
                // to no contribution.
                return null;
            }
            return weights;
        }

        /// <summary>
        /// Determine the dominant cardinal direction from the actual raw mouse travel.
        /// Windows RawInput: DX+ = right, DY+ = down.
        ///   0 = Right (+X), 1 = Down (+Y), 2 = Left (-X), 3 = Up (-Y).
        /// Uses |sum(DX)| vs |sum(DY)| as the 45-degree discriminant, so a 30-degree
        /// diagonal flick is still classified as Right (closer to 0° than to 90°).
        /// </summary>
        public static int DirectionFromSamples(DesktopSensTrial trial)
        {
            if (trial.RawSamples == null || trial.RawSamples.Count == 0) return -1;
            double sumDx = 0, sumDy = 0;
            foreach (var s in trial.RawSamples)
            {
                if ((object)s == null) continue;
                sumDx += s.DX;
                sumDy += s.DY;
            }
            if (Math.Abs(sumDx) < 1e-6 && Math.Abs(sumDy) < 1e-6) return -1;
            return Math.Abs(sumDx) >= Math.Abs(sumDy)
                ? (sumDx >= 0 ? 0 : 2)
                : (sumDy >= 0 ? 1 : 3);
        }

        /// <summary>
        /// FIX (8/16-way): classify a trial's direction into one of N
        /// angular buckets, where N is 4, 8, or 16. Returns the axis index
        /// in the requested mode, or -1 if the trial has no measurable
        /// motion.
        ///
        /// The classification uses the same atan2(sumDy, sumDx) angle as
        /// the per-cell calibrator's zone resolver. Each axis owns a
        /// non-overlapping angular wedge of width (360° / N) centered on
        /// its axis direction:
        ///   • 4-way:  wedges are 90° wide (±45° around each cardinal)
        ///   • 8-way:  wedges are 45° wide (±22.5° around each axis)
        ///   • 16-way: wedges are 22.5° wide (±11.25° around each axis)
        /// Adjacent wedges share only the boundary line, never an
        /// interior point — exactly the "no overlap" rule the user asked
        /// for. A flick whose direction lies exactly on a boundary is
        /// attributed to the lower-indexed axis (deterministic tiebreak).
        /// </summary>
        public static int DirectionFromSamplesMulti(DesktopSensTrial trial, int directionCount)
        {
            if (trial == null || trial.RawSamples == null || trial.RawSamples.Count == 0)
                return -1;
            if (directionCount != 4 && directionCount != 8 && directionCount != 16)
                directionCount = 4;

            double sumDx = 0, sumDy = 0;
            foreach (var s in trial.RawSamples)
            {
                if ((object)s == null) continue;
                sumDx += s.DX;
                sumDy += s.DY;
            }
            if (Math.Abs(sumDx) < 1e-6 && Math.Abs(sumDy) < 1e-6) return -1;

            // Compute the angle in degrees, measured clockwise from +X
            // (Right) in the calibration frame: 0° = Right, 90° = Down,
            // 180° = Left, 270° = Up. This matches the calibration-frame
            // convention used everywhere else.
            double angleDeg = Math.Atan2(sumDy, sumDx) * 180.0 / Math.PI;
            // Wrap to [0, 360)
            angleDeg = ((angleDeg % 360.0) + 360.0) % 360.0;

            // Each axis is at i * (360 / N) degrees. Find the nearest one
            // by rounding the angle to the nearest (360/N) multiple. This
            // automatically handles the wedge boundaries correctly.
            double stepDeg = 360.0 / directionCount;
            double nearestBucket = Math.Round(angleDeg / stepDeg) % directionCount;
            int axis = (int)nearestBucket;
            if (axis < 0) axis += directionCount;
            return axis;
        }

        private static void AddCurveSample(List<SensitivitySample> samples, DesktopSensTrial trial, double sessionBiasLogGain)
        {
            if (!IsFinite(trial.TargetDistanceCm) || trial.TargetDistanceCm <= 0.0) return;
            if (!IsFinite(trial.FlickDistanceCm) || trial.FlickDistanceCm <= 0.0) return;
            if (!IsFinite(trial.DurationMs) || trial.DurationMs <= 0.0) return;

            double speedCmPerSec = trial.FlickDistanceCm / (trial.DurationMs / 1000.0);
            if (!IsFinite(speedCmPerSec) || speedCmPerSec <= 0.0) return;

            double ratio = trial.FlickDistanceCm / trial.TargetDistanceCm;
            if (IsFinite(sessionBiasLogGain)) ratio *= Math.Exp(-sessionBiasLogGain);
            if (!IsFinite(ratio) || ratio <= 0.0) return;

            samples.Add(new SensitivitySample
            {
                InputSpeedCmPerSec = speedCmPerSec,
                ObservedRatio = ratio,
                Weight = 1.0
            });
        }

        /// <summary>
        /// DIAGNOSTIC helper: format a curve-parameter vector for the log
        /// message in the curve-fit branch. Returns a comma-separated list
        /// of doubles with 4 decimal places, or "(empty)" if parameters
        /// is null or empty.
        /// </summary>
        private static string FormatParams(double[] parameters)
        {
            if (parameters == null || parameters.Length == 0) return "(empty)";
            var sb = new System.Text.StringBuilder(parameters.Length * 8);
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(parameters[i].ToString("0.0000"));
            }
            return sb.ToString();
        }

        private static double CircularMean(double sin, double cos, int count)
        {
            if (count == 0) return 0.0;
            return Math.Atan2(sin, cos) * 180.0 / Math.PI;
        }

        private static double Clamp(double v, double min, double max)
        {
            if (!IsFinite(v)) return 1.0;
            return Math.Max(min, Math.Min(max, v));
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
