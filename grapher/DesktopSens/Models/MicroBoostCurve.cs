using System;
using System.Collections.Generic;

namespace grapher.DesktopSens.Models
{
    /// <summary>
    /// Micro-flick boost curve.
    ///
    /// Purpose: improve micro-flick precision by giving the player very low
    /// sensitivity at the very start of a movement, then ramping up steeply
    /// to the base sensitivity. After the boost range, the normal calibrated
    /// sensitivity (and any fitted acceleration curve) takes over.
    ///
    /// Curve shape (multiplier on top of base sensitivity):
    /// <code>
    ///   multiplier(v) =
    ///     lowStart + (1.0 - lowStart) * (v / boostRange)   for v &lt; boostRange
    ///     1.0                                                for v &gt;= boostRange
    /// </code>
    /// Where:
    ///   - <c>v</c> is the input speed in cm/s
    ///   - <c>lowStart</c> is the starting multiplier (e.g. 0.5 = half sensitivity at v=0)
    ///   - <c>boostRange</c> is the input speed (cm/s) at which the multiplier reaches 1.0
    ///
    /// The multiplier is CAPPED AT 1.0 — it never exceeds the base sensitivity,
    /// which is exactly what the user asked for: "if acceleration gives very fast
    /// [rise], it should be limited so it doesn't go above base acceleration".
    ///
    /// After <c>boostRange</c>, the multiplier is exactly 1.0, so the underlying
    /// calibrated curve (Linear/Natural/Power/Synchronous or just the flat base
    /// sensitivity) is applied unmodified.
    /// </summary>
    public static class MicroBoostCurve
    {
        /// <summary>
        /// Default low-start multiplier. 0.5 means the player starts at 50% of
        /// base sensitivity for the very first sample of any movement.
        /// </summary>
        public const double DefaultLowStart = 0.5;

        /// <summary>
        /// Default boost range in cm/s. Tuned so that a typical micro-flick
        /// (1 cm of physical travel over ~150 ms) falls entirely inside the
        /// boost range — the player gets fine control throughout the micro-flick,
        /// then full sensitivity kicks in for any larger movement.
        /// </summary>
        public const double DefaultBoostRangeCmPerSec = 8.0;

        /// <summary>
        /// Minimum boost range. Below this the boost effectively does nothing
        /// because a single mouse poll already exceeds the range.
        /// </summary>
        public const double MinBoostRangeCmPerSec = 1.0;

        /// <summary>
        /// Maximum boost range. Above this the boost stops being a "micro" boost
        /// and starts affecting normal flicks.
        /// </summary>
        public const double MaxBoostRangeCmPerSec = 30.0;

        /// <summary>
        /// Minimum low-start. 0.1 = 10% of base sensitivity at v=0.
        /// Going lower makes the cursor feel dead for the first sample.
        /// </summary>
        public const double MinLowStart = 0.1;

        /// <summary>
        /// Maximum low-start. 1.0 = no boost (multiplier is always 1.0).
        /// Values above 1.0 are forbidden — that would amplify, not attenuate.
        /// </summary>
        public const double MaxLowStart = 1.0;

        /// <summary>
        /// True when the configured boost is mathematically an identity curve.
        /// The current RawAccel bridge has one analytic acceleration curve slot
        /// per axis and cannot represent the capped MicroBoost ramp (or compose
        /// it with a fitted curve) without a LUT-capable adapter.
        /// </summary>
        public static bool IsIdentity(double lowStart)
        {
            return IsFinite(lowStart) && lowStart >= MaxLowStart;
        }

        /// <summary>
        /// Evaluate the boost multiplier at the given input speed.
        /// Returns a value in [lowStart, 1.0]. At v=0 returns lowStart.
        /// At v>=boostRange returns exactly 1.0. Never exceeds 1.0.
        /// </summary>
        public static double Evaluate(double lowStart, double boostRange, double inputSpeedCmPerSec)
        {
            if (!IsFinite(lowStart) || !IsFinite(boostRange) || !IsFinite(inputSpeedCmPerSec))
                return 1.0;
            if (boostRange <= 0.0) return 1.0;
            if (lowStart >= MaxLowStart) return 1.0;
            if (lowStart < MinLowStart) lowStart = MinLowStart;

            if (inputSpeedCmPerSec <= 0.0) return lowStart;
            if (inputSpeedCmPerSec >= boostRange) return 1.0;

            // Linear ramp from lowStart to 1.0 across [0, boostRange].
            double t = inputSpeedCmPerSec / boostRange;
            double multiplier = lowStart + (1.0 - lowStart) * t;
            // Cap at 1.0 to honor the user's "don't exceed base" requirement.
            if (multiplier > 1.0) multiplier = 1.0;
            if (multiplier < lowStart) multiplier = lowStart;
            return multiplier;
        }

        /// <summary>
        /// Compose the boost multiplier with a base sensitivity. The result is
        /// what the driver should actually output for a given input speed.
        /// <code>
        ///   final_sensitivity = base_sensitivity * boost_multiplier(v)
        /// </code>
        /// </summary>
        public static double ApplyOnTopOfBase(double lowStart, double boostRange,
            double baseSensitivity, double inputSpeedCmPerSec)
        {
            if (!IsFinite(baseSensitivity) || baseSensitivity <= 0.0) baseSensitivity = 1.0;
            return baseSensitivity * Evaluate(lowStart, boostRange, inputSpeedCmPerSec);
        }

        /// <summary>
        /// Validate user-provided MicroBoost parameters and clamp them into
        /// legal ranges. Returns the clamped values.
        /// </summary>
        public static (double lowStart, double boostRange) ClampParameters(
            double lowStart, double boostRange)
        {
            if (!IsFinite(lowStart)) lowStart = DefaultLowStart;
            if (!IsFinite(boostRange)) boostRange = DefaultBoostRangeCmPerSec;
            if (lowStart < MinLowStart) lowStart = MinLowStart;
            if (lowStart > MaxLowStart) lowStart = MaxLowStart;
            if (boostRange < MinBoostRangeCmPerSec) boostRange = MinBoostRangeCmPerSec;
            if (boostRange > MaxBoostRangeCmPerSec) boostRange = MaxBoostRangeCmPerSec;
            return (lowStart, boostRange);
        }

        /// <summary>
        /// Sample the boost curve at N evenly-spaced speeds and return them as
        /// (speed, multiplier) pairs. Used by the UI to draw a preview chart.
        /// </summary>
        public static List<(double speed, double multiplier)> SampleCurve(
            double lowStart, double boostRange, int sampleCount = 64,
            double maxSpeed = 20.0)
        {
            var result = new List<(double, double)>(sampleCount);
            if (sampleCount < 2) sampleCount = 2;
            for (int i = 0; i < sampleCount; i++)
            {
                double v = maxSpeed * i / (sampleCount - 1);
                result.Add((v, Evaluate(lowStart, boostRange, v)));
            }
            return result;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
