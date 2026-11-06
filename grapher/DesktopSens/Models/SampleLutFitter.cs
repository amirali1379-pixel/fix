using System;
using System.Collections.Generic;
using System.Diagnostics;
using grapher.Models.Mouse;

namespace grapher.DesktopSens.Models
{
    /// <summary>
    /// Fits a continuous sensitivity multiplier f(v) directly to the raw
    /// per-sample (speed, displacement) observations of each trial.
    ///
    /// Why this replaces the median-speed LUT:
    ///   RawAccel's LUT applies the multiplier at the INSTANT speed of each
    ///   sample. A flick sweeps through many speeds, so summarizing a cell
    ///   to a single median speed discards the shape of the curve and can
    ///   leave zones effectively uncalibrated. This fitter reconstructs
    ///   the true f(v) from every sample:
    ///
    ///       Σ f(v_i) · d_i  ≈  effective target · currentBase / capturedBase
    ///
    ///   where d_i is the signed displacement projected onto the target axis.
    ///
    /// The output is a fixed-node piecewise-linear curve in RawAccel's LUT
    /// speed unit (cm/s ÷ 2.54), suitable for direct serialization by
    /// RawAccelSettingsBridge.TryBuildLutValues.
    ///
    /// Constraints:
    ///   • f > 0 at every node (clamped to a small positive floor).
    ///   • Small ridge prior toward 1.0 so sparse data cannot explode.
    ///   • Small smoothness penalty so the emitted curve is not noisy.
    ///   • The caller controls the upper-bookend behaviour (NeutralOne or
    ///     ExtendLastFactor) via the profile.
    /// </summary>
    public static class SampleLutFitter
    {
        public sealed class FitResult
        {
            public double[] NodeSpeedCmPerSec;
            public double[] NodeFactors;
            public bool IsValid;
            public double RmseCm;
            public int SampleCount;
            public int TrialCount;
            public string FailureReason;
        }

        public static FitResult Fit(
            IList<DesktopSensTrial> trials,
            int nodeCount = DesktopSensConstants.SampleLutDefaultNodes,
            bool includeYellowCells = true)
        {
            return Fit(trials, null, nodeCount, includeYellowCells, null, 1.0);
        }

        /// <summary>
        /// Fit f(v) with optional anchors. Each anchor is a (speed, factor) pair
        /// from a previously-built green-cell LUT. Anchors pin the nearest node
        /// to its previous factor with a high weight so that adding a new
        /// green cell cannot rewrite the curve at speeds the user already
        /// verified. This is the per-point isolation guarantee described in
        /// the LUT-fix README: previously-verified LUT points survive a
        /// re-fit and only the unverified region of the curve is free to
        /// move.
        /// </summary>
        public static FitResult Fit(
            IList<DesktopSensTrial> trials,
            IList<(double speedCmPerSec, double factor)> anchors,
            int nodeCount = DesktopSensConstants.SampleLutDefaultNodes,
            bool includeYellowCells = true)
        {
            return Fit(trials, anchors, nodeCount, includeYellowCells, null, 1.0);
        }

        /// <summary>
        /// Weighted fit. trialWeights is index-aligned with trials; green cells
        /// use 1.0, yellow cells use a capped confidence weight. targetScale
        /// converts the target distance into the multiplier domain of the
        /// currently-applied directional base sensitivity.
        /// </summary>
        public static FitResult Fit(
            IList<DesktopSensTrial> trials,
            IList<(double speedCmPerSec, double factor)> anchors,
            int nodeCount,
            bool includeYellowCells,
            IList<double> trialWeights,
            double targetScale,
            IList<double> targetScales = null)
        {
            var result = new FitResult
            {
                IsValid = false,
                FailureReason = "no data"
            };
            if (trials == null || trials.Count == 0)
                return result;
            if (!IsFinite(targetScale) || targetScale <= 0.0)
            {
                result.FailureReason = "invalid target scale";
                return result;
            }

            if (nodeCount < DesktopSensConstants.SampleLutMinNodes)
                nodeCount = DesktopSensConstants.SampleLutMinNodes;
            if (nodeCount > DesktopSensConstants.SampleLutMaxNodes)
                nodeCount = DesktopSensConstants.SampleLutMaxNodes;

            // Pre-pass: determine the actual speed range covered by the
            // accepted trials so the nodes span the data rather than the
            // global default range.
            double minSpeed = double.MaxValue;
            double maxSpeed = double.MinValue;
            int usableTrialCount = 0;
            for (int trialIndex = 0; trialIndex < trials.Count; trialIndex++)
            {
                DesktopSensTrial trial = trials[trialIndex];
                if (!IsTrialEligible(trial, includeYellowCells) ||
                    GetTrialWeight(trialWeights, trialIndex) <= 0.0) continue;
                double perTrialScale = targetScale * GetTargetScale(targetScales, trialIndex);
                List<(double speed, double displacement)> samples =
                    ExtractProjectedSamples(trial, out _, perTrialScale);
                if (samples.Count == 0) continue;
                foreach (var s in samples)
                {
                    if (s.speed < minSpeed) minSpeed = s.speed;
                    if (s.speed > maxSpeed) maxSpeed = s.speed;
                }
                usableTrialCount++;
            }
            if (usableTrialCount == 0 || !IsFinite(minSpeed) || !IsFinite(maxSpeed))
            {
                result.FailureReason = "no usable trial samples";
                return result;
            }

            // Expand the observed range with a margin so the bookends do
            // not sit on the very edge of the data.
            minSpeed = Math.Max(minSpeed * 0.5, DesktopSensConstants.SampleLutMinSpeedCmPerSec);
            maxSpeed = Math.Min(maxSpeed * 2.0, DesktopSensConstants.SampleLutMaxSpeedCmPerSec);
            if (maxSpeed <= minSpeed * 1.1)
                maxSpeed = minSpeed * 2.0;

            // Log-spaced nodes over [minSpeed, maxSpeed].
            double[] nodeSpeeds = new double[nodeCount];
            double logMin = Math.Log(minSpeed);
            double logMax = Math.Log(maxSpeed);
            for (int i = 0; i < nodeCount; i++)
            {
                double t = nodeCount == 1 ? 0.0 : (double)i / (nodeCount - 1);
                nodeSpeeds[i] = Math.Exp(logMin + (logMax - logMin) * t);
            }

            // Build the linear system:
            //   A[k, trial] = contribution of node k to trial's prediction
            //   b[trial]    = NeededDistanceCm (or TargetDistanceCm)
            // Solve (A^T W A + λ D + μ I) f = A^T W b + μ 1
            // where D is the second-difference smoothness matrix.
            int n = nodeCount;
            double[,] normal = new double[n, n];
            double[] rhs = new double[n];

            // Ridge prior toward 1.0.
            double prior = DesktopSensConstants.SampleLutPriorWeight;
            for (int i = 0; i < n; i++)
            {
                normal[i, i] += prior;
                rhs[i] += prior * 1.0;
            }

            // Smoothness penalty: λ Σ (f_{i+1} - f_i)^2
            double smooth = DesktopSensConstants.SampleLutSmoothWeight;
            for (int i = 0; i < n - 1; i++)
            {
                normal[i, i] += smooth;
                normal[i + 1, i + 1] += smooth;
                normal[i, i + 1] -= smooth;
                normal[i + 1, i] -= smooth;
            }

            // NEW (Model v6, per-point isolation): anchor nodes to their
            // previously-built factors. Each anchor is a (speed, factor)
            // pair from a previously-verified LUT point. We pin the nearest
            // node to that factor with a high weight so adding a new green
            // cell cannot rewrite the curve at speeds the user already
            // verified — only the unverified region of the curve is free
            // to move. This implements the isolation guarantee described in
            // README_LUT_FIX_V2.md: previously-built LUT points survive a
            // re-fit instead of being silently overwritten.
            if (anchors != null && anchors.Count > 0)
            {
                double anchorWeight = DesktopSensConstants.SampleLutAnchorWeight;
                foreach (var anchor in anchors)
                {
                    if (!IsFinite(anchor.speedCmPerSec) || anchor.speedCmPerSec <= 0.0 ||
                        !IsFinite(anchor.factor) || anchor.factor <= 0.0) continue;
                    // Find the nearest node by log-speed (consistent with
                    // how samples are projected onto the node basis below).
                    double logV = Math.Log(anchor.speedCmPerSec);
                    double u = (logV - logMin) / Math.Max(1e-9, (logMax - logMin));
                    u = Math.Max(0.0, Math.Min(1.0, u));
                    double nodePos = u * (n - 1);
                    int k0 = (int)Math.Floor(nodePos);
                    int k1 = Math.Min(n - 1, k0 + 1);
                    double frac = nodePos - k0;
                    // Split the anchor between the two bracketing nodes
                    // proportional to their interpolation weights. A node
                    // that receives a non-zero anchor fraction is pinned
                    // toward the anchor's factor.
                    double w0 = (1.0 - frac) * anchorWeight;
                    double w1 = frac * anchorWeight;
                    if (w0 > 0.0)
                    {
                        normal[k0, k0] += w0;
                        rhs[k0] += w0 * anchor.factor;
                    }
                    if (w1 > 0.0)
                    {
                        normal[k1, k1] += w1;
                        rhs[k1] += w1 * anchor.factor;
                    }
                }
            }

            int totalSamples = 0;
            for (int trialIndex = 0; trialIndex < trials.Count; trialIndex++)
            {
                DesktopSensTrial trial = trials[trialIndex];
                if (!IsTrialEligible(trial, includeYellowCells)) continue;
                double weight = GetTrialWeight(trialWeights, trialIndex);
                if (weight <= 0.0) continue;
                double perTrialScale = targetScale * GetTargetScale(targetScales, trialIndex);
                List<(double speed, double displacement)> samples =
                    ExtractProjectedSamples(trial, out double targetCm, perTrialScale);
                if (samples.Count == 0 || !IsFinite(targetCm)) continue;

                // Project the trial's samples onto the node basis using
                // linear interpolation in log-speed.
                double[] row = new double[n];
                double rowSum = 0.0;
                foreach (var s in samples)
                {
                    if (!IsFinite(s.speed) || s.speed <= 0.0 ||
                        !IsFinite(s.displacement) || s.displacement == 0.0)
                        continue;
                    double logV = Math.Log(s.speed);
                    double u = (logV - logMin) / Math.Max(1e-9, (logMax - logMin));
                    u = Math.Max(0.0, Math.Min(1.0, u));
                    double nodePos = u * (n - 1);
                    int k0 = (int)Math.Floor(nodePos);
                    int k1 = Math.Min(n - 1, k0 + 1);
                    double frac = nodePos - k0;
                    row[k0] += s.displacement * (1.0 - frac);
                    row[k1] += s.displacement * frac;
                    rowSum += s.displacement;
                    totalSamples++;
                }
                if (Math.Abs(rowSum) < 1e-9) continue;

                // Normal equations: A^T W A and A^T W b.
                for (int i = 0; i < n; i++)
                {
                    if (Math.Abs(row[i]) < 1e-12) continue;
                    rhs[i] += weight * row[i] * targetCm;
                    for (int j = 0; j < n; j++)
                        if (Math.Abs(row[j]) >= 1e-12)
                            normal[i, j] += weight * row[i] * row[j];
                }
            }

            // Solve via Gaussian elimination with partial pivoting.
            double[] solution;
            if (!TrySolve(normal, rhs, out solution))
            {
                result.FailureReason = "singular normal equations";
                return result;
            }

            // Keep fitted factors inside the same broad safety range used
            // by the calibrator and merger. This is deliberately much wider
            // than a 0.5x-2.0x clamp, but prevents an ill-conditioned sparse
            // fit from emitting a near-zero or explosive driver multiplier.
            for (int i = 0; i < n; i++)
            {
                if (!IsFinite(solution[i])) solution[i] = 1.0;
                if (solution[i] < DesktopSensConstants.MinimumCalibrationFactor)
                    solution[i] = DesktopSensConstants.MinimumCalibrationFactor;
                if (solution[i] > DesktopSensConstants.MaximumCalibrationFactor)
                    solution[i] = DesktopSensConstants.MaximumCalibrationFactor;
            }

            // Compute RMSE for diagnostics.
            double sumSq = 0.0;
            double errorWeight = 0.0;
            int errors = 0;
            for (int trialIndex = 0; trialIndex < trials.Count; trialIndex++)
            {
                DesktopSensTrial trial = trials[trialIndex];
                if (!IsTrialEligible(trial, includeYellowCells)) continue;
                double weight = GetTrialWeight(trialWeights, trialIndex);
                if (weight <= 0.0) continue;
                double perTrialScale = targetScale * GetTargetScale(targetScales, trialIndex);
                List<(double speed, double displacement)> samples =
                    ExtractProjectedSamples(trial, out double targetCm, perTrialScale);
                if (samples.Count == 0 || !IsFinite(targetCm)) continue;
                double predicted = 0.0;
                foreach (var s in samples)
                {
                    double logV = Math.Log(Math.Max(1e-9, s.speed));
                    double u = (logV - logMin) / Math.Max(1e-9, (logMax - logMin));
                    u = Math.Max(0.0, Math.Min(1.0, u));
                    double nodePos = u * (n - 1);
                    int k0 = (int)Math.Floor(nodePos);
                    int k1 = Math.Min(n - 1, k0 + 1);
                    double frac = nodePos - k0;
                    double f = solution[k0] * (1.0 - frac) + solution[k1] * frac;
                    predicted += f * s.displacement;
                }
                double err = predicted - targetCm;
                sumSq += weight * err * err;
                errorWeight += weight;
                errors++;
            }
            double rmse = errorWeight > 0.0 ? Math.Sqrt(sumSq / errorWeight) : double.PositiveInfinity;

            result.NodeSpeedCmPerSec = nodeSpeeds;
            result.NodeFactors = solution;
            result.RmseCm = rmse;
            result.SampleCount = totalSamples;
            result.TrialCount = usableTrialCount;
            result.IsValid = errors > 0 && IsFinite(rmse) && totalSamples > 0;
            if (!result.IsValid && string.IsNullOrEmpty(result.FailureReason))
                result.FailureReason = "no errors computed";
            return result;
        }

        /// <summary>
        /// Convert a SampleLutFitter.FitResult into a List&lt;LutPoint&gt;
        /// suitable for TryBuildLutValues. Speeds are in cm/s.
        /// </summary>
        public static List<LutPoint> ToLutPoints(
            FitResult fit,
            double confidence = 0.8)
        {
            var points = new List<LutPoint>();
            if (fit == null || !fit.IsValid || fit.NodeSpeedCmPerSec == null ||
                fit.NodeFactors == null || fit.NodeSpeedCmPerSec.Length != fit.NodeFactors.Length)
                return points;

            for (int i = 0; i < fit.NodeSpeedCmPerSec.Length; i++)
            {
                double speed = fit.NodeSpeedCmPerSec[i];
                double factor = fit.NodeFactors[i];
                if (!IsFinite(speed) || speed <= 0.0) continue;
                if (!IsFinite(factor) || factor <= 0.0) continue;
                points.Add(new LutPoint(speed, factor, confidence));
            }
            return points;
        }

        private static bool IsTrialEligible(DesktopSensTrial trial, bool includeYellowCells)
        {
            if (trial == null || trial.IsEvaluationOnly || trial.IsSessionAnchor ||
                trial.IsCalibrationExcluded || trial.WasRejected) return false;
            // Match DesktopSensCalibrator.TryBuildCurveSample: an ASDW-assisted
            // trial is usable only when FinishTrial confirmed the final landing.
            // Failed ASDW trials deliberately keep NeededDistanceCm as NaN.
            if (trial.HasManualAsdwCorrection &&
                (!IsFinite(trial.NeededDistanceCm) ||
                 trial.NeededDistanceCm <= DesktopSensConstants.MinimumDenominatorCm))
                return false;
            if (!trial.HasMovementMeasurement) return false;
            if (trial.RawSamples == null || trial.RawSamples.Count == 0) return false;
            if (!IsFinite(trial.FlickDistanceCm) || trial.FlickDistanceCm <= 0.0) return false;
            if (!IsFinite(trial.DurationMs) || trial.DurationMs <= 0.0) return false;
            double target = IsFinite(trial.NeededDistanceCm) &&
                            trial.NeededDistanceCm > 0.0
                ? trial.NeededDistanceCm
                : trial.TargetDistanceCm;
            if (!IsFinite(target) || target <= DesktopSensConstants.MinimumDenominatorCm) return false;

            // Yellow cells are included only if the caller allows it. Their
            // weight is supplied by the profile-aware caller.
            if (!includeYellowCells) return true; // caller already filtered green
            return true;
        }

        private static double GetTargetScale(IList<double> targetScales, int trialIndex)
        {
            if (targetScales == null) return 1.0;
            if (trialIndex < 0 || trialIndex >= targetScales.Count) return 0.0;
            double scale = targetScales[trialIndex];
            return IsFinite(scale) && scale > 0.0 ? scale : 0.0;
        }

        private static double GetTrialWeight(IList<double> trialWeights, int trialIndex)
        {
            if (trialWeights == null) return 1.0;
            if (trialIndex < 0 || trialIndex >= trialWeights.Count) return 0.0;
            double weight = trialWeights[trialIndex];
            return IsFinite(weight) && weight > 0.0 ? weight : 0.0;
        }

        /// <summary>
        /// Public weight calculator so tests and external callers can verify
        /// the green-vs-yellow weighting policy without re-implementing it.
        ///
        /// Green cells (Verified + PrecisionVerified) get weight 1.0.
        /// Yellow cells (at least one accepted attempt, not yet verified) get
        /// the cell's measured confidence, floored at
        /// DesktopSensConstants.YellowCellMinLutWeight so a low-confidence
        /// yellow cell still pulls the fit toward the correct shape.
        /// Cells with no accepted attempts get weight 0 (excluded).
        /// </summary>
        public static double ComputeCellWeight(
            bool isGreen,
            bool isYellow,
            double cellConfidence)
        {
            if (isGreen) return 1.0;
            if (!isYellow) return 0.0;
            double c = IsFinite(cellConfidence) ? cellConfidence : 0.0;
            if (c < 0.0) c = 0.0;
            if (c > DesktopSensConstants.YellowCellMaxLutWeight)
                c = DesktopSensConstants.YellowCellMaxLutWeight;
            if (c < DesktopSensConstants.YellowCellMinLutWeight)
                c = DesktopSensConstants.YellowCellMinLutWeight;
            return c;
        }

        /// <summary>Evaluate the fitted piecewise-linear curve, clamping to its nearest endpoint.</summary>
        public static double EvaluateFactor(FitResult fit, double speedCmPerSec)
        {
            if (fit == null || !fit.IsValid || fit.NodeSpeedCmPerSec == null ||
                fit.NodeFactors == null || fit.NodeSpeedCmPerSec.Length == 0 ||
                fit.NodeSpeedCmPerSec.Length != fit.NodeFactors.Length ||
                !IsFinite(speedCmPerSec) || speedCmPerSec <= 0.0)
                return double.NaN;

            int last = fit.NodeSpeedCmPerSec.Length - 1;
            if (speedCmPerSec <= fit.NodeSpeedCmPerSec[0]) return fit.NodeFactors[0];
            if (speedCmPerSec >= fit.NodeSpeedCmPerSec[last]) return fit.NodeFactors[last];
            for (int i = 1; i <= last; i++)
            {
                if (speedCmPerSec > fit.NodeSpeedCmPerSec[i]) continue;
                double x0 = fit.NodeSpeedCmPerSec[i - 1];
                double x1 = fit.NodeSpeedCmPerSec[i];
                double t = (speedCmPerSec - x0) / (x1 - x0);
                return fit.NodeFactors[i - 1] * (1.0 - t) + fit.NodeFactors[i] * t;
            }
            return fit.NodeFactors[last];
        }

        private static List<(double speed, double displacement)>
            ExtractProjectedSamples(DesktopSensTrial trial, out double targetCm)
        {
            return ExtractProjectedSamples(trial, out targetCm, 1.0);
        }

        private static List<(double speed, double displacement)>
            ExtractProjectedSamples(DesktopSensTrial trial, out double targetCm, double targetScale)
        {
            double baseTarget = IsFinite(trial.NeededDistanceCm) &&
                                trial.NeededDistanceCm > DesktopSensConstants.MinimumDenominatorCm
                ? trial.NeededDistanceCm
                : trial.TargetDistanceCm;
            targetCm = baseTarget * targetScale;

            var resultList = new List<(double, double)>();
            if (!IsFinite(targetCm) || targetCm <= DesktopSensConstants.MinimumDenominatorCm)
                return resultList;
            if (trial.RawSamples == null || trial.RawSamples.Count < 2) return resultList;

            // Project each sample onto the target axis so a pure forward
            // flick yields positive displacement and backtracking is
            // correctly cancelled out.
            double angleRad = trial.TargetAngleDegrees * Math.PI / 180.0;
            double axisX = Math.Cos(angleRad);
            double axisY = Math.Sin(angleRad);
            double cmPerCount = trial.EffectiveCpi > 0.0
                ? DesktopSensConstants.InchesToCm / trial.EffectiveCpi
                : 0.0;
            if (cmPerCount <= 0.0) return resultList;

            long previousTimestamp = trial.RawSamples[0].Timestamp;
            for (int i = 0; i < trial.RawSamples.Count; i++)
            {
                MouseSample sample = trial.RawSamples[i];
                if (i > 0)
                {
                    long delta = sample.Timestamp - previousTimestamp;
                    if (delta <= 0) continue;
                    double dtSec = delta / (double)Stopwatch.Frequency;
                    if (dtSec <= 0.0) continue;
                    double dx = sample.DX;
                    double dy = sample.DY;
                    double projCounts = dx * axisX + dy * axisY;
                    double displacementCm = projCounts * cmPerCount;
                    double speedCmPerSec = Math.Abs(displacementCm) / dtSec;
                    if (IsFinite(speedCmPerSec) && speedCmPerSec > 0.0 &&
                        IsFinite(displacementCm) && displacementCm != 0.0)
                        resultList.Add((speedCmPerSec, displacementCm));
                }
                previousTimestamp = sample.Timestamp;
            }
            return resultList;
        }

        private static bool TrySolve(double[,] matrix, double[] vector, out double[] solution)
        {
            int size = vector.Length;
            var aug = new double[size, size + 1];
            for (int r = 0; r < size; r++)
            {
                for (int c = 0; c < size; c++) aug[r, c] = matrix[r, c];
                aug[r, size] = vector[r];
            }
            for (int pivot = 0; pivot < size; pivot++)
            {
                int bestRow = pivot;
                double bestMag = Math.Abs(aug[pivot, pivot]);
                for (int r = pivot + 1; r < size; r++)
                {
                    double mag = Math.Abs(aug[r, pivot]);
                    if (mag > bestMag) { bestMag = mag; bestRow = r; }
                }
                if (bestMag < 1e-14)
                {
                    solution = new double[size];
                    return false;
                }
                if (bestRow != pivot)
                {
                    for (int c = pivot; c <= size; c++)
                    {
                        double t = aug[pivot, c];
                        aug[pivot, c] = aug[bestRow, c];
                        aug[bestRow, c] = t;
                    }
                }
                double div = aug[pivot, pivot];
                for (int c = pivot; c <= size; c++) aug[pivot, c] /= div;
                for (int r = 0; r < size; r++)
                {
                    if (r == pivot) continue;
                    double factor = aug[r, pivot];
                    if (Math.Abs(factor) < 1e-14) continue;
                    for (int c = pivot; c <= size; c++) aug[r, c] -= factor * aug[pivot, c];
                }
            }
            solution = new double[size];
            for (int r = 0; r < size; r++)
            {
                solution[r] = aug[r, size];
                if (!IsFinite(solution[r])) return false;
            }
            return true;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
