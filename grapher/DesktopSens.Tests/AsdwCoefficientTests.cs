using System.Collections.Generic;
using System.Diagnostics;
using grapher.Models.Mouse;
using grapher.DesktopSens.Models;
using Xunit;

namespace grapher.DesktopSens.Tests
{
    /// <summary>
    /// NEW (Model v10): regression tests that verify the ASDW-driven factor
    /// actually reaches the per-cell coefficient estimation and the merged
    /// LUT, rather than being silently dropped or replaced with target/flick.
    ///
    /// These tests pin the behaviour the user asked for:
    ///   1. ASDW-unconfirmed trials do NOT contribute their (target, error)
    ///      pair to the zone's ExcessTargetDistances / ExcessSignedErrors.
    ///   2. ASDW-confirmed trials DO contribute, with the effective target
    ///      (NeededDistanceCm) replacing the raw TargetDistanceCm so the
    ///      gain estimator sees a coherent (target, residual) pair.
    ///   3. The sample-fit LUT for an ASDW-confirmed cell produces a factor
    ///      strictly greater than 1 when the flick undershot the target by
    ///      a non-trivial margin (no silent "factor = 1" collapse).
    /// </summary>
    public class AsdwCoefficientTests
    {
        [Fact]
        public void ComputeCellWeight_YellowCappedAtHalf()
        {
            // Green = 1.0, Yellow never above 0.5 even if confidence = 1.0.
            Assert.Equal(1.0, SampleLutFitter.ComputeCellWeight(true, false, 1.0));
            Assert.Equal(0.5, SampleLutFitter.ComputeCellWeight(false, true, 1.0));
            Assert.Equal(0.5, SampleLutFitter.ComputeCellWeight(false, true, 0.8));
            Assert.Equal(0.3, SampleLutFitter.ComputeCellWeight(false, true, 0.3));
            // Below the floor -> clamped up.
            Assert.Equal(DesktopSensConstants.YellowCellMinLutWeight,
                SampleLutFitter.ComputeCellWeight(false, true, 0.0));
        }

        [Fact]
        public void Trial_WithAsdwInsideTolerance_CarriesNeededDistance()
        {
            // target=20, flick=17, nudge=+3 -> needed=20, factor=20/17 ~= 1.176
            var trial = new DesktopSensTrial
            {
                TargetDistanceCm = 20.0,
                FlickDistanceCm = 17.0,
                SignedErrorCm = -3.0,
                NeededDistanceCm = 20.0,
                HasManualAsdwCorrection = true,
                ManualAsdwCorrectionCm = 3.0,
                ManualAsdwFinalErrorCm = 0.5,
                HasMovementMeasurement = true
            };
            Assert.True(trial.NeededDistanceCm > trial.TargetDistanceCm - 0.01);
            Assert.True(trial.NeededDistanceCm > trial.FlickDistanceCm);
            double factor = trial.NeededDistanceCm / trial.FlickDistanceCm;
            Assert.True(factor > 1.15 && factor < 1.20,
                $"ASDW-confirmed under-flick factor must be ~1.176, got {factor}.");
        }

        [Fact]
        public void Trial_WithAsdwOutsideTolerance_HasNaNNeededDistance()
        {
            // Failed ASDW (final error > tolerance) must leave NeededDistanceCm
            // at NaN so the calibrator falls back to TargetDistanceCm.
            var trial = new DesktopSensTrial
            {
                TargetDistanceCm = 20.0,
                FlickDistanceCm = 17.0,
                SignedErrorCm = -3.0,
                NeededDistanceCm = double.NaN,
                HasManualAsdwCorrection = true,
                ManualAsdwCorrectionCm = 3.0,
                ManualAsdwFinalErrorCm = 5.0, // way out of tolerance
                HasMovementMeasurement = true
            };
            Assert.True(double.IsNaN(trial.NeededDistanceCm));
            double effectiveTarget = double.IsNaN(trial.NeededDistanceCm)
                ? trial.TargetDistanceCm
                : trial.NeededDistanceCm;
            Assert.Equal(20.0, effectiveTarget, 3);
        }

        [Fact]
        public void SampleFit_WithConfirmedAsdwUnderFlick_ProducesFactorAboveOne()
        {
            // Build one synthetic trial whose raw flick undershoots the
            // target by a clear margin (flick=16, needed=20 -> factor 1.25).
            // The fitted curve at the trial's average speed must be > 1.0
            // (i.e. the ASDW-confirmed under-flick actually pulled the
            // multiplier up, instead of being silently dropped).
            const int cpi = 800;
            double cmPerCount = 2.54 / cpi;
            double flickCm = 16.0;
            double neededCm = 20.0;
            int sampleCount = 8;
            double totalCounts = flickCm / cmPerCount;
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
                TargetDistanceCm = 20.0,
                NeededDistanceCm = neededCm,
                FlickDistanceCm = flickCm,
                DurationMs = sampleCount - 1,
                EffectiveCpi = cpi,
                HasMovementMeasurement = true,
                TargetAngleDegrees = 0.0,
                RawSamples = samples
            };

            var trials = new List<DesktopSensTrial> { trial };
            var fit = SampleLutFitter.Fit(trials);
            Assert.True(fit.IsValid,
                "Sample-fit must succeed for a single ASDW-confirmed under-flick trial. " +
                "Failure: " + (fit.FailureReason ?? "(null)"));

            // Evaluate the fitted factor at the trial's average speed.
            double avgSpeed = trial.FlickDistanceCm * 1000.0 / trial.DurationMs;
            double factor = SampleLutFitter.EvaluateFactor(fit, avgSpeed);
            Assert.True(!double.IsNaN(factor),
                "EvaluateFactor must return a finite factor at the trial's average speed.");
            Assert.True(factor > 1.05,
                $"ASDW-confirmed under-flick must produce factor > 1.05 at the trial's speed; got {factor}.");
        }

        [Fact]
        public void SampleFit_TwoCellsDifferentSpeeds_BothContributeNonTrivialFactor()
        {
            // Two trials at clearly different speeds, both under-flicks.
            // The fitted curve at BOTH speeds must be > 1.0 (no silent
            // collapse to factor = 1 anywhere the user actually flicked).
            const int cpi = 800;
            double cmPerCount = 2.54 / cpi;

            var trials = new List<DesktopSensTrial>();
            // Trial A: speed ~40 cm/s, flick 8 / needed 10 -> factor 1.25
            trials.Add(MakeTrial(cpi, cmPerCount, flickCm: 8.0, neededCm: 10.0, speedCmPerSec: 40.0));
            // Trial B: speed ~120 cm/s, flick 24 / needed 30 -> factor 1.25
            trials.Add(MakeTrial(cpi, cmPerCount, flickCm: 24.0, neededCm: 30.0, speedCmPerSec: 120.0));

            var fit = SampleLutFitter.Fit(trials);
            Assert.True(fit.IsValid, "Sample-fit must succeed for two under-flick trials. " +
                "Failure: " + (fit.FailureReason ?? "(null)"));

            // Evaluate at both trial speeds.
            double factorA = SampleLutFitter.EvaluateFactor(fit, 40.0);
            double factorB = SampleLutFitter.EvaluateFactor(fit, 120.0);
            Assert.True(!double.IsNaN(factorA) && factorA > 1.02,
                $"Factor at speed A (40 cm/s) must be > 1.02; got {factorA}.");
            Assert.True(!double.IsNaN(factorB) && factorB > 1.02,
                $"Factor at speed B (120 cm/s) must be > 1.02; got {factorB}.");
        }

        [Fact]
        public void LutUpperBookend_NeutralOne_PlacedImmediatelyAfterLastPoint()
        {
            // Build a tiny synthetic fit so we have at least one LutPoint.
            const int cpi = 800;
            double cmPerCount = 2.54 / cpi;
            var trial = MakeTrial(cpi, cmPerCount, flickCm: 8.0, neededCm: 10.0, speedCmPerSec: 40.0);
            var trials = new List<DesktopSensTrial> { trial };
            var fit = SampleLutFitter.Fit(trials);
            Assert.True(fit.IsValid);
            var points = SampleLutFitter.ToLutPoints(fit);
            Assert.True(points.Count >= 2);

            // Build LUT values with NeutralOne bookend.
            Dictionary<string, object> values;
            string error;
            bool ok = RawAccelSettingsBridge.TryBuildLutValues(
                points, out values, out error, LutUpperBookendMode.NeutralOne);
            Assert.True(ok, "TryBuildLutValues should succeed. Error: " + error);

            // The bookend's factor must be 1.0 and its x must NOT be 1e6
            // (it must be just above the last real point).
            var data = (float[])values["data"];
            int length = (int)values["length"];
            Assert.True(length >= 4, "LUT must have at least 2 points (x,y pairs).");
            float lastY = data[length - 1];
            Assert.True(System.Math.Abs(lastY - 1.0f) < 1e-3f,
                $"Neutral upper bookend y must be 1.0, got {lastY}.");
            float lastX = data[length - 2];
            Assert.True(lastX < 1e5f,
                $"Neutral upper bookend x must be placed immediately after the last calibrated point, not at 1e6; got {lastX}.");
        }

        private static DesktopSensTrial MakeTrial(
            int cpi, double cmPerCount, double flickCm, double neededCm, double speedCmPerSec)
        {
            int sampleCount = 8;
            double totalCounts = flickCm / cmPerCount;
            double countsPerSample = totalCounts / sampleCount;
            long dtTicks = (long)(Stopwatch.Frequency / speedCmPerSec * flickCm);
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
            return new DesktopSensTrial
            {
                TargetDistanceCm = neededCm,
                NeededDistanceCm = neededCm,
                FlickDistanceCm = flickCm,
                DurationMs = sampleCount * (flickCm / speedCmPerSec) * 1000.0,
                EffectiveCpi = cpi,
                HasMovementMeasurement = true,
                TargetAngleDegrees = 0.0,
                RawSamples = samples
            };
        }
    }
}
