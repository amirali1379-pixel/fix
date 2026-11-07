using System.Collections.Generic;
using System.Globalization;
using grapher.DesktopSens.Models;
using Xunit;
using Xunit.Abstractions;

namespace grapher.DesktopSens.Tests
{
    /// <summary>
    /// Diagnostic test: how does the per-cell acceleration factor change
    /// as the ASDW nudge magnitude grows, with everything else held fixed?
    /// </summary>
    public class AsdwSlopeDiagnosticTests
    {
        private readonly ITestOutputHelper _output;

        public AsdwSlopeDiagnosticTests(ITestOutputHelper output)
        {
            _output = output;
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 100.0);
        }

        [Fact]
        public void Diagnostic_FactorVsNudgeSize()
        {
            int angleZone = 0;
            double targetCm = 20.0;
            int distanceZone = ZoneCalculations.DistanceToZoneIndex(targetCm, 100.0);
            int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, distanceZone);

            // flick stays at 17; raw error is -3.
            // ASDW nudge grows from 1 → 6 cm (all confirmed, NeededDistanceCm = 17+nudge).
            double[] nudges = { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0 };
            double[] factors = new double[nudges.Length];

            _output.WriteLine("");
            _output.WriteLine("=== Test 1: same zone, growing under-flick ===");
            _output.WriteLine("Nudge(cm) | Under-flick(cm) | Factor   | dFactor/dNudge");
            _output.WriteLine("----------|-----------------|----------|---------------");

            for (int i = 0; i < nudges.Length; i++)
            {
                double nudge = nudges[i];
                double needed = 17.0 + nudge; // 18, 19, 20, 21, 22, 23
                double underFlick = needed - 17.0; // == nudge (flick=17, target=needed)
                var profile = new DesktopSensProfile { MouseDPI = 800 };
                for (int t = 0; t < 5; t++)
                {
                    profile.Trials.Add(new DesktopSensTrial
                    {
                        AngleZoneIndex = angleZone,
                        DistanceZoneIndex = distanceZone,
                        TargetDistanceCm = targetCm,
                        FlickDistanceCm = 17.0,
                        SignedErrorCm = 17.0 - needed, // negative = under
                        NeededDistanceCm = needed,
                        HasManualAsdwCorrection = true,
                        ManualAsdwCorrectionCm = nudge,
                        ManualAsdwFinalErrorCm = 0.05,
                        HasMovementMeasurement = true,
                        DurationMs = 200.0,
                        EffectiveCpi = 800
                    });
                }
                DesktopSensCalibrator.Calibrate(profile);
                factors[i] = profile.CellAccelerationFactor[cellIndex];

                string slope = (i == 0) ? "-" :
                    (factors[i] - factors[i - 1]).ToString("+0.0000;-0.0000;0", CultureInfo.InvariantCulture);
                _output.WriteLine($"{nudge,9:F1} | {underFlick,15:F1} | {factors[i],8:F4} | {slope}");
            }

            _output.WriteLine("");
            _output.WriteLine("Expected raw ratios (needed/flick):");
            for (int i = 0; i < nudges.Length; i++)
            {
                double needed = 17.0 + nudges[i];
                double rawRatio = needed / 17.0;
                _output.WriteLine($"  nudge={nudges[i]:F1} → needed/flick = {needed:F1}/17.0 = {rawRatio:F4}");
            }

            // Each extra cm of under-flick should produce a larger factor.
            for (int i = 1; i < nudges.Length; i++)
            {
                double dFactor = factors[i] - factors[i - 1];
                Assert.True(dFactor > 0,
                    $"nudge {nudges[i - 1]}→{nudges[i]}: factor {factors[i - 1]:F4}→{factors[i]:F4} " +
                    $"(dFactor={dFactor:+0.0000;-0.0000;0} per cm of nudge). " +
                    "ASDW must produce a steeper correction for larger under-flicks.");
            }
        }

        [Fact]
        public void Diagnostic_FactorVsZoneIndex_SameUnderFlick()
        {
            // THE key test for the user's directive: with the SAME under-flick
            // magnitude (3 cm), the factor should be the SAME regardless of
            // zone index. The user explicitly said the slope must depend on
            // the under-flick MAGNITUDE, not on which zone the trial landed in.
            // A 3-cm under-flick in zone 0 produces the same boost as a 3-cm
            // under-flick in zone 8.
            int angleZone = 0;
            double flickCm = 17.0;
            double underFlickCm = 3.0; // fixed
            double neededCm = flickCm + underFlickCm; // 20

            int[] zoneIndices = { 0, 1, 2, 3, 5, 8 };
            double[] factors = new double[zoneIndices.Length];

            _output.WriteLine("");
            _output.WriteLine("=== Test 2: same under-flick (3cm), different zones ===");
            _output.WriteLine("ZoneIndex | Factor   | Diff-from-zone0");
            _output.WriteLine("----------|----------|------------------");

            double baseFactor = 0.0;
            for (int i = 0; i < zoneIndices.Length; i++)
            {
                int zoneIndex = zoneIndices[i];
                int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, zoneIndex);

                var profile = new DesktopSensProfile { MouseDPI = 800 };
                for (int t = 0; t < 5; t++)
                {
                    profile.Trials.Add(new DesktopSensTrial
                    {
                        AngleZoneIndex = angleZone,
                        DistanceZoneIndex = zoneIndex,
                        TargetDistanceCm = neededCm,
                        FlickDistanceCm = flickCm,
                        SignedErrorCm = flickCm - neededCm, // -3
                        NeededDistanceCm = neededCm,
                        HasManualAsdwCorrection = true,
                        ManualAsdwCorrectionCm = underFlickCm,
                        ManualAsdwFinalErrorCm = 0.05,
                        HasMovementMeasurement = true,
                        DurationMs = 200.0,
                        EffectiveCpi = 800
                    });
                }
                DesktopSensCalibrator.Calibrate(profile);
                factors[i] = profile.CellAccelerationFactor[cellIndex];
                if (i == 0) baseFactor = factors[i];

                string diff = (i == 0) ? "-" :
                    (factors[i] - baseFactor).ToString("+0.0000;-0.0000;0", CultureInfo.InvariantCulture);
                _output.WriteLine($"{zoneIndex,9} | {factors[i],8:F4} | {diff}");
            }

            _output.WriteLine("");
            _output.WriteLine("Expected: factor must be IDENTICAL across all zones (the boost");
            _output.WriteLine("depends only on the under-flick magnitude, not on the zone index).");

            // All factors should be within 1% of each other — the boost must
            // be zone-agnostic.
            for (int i = 1; i < zoneIndices.Length; i++)
            {
                double delta = System.Math.Abs(factors[i] - factors[0]);
                Assert.True(delta < 0.01,
                    $"Factor at zone {zoneIndices[i]} ({factors[i]:F4}) must equal factor at zone 0 ({factors[0]:F4}). " +
                    "The boost must depend only on the under-flick magnitude, not on the zone index. " +
                    $"Delta={delta:F4}.");
            }
        }

        [Fact]
        public void Diagnostic_OverFlickProducesNegativeBoost()
        {
            // The user's directive: over-flick must produce a NEGATIVE boost
            // of the same magnitude as the corresponding under-flick.
            int angleZone = 0;
            int distanceZone = ZoneCalculations.DistanceToZoneIndex(20.0, 100.0);
            int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, distanceZone);

            // Under-flick: flick=17, needed=20, signedError=-3
            var profileUnder = new DesktopSensProfile { MouseDPI = 800 };
            for (int t = 0; t < 5; t++)
            {
                profileUnder.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = angleZone,
                    DistanceZoneIndex = distanceZone,
                    TargetDistanceCm = 20.0,
                    FlickDistanceCm = 17.0,
                    SignedErrorCm = -3.0,
                    NeededDistanceCm = 20.0,
                    HasManualAsdwCorrection = true,
                    ManualAsdwCorrectionCm = 3.0,
                    ManualAsdwFinalErrorCm = 0.05,
                    HasMovementMeasurement = true,
                    DurationMs = 200.0,
                    EffectiveCpi = 800
                });
            }
            DesktopSensCalibrator.Calibrate(profileUnder);
            double factorUnder = profileUnder.CellAccelerationFactor[cellIndex];

            // Over-flick: flick=23, needed=20, signedError=+3
            var profileOver = new DesktopSensProfile { MouseDPI = 800 };
            for (int t = 0; t < 5; t++)
            {
                profileOver.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = angleZone,
                    DistanceZoneIndex = distanceZone,
                    TargetDistanceCm = 20.0,
                    FlickDistanceCm = 23.0,
                    SignedErrorCm = +3.0,
                    NeededDistanceCm = 20.0,
                    HasManualAsdwCorrection = true,
                    ManualAsdwCorrectionCm = -3.0,
                    ManualAsdwFinalErrorCm = 0.05,
                    HasMovementMeasurement = true,
                    DurationMs = 200.0,
                    EffectiveCpi = 800
                });
            }
            DesktopSensCalibrator.Calibrate(profileOver);
            double factorOver = profileOver.CellAccelerationFactor[cellIndex];

            _output.WriteLine("");
            _output.WriteLine("=== Test 3: over-flick produces negative boost ===");
            _output.WriteLine($"Under-flick (flick=17, needed=20, error=-3): factor = {factorUnder:F4}");
            _output.WriteLine($"Over-flick  (flick=23, needed=20, error=+3): factor = {factorOver:F4}");
            _output.WriteLine($"Base (no boost) for under = 20/17 = {20.0/17.0:F4}");
            _output.WriteLine($"Base (no boost) for over  = 20/23 = {20.0/23.0:F4}");
            _output.WriteLine("");

            // Under-flick must produce factor > 1.0 (boost positive).
            Assert.True(factorUnder > 1.0,
                $"Under-flick must produce factor > 1.0; got {factorUnder}.");

            // Over-flick must produce factor < 1.0 (boost negative).
            Assert.True(factorOver < 1.0,
                $"Over-flick must produce factor < 1.0; got {factorOver}.");

            // Over-flick must produce a SMALLER factor than its base (the
            // boost is negative, even if not exactly symmetric in magnitude
            // because the base factors themselves differ).
            double overBase = 20.0 / 23.0;
            Assert.True(factorOver < overBase,
                $"Over-flick factor ({factorOver:F4}) must be smaller than its base " +
                $"(20/23 = {overBase:F4}); the boost must be negative.");
        }
    }
}
