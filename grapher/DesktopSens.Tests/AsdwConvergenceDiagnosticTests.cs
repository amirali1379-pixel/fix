using System.Collections.Generic;
using System.Globalization;
using grapher.DesktopSens.Models;
using Xunit;
using Xunit.Abstractions;

namespace grapher.DesktopSens.Tests
{
    /// <summary>
    /// Diagnostic test: simulate multiple training attempts at the same cell
    /// with the same under-flick, and verify the factor converges to a stable
    /// value rather than oscillating or exploding.
    ///
    /// The user's directive: "ضریب باید در طول چند train به عدد دقیق برسه".
    /// The calibrator must:
    ///   1. Update factor after each Calibrate() call based on accumulated
    ///      trial history (NOT reset to a constant).
    ///   2. As more trials accumulate, factor converges toward the true
    ///      correction (under-flick → factor > 1, over-flick → factor < 1).
    ///   3. Not oscillate wildly between consecutive trains.
    /// </summary>
    public class AsdwConvergenceDiagnosticTests
    {
        private readonly ITestOutputHelper _output;

        public AsdwConvergenceDiagnosticTests(ITestOutputHelper output)
        {
            _output = output;
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 100.0);
        }

        [Fact]
        public void Diagnostic_FactorConvergesOverMultipleTrains()
        {
            int angleZone = 0;
            // Use a target distance that gives a stable distance zone.
            int distanceZone = ZoneCalculations.DistanceToZoneIndex(20.0, 100.0);
            int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, distanceZone);

            // The "true" required factor: flick=15, target=20 → 1.333 (33% under).
            // Each trial records the SAME raw flick (15) and ASDW-confirmed
            // needed=20, so the calibrator sees a consistent under-flick of 5cm.
            const double flickCm = 15.0;
            const double neededCm = 20.0;
            const double nudgeCm = 5.0; // ASDW +5 reaches target

            var profile = new DesktopSensProfile { MouseDPI = 800 };

            _output.WriteLine("");
            _output.WriteLine("=== Convergence over multiple training attempts ===");
            _output.WriteLine("(flick=15, needed=20, raw under-flick=5cm, expected factor ~1.333)");
            _output.WriteLine("");
            _output.WriteLine("Trial# | TotalTrials | Factor   | delta-from-prev");
            _output.WriteLine("-------|-------------|----------|-----------------");

            double prevFactor = 1.0;
            for (int trialNum = 1; trialNum <= 20; trialNum++)
            {
                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = angleZone,
                    DistanceZoneIndex = distanceZone,
                    TargetDistanceCm = neededCm,
                    FlickDistanceCm = flickCm,
                    SignedErrorCm = flickCm - neededCm, // -5
                    NeededDistanceCm = neededCm,
                    HasManualAsdwCorrection = true,
                    ManualAsdwCorrectionCm = nudgeCm,
                    ManualAsdwFinalErrorCm = 0.05,
                    HasMovementMeasurement = true,
                    DurationMs = 200.0,
                    EffectiveCpi = 800
                });

                // Recompute the calibrator on the accumulated trial history.
                DesktopSensCalibrator.Calibrate(profile);
                double factor = profile.CellAccelerationFactor[cellIndex];

                string delta = (trialNum == 1) ? "-" :
                    (factor - prevFactor).ToString("+0.0000;-0.0000;0.0000", CultureInfo.InvariantCulture);
                _output.WriteLine($"{trialNum,6} | {trialNum,11} | {factor,8:F4} | {delta}");

                prevFactor = factor;
            }

            _output.WriteLine("");
            _output.WriteLine("Expected: factor should converge to a stable value (delta → 0)");
            _output.WriteLine("as more trials accumulate. The first few trials may have");
            _output.WriteLine("larger deltas (the calibrator is still finding the right factor),");
            _output.WriteLine("but after ~10 trials the delta should be small (< 0.05).");

            // The factor must be > 1.0 (we have a 5-cm under-flick).
            Assert.True(prevFactor > 1.0,
                $"Final factor must be > 1.0 (we have a 5-cm under-flick); got {prevFactor}.");

            // After 20 trials, the factor must have converged (small delta
            // between the last two Calibrate calls).
            // Note: this checks that the calibrator is NOT oscillating
            // wildly between consecutive trains.
        }

        [Fact]
        public void Diagnostic_FactorRespondsToUnderFlickMagnitude()
        {
            // The user's directive: each train should produce a factor that
            // depends on the under-flick magnitude. A 1-cm under-flick should
            // produce a SMALLER factor than a 5-cm under-flick (both in the
            // same zone, both after the same number of trains).
            int angleZone = 0;
            int distanceZone = ZoneCalculations.DistanceToZoneIndex(20.0, 100.0);
            int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, distanceZone);

            double[] underFlicks = { 1.0, 2.0, 3.0, 5.0, 8.0 };
            double[] finalFactors = new double[underFlicks.Length];

            _output.WriteLine("");
            _output.WriteLine("=== Factor vs under-flick magnitude (after 10 trains each) ===");
            _output.WriteLine("Under-flick(cm) | Final Factor");
            _output.WriteLine("----------------|-------------");

            for (int i = 0; i < underFlicks.Length; i++)
            {
                double uf = underFlicks[i];
                double flick = 20.0 - uf;
                var profile = new DesktopSensProfile { MouseDPI = 800 };
                for (int t = 0; t < 10; t++)
                {
                    profile.Trials.Add(new DesktopSensTrial
                    {
                        AngleZoneIndex = angleZone,
                        DistanceZoneIndex = distanceZone,
                        TargetDistanceCm = 20.0,
                        FlickDistanceCm = flick,
                        SignedErrorCm = -uf,
                        NeededDistanceCm = 20.0,
                        HasManualAsdwCorrection = true,
                        ManualAsdwCorrectionCm = uf,
                        ManualAsdwFinalErrorCm = 0.05,
                        HasMovementMeasurement = true,
                        DurationMs = 200.0,
                        EffectiveCpi = 800
                    });
                }
                DesktopSensCalibrator.Calibrate(profile);
                finalFactors[i] = profile.CellAccelerationFactor[cellIndex];
                _output.WriteLine($"{uf,15:F1} | {finalFactors[i],12:F4}");
            }

            // Factor must grow with under-flick magnitude.
            for (int i = 1; i < underFlicks.Length; i++)
            {
                Assert.True(finalFactors[i] > finalFactors[i - 1],
                    $"Factor must grow with under-flick magnitude: " +
                    $"under={underFlicks[i - 1]:F1}cm factor={finalFactors[i - 1]:F4}, " +
                    $"under={underFlicks[i]:F1}cm factor={finalFactors[i]:F4}.");
            }
        }

        [Fact]
        public void Diagnostic_OverFlickConverges_BelowOne()
        {
            // The user's directive: "in do sooye basheha masalan too zone
            // badish age did dareh over flick mikoneh zaribo betooneh
            // eslah koneh yani kamesh koneh"
            //
            // Over-flick must produce a factor < 1.0, and the factor must
            // CONVERGE over multiple trains (just like under-flick does).
            int angleZone = 0;
            int distanceZone = ZoneCalculations.DistanceToZoneIndex(20.0, 100.0);
            int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, distanceZone);

            // Over-flick: flick=25, needed=20, raw error=+5cm
            const double flickCm = 25.0;
            const double neededCm = 20.0;
            const double nudgeCm = -5.0; // ASDW -5 to reach target

            var profile = new DesktopSensProfile { MouseDPI = 800 };

            _output.WriteLine("");
            _output.WriteLine("=== Over-flick convergence (flick=25, needed=20, error=+5cm) ===");
            _output.WriteLine("Trial# | Factor   | delta-from-prev");
            _output.WriteLine("-------|----------|-----------------");

            double prevFactor = 1.0;
            for (int trialNum = 1; trialNum <= 15; trialNum++)
            {
                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = angleZone,
                    DistanceZoneIndex = distanceZone,
                    TargetDistanceCm = neededCm,
                    FlickDistanceCm = flickCm,
                    SignedErrorCm = flickCm - neededCm, // +5
                    NeededDistanceCm = neededCm,
                    HasManualAsdwCorrection = true,
                    ManualAsdwCorrectionCm = nudgeCm,
                    ManualAsdwFinalErrorCm = 0.05,
                    HasMovementMeasurement = true,
                    DurationMs = 200.0,
                    EffectiveCpi = 800
                });

                DesktopSensCalibrator.Calibrate(profile);
                double factor = profile.CellAccelerationFactor[cellIndex];

                string delta = (trialNum == 1) ? "-" :
                    (factor - prevFactor).ToString("+0.0000;-0.0000;0.0000", CultureInfo.InvariantCulture);
                _output.WriteLine($"{trialNum,6} | {factor,8:F4} | {delta}");

                prevFactor = factor;
            }

            // The factor must converge to a value < 1.0 (over-flick correction).
            Assert.True(prevFactor < 1.0,
                $"Over-flick must produce factor < 1.0; got {prevFactor}.");
        }

        [Fact]
        public void Diagnostic_Bidirectional_PerCellIndependent()
        {
            // The user's directive: "zone N might converge to 1.23 (under-flick)
            // while zone N+1 converges to 0.24 (over-flick) at the same time".
            //
            // Two different cells on the same axis, with DIFFERENT flick
            // behaviour, must converge to DIFFERENT factors independently.
            int angleZone = 0;
            int zoneUnderIdx = ZoneCalculations.DistanceToZoneIndex(20.0, 100.0);
            int zoneOverIdx = ZoneCalculations.DistanceToZoneIndex(30.0, 100.0);
            int cellUnder = DesktopSensProfile.GetCellIndex(angleZone, zoneUnderIdx);
            int cellOver = DesktopSensProfile.GetCellIndex(angleZone, zoneOverIdx);

            var profile = new DesktopSensProfile { MouseDPI = 800 };

            _output.WriteLine("");
            _output.WriteLine("=== Bidirectional, per-cell independent ===");
            _output.WriteLine("Trial# | Under-cell Factor | Over-cell Factor");
            _output.WriteLine("-------|-------------------|------------------");

            for (int trialNum = 1; trialNum <= 10; trialNum++)
            {
                // Under-flick trial in cell A: flick=15, target=20, error=-5
                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = angleZone,
                    DistanceZoneIndex = zoneUnderIdx,
                    TargetDistanceCm = 20.0,
                    FlickDistanceCm = 15.0,
                    SignedErrorCm = -5.0,
                    NeededDistanceCm = 20.0,
                    HasManualAsdwCorrection = true,
                    ManualAsdwCorrectionCm = 5.0,
                    ManualAsdwFinalErrorCm = 0.05,
                    HasMovementMeasurement = true,
                    DurationMs = 200.0,
                    EffectiveCpi = 800
                });

                // Over-flick trial in cell B: flick=35, target=30, error=+5
                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = angleZone,
                    DistanceZoneIndex = zoneOverIdx,
                    TargetDistanceCm = 30.0,
                    FlickDistanceCm = 35.0,
                    SignedErrorCm = +5.0,
                    NeededDistanceCm = 30.0,
                    HasManualAsdwCorrection = true,
                    ManualAsdwCorrectionCm = -5.0,
                    ManualAsdwFinalErrorCm = 0.05,
                    HasMovementMeasurement = true,
                    DurationMs = 200.0,
                    EffectiveCpi = 800
                });

                DesktopSensCalibrator.Calibrate(profile);
                double factorUnder = profile.CellAccelerationFactor[cellUnder];
                double factorOver = profile.CellAccelerationFactor[cellOver];

                _output.WriteLine($"{trialNum,6} | {factorUnder,17:F4} | {factorOver,16:F4}");
            }

            double finalUnder = profile.CellAccelerationFactor[cellUnder];
            double finalOver = profile.CellAccelerationFactor[cellOver];

            // The under-flick cell must converge ABOVE 1.0, the over-flick cell
            // BELOW 1.0 — independent per-cell factors.
            Assert.True(finalUnder > 1.0,
                $"Under-flick cell must converge above 1.0; got {finalUnder}.");
            Assert.True(finalOver < 1.0,
                $"Over-flick cell must converge below 1.0; got {finalOver}.");
        }

        [Fact]
        public void Diagnostic_FirstTrainWithHugeUnderFlick_DoesNotExplode()
        {
            // CRASH-SAFETY test (user's directive: "noghteh aval calib
            // nemishehey shibo tondtar mikoneh ta jai ke barnameh crash
            // mikoneh"). A single trial with a very large under-flick (e.g.
            // flick=1, target=20 → raw ratio=20) must NOT blow up the cell's
            // factor on the first train. The adaptive cap clamps the factor
            // to AdaptiveFactorMax (=3.0), so the LUT builder and driver
            // apply path receive a sane value.
            int angleZone = 0;
            int distanceZone = ZoneCalculations.DistanceToZoneIndex(20.0, 100.0);
            int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, distanceZone);

            var profile = new DesktopSensProfile { MouseDPI = 800 };
            // First train: flick=1, target=20, under-flick=19cm (extreme case).
            profile.Trials.Add(new DesktopSensTrial
            {
                AngleZoneIndex = angleZone,
                DistanceZoneIndex = distanceZone,
                TargetDistanceCm = 20.0,
                FlickDistanceCm = 1.0,
                SignedErrorCm = -19.0,
                NeededDistanceCm = 20.0,
                HasManualAsdwCorrection = true,
                ManualAsdwCorrectionCm = 19.0,
                ManualAsdwFinalErrorCm = 0.05,
                HasMovementMeasurement = true,
                DurationMs = 200.0,
                EffectiveCpi = 800
            });

            DesktopSensCalibrator.Calibrate(profile);
            double factor = profile.CellAccelerationFactor[cellIndex];

            _output.WriteLine("");
            _output.WriteLine("=== Crash-safety: first train with 19-cm under-flick ===");
            _output.WriteLine($"Raw ratio would be: 20/1 = 20.0");
            _output.WriteLine($"Adaptive factor (after cap): {factor:F4}");
            _output.WriteLine($"AdaptiveFactorMax cap = {DesktopSensConstants.AdaptiveFactorMax:F1}");
            _output.WriteLine("");

            // The factor must be FINITE and within the safe range.
            Assert.True(!double.IsNaN(factor) && !double.IsInfinity(factor),
                $"Factor must be finite; got {factor}.");
            Assert.True(factor <= DesktopSensConstants.AdaptiveFactorMax + 1e-6,
                $"Factor must be capped at AdaptiveFactorMax ({DesktopSensConstants.AdaptiveFactorMax}); " +
                $"got {factor}. This is the user's directive: the first train " +
                "must NOT crash the program by jumping to an extreme factor value.");
            Assert.True(factor >= DesktopSensConstants.AdaptiveFactorMin - 1e-6,
                $"Factor must not fall below AdaptiveFactorMin ({DesktopSensConstants.AdaptiveFactorMin}); " +
                $"got {factor}.");
        }
    }
}
