using System;
using System.Collections.Generic;

namespace grapher.DesktopSens.Models
{
    /// <summary>
    /// Outlier-resistant statistical estimators used by the calibrators.
    ///
    /// Mouse flick data is noisy: a single misfire, micro-stutter, or sensor glitch
    /// can produce a sample whose value is 5-10x the true median. The previous
    /// calibrators used plain <c>Average()</c>, which let one bad flick pull the
    /// fitted factor by 10-20% on cells with only a handful of samples.
    ///
    /// The estimators here replace <c>Average()</c> with robust alternatives:
    ///   - <see cref="Median"/>: 50th percentile, breaks down only when &gt;50% of
    ///     samples are outliers (essentially never for &gt;5 samples).
    ///   - <see cref="Mad"/> (Median Absolute Deviation): robust scale estimator,
    ///     0.6745 * MAD estimates the standard deviation under Gaussian noise.
    ///   - <see cref="TrimmedMean"/>: drops the smallest and largest X% before
    ///     averaging — keeps the efficiency of the mean for clean data while
    ///     rejecting a configurable fraction of outliers.
    ///   - <see cref="HuberMean"/>: iterative M-estimator that behaves like the
    ///     mean for small residuals and like the median for large ones. Best
    ///     statistical efficiency under mixed clean/contaminated data.
    /// </summary>
    public static class RobustStatistics
    {
        private const double HuberDeltaDefault = 1.345;   // 95% Gaussian efficiency
        private const int HuberMaxIterations = 30;
        private const double HuberTolerance = 1e-6;

        /// <summary>
        /// Estimates the multiplicative observed/target gain from paired
        /// target distances and signed errors. Pairing is preserved: invalid
        /// observations are skipped as a pair, never independently.
        /// </summary>
        public static bool TryLogGain(
            IList<double> targetCm,
            IList<double> signedErrorCm,
            out double logGain,
            out double standardError,
            out int sampleCount)
        {
            logGain = 0.0;
            standardError = double.PositiveInfinity;
            sampleCount = 0;
            if (targetCm == null || signedErrorCm == null ||
                targetCm.Count != signedErrorCm.Count)
                return false;

            var logs = new List<double>(targetCm.Count);
            for (int i = 0; i < targetCm.Count; i++)
            {
                double target = targetCm[i];
                double error = signedErrorCm[i];
                if (!IsFinite(target) || !IsFinite(error) || target <= 0.05)
                    continue;

                double observed = target + error;
                if (!IsFinite(observed) || observed <= 0.05)
                    continue;

                double value = Math.Log(observed / target);
                if (IsFinite(value)) logs.Add(value);
            }

            sampleCount = logs.Count;
            if (sampleCount < 3) return false;

            logGain = HuberMean(logs);
            double sigma = Math.Max(RobustSigma(logs), 0.003);
            standardError = 1.05 * sigma / Math.Sqrt(sampleCount);
            return IsFinite(logGain) && IsFinite(standardError);
        }

        /// <summary>
        /// Returns a conservative 95% confidence half-width for a log-gain
        /// estimate. False means there are not enough valid paired samples.
        /// </summary>
        public static bool TryLogGainConfidence(
            IList<double> targetCm,
            IList<double> signedErrorCm,
            out double logGain,
            out double standardError,
            out double halfWidth95,
            out int sampleCount)
        {
            halfWidth95 = double.PositiveInfinity;
            bool valid = TryLogGain(
                targetCm, signedErrorCm,
                out logGain, out standardError, out sampleCount);
            if (!valid) return false;
            halfWidth95 = 1.96 * standardError;
            return IsFinite(halfWidth95);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>
        /// Median of a sample set. Returns 0 for empty input.
        /// </summary>
        public static double Median(IList<double> values)
        {
            if (values == null || values.Count == 0) return 0.0;
            var sorted = new double[values.Count];
            for (int i = 0; i < values.Count; i++) sorted[i] = values[i];
            Array.Sort(sorted);
            int mid = sorted.Length / 2;
            return sorted.Length % 2 == 0
                ? 0.5 * (sorted[mid - 1] + sorted[mid])
                : sorted[mid];
        }

        /// <summary>
        /// Median Absolute Deviation: median(|x_i - median|).
        /// Multiply by 1.4826 to get a robust estimate of the standard deviation.
        /// </summary>
        public static double Mad(IList<double> values)
        {
            if (values == null || values.Count == 0) return 0.0;
            double med = Median(values);
            var deviations = new double[values.Count];
            for (int i = 0; i < values.Count; i++)
                deviations[i] = Math.Abs(values[i] - med);
            return Median(deviations);
        }

        /// <summary>
        /// Robust standard deviation estimate = 1.4826 * MAD.
        /// Equals the ordinary standard deviation for clean Gaussian data.
        /// </summary>
        public static double RobustSigma(IList<double> values)
        {
            return 1.4826 * Mad(values);
        }

        /// <summary>
        /// Trimmed mean: drops the smallest and largest <paramref name="trimFraction"/>
        /// of samples (e.g. 0.2 drops 20% from each tail = 40% total) before averaging.
        /// <paramref name="trimFraction"/> must be in [0, 0.5).
        /// </summary>
        public static double TrimmedMean(IList<double> values, double trimFraction)
        {
            if (values == null || values.Count == 0) return 0.0;
            if (trimFraction < 0.0) trimFraction = 0.0;
            if (trimFraction >= 0.5) trimFraction = 0.49;

            var sorted = new double[values.Count];
            for (int i = 0; i < values.Count; i++) sorted[i] = values[i];
            Array.Sort(sorted);

            int drop = (int)Math.Floor(sorted.Length * trimFraction);
            if (drop * 2 >= sorted.Length) drop = (sorted.Length - 1) / 2;

            double sum = 0.0;
            int count = 0;
            for (int i = drop; i < sorted.Length - drop; i++)
            {
                sum += sorted[i];
                count++;
            }
            return count > 0 ? sum / count : 0.0;
        }

        /// <summary>
        /// Huber M-estimator mean. Iteratively reweighted mean that uses quadratic
        /// loss for residuals within <c>delta * robustSigma</c> and linear loss
        /// outside — the standard trade-off between efficiency (mean) and
        /// resistance (median). Default <c>delta = 1.345</c> gives 95% Gaussian
        /// efficiency while bounding the influence of any single outlier.
        /// </summary>
        public static double HuberMean(IList<double> values, double delta = HuberDeltaDefault)
        {
            if (values == null || values.Count == 0) return 0.0;
            if (delta <= 0.0) delta = HuberDeltaDefault;

            double estimate = Median(values);
            double scale = RobustSigma(values);
            if (scale < 1e-9) scale = 1e-9;

            for (int iter = 0; iter < HuberMaxIterations; iter++)
            {
                double previous = estimate;
                double sum = 0.0;
                double weightSum = 0.0;
                for (int i = 0; i < values.Count; i++)
                {
                    double r = (values[i] - estimate) / scale;
                    double w = Math.Abs(r) <= delta
                        ? 1.0
                        : delta / Math.Max(1e-12, Math.Abs(r));
                    sum += w * values[i];
                    weightSum += w;
                }
                if (weightSum < 1e-12) break;
                estimate = sum / weightSum;
                if (Math.Abs(estimate - previous) < HuberTolerance * Math.Max(1.0, Math.Abs(previous)))
                    break;
            }
            return estimate;
        }

        /// <summary>
        /// Huber-weighted circular mean for angular data (degrees). Uses the same
        /// robust M-estimator approach but operates on sin/cos accumulators so
        /// wraparound at ±180° does not bias the result.
        /// </summary>
        public static double HuberCircularMean(IList<double> degrees, double delta = HuberDeltaDefault)
        {
            if (degrees == null || degrees.Count == 0) return 0.0;
            if (delta <= 0.0) delta = HuberDeltaDefault;

            // Initial estimate: circular median (atan2 of summed sin/cos).
            double sinSum = 0.0, cosSum = 0.0;
            for (int i = 0; i < degrees.Count; i++)
            {
                double rad = degrees[i] * Math.PI / 180.0;
                sinSum += Math.Sin(rad);
                cosSum += Math.Cos(rad);
            }
            double estimateRad = Math.Atan2(sinSum, cosSum);
            double estimateDeg = estimateRad * 180.0 / Math.PI;

            // Robust scale on circular residuals (signed shortest angle).
            var residuals = new double[degrees.Count];
            for (int i = 0; i < degrees.Count; i++)
                residuals[i] = SignedAngleDiff(degrees[i], estimateDeg);
            double scale = 1.4826 * Mad(ToMutableList(residuals));
            if (scale < 1e-9) scale = 1e-9;

            for (int iter = 0; iter < HuberMaxIterations; iter++)
            {
                double prevDeg = estimateDeg;
                double sSin = 0.0, sCos = 0.0, wSum = 0.0;
                for (int i = 0; i < degrees.Count; i++)
                {
                    double r = SignedAngleDiff(degrees[i], estimateDeg) / scale;
                    double w = Math.Abs(r) <= delta
                        ? 1.0
                        : delta / Math.Max(1e-12, Math.Abs(r));
                    double rad = degrees[i] * Math.PI / 180.0;
                    sSin += w * Math.Sin(rad);
                    sCos += w * Math.Cos(rad);
                    wSum += w;
                }
                if (wSum < 1e-12) break;
                estimateRad = Math.Atan2(sSin, sCos);
                estimateDeg = estimateRad * 180.0 / Math.PI;
                if (Math.Abs(SignedAngleDiff(estimateDeg, prevDeg)) < HuberTolerance)
                    break;
            }
            return estimateDeg;
        }

        /// <summary>
        /// Outlier rejection by MAD threshold. Returns indices of inliers only.
        /// A sample is an inlier iff |x - median| &lt;= <paramref name="k"/> * robustSigma.
        /// Default k = 3.0 keeps ~99.7% of Gaussian data.
        /// </summary>
        public static List<int> MadInlierIndices(IList<double> values, double k = 3.0)
        {
            var result = new List<int>();
            if (values == null || values.Count == 0) return result;

            double med = Median(values);
            double sigma = RobustSigma(values);
            if (sigma < 1e-12)
            {
                // All values identical — keep everything.
                for (int i = 0; i < values.Count; i++) result.Add(i);
                return result;
            }
            double cutoff = k * sigma;
            for (int i = 0; i < values.Count; i++)
                if (Math.Abs(values[i] - med) <= cutoff)
                    result.Add(i);
            return result;
        }

        /// <summary>
        /// Robust confidence: combines sample count with 1 - relative_residual,
        /// so a cell with 20 samples scattered randomly scores lower than a cell
        /// with 5 samples tightly clustered around the fitted value.
        /// </summary>
        public static double RobustConfidence(int sampleCount, double rmse, double valueScale)
        {
            double countPart = Math.Min(1.0, sampleCount / 10.0);
            if (valueScale < 1e-9) return 0.5 * countPart;
            double relError = rmse / valueScale;
            double qualityPart = Math.Max(0.0, 1.0 - relError);
            return 0.5 * countPart + 0.5 * qualityPart;
        }

        private static double SignedAngleDiff(double a, double b)
        {
            double diff = (a - b) % 360.0;
            if (diff > 180.0) diff -= 360.0;
            if (diff < -180.0) diff += 360.0;
            return diff;
        }

        private static List<double> ToMutableList(double[] values)
        {
            var list = new List<double>(values.Length);
            for (int i = 0; i < values.Length; i++) list.Add(values[i]);
            return list;
        }
    }
}
