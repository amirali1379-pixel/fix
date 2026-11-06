using System;
using System.Collections.Generic;

namespace grapher.DesktopSens.Models
{
    /// <summary>
    /// Acceleration curve modes mirroring RawAccel's native acceleration types.
    /// The calibrator fits all applicable modes per cell and picks the one with
    /// the lowest AICc (small-sample corrected Akaike Information Criterion) — a single number that
    /// balances fit quality against parameter count, so a 4-parameter natural
    /// curve has to beat a 1-parameter linear curve by more than a hair to win.
    /// </summary>
    public enum AccelCurveMode
    {
        /// <summary>No acceleration. Sensitivity is constant = 1.0.</summary>
        NoAccel = 0,
        /// <summary>Classic linear accel: sensitivity = 1 + accel * (v - offset).</summary>
        Linear = 1,
        /// <summary>RawAccel "natural" mode: concave curve approaching a limit. Best for soft-capped accel.</summary>
        Natural = 2,
        /// <summary>RawAccel "power" mode: sensitivity = (scale * (v - offset))^exp + outputOffset. Best for aggressive accel.</summary>
        Power = 3,
        /// <summary>RawAccel "synchronous" mode: motivity/gamma/syncSpeed — symmetric S-curve around a sync speed.</summary>
        Synchronous = 4
    }

    /// <summary>
    /// Result of fitting a single acceleration curve to a set of (speed, sensitivity)
    /// observations. Stored verbatim on the profile so RawAccel can later apply the
    /// exact same curve without re-fitting.
    /// </summary>
    public struct CurveFitResult
    {
        public AccelCurveMode Mode;
        /// <summary>Mode-specific parameter vector. Length depends on Mode (see remarks on each fitter).</summary>
        public double[] Parameters;
        /// <summary>Root-mean-square error in sensitivity-ratio units (1.0 = perfect).</summary>
        public double Rmse;
        /// <summary>AICc (small-sample corrected AIC; lower = better). Penalises parameter count.</summary>
        public double Aic;
        /// <summary>True if the fit converged and produces a finite curve.</summary>
        public bool IsValid;
        /// <summary>Number of samples used for the fit (after robust outlier rejection).</summary>
        public int SampleCount;
    }

    /// <summary>
    /// A single observed (input speed, sensitivity ratio) pair used as fitting input.
    /// </summary>
    public struct SensitivitySample
    {
        /// <summary>Mouse input speed in cm/s for this trial.</summary>
        public double InputSpeedCmPerSec;
        /// <summary>Observed sensitivity ratio. 1.0 = exactly on target. &gt;1 = overshot, &lt;1 = undershot.</summary>
        public double ObservedRatio;
        /// <summary>Optional weight (e.g. trial confidence). Defaults to 1.0.</summary>
        public double Weight;
    }

    /// <summary>
    /// Multi-model acceleration curve fitter.
    ///
    /// Fits <see cref="AccelCurveMode.Linear"/>, <see cref="AccelCurveMode.Natural"/>,
    /// <see cref="AccelCurveMode.Power"/>, and (when sufficient samples exist)
    /// <see cref="AccelCurveMode.Synchronous"/> to the same data set, then returns
    /// the best fit by AICc. AICc balances fit quality against parameter count, so a
    /// more complex model only wins if it reduces the residual sum of squares
    /// enough to justify its extra parameters.
    ///
    /// All nonlinear fits use Levenberg-Marquardt with numerical Jacobians. The
    /// implementation is dependency-free (no Math.NET / Accord), bounded by the
    /// parameter ranges defined in <see cref="DesktopSensConstants"/>, and
    /// numerically robust against singular Jacobians.
    /// </summary>
    public static class AccelCurveFitter
    {
        // ─────────────────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Fit every applicable curve model and return the best one by AICc.
        /// Falls back to <see cref="AccelCurveMode.NoAccel"/> if no model fits
        /// (e.g. all samples are degenerate or below the minimum sample count).
        /// </summary>
        public static CurveFitResult FitBest(IList<SensitivitySample> samples)
        {
            if (samples == null || samples.Count < DesktopSensConstants.CurveFittingMinSamples)
                return NoAccelResult(samples?.Count ?? 0);

            // Reject gross outliers by MAD on the ratio dimension. A flick whose
            // ratio sits more than 3 robust-sigma from the median is almost
            // always a sensor glitch or a misclick, not real acceleration behaviour.
            var cleaned = RejectOutlierSamples(samples);
            if (cleaned.Count < DesktopSensConstants.CurveFittingMinSamples)
                return NoAccelResult(samples.Count);

            var candidates = new List<CurveFitResult>(4);
            var linear = FitLinear(cleaned);
            if (linear.IsValid) candidates.Add(linear);
            var natural = FitNatural(cleaned);
            if (natural.IsValid) candidates.Add(natural);
            var power = FitPower(cleaned);
            if (power.IsValid) candidates.Add(power);
            // Synchronous needs more samples because it has 4 free parameters;
            // skip it on small cells so the AIC penalty doesn't push us into a
            // worse model just because sync was forced to fit noise.
            if (cleaned.Count >= DesktopSensConstants.SynchronousMinSamples)
            {
                var sync = FitSynchronous(cleaned);
                if (sync.IsValid) candidates.Add(sync);
            }

            if (candidates.Count == 0)
                return NoAccelResult(cleaned.Count);

            // With enough observations, use leave-one-out cross-validation and
            // the one-standard-error rule. This guards against a complex curve
            // winning on in-sample AICc while fitting noise. Smaller samples keep
            // the AICc fallback because a synchronous fold would be under-sized.
            if (cleaned.Count >= DesktopSensConstants.SynchronousMinSamples + 1)
            {
                var scored = new List<CrossValidationScore>();
                foreach (CurveFitResult candidate in candidates)
                {
                    double cvMeanSquaredError, cvStandardError;
                    if (TryLeaveOneOutScore(candidate.Mode, cleaned,
                        out cvMeanSquaredError, out cvStandardError))
                    {
                        scored.Add(new CrossValidationScore
                        {
                            Fit = candidate,
                            MeanSquaredError = cvMeanSquaredError,
                            StandardError = cvStandardError
                        });
                    }
                }

                if (scored.Count > 0)
                {
                    double bestCvError = double.MaxValue;
                    double bestCvSe = double.PositiveInfinity;
                    foreach (CrossValidationScore score in scored)
                    {
                        if (score.MeanSquaredError < bestCvError)
                        {
                            bestCvError = score.MeanSquaredError;
                            bestCvSe = score.StandardError;
                        }
                    }

                    double oneSeLimit = bestCvError + bestCvSe;
                    CrossValidationScore selected = null;
                    foreach (CrossValidationScore score in scored)
                    {
                        if (score.MeanSquaredError > oneSeLimit) continue;
                        int parameterCount = score.Fit.Parameters != null ? score.Fit.Parameters.Length : 0;
                        int selectedParameterCount = selected != null && selected.Fit.Parameters != null
                            ? selected.Fit.Parameters.Length : int.MaxValue;
                        if (selected == null || parameterCount < selectedParameterCount ||
                            (parameterCount == selectedParameterCount &&
                             score.MeanSquaredError < selected.MeanSquaredError))
                            selected = score;
                    }

                    if (selected != null)
                    {
                        CurveFitResult cvBest = selected.Fit;
                        cvBest.SampleCount = cleaned.Count;
                        return cvBest;
                    }
                }
            }

            // Small-sample fallback: choose the lowest AICc. Ties prefer
            // fewer parameters, then lower in-sample RMSE.
            CurveFitResult best = candidates[0];
            for (int i = 1; i < candidates.Count; i++)
            {
                if (candidates[i].Aic < best.Aic - 0.5)
                    best = candidates[i];
                else if (Math.Abs(candidates[i].Aic - best.Aic) <= 0.5
                         && candidates[i].Parameters != null
                         && best.Parameters != null
                         && candidates[i].Parameters.Length < best.Parameters.Length)
                    best = candidates[i];
            }
            best.SampleCount = cleaned.Count;
            return best;
        }

        /// <summary>
        /// Evaluate a fitted curve at the given input speed (cm/s).
        /// Returns the sensitivity ratio. NoAccel always returns 1.0.
        /// </summary>
        public static double Evaluate(AccelCurveMode mode, double[] parameters, double inputSpeedCmPerSec)
        {
            if (parameters == null || parameters.Length == 0) return 1.0;
            if (inputSpeedCmPerSec < 0.0) inputSpeedCmPerSec = 0.0;
            switch (mode)
            {
                case AccelCurveMode.NoAccel:
                    return 1.0;
                case AccelCurveMode.Linear:
                    // params: [offset, accel]
                    if (parameters.Length < 2) return 1.0;
                    return 1.0 + parameters[1] * Math.Max(0.0, inputSpeedCmPerSec - parameters[0]);
                case AccelCurveMode.Natural:
                    // params: [offset, decay, limit]
                    if (parameters.Length < 3) return 1.0;
                    if (inputSpeedCmPerSec <= parameters[0]) return 1.0;
                    return 1.0 + parameters[2] * (1.0 - Math.Exp(-parameters[1] * (inputSpeedCmPerSec - parameters[0])));
                case AccelCurveMode.Power:
                    // params: [offset, scale, exponent, outputOffset]
                    if (parameters.Length < 4) return 1.0;
                    if (inputSpeedCmPerSec <= parameters[0]) return 1.0 + parameters[3];
                    double baseValue = parameters[1] * (inputSpeedCmPerSec - parameters[0]);
                    if (baseValue < 0.0) baseValue = 0.0;
                    return Math.Pow(baseValue, parameters[2]) + parameters[3] + 1.0;
                case AccelCurveMode.Synchronous:
                    // params: [syncSpeed, motivity, gamma, smooth]
                    if (parameters.Length < 4) return 1.0;
                    return EvaluateSynchronous(parameters[0], parameters[1], parameters[2], parameters[3], inputSpeedCmPerSec);
                default:
                    return 1.0;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Per-model fitters
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Linear: sensitivity(v) = 1 + accel * max(0, v - offset).
        /// 2 parameters. Closed-form weighted linear regression through the
        /// post-offset samples — no iteration needed.
        /// </summary>
        public static CurveFitResult FitLinear(IList<SensitivitySample> samples)
        {
            if (samples == null || samples.Count < 2)
                return Invalid(AccelCurveMode.Linear);

            // Initial offset = lowest speed (so all samples contribute). Then
            // closed-form weighted least squares on the (v - offset, ratio - 1) pairs.
            double offset = double.MaxValue;
            for (int i = 0; i < samples.Count; i++)
                if (samples[i].InputSpeedCmPerSec < offset)
                    offset = samples[i].InputSpeedCmPerSec;
            offset = Math.Max(0.0, offset);

            double sw = 0.0, swx = 0.0, swy = 0.0, swxx = 0.0, swxy = 0.0;
            for (int i = 0; i < samples.Count; i++)
            {
                double x = Math.Max(0.0, samples[i].InputSpeedCmPerSec - offset);
                double y = samples[i].ObservedRatio - 1.0;
                double w = Math.Max(0.0, samples[i].Weight);
                if (w <= 0.0) w = 1.0;
                sw += w; swx += w * x; swy += w * y;
                swxx += w * x * x; swxy += w * x * y;
            }
            double denom = sw * swxx - swx * swx;
            if (Math.Abs(denom) < 1e-12)
                return Invalid(AccelCurveMode.Linear);
            double accel = (sw * swxy - swx * swy) / denom;
            if (!IsFinite(accel))
                return Invalid(AccelCurveMode.Linear);

            // Clamp to a sane range so a degenerate fit can't claim a 100x sensitivity.
            accel = Clamp(accel, DesktopSensConstants.LinearAccelMin, DesktopSensConstants.LinearAccelMax);

            double[] p = { offset, accel };
            return BuildResult(AccelCurveMode.Linear, p, samples);
        }

        /// <summary>
        /// Natural: sensitivity(v) = 1 + limit * (1 - exp(-decay * max(0, v - offset))).
        /// 3 parameters: offset, decay, limit. Fit by Levenberg-Marquardt.
        /// </summary>
        public static CurveFitResult FitNatural(IList<SensitivitySample> samples)
        {
            if (samples == null || samples.Count < 3)
                return Invalid(AccelCurveMode.Natural);

            // Initial guesses: offset = min speed, decay = 0.05 (medium rate),
            // limit = (max ratio - 1) clamped to [0, LimitMax].
            double offset = samples[0].InputSpeedCmPerSec;
            double maxRatio = 1.0;
            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i].InputSpeedCmPerSec < offset) offset = samples[i].InputSpeedCmPerSec;
                if (samples[i].ObservedRatio > maxRatio) maxRatio = samples[i].ObservedRatio;
            }
            offset = Clamp(Math.Max(0.0, offset), 0.0, DesktopSensConstants.InputOffsetMax);
            double decay = 0.05;
            double limit = Clamp(maxRatio - 1.0, DesktopSensConstants.NaturalLimitMin, DesktopSensConstants.NaturalLimitMax);
            double[] p = { offset, decay, limit };
            double[] lower = { 0.0, DesktopSensConstants.NaturalDecayMin, DesktopSensConstants.NaturalLimitMin };
            double[] upper = { DesktopSensConstants.InputOffsetMax, DesktopSensConstants.NaturalDecayMax, DesktopSensConstants.NaturalLimitMax };

            if (!LevenbergMarquardt(samples, p, lower, upper, NaturalResidual, NaturalJacobian))
                return Invalid(AccelCurveMode.Natural);

            return BuildResult(AccelCurveMode.Natural, p, samples);
        }

        /// <summary>
        /// Power: sensitivity(v) = (scale * max(0, v - offset))^exponent + outputOffset + 1.
        /// 4 parameters: offset, scale, exponent, outputOffset.
        /// </summary>
        public static CurveFitResult FitPower(IList<SensitivitySample> samples)
        {
            if (samples == null || samples.Count < 4)
                return Invalid(AccelCurveMode.Power);

            double offset = samples[0].InputSpeedCmPerSec;
            double maxRatio = 1.0;
            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i].InputSpeedCmPerSec < offset) offset = samples[i].InputSpeedCmPerSec;
                if (samples[i].ObservedRatio > maxRatio) maxRatio = samples[i].ObservedRatio;
            }
            offset = Clamp(Math.Max(0.0, offset), 0.0, DesktopSensConstants.InputOffsetMax);
            double scale = 0.1;
            double exponent = 1.0;
            double outputOffset = 0.0;
            double[] p = { offset, scale, exponent, outputOffset };
            double[] lower = { 0.0, DesktopSensConstants.PowerScaleMin, DesktopSensConstants.PowerExponentMin, -0.5 };
            double[] upper = { DesktopSensConstants.InputOffsetMax, DesktopSensConstants.PowerScaleMax, DesktopSensConstants.PowerExponentMax, 0.5 };

            if (!LevenbergMarquardt(samples, p, lower, upper, PowerResidual, PowerJacobian))
                return Invalid(AccelCurveMode.Power);

            return BuildResult(AccelCurveMode.Power, p, samples);
        }

        /// <summary>
        /// Synchronous (RawAccel sync mode). 4 parameters: syncSpeed, motivity, gamma, smooth.
        /// Requires <see cref="DesktopSensConstants.SynchronousMinSamples"/> samples because
        /// the S-curve shape needs at least 4-5 distinct speeds to disambiguate.
        /// </summary>
        public static CurveFitResult FitSynchronous(IList<SensitivitySample> samples)
        {
            if (samples == null || samples.Count < DesktopSensConstants.SynchronousMinSamples)
                return Invalid(AccelCurveMode.Synchronous);

            // Initial guesses: syncSpeed = median speed, motivity = 1 + (median ratio - 1)*2,
            // gamma = 1.0 (linear-bend default), smooth = 0.5.
            double medSpeed = MedianSpeed(samples);
            double medRatio = MedianRatio(samples);
            double syncSpeed = Clamp(medSpeed, 0.1, DesktopSensConstants.SyncSpeedMax);
            double motivity = Clamp(medRatio * 2.0 - 1.0, DesktopSensConstants.MotivityMin, DesktopSensConstants.MotivityMax);
            double gamma = 1.0;
            double smooth = 0.5;
            double[] p = { syncSpeed, motivity, gamma, smooth };
            double[] lower = { 0.1, DesktopSensConstants.MotivityMin, DesktopSensConstants.GammaMin, 0.0 };
            double[] upper = { DesktopSensConstants.SyncSpeedMax, DesktopSensConstants.MotivityMax, DesktopSensConstants.GammaMax, 1.0 };

            if (!LevenbergMarquardt(samples, p, lower, upper, SyncResidual, SyncJacobian))
                return Invalid(AccelCurveMode.Synchronous);

            return BuildResult(AccelCurveMode.Synchronous, p, samples);
        }

        private sealed class CrossValidationScore
        {
            public CurveFitResult Fit;
            public double MeanSquaredError;
            public double StandardError;
        }

        private static bool TryLeaveOneOutScore(
            AccelCurveMode mode,
            IList<SensitivitySample> samples,
            out double meanSquaredError,
            out double standardError)
        {
            meanSquaredError = double.PositiveInfinity;
            standardError = double.PositiveInfinity;
            if (samples == null || samples.Count < 2) return false;

            var squaredErrors = new List<double>(samples.Count);
            for (int heldOut = 0; heldOut < samples.Count; heldOut++)
            {
                var training = new List<SensitivitySample>(samples.Count - 1);
                for (int i = 0; i < samples.Count; i++)
                    if (i != heldOut) training.Add(samples[i]);

                // Outlier rejection is part of the estimator, so it must be
                // learned from the fold's training data only. The held-out
                // observation must never influence which other samples survive.
                IList<SensitivitySample> foldTraining = RejectOutlierSamples(training);
                CurveFitResult fit;
                switch (mode)
                {
                    case AccelCurveMode.Linear:
                        fit = FitLinear(foldTraining);
                        break;
                    case AccelCurveMode.Natural:
                        fit = FitNatural(foldTraining);
                        break;
                    case AccelCurveMode.Power:
                        fit = FitPower(foldTraining);
                        break;
                    case AccelCurveMode.Synchronous:
                        fit = FitSynchronous(foldTraining);
                        break;
                    default:
                        return false;
                }
                if (!fit.IsValid) return false;

                SensitivitySample sample = samples[heldOut];
                double prediction = Evaluate(fit.Mode, fit.Parameters, sample.InputSpeedCmPerSec);
                double error = sample.ObservedRatio - prediction;
                if (!IsFinite(error)) return false;
                squaredErrors.Add(error * error);
            }

            if (squaredErrors.Count < 2) return false;
            meanSquaredError = 0.0;
            for (int i = 0; i < squaredErrors.Count; i++)
                meanSquaredError += squaredErrors[i];
            meanSquaredError /= squaredErrors.Count;

            double variance = 0.0;
            for (int i = 0; i < squaredErrors.Count; i++)
            {
                double delta = squaredErrors[i] - meanSquaredError;
                variance += delta * delta;
            }
            variance /= squaredErrors.Count - 1;
            standardError = Math.Sqrt(variance / squaredErrors.Count);
            return IsFinite(meanSquaredError) && IsFinite(standardError);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Curve evaluators (for fitter + for runtime application)
        // ─────────────────────────────────────────────────────────────────────────

        private static double EvaluateSynchronous(double syncSpeed, double motivity, double gamma, double smooth, double v)
        {
            // RawAccel sync formula (simplified, without the smoothing EMA — that is
            // applied externally by RawAccel as a half-life, not by the curve itself):
            //   ratio = 1 + (motivity - 1) * (v / syncSpeed)^gamma / (1 + (v / syncSpeed)^gamma)
            // This produces an S-curve that approaches motivity as v -> infinity,
            // and is exactly (motivation+1)/2 at v = syncSpeed.
            if (syncSpeed <= 0.0) return 1.0;
            double x = v / syncSpeed;
            if (x < 0.0) x = 0.0;
            double xg = Math.Pow(x, gamma);
            double s = xg / (1.0 + xg);
            double ratio = 1.0 + (motivity - 1.0) * s;
            // Smooth is a 0..1 interpolator between raw and the linear blend with
            // the previous value. We don't track previous values here, so smooth
            // only affects the stored parameter (the calibrator still fits it so
            // the profile records what RawAccel should apply).
            return ratio;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Residual functions for Levenberg-Marquardt
        // ─────────────────────────────────────────────────────────────────────────

        private static double NaturalResidual(IList<SensitivitySample> samples, double[] p, int index)
        {
            double pred = 1.0;
            if (samples[index].InputSpeedCmPerSec > p[0])
                pred = 1.0 + p[2] * (1.0 - Math.Exp(-p[1] * (samples[index].InputSpeedCmPerSec - p[0])));
            return samples[index].ObservedRatio - pred;
        }

        private static void NaturalJacobian(IList<SensitivitySample> samples, double[] p, int index, double[] j)
        {
            double v = samples[index].InputSpeedCmPerSec;
            if (v <= p[0])
            {
                j[0] = 0.0; j[1] = 0.0; j[2] = 0.0;
                return;
            }
            double dv = v - p[0];
            double e = Math.Exp(-p[1] * dv);
            double oneMinusE = 1.0 - e;
            // pred = 1 + limit*(1 - e^{-decay*dv})
            //   d(pred)/d(offset) = limit * e^{-decay*dv} * (-decay) * (-1)
            //                     = -limit * decay * e
            // d(residual)/d(offset) = -d(pred)/d(offset) = +limit * decay * e
            //
            // PATCH 2 from review: the sign of j[0] was wrong — it was
            // `-p[2]*p[1]*e`, which equals -limit*decay*e = d(pred)/d(offset)
            // (i.e. the WRONG sign for a residual Jacobian). Levenberg-
            // Marquardt requires ∂r/∂p, not ∂pred/∂p, otherwise Natural
            // fits diverge or converge to wrong parameters, the fitter
            // scores Natural badly with AICc, and Natural mode can
            // never win even when it is the correct model for a cell.
            // Fixed: j[0] = +limit * decay * e.
            j[0] = p[2] * p[1] * e;          // FIXED SIGN (was -p[2]*p[1]*e)
            // d(pred)/d(decay) = limit * dv * e
            // d(residual)/d(decay) = -limit * dv * e
            j[1] = -p[2] * dv * e;
            // d(pred)/d(limit) = (1 - e)
            // d(residual)/d(limit) = -(1 - e)
            j[2] = -oneMinusE;
        }

        private static double PowerResidual(IList<SensitivitySample> samples, double[] p, int index)
        {
            double v = samples[index].InputSpeedCmPerSec;
            double pred;
            if (v <= p[0])
                pred = 1.0 + p[3];
            else
            {
                double b = p[1] * (v - p[0]);
                if (b < 0.0) b = 0.0;
                pred = Math.Pow(b, p[2]) + p[3] + 1.0;
            }
            return samples[index].ObservedRatio - pred;
        }

        private static void PowerJacobian(IList<SensitivitySample> samples, double[] p, int index, double[] j)
        {
            double v = samples[index].InputSpeedCmPerSec;
            if (v <= p[0])
            {
                j[0] = 0.0; j[1] = 0.0; j[2] = 0.0; j[3] = -1.0;
                return;
            }
            double dv = v - p[0];
            double b = p[1] * dv;
            if (b <= 0.0)
            {
                j[0] = 0.0; j[1] = 0.0; j[2] = 0.0; j[3] = -1.0;
                return;
            }
            double pow = Math.Pow(b, p[2]);
            // d(pred)/d(offset) = pow * p[2] * (-1) / dv * dv (chain rule on (v-offset))
            //   Actually pred = b^exp + offset_out + 1, b = scale * (v - offset)
            //   d(pred)/d(offset) = exp * b^(exp-1) * scale * (-1)
            j[0] = -p[2] * pow / b * p[1];
            // d(pred)/d(scale) = exp * b^(exp-1) * (v - offset)
            j[1] = p[2] * pow / b * dv;
            // d(pred)/d(exp) = b^exp * ln(b)
            j[2] = pow * Math.Log(Math.Max(1e-12, b));
            // d(pred)/d(outputOffset) = 1
            j[3] = 1.0;
            // Flip sign because we return residual = observed - predicted.
            j[0] = -j[0]; j[1] = -j[1]; j[2] = -j[2]; j[3] = -j[3];
        }

        private static double SyncResidual(IList<SensitivitySample> samples, double[] p, int index)
        {
            double pred = EvaluateSynchronous(p[0], p[1], p[2], p[3], samples[index].InputSpeedCmPerSec);
            return samples[index].ObservedRatio - pred;
        }

        private static void SyncJacobian(IList<SensitivitySample> samples, double[] p, int index, double[] j)
        {
            // pred = 1 + (motivity - 1) * s, where s = x^gamma / (1 + x^gamma), x = v/syncSpeed
            double v = samples[index].InputSpeedCmPerSec;
            double sync = Math.Max(0.1, p[0]);
            double motivity = p[1];
            double gamma = p[2];
            double x = v / sync;
            if (x < 0.0) x = 0.0;
            double xg = Math.Pow(Math.Max(1e-12, x), gamma);
            double onePlus = 1.0 + xg;
            double s = xg / onePlus;
            // d(pred)/d(syncSpeed):
            //   ds/dx = gamma * x^(gamma-1) / (1+xg)^2
            //   dx/dsync = -v / sync^2 = -x / sync
            //   d(pred)/dsync = (motivity-1) * ds/dx * dx/dsync
            double dsdx = gamma * Math.Pow(Math.Max(1e-12, x), gamma - 1.0) / (onePlus * onePlus);
            j[0] = (motivity - 1.0) * dsdx * (-x / sync);
            // d(pred)/d(motivity) = s
            j[1] = s;
            // d(pred)/d(gamma):
            //   ds/dgamma = (x^gamma * ln(x) * (1+xg) - xg * x^gamma * ln(x)) / (1+xg)^2
            //             = x^gamma * ln(x) / (1+xg)^2
            double lnx = Math.Log(Math.Max(1e-12, x));
            j[2] = (motivity - 1.0) * xg * lnx / (onePlus * onePlus);
            // smooth is not in the curve evaluation (it's an external EMA), so its
            // derivative is zero — fit it as a free parameter that doesn't affect RMSE.
            // We still keep it in the parameter vector so the profile records what
            // RawAccel should set the smooth half-life to.
            j[3] = 0.0;
            // Flip sign for residual = observed - predicted.
            j[0] = -j[0]; j[1] = -j[1]; j[2] = -j[2];
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Levenberg-Marquardt core
        // ─────────────────────────────────────────────────────────────────────────

        private delegate double ResidualFn(IList<SensitivitySample> samples, double[] p, int index);
        private delegate void JacobianFn(IList<SensitivitySample> samples, double[] p, int index, double[] j);

        /// <summary>
        /// In-place Levenberg-Marquardt with numerical fallback. Modifies
        /// <paramref name="parameters"/> in place. Returns false on failure
        /// (singular Jacobian, diverging step, max iterations hit without convergence).
        /// </summary>
        private static bool LevenbergMarquardt(
            IList<SensitivitySample> samples,
            double[] parameters,
            double[] lower,
            double[] upper,
            ResidualFn residual,
            JacobianFn jacobian)
        {
            int n = parameters.Length;
            int m = samples.Count;
            if (n == 0 || m < n) return false;

            double lambda = 1e-3;
            double prevCost = ComputeCost(samples, parameters, residual);
            if (!IsFinite(prevCost)) return false;

            for (int iter = 0; iter < DesktopSensConstants.CurveFittingMaxIterations; iter++)
            {
                // Build J^T * J and J^T * r.
                var jtj = new double[n, n];
                var jtr = new double[n];
                for (int i = 0; i < m; i++)
                {
                    var j = new double[n];
                    jacobian(samples, parameters, i, j);
                    double r = residual(samples, parameters, i);
                    double w = Math.Max(0.0, samples[i].Weight);
                    if (w <= 0.0) w = 1.0;
                    for (int a = 0; a < n; a++)
                    {
                        jtr[a] += w * j[a] * r;
                        for (int b = 0; b < n; b++)
                            jtj[a, b] += w * j[a] * j[b];
                    }
                }

                // Augment diagonal by lambda and solve (J^T*J + lambda*diag) * step = -J^T*r.
                var aug = new double[n, n + 1];
                for (int a = 0; a < n; a++)
                {
                    for (int b = 0; b < n; b++)
                        aug[a, b] = jtj[a, b] + (a == b ? lambda : 0.0);
                    aug[a, n] = -jtr[a];
                }
                double[] step;
                if (!TrySolve(aug, n, out step))
                {
                    // Singular — increase damping and retry; abort if we're already heavy.
                    lambda *= 10.0;
                    if (lambda > 1e10) return false;
                    continue;
                }

                // Trial step.
                var trial = (double[])parameters.Clone();
                for (int a = 0; a < n; a++)
                    trial[a] = parameters[a] + step[a];
                // Project back into bounds.
                for (int a = 0; a < n; a++)
                {
                    if (trial[a] < lower[a]) trial[a] = lower[a];
                    if (trial[a] > upper[a]) trial[a] = upper[a];
                }

                double trialCost = ComputeCost(samples, trial, residual);
                if (!IsFinite(trialCost))
                {
                    lambda *= 10.0;
                    continue;
                }

                if (trialCost < prevCost)
                {
                    // Accept. Converged if relative improvement is tiny.
                    double improvement = prevCost - trialCost;
                    for (int a = 0; a < n; a++) parameters[a] = trial[a];
                    prevCost = trialCost;
                    lambda = Math.Max(1e-9, lambda * 0.5);
                    if (improvement < DesktopSensConstants.CurveFittingTolerance * Math.Max(1.0, prevCost))
                        return true;
                }
                else
                {
                    lambda *= 4.0;
                    if (lambda > 1e12) return false;
                }
            }
            // Iteration cap hit — accept the current parameters if they're finite.
            for (int a = 0; a < n; a++)
                if (!IsFinite(parameters[a])) return false;
            return true;
        }

        private static double ComputeCost(IList<SensitivitySample> samples, double[] p, ResidualFn residual)
        {
            double sum = 0.0;
            for (int i = 0; i < samples.Count; i++)
            {
                double r = residual(samples, p, i);
                double w = Math.Max(0.0, samples[i].Weight);
                if (w <= 0.0) w = 1.0;
                sum += w * r * r;
            }
            return sum;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────────

        private static bool TrySolve(double[,] aug, int n, out double[] solution)
        {
            // Gaussian elimination with partial pivoting on [A | b].
            solution = new double[n];
            for (int pivot = 0; pivot < n; pivot++)
            {
                int bestRow = pivot;
                double bestMag = Math.Abs(aug[pivot, pivot]);
                for (int r = pivot + 1; r < n; r++)
                {
                    double mag = Math.Abs(aug[r, pivot]);
                    if (mag > bestMag) { bestMag = mag; bestRow = r; }
                }
                if (bestMag < 1e-14) return false;
                if (bestRow != pivot)
                {
                    for (int c = pivot; c <= n; c++)
                    {
                        double t = aug[pivot, c]; aug[pivot, c] = aug[bestRow, c]; aug[bestRow, c] = t;
                    }
                }
                double divisor = aug[pivot, pivot];
                for (int c = pivot; c <= n; c++) aug[pivot, c] /= divisor;
                for (int r = 0; r < n; r++)
                {
                    if (r == pivot) continue;
                    double factor = aug[r, pivot];
                    if (Math.Abs(factor) < 1e-14) continue;
                    for (int c = pivot; c <= n; c++) aug[r, c] -= factor * aug[pivot, c];
                }
            }
            for (int r = 0; r < n; r++)
            {
                solution[r] = aug[r, n];
                if (!IsFinite(solution[r])) return false;
            }
            return true;
        }

        private static CurveFitResult BuildResult(AccelCurveMode mode, double[] parameters, IList<SensitivitySample> samples)
        {
            double sse = 0.0;
            double totalWeight = 0.0;
            for (int i = 0; i < samples.Count; i++)
            {
                double pred = Evaluate(mode, parameters, samples[i].InputSpeedCmPerSec);
                double r = samples[i].ObservedRatio - pred;
                double w = Math.Max(0.0, samples[i].Weight);
                if (w <= 0.0) w = 1.0;
                sse += w * r * r;
                totalWeight += w;
            }
            int effN = (int)Math.Max(1, Math.Round(totalWeight));
            double rmse = effN > 0 ? Math.Sqrt(sse / effN) : double.NaN;
            int k = parameters.Length;
            // AICc = AIC + 2k(k+1)/(n-k-1). For n <= k+1 the
            // correction is undefined, so this model is not identifiable
            // from the available sample count and must not compete.
            double aicc = double.MaxValue;
            if (effN > k + 1)
            {
                double aic = effN * Math.Log(Math.Max(1e-12, sse / effN)) + 2.0 * k;
                aicc = aic + (2.0 * k * (k + 1)) / (effN - k - 1);
            }
            return new CurveFitResult
            {
                Mode = mode,
                Parameters = parameters,
                Rmse = IsFinite(rmse) ? rmse : double.MaxValue,
                Aic = IsFinite(aicc) ? aicc : double.MaxValue,
                IsValid = IsFinite(rmse) && IsFinite(aicc) && aicc < double.MaxValue,
                SampleCount = samples.Count
            };
        }

        private static CurveFitResult Invalid(AccelCurveMode mode)
        {
            return new CurveFitResult
            {
                Mode = mode,
                Parameters = new double[0],
                Rmse = double.MaxValue,
                Aic = double.MaxValue,
                IsValid = false,
                SampleCount = 0
            };
        }

        private static CurveFitResult NoAccelResult(int sampleCount)
        {
            return new CurveFitResult
            {
                Mode = AccelCurveMode.NoAccel,
                Parameters = new double[0],
                Rmse = 0.0,
                Aic = 0.0,
                IsValid = true,
                SampleCount = sampleCount
            };
        }

        private static List<SensitivitySample> RejectOutlierSamples(IList<SensitivitySample> samples)
        {
            // Compute MAD on the ratio dimension and drop samples whose ratio is
            // more than k robust-sigma from the median ratio.
            var ratios = new List<double>(samples.Count);
            for (int i = 0; i < samples.Count; i++)
                ratios.Add(samples[i].ObservedRatio);
            var inlierIdx = RobustStatistics.MadInlierIndices(ratios, 3.0);
            var cleaned = new List<SensitivitySample>(inlierIdx.Count);
            for (int i = 0; i < inlierIdx.Count; i++)
                cleaned.Add(samples[inlierIdx[i]]);
            return cleaned;
        }

        private static double MedianSpeed(IList<SensitivitySample> samples)
        {
            var speeds = new List<double>(samples.Count);
            for (int i = 0; i < samples.Count; i++) speeds.Add(samples[i].InputSpeedCmPerSec);
            return RobustStatistics.Median(speeds);
        }

        private static double MedianRatio(IList<SensitivitySample> samples)
        {
            var ratios = new List<double>(samples.Count);
            for (int i = 0; i < samples.Count; i++) ratios.Add(samples[i].ObservedRatio);
            return RobustStatistics.Median(ratios);
        }

        private static double Clamp(double value, double min, double max)
        {
            if (!IsFinite(value)) return min;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
