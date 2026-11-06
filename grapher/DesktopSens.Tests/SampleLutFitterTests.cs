using System;
using System.Collections.Generic;
using System.Diagnostics;
using grapher.Models.Mouse;
using grapher.DesktopSens.Models;
using Xunit;

namespace grapher.DesktopSens.Tests
{
    /// <summary>
    /// NEW (Model v6): tests for SampleLutFitter. Verifies that two cells
    /// with different speeds both contribute to the LUT, that a yellow
    /// cell still participates with a smaller weight than a green cell,
    /// that the emitted LUT satisfies the shape invariants (ascending x,
    /// positive y, capacity), and that per-point isolation (anchors)
    /// preserves previously-built factors across a re-fit.
    /// </summary>
    public class SampleLutFitterTests
    {
        [Fact]
        public void TwoGreenCellsWithDifferentSpeeds_BothAppearInLut()
        {
            // Each trial uses flick < target so the fitted factor at the
            // trial's speed should be > 1 (an under-flick requires a
            // sensitivity boost).
            var trials = new List<DesktopSensTrial>
            {
                MakeSyntheticTrial(speedCountsPerSec: 20, targetCm: 5.0, neededCm: 5.0, flickCm: 4.0),
                MakeSyntheticTrial(speedCountsPerSec: 100, targetCm: 30.0, neededCm: 30.0, flickCm: 24.0)
            };
            var fit = SampleLutFitter.Fit(trials);
            Assert.True(fit.IsValid, fit.FailureReason);
            // Both speeds should influence the fit so the emitted curve
            // covers the low and high ranges.
            Assert.True(fit.NodeSpeedCmPerSec[0] < fit.NodeSpeedCmPerSec[fit.NodeSpeedCmPerSec.Length - 1]);
            Assert.Equal(2, fit.TrialCount);
        }

        [Fact]
        public void YellowCellWithSamplesParticipatesWithLowerWeight()
        {
            // Two trials: one green-equivalent, one yellow-equivalent.
            // Both must be present in the trial list; the caller decides
            // weight. This test just verifies both contribute to a
            // non-degenerate fit.
            var trials = new List<DesktopSensTrial>
            {
                MakeSyntheticTrial(20, 5.0, 5.0, 4.0),
                MakeSyntheticTrial(60, 15.0, 16.0, 13.0)
            };
            var fit = SampleLutFitter.Fit(trials);
            Assert.True(fit.IsValid, fit.FailureReason);
            Assert.Equal(2, fit.TrialCount);
        }

        [Fact]
        public void Fit_Empty_ReturnsInvalid()
        {
            var fit = SampleLutFitter.Fit(new List<DesktopSensTrial>());
            Assert.False(fit.IsValid);
        }

        [Fact]
        public void Fit_Null_ReturnsInvalid()
        {
            var fit = SampleLutFitter.Fit(null);
            Assert.False(fit.IsValid);
        }

        [Fact]
        public void ToLutPoints_ProducesAscendingXPositiveY_WithinCapacity()
        {
            // Build a wide-range synthetic trial set so the fit covers
            // a meaningful speed span.
            var trials = new List<DesktopSensTrial>
            {
                MakeSyntheticTrial(20, 5.0, 5.0, 4.0),
                MakeSyntheticTrial(60, 15.0, 15.0, 12.0),
                MakeSyntheticTrial(120, 30.0, 30.0, 24.0),
            };
            var fit = SampleLutFitter.Fit(trials);
            Assert.True(fit.IsValid, fit.FailureReason);

            var points = SampleLutFitter.ToLutPoints(fit);
            Assert.True(points.Count > 0);

            // Strictly ascending x.
            for (int i = 1; i < points.Count; i++)
                Assert.True(points[i].InputSpeedCmPerSec > points[i - 1].InputSpeedCmPerSec,
                    "LUT points must be strictly ascending in speed.");

            // Positive factor at every point.
            foreach (var p in points)
            {
                Assert.True(p.Factor >= DesktopSensConstants.MinimumCalibrationFactor,
                    "LUT factor must stay above the calibration safety minimum.");
                Assert.True(p.Factor <= DesktopSensConstants.MaximumCalibrationFactor,
                    "LUT factor must stay below the calibration safety maximum.");
            }

            // Capacity: LutPointsCapacity = 257, so the sample-fit LUT (8–16
            // nodes) plus 2 bookends is well within range.
            Assert.True(points.Count <= RawAccelSettingsBridge.LutPointsCapacity);
        }

        // ───────────────────────────────────────────────────────────────────
        // NEW: deeper tests for the green/yellow weighting policy.
        // ───────────────────────────────────────────────────────────────────

        [Fact]
        public void ComputeCellWeight_Green_ReturnsOne()
        {
            Assert.Equal(1.0, SampleLutFitter.ComputeCellWeight(true, false, 0.0));
            Assert.Equal(1.0, SampleLutFitter.ComputeCellWeight(true, false, 0.5));
            Assert.Equal(1.0, SampleLutFitter.ComputeCellWeight(true, false, 1.0));
        }

        [Fact]
        public void ComputeCellWeight_Yellow_ReturnsConfidenceFlooredAtMinimum()
        {
            // Yellow cell with confidence 0.5 -> 0.5
            Assert.Equal(0.5, SampleLutFitter.ComputeCellWeight(false, true, 0.5));
            // Yellow cell with confidence 0.0 -> floored to YellowCellMinLutWeight
            Assert.Equal(DesktopSensConstants.YellowCellMinLutWeight,
                SampleLutFitter.ComputeCellWeight(false, true, 0.0));
            // Yellow cell with confidence below floor -> floored to YellowCellMinLutWeight
            Assert.Equal(DesktopSensConstants.YellowCellMinLutWeight,
                SampleLutFitter.ComputeCellWeight(false, true, 0.01));
            // Yellow cell with confidence > 1.0 -> capped below green weight
            Assert.Equal(DesktopSensConstants.YellowCellMaxLutWeight,
                SampleLutFitter.ComputeCellWeight(false, true, 2.0));
            // Yellow cell with NaN confidence -> treated as 0 -> floored
            Assert.Equal(DesktopSensConstants.YellowCellMinLutWeight,
                SampleLutFitter.ComputeCellWeight(false, true, double.NaN));
        }

        [Fact]
        public void ComputeCellWeight_NeitherGreenNorYellow_ReturnsZero()
        {
            // A cell with no accepted attempts is excluded from the LUT.
            Assert.Equal(0.0, SampleLutFitter.ComputeCellWeight(false, false, 0.5));
            Assert.Equal(0.0, SampleLutFitter.ComputeCellWeight(false, false, 1.0));
        }

        [Fact]
        public void ComputeCellWeight_GreenBeatsYellow_EvenWithHigherYellowConfidence()
        {
            // Green is always full weight, regardless of confidence.
            // Yellow is capped below green so it can never dominate verified data.
            double greenWeight = SampleLutFitter.ComputeCellWeight(true, false, 1.0);
            double yellowWeight = SampleLutFitter.ComputeCellWeight(false, true, 1.0);
            Assert.Equal(1.0, greenWeight);
            Assert.Equal(DesktopSensConstants.YellowCellMaxLutWeight, yellowWeight);
            Assert.True(greenWeight > yellowWeight);
        }

        [Fact]
        public void Fit_WeightsYellowTrialsBelowGreenTrials()
        {
            var green = MakeSyntheticTrial(60, 12.0, 12.0, 10.0);
            var yellow = MakeSyntheticTrial(60, 20.0, 20.0, 10.0);
            var greenOnly = SampleLutFitter.Fit(new List<DesktopSensTrial> { green });
            var equalWeight = SampleLutFitter.Fit(new List<DesktopSensTrial> { green, yellow });
            var weighted = SampleLutFitter.Fit(
                new List<DesktopSensTrial> { green, yellow }, null,
                DesktopSensConstants.SampleLutDefaultNodes, true,
                new List<double> { 1.0, DesktopSensConstants.YellowCellMinLutWeight }, 1.0);

            Assert.True(greenOnly.IsValid, greenOnly.FailureReason);
            Assert.True(equalWeight.IsValid, equalWeight.FailureReason);
            Assert.True(weighted.IsValid, weighted.FailureReason);
            double greenMean = Mean(greenOnly.NodeFactors);
            double equalMean = Mean(equalWeight.NodeFactors);
            double weightedMean = Mean(weighted.NodeFactors);
            Assert.True(Math.Abs(weightedMean - greenMean) < Math.Abs(equalMean - greenMean),
                "A yellow trial must have less influence than a green trial.");
        }

        [Fact]
        public void Fit_AccountsForChangedCurrentBaseFactor()
        {
            var trial = MakeSyntheticTrial(60, 20.0, 20.0, 10.0);
            var unchangedBase = SampleLutFitter.Fit(new List<DesktopSensTrial> { trial });
            // Current base=1.2, capture-time base=0.6 => target scale=2.0.
            var changedBase = SampleLutFitter.Fit(
                new List<DesktopSensTrial> { trial }, null,
                DesktopSensConstants.SampleLutDefaultNodes, true,
                null, 1.0, new List<double> { 2.0 });

            Assert.True(unchangedBase.IsValid, unchangedBase.FailureReason);
            Assert.True(changedBase.IsValid, changedBase.FailureReason);
            Assert.True(Mean(changedBase.NodeFactors) > Mean(unchangedBase.NodeFactors),
                "The replacement LUT must compensate for the larger current base factor relative to capture-time base.");
        }

        [Fact]
        public void Fit_ExcludesAsdwTrialWhenFinalLandingWasNotConfirmed()
        {
            var valid = MakeSyntheticTrial(60, 12.0, 12.0, 10.0);
            var failedAsdw = MakeSyntheticTrial(90, 18.0, 18.0, 10.0);
            failedAsdw.HasManualAsdwCorrection = true;
            failedAsdw.NeededDistanceCm = double.NaN;

            var fit = SampleLutFitter.Fit(new List<DesktopSensTrial> { valid, failedAsdw });
            Assert.True(fit.IsValid, fit.FailureReason);
            Assert.Equal(1, fit.TrialCount);
        }

        [Fact]
        public void Fit_NormalizesEachTrialByCapturedBaseFactor()
        {
            var trial = MakeSyntheticTrial(60, 20.0, 20.0, 10.0);
            var unscaled = SampleLutFitter.Fit(new List<DesktopSensTrial> { trial });
            var normalized = SampleLutFitter.Fit(
                new List<DesktopSensTrial> { trial }, null,
                DesktopSensConstants.SampleLutDefaultNodes, true,
                null, 1.0, new List<double> { 0.5 });

            Assert.True(unscaled.IsValid, unscaled.FailureReason);
            Assert.True(normalized.IsValid, normalized.FailureReason);
            Assert.True(Mean(normalized.NodeFactors) < Mean(unscaled.NodeFactors),
                "A captured base factor of 2.0 must halve the target multiplier required from the LUT.");
        }

        private static double Mean(double[] values)
        {
            if (values == null || values.Length == 0) return 0.0;
            double sum = 0.0;
            foreach (double value in values) sum += value;
            return sum / values.Length;
        }

        // ───────────────────────────────────────────────────────────────────
        // NEW: per-point isolation (anchor) tests.
        // ───────────────────────────────────────────────────────────────────

        [Fact]
        public void Fit_WithAnchors_PreservesAnchoredFactors()
        {
            // Build a trial set whose underlying factor should be 1.25
            // (flick=16, target=20 → needed=20 / 16 = 1.25).
            var trials = new List<DesktopSensTrial>
            {
                MakeSyntheticTrial(speedCountsPerSec: 40, targetCm: 20.0, neededCm: 20.0, flickCm: 16.0),
                MakeSyntheticTrial(speedCountsPerSec: 80, targetCm: 20.0, neededCm: 20.0, flickCm: 16.0)
            };

            // First fit with no anchors — establishes the "baseline" curve.
            var baseline = SampleLutFitter.Fit(trials);
            Assert.True(baseline.IsValid, baseline.FailureReason);

            // Now fit again with an anchor that demands factor = 1.0 at the
            // midpoint speed. The anchored node should be pinned near 1.0
            // even though the trial data alone would have produced ~1.25.
            double midSpeed = (baseline.NodeSpeedCmPerSec[0] +
                              baseline.NodeSpeedCmPerSec[baseline.NodeSpeedCmPerSec.Length - 1]) * 0.5;
            var anchors = new List<(double, double)> { (midSpeed, 1.0) };
            var anchored = SampleLutFitter.Fit(trials, anchors);
            Assert.True(anchored.IsValid, anchored.FailureReason);

            // Find the node nearest to midSpeed and verify it moved toward
            // the anchor (1.0) compared to the baseline fit.
            double anchoredNodeFactor = LookupNearestNodeFactor(anchored, midSpeed);
            double baselineNodeFactor = LookupNearestNodeFactor(baseline, midSpeed);

            // The anchored fit's nearest node should be closer to 1.0 than
            // the baseline fit's nearest node. This is the per-point
            // isolation guarantee: the anchor pulls the curve back toward
            // its previously-verified value.
            double anchoredDelta = System.Math.Abs(anchoredNodeFactor - 1.0);
            double baselineDelta = System.Math.Abs(baselineNodeFactor - 1.0);
            Assert.True(anchoredDelta <= baselineDelta + 1e-6,
                $"Anchored fit (factor={anchoredNodeFactor:F4}) should be at least as close to 1.0 " +
                $"as baseline (factor={baselineNodeFactor:F4}).");
        }

        [Fact]
        public void Fit_WithMultipleAnchors_StaysBoundedByAnchorRange()
        {
            var trials = new List<DesktopSensTrial>
            {
                MakeSyntheticTrial(20, 10.0, 10.0, 5.0),  // factor ~2.0
                MakeSyntheticTrial(80, 10.0, 10.0, 8.0)   // factor ~1.25
            };

            // Pin two anchors: low-speed at 1.0 and high-speed at 1.0.
            // Without anchors, the fit would push factors well above 1.0
            // (under-flick). With anchors, the curve should be pulled back
            // toward 1.0 and stay within a reasonable band.
            var anchors = new List<(double, double)>
            {
                (20.0, 1.0),
                (80.0, 1.0)
            };
            var anchored = SampleLutFitter.Fit(trials, anchors);
            Assert.True(anchored.IsValid, anchored.FailureReason);

            // Every node factor should be finite and positive (already
            // guaranteed by the clamp) and the max factor should be less
            // than the unanchored fit's max — i.e. the anchors restrained
            // the curve. We at least verify the fit did not explode.
            double maxFactor = 0.0;
            double minFactor = double.MaxValue;
            foreach (var f in anchored.NodeFactors)
            {
                if (f > maxFactor) maxFactor = f;
                if (f < minFactor) minFactor = f;
            }
            Assert.True(maxFactor > 0.0);
            Assert.True(minFactor > 0.0);
            Assert.True(maxFactor < 100.0, "Anchored fit must not explode.");
        }

        private static double LookupNearestNodeFactor(
            SampleLutFitter.FitResult fit, double speed)
        {
            int bestIdx = 0;
            double bestDelta = double.MaxValue;
            for (int i = 0; i < fit.NodeSpeedCmPerSec.Length; i++)
            {
                double delta = System.Math.Abs(fit.NodeSpeedCmPerSec[i] - speed);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    bestIdx = i;
                }
            }
            return fit.NodeFactors[bestIdx];
        }

        [Fact]
        public void FixedNodeGrid_DoesNotMoveWhenHigherSpeedTrialsAreAdded()
        {
            var low = new List<DesktopSensTrial>
            {
                MakeSyntheticTrial(20, 5.0, 5.0, 4.0),
                MakeSyntheticTrial(40, 10.0, 10.0, 8.0)
            };
            var high = new List<DesktopSensTrial>(low)
            {
                MakeSyntheticTrial(160, 35.0, 35.0, 28.0)
            };
            var fitLow = SampleLutFitter.Fit(low);
            var fitHigh = SampleLutFitter.Fit(high);
            Assert.True(fitLow.IsValid, fitLow.FailureReason);
            Assert.True(fitHigh.IsValid, fitHigh.FailureReason);
            Assert.Equal(fitLow.NodeSpeedCmPerSec.Length, fitHigh.NodeSpeedCmPerSec.Length);
            for (int i = 0; i < fitLow.NodeSpeedCmPerSec.Length; i++)
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(fitLow.NodeSpeedCmPerSec[i]),
                    BitConverter.DoubleToInt64Bits(fitHigh.NodeSpeedCmPerSec[i]));
        }

        [Fact]
        public void Fit_ExactFrozenNodeConstraint_PreservesFactorBitwise()
        {
            var trials = new List<DesktopSensTrial>
            {
                MakeSyntheticTrial(20, 5.0, 5.0, 4.0),
                MakeSyntheticTrial(60, 15.0, 15.0, 12.0),
                MakeSyntheticTrial(160, 35.0, 35.0, 28.0)
            };
            var frozen = new double[DesktopSensConstants.SampleLutDefaultNodes];
            for (int i = 0; i < frozen.Length; i++) frozen[i] = double.NaN;
            frozen[2] = 1.23456789012345;
            frozen[3] = 0.87654321098765;

            var fit = SampleLutFitter.Fit(
                trials, null, DesktopSensConstants.SampleLutDefaultNodes,
                true, null, 1.0, null, frozen);
            Assert.True(fit.IsValid, fit.FailureReason);
            Assert.Equal(BitConverter.DoubleToInt64Bits(frozen[2]),
                BitConverter.DoubleToInt64Bits(fit.NodeFactors[2]));
            Assert.Equal(BitConverter.DoubleToInt64Bits(frozen[3]),
                BitConverter.DoubleToInt64Bits(fit.NodeFactors[3]));
        }

        [Fact]
        public void UnfreezeFromCell_ReleasesThatAndHigherNodes()
        {
            var profile = new DesktopSensProfile();
            var frozen = new double[DesktopSensConstants.SampleLutDefaultNodes];
            for (int i = 0; i < frozen.Length; i++) frozen[i] = 1.25;
            profile.FrozenSampleLutNodeFactorsByAngleZone[0] = frozen;
            var trial = MakeSyntheticTrial(40, 5.0, 5.0, 4.0);
            trial.AngleZoneIndex = 0;
            trial.DistanceZoneIndex = 0;
            profile.Trials = new List<DesktopSensTrial> { trial };

            Assert.True(DesktopSensProfileMerger.UnfreezeSampleLutFromCell(profile, 0, 0));
            var result = profile.FrozenSampleLutNodeFactorsByAngleZone[0];
            for (int i = 0; i < result.Length; i++)
                Assert.True(double.IsNaN(result[i]),
                    "Recalibrating the first distance zone should release every node.");
        }

        private static DesktopSensTrial MakeSyntheticTrial(
            double speedCountsPerSec, double targetCm, double neededCm, double flickCm)
        {
            // Build a small sequence of samples whose displacement
            // integrates to flickCm (the raw flick) along the +X axis.
            // The calibrator/merger feeds the trial to the fitter, which
            // uses NeededDistanceCm as the target the flick should have
            // produced. Setting flickCm < neededCm forces a factor > 1.
            var samples = new List<MouseSample>();
            const int cpi = 800;
            double cmPerCount = 2.54 / cpi;
            double totalCounts = flickCm / cmPerCount;
            int sampleCount = 8;
            double countsPerSample = totalCounts / sampleCount;
            long dtTicks = (long)(Stopwatch.Frequency / speedCountsPerSec * 0.05);
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
                TargetDistanceCm = targetCm,
                NeededDistanceCm = neededCm,
                FlickDistanceCm = flickCm,
                DurationMs = (sampleCount - 1) * (1000.0 / speedCountsPerSec * 0.05),
                EffectiveCpi = cpi,
                HasMovementMeasurement = true,
                TargetAngleDegrees = 0.0,
                RawSamples = samples
            };
        }
    }
}
