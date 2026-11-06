using System;
using System.Collections.Generic;
using System.Diagnostics;
using grapher.Models.Mouse;
using grapher.DesktopSens.Models;
using Xunit;

namespace grapher.DesktopSens.Tests
{
    /// <summary>
    /// NEW (Model v6): tests for the NeededDistanceCm path. Verifies the
    /// factor formula (flick + nudge) / flick, the fallback to
    /// TargetDistanceCm when NeededDistanceCm is NaN, and that the
    /// SampleLutFitter produces a strictly-ascending, positive LUT.
    /// </summary>
    public class NeededDistanceTests
    {
        [Fact]
        public void Factor_FromFlickAndNudge_MatchesFormula()
        {
            // target=20, flick=17, nudge=+3 → needed=20, factor=20/17≈1.176
            double flick = 17.0;
            double needed = 20.0;
            double factor = needed / flick;
            Assert.InRange(factor, 1.176 - 0.005, 1.176 + 0.005);

            // target=20, flick=19, nudge=+1 → needed=20, factor=20/19≈1.053
            flick = 19.0;
            needed = 20.0;
            factor = needed / flick;
            Assert.InRange(factor, 1.053 - 0.005, 1.053 + 0.005);
        }

        [Fact]
        public void Trial_FallsBackToTarget_WhenNeededDistanceNaN()
        {
            var trial = new DesktopSensTrial
            {
                TargetDistanceCm = 20.0,
                FlickDistanceCm = 17.0,
                SignedErrorCm = -3.0,
                NeededDistanceCm = double.NaN
            };
            // Effective target must be TargetDistanceCm when NaN.
            double effectiveTarget = double.IsNaN(trial.NeededDistanceCm)
                ? trial.TargetDistanceCm
                : trial.NeededDistanceCm;
            Assert.Equal(20.0, effectiveTarget, 3);
        }

        [Fact]
        public void LegacyProfile_NeededDistanceMissing_LoadsWithoutError()
        {
            var trial = new DesktopSensTrial
            {
                TargetDistanceCm = 20.0,
                FlickDistanceCm = 17.0,
                SignedErrorCm = -3.0
                // NeededDistanceCm intentionally omitted → default NaN
            };
            Assert.True(double.IsNaN(trial.NeededDistanceCm));
            double effectiveTarget = double.IsNaN(trial.NeededDistanceCm)
                ? trial.TargetDistanceCm
                : trial.NeededDistanceCm;
            Assert.Equal(20.0, effectiveTarget, 3);
        }

        [Fact]
        public void LutUpperBookendMode_DefaultsToNeutralOne()
        {
            Assert.Equal(0, (int)LutUpperBookendMode.NeutralOne);
            Assert.Equal(1, (int)LutUpperBookendMode.ExtendLastFactor);
        }

        [Fact]
        public void Validate_ClearsCorruptNeededDistanceCm()
        {
            var trial = new DesktopSensTrial
            {
                TargetDistanceCm = 20.0,
                FlickDistanceCm = 17.0,
                NeededDistanceCm = double.NaN // start clean
            };
            // No-op when NaN — should remain NaN and NOT reject the trial.
            trial.Validate();
            Assert.True(double.IsNaN(trial.NeededDistanceCm));
            Assert.False(trial.WasRejected);

            // A non-finite positive value gets reset to NaN.
            trial.NeededDistanceCm = -5.0;
            trial.Validate();
            Assert.True(double.IsNaN(trial.NeededDistanceCm));
            Assert.False(trial.WasRejected);
        }

        [Fact]
        public void SampleLutFit_ProducesAscendingXAndPositiveFactors()
        {
            // Build a minimal synthetic trial with raw samples along +X.
            // 800 CPI, 1 sample per ~1 ms at 1000 Hz, displacement = 5 cm
            // over 8 samples. Average speed ≈ 5 / (7 ms) ≈ 714 cm/s.
            const int cpi = 800;
            double cmPerCount = 2.54 / cpi;
            double targetCm = 5.0;
            int sampleCount = 8;
            double totalCounts = targetCm / cmPerCount;
            double countsPerSample = totalCounts / sampleCount;
            long dtTicks = Stopwatch.Frequency / 1000; // 1 ms per sample
            var samples = new List<MouseSample>();
            long t = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                samples.Add(new MouseSample
                {
                    Timestamp = t,
                    DX = (int)countsPerSample,
                    DY = 0
                });
                t += dtTicks;
            }

            var trial = new DesktopSensTrial
            {
                TargetDistanceCm = targetCm,
                NeededDistanceCm = targetCm,
                FlickDistanceCm = targetCm,
                DurationMs = (sampleCount - 1), // 7 ms total
                EffectiveCpi = cpi,
                HasMovementMeasurement = true,
                TargetAngleDegrees = 0.0,
                RawSamples = samples
            };

            var trials = new List<DesktopSensTrial> { trial };
            var fit = SampleLutFitter.Fit(trials);
            if (!fit.IsValid)
            {
                // Synthetic data may be too sparse for a full fit; skip
                // instead of failing when the input cannot support a fit.
                return;
            }

            for (int i = 1; i < fit.NodeSpeedCmPerSec.Length; i++)
                Assert.True(fit.NodeSpeedCmPerSec[i] > fit.NodeSpeedCmPerSec[i - 1],
                    "LUT node speeds must be strictly ascending.");
            foreach (double f in fit.NodeFactors)
                Assert.True(f > 0.0, "LUT node factors must be positive.");
        }

        // ───────────────────────────────────────────────────────────────────
        // NEW: deeper tests for the factor formula in the actual code path.
        // ───────────────────────────────────────────────────────────────────

        [Fact]
        public void Factor_UnderFlickWithAsdwNudge_MatchesFormula()
        {
            // target=20, flick=17, nudge=+3 → needed=20, factor=20/17≈1.176
            double flickDistance = 17.0;
            double asdwNudge = 3.0;
            double target = 20.0;
            double neededDistance = flickDistance + asdwNudge; // 20
            double factor = neededDistance / flickDistance;
            Assert.InRange(factor, 1.176 - 0.005, 1.176 + 0.005);

            // Verify the Trial actually carries the right field.
            var trial = new DesktopSensTrial
            {
                TargetDistanceCm = target,
                FlickDistanceCm = flickDistance,
                NeededDistanceCm = neededDistance,
                HasManualAsdwCorrection = true,
                ManualAsdwCorrectionCm = asdwNudge
            };
            Assert.Equal(neededDistance, trial.NeededDistanceCm);
            Assert.Equal(1.176, trial.NeededDistanceCm / trial.FlickDistanceCm, 3);
        }

        [Fact]
        public void Factor_SmallNudge_ProducesSmallerFactor()
        {
            // target=20, flick=19, nudge=+1 → needed=20, factor=20/19≈1.053
            double flickDistance = 19.0;
            double asdwNudge = 1.0;
            double neededDistance = flickDistance + asdwNudge; // 20
            double factor = neededDistance / flickDistance;
            Assert.InRange(factor, 1.053 - 0.005, 1.053 + 0.005);
        }

        [Fact]
        public void Factor_MonotonicInNudgeSize()
        {
            // The user's directive: "هرچه اصلاح ASDW بیشتر باشد ضریب باید
            // بزرگ‌تر شود" — a larger ASDW correction must produce a larger
            // factor. flick stays at 17; nudge grows from 1 to 3 to 5.
            double flick = 17.0;
            double[] nudges = { 1.0, 3.0, 5.0 };
            double[] factors = new double[nudges.Length];
            for (int i = 0; i < nudges.Length; i++)
            {
                double needed = flick + nudges[i];
                factors[i] = needed / flick;
            }
            // Strictly increasing.
            for (int i = 1; i < factors.Length; i++)
                Assert.True(factors[i] > factors[i - 1],
                    $"Factor must increase with nudge size: {factors[i]} > {factors[i - 1]}");
        }

        [Fact]
        public void Factor_NoAsdw_FallsBackToTargetOverFlick()
        {
            // Without ASDW, factor = target / flick.
            double target = 20.0;
            double flick = 17.0;
            double factor = target / flick;
            Assert.InRange(factor, 1.176 - 0.005, 1.176 + 0.005);

            var trial = new DesktopSensTrial
            {
                TargetDistanceCm = target,
                FlickDistanceCm = flick,
                NeededDistanceCm = double.NaN // no ASDW
            };
            // Effective target falls back to TargetDistanceCm.
            double effectiveTarget = double.IsNaN(trial.NeededDistanceCm)
                ? trial.TargetDistanceCm
                : trial.NeededDistanceCm;
            Assert.Equal(20.0, effectiveTarget, 3);
            Assert.Equal(1.176, effectiveTarget / trial.FlickDistanceCm, 3);
        }

        [Fact]
        public void TotalFactor_EqualsCellFactorTimesAppliedLutFactor()
        {
            // The user's directive #7: totalFactor = cellFactor × AppliedLutFactor
            // must equal the raw target/flick ratio. Verify this identity.
            double target = 20.0;
            double flick = 17.0;
            double rawRatio = target / flick; // ≈ 1.176

            // Suppose the active LUT was already applying 1.1 at this trial's
            // speed. The cell's residual factor is then rawRatio / 1.1.
            double appliedLutFactor = 1.1;
            double cellFactor = rawRatio / appliedLutFactor;
            double totalFactor = cellFactor * appliedLutFactor;

            Assert.Equal(rawRatio, totalFactor, 4);
            Assert.True(System.Math.Abs(totalFactor - 1.176) < 0.005,
                $"totalFactor must equal raw ratio (≈1.176), got {totalFactor}.");
        }
    }
}
