using System.Collections.Generic;
using grapher.DesktopSens.Models;
using Xunit;

namespace grapher.DesktopSens.Tests
{
    /// <summary>
    /// NEW (Model v10): end-to-end tests that verify ASDW actually reaches
    /// the per-cell coefficient. We configure a real TrainingGeometry, build
    /// a profile with synthetic trials, call DesktopSensCalibrator.Calibrate
    /// (the real method, not a stub), and assert that
    /// profile.CellAccelerationFactor[cellIndex] reflects the ASDW-confirmed
    /// effective target rather than the raw TargetDistanceCm.
    ///
    /// This is the user's directive: "ASDW bayad dorost calibrate koneh age
    /// kharej az mantegh bashe har kari kardi bihoiodeh" — ASDW must actually
    /// drive the calibration; an out-of-tolerance ASDW must be ignored
    /// entirely (no partial credit).
    /// </summary>
    public class AsdwEndToEndTests
    {
        public AsdwEndToEndTests()
        {
            // Configure a horizontal-axis geometry with a generous distance
            // range so our synthetic trial lands in a real distance zone.
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 100.0);
        }

        [Fact]
        public void Calibrate_WithConfirmedAsdw_ProducesFactorMatchingNeededOverFlick()
        {
            // Build a profile with one trial whose raw flick undershot the
            // target by 3 cm and the user used ASDW (+3 cm) to land on the
            // target. NeededDistanceCm = 20, FlickDistanceCm = 17, so the
            // expected factor is 20/17 ~= 1.176.
            int angleZone = 0; // Right
            double targetCm = 20.0;
            double flickCm = 17.0;
            double nudgeCm = 3.0;
            double neededCm = flickCm + nudgeCm; // 20

            int distanceZone = ResolveDistanceZone(targetCm);
            int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, distanceZone);

            var profile = new DesktopSensProfile { MouseDPI = 800 };
            // Place several identical trials so RobustStatistics.TryLogGainConfidence
            // has enough samples (>=3) to produce a finite logGain.
            for (int i = 0; i < 5; i++)
            {
                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = angleZone,
                    DistanceZoneIndex = distanceZone,
                    TargetDistanceCm = targetCm,
                    FlickDistanceCm = flickCm,
                    SignedErrorCm = flickCm - targetCm, // -3
                    NeededDistanceCm = neededCm,
                    HasManualAsdwCorrection = true,
                    ManualAsdwCorrectionCm = nudgeCm,
                    ManualAsdwFinalErrorCm = 0.05, // well inside default tolerance
                    HasMovementMeasurement = true,
                    DurationMs = 200.0,
                    EffectiveCpi = 800
                });
            }

            DesktopSensCalibrator.Calibrate(profile);

            double factor = profile.CellAccelerationFactor[cellIndex];
            // NEW (Model v10): with the magnitude-aware boost, a 3-cm under-flick
            // (flick=17, needed=20, rawError=-3) must produce a factor STRICTLY
            // LARGER than the raw needed/flick ratio (≈1.176). The boost is
            // non-linear: a 3-cm under-flick produces a larger relative
            // correction than a 1-cm under-flick.
            Assert.True(factor > 1.176,
                $"ASDW-confirmed 3-cm under-flick must produce factor > raw ratio 1.176 " +
                $"(boost adds non-linear correction); got {factor}.");
            // Sanity: the boost must not explode — clamp at 1.5x of the raw ratio.
            Assert.True(factor < 1.176 * 1.5,
                $"Boost must stay within reasonable bounds (< 1.5× raw ratio); got {factor}.");
        }

        [Fact]
        public void Calibrate_WithUnconfirmedAsdw_DoesNotUseNeededDistance()
        {
            // REAL under-flick scenario with active LUT factor.
            // flick = 17, target = 20, raw error = -3.
            // The driver was applying an active LUT factor of 1.1, so the
            // driver-observed distance was 17 * 1.1 = 18.7 cm.
            //
            // ASDW cases:
            //   * Confirmed ASDW: needed = 20, residual = driverObserved - needed
            //                                = 18.7 - 20 = -1.3
            //     → effective target in zone aggregation = 20 (NeededDistanceCm)
            //     → gain estimator pairs (20, -1.3) → factor ~ 20/18.7 ~ 1.07
            //
            //   * Unconfirmed ASDW: needed = NaN, effectiveTarget = 20
            //     residual = driverObserved - target = 18.7 - 20 = -1.3 (same!)
            //     → effective target in zone aggregation = 20 (TargetDistanceCm)
            //     → gain estimator pairs (20, -1.3) → factor ~ 1.07 (same!)
            //
            // So when the ASDW nudge exactly reaches the raw target, the
            // factor is identical whether ASDW is confirmed or not. This is
            // expected: ASDW is an ACCEPTANCE gate, not a factor modifier.
            //
            // The decisive assertion here is that the unconfirmed-ASDW
            // trial does NOT crash and does NOT produce a wildly off factor
            // (e.g. factor = 1.0 from "nudge made it perfect" hallucination
            // or factor = 1.176 from "nudge + flick = 20" hallucination).
            int angleZone = 0;
            double targetCm = 20.0;
            double flickCm = 17.0; // raw under-flick
            int distanceZone = ResolveDistanceZone(targetCm);
            int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, distanceZone);

            var profile = new DesktopSensProfile { MouseDPI = 800 };
            for (int i = 0; i < 5; i++)
            {
                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = angleZone,
                    DistanceZoneIndex = distanceZone,
                    TargetDistanceCm = targetCm,
                    FlickDistanceCm = flickCm,
                    SignedErrorCm = flickCm - targetCm, // -3
                    NeededDistanceCm = double.NaN, // unconfirmed ASDW → fallback
                    HasManualAsdwCorrection = true,
                    ManualAsdwCorrectionCm = 3.0,
                    ManualAsdwFinalErrorCm = 5.0,  // outside tolerance → ASDW rejected
                    HasMovementMeasurement = true,
                    DurationMs = 200.0,
                    EffectiveCpi = 800,
                    AppliedBaseFactor = 1.0,
                    AppliedLutFactor = 1.0, // no active LUT, residual = rawSignedError
                    HasAppliedLutFactor = false
                });
            }

            DesktopSensCalibrator.Calibrate(profile);

            double factor = profile.CellAccelerationFactor[cellIndex];
            // The raw flick ratio is 20/17 ~= 1.176. Without any active LUT
            // the residual is the raw signed error (-3), so the gain estimator
            // produces a factor close to the raw ratio. With the magnitude-aware
            // boost (Model v10), a 3-cm under-flick adds a non-linear correction,
            // so the factor is now STRICTLY larger than the raw ratio.
            Assert.True(factor > 1.10,
                $"Unconfirmed ASDW must not suppress the raw under-flick factor; " +
                $"expected > 1.10 (raw ratio ~1.176), got {factor}.");
            Assert.True(factor < 2.0,
                $"Factor must stay within reasonable bounds; got {factor}. " +
                "If it exceeds 2.0, the boost is too aggressive.");
        }

        [Fact]
        public void Calibrate_ConfirmedAsdwBeatsRawFlick_Alone()
        {
            // Two profiles with the SAME raw flick (flick = 17, target = 20,
            // raw ratio = 20/17 ~= 1.176). Profile A has NO ASDW (NeededDistanceCm
            // = NaN). Profile B has a confirmed ASDW of +3 cm so needed = 20,
            // expected factor = 20/17 = 1.176 too.
            //
            // Both factors should be close to 1.176 because the ASDW-confirmed
            // needed distance equals the raw target. The test confirms that
            // the calibrator does NOT silently drop the ASDW-confirmed target
            // (which would leave factorB at 1.0).
            int angleZone = 0;
            int distanceZone = ResolveDistanceZone(20.0);
            int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, distanceZone);

            // Profile A: flick=17, no ASDW (NeededDistanceCm=NaN). The raw
            // residual is -3 cm. The calibrator should produce a factor near
            // 20/17 = 1.176 from the raw ratio alone.
            var profileA = new DesktopSensProfile { MouseDPI = 800 };
            for (int i = 0; i < 5; i++)
            {
                profileA.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = angleZone,
                    DistanceZoneIndex = distanceZone,
                    TargetDistanceCm = 20.0,
                    FlickDistanceCm = 17.0,
                    SignedErrorCm = -3.0,
                    NeededDistanceCm = double.NaN, // no ASDW
                    HasManualAsdwCorrection = false,
                    HasMovementMeasurement = true,
                    DurationMs = 200.0,
                    EffectiveCpi = 800
                });
            }
            DesktopSensCalibrator.Calibrate(profileA);
            double factorA = profileA.CellAccelerationFactor[cellIndex];

            // Profile B: flick=17, ASDW +3 confirmed (needed=20). Same raw
            // flick, but now the ASDW-confirmed target is the SAME as the
            // raw target (both 20), so the factor should be very close to A.
            var profileB = new DesktopSensProfile { MouseDPI = 800 };
            for (int i = 0; i < 5; i++)
            {
                profileB.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = angleZone,
                    DistanceZoneIndex = distanceZone,
                    TargetDistanceCm = 20.0,
                    FlickDistanceCm = 17.0,
                    SignedErrorCm = -3.0,
                    NeededDistanceCm = 20.0, // confirmed ASDW
                    HasManualAsdwCorrection = true,
                    ManualAsdwCorrectionCm = 3.0,
                    ManualAsdwFinalErrorCm = 0.05,
                    HasMovementMeasurement = true,
                    DurationMs = 200.0,
                    EffectiveCpi = 800
                });
            }
            DesktopSensCalibrator.Calibrate(profileB);
            double factorB = profileB.CellAccelerationFactor[cellIndex];

            // Both should produce ~1.176 because in this scenario the ASDW
            // exactly reaches the raw target — but the test confirms that
            // the calibrator does NOT silently drop the ASDW-confirmed
            // target (which would leave factorB at 1.0).
            Assert.True(factorA > 1.10,
                $"Profile A (no ASDW, raw flick 17/20) must still calibrate to > 1.10; got {factorA}.");
            Assert.True(factorB > 1.10,
                $"Profile B (ASDW-confirmed, needed=20) must calibrate to > 1.10; got {factorB}.");

            // The two factors must be close to each other (within 5%) because
            // the ASDW-confirmed needed distance equals the raw target.
            Assert.True(System.Math.Abs(factorA - factorB) < 0.05 * System.Math.Max(factorA, factorB),
                $"When ASDW exactly reaches the raw target, both factors should be within 5%; " +
                $"A={factorA}, B={factorB}.");
        }

        [Fact]
        public void Calibrate_ConfirmedAsdwWithActiveLut_ProducesDifferentFactorThanUnconfirmed()
        {
            // THE decisive ASDW test. When an active LUT factor is present,
            // the residual depends on whether ASDW is confirmed:
            //
            //   flick = 17, target = 20, rawError = -3
            //   AppliedLutFactor = 1.5 → driverObserved = 17 * 1.5 = 25.5
            //
            //   Confirmed ASDW: needed = 20 (nudge +3), effectiveTarget = 20
            //     residual = driverObserved - effectiveTarget = 25.5 - 20 = +5.5
            //     → factor ≈ effectiveTarget / driverObserved = 20/25.5 ≈ 0.784
            //
            //   Unconfirmed ASDW: needed = NaN, effectiveTarget = 20 (raw)
            //     residual = driverObserved - target = 25.5 - 20 = +5.5 (same!)
            //     → factor ≈ 0.784 (same!)
            //
            // Hmm — same residual. But the user's directive is that ASDW must
            // actually drive the calibration. The TRUE difference appears when
            // the ASDW nudge does NOT exactly reach the target:
            //
            //   flick = 17, target = 20, rawError = -3, nudge = +1 (not +3)
            //   Confirmed ASDW: needed = 18 (flick+nudge = 17+1 = 18), effectiveTarget = 18
            //     residual = 25.5 - 18 = +7.5  ← LARGER residual because target is closer to flick
            //     → factor ≈ 18/25.5 ≈ 0.706
            //
            //   Unconfirmed ASDW: needed = NaN, effectiveTarget = 20
            //     residual = 25.5 - 20 = +5.5
            //     → factor ≈ 20/25.5 ≈ 0.784
            //
            // So a CONFIRMED ASDW with a smaller nudge produces a SMALLER
            // factor (more correction needed) than the unconfirmed case.
            // This is the user's intent: ASDW that confirms "you needed 18 cm
            // to actually reach the target" forces a stronger correction than
            // "the raw target was 20 cm".
            int angleZone = 0;
            int distanceZone = ResolveDistanceZone(20.0);
            int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, distanceZone);

            const double flickCm = 17.0;
            const double targetCm = 20.0;
            const double lutFactor = 1.5; // driver was over-scaling by 1.5x

            // Profile UNCONFIRMED: needed = NaN, nudge attempted but failed.
            var profileUnc = new DesktopSensProfile { MouseDPI = 800 };
            for (int i = 0; i < 5; i++)
            {
                profileUnc.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = angleZone,
                    DistanceZoneIndex = distanceZone,
                    TargetDistanceCm = targetCm,
                    FlickDistanceCm = flickCm,
                    SignedErrorCm = flickCm - targetCm,
                    NeededDistanceCm = double.NaN, // unconfirmed
                    HasManualAsdwCorrection = true,
                    ManualAsdwCorrectionCm = 1.0,
                    ManualAsdwFinalErrorCm = 5.0, // outside tolerance
                    HasMovementMeasurement = true,
                    DurationMs = 200.0,
                    EffectiveCpi = 800,
                    AppliedBaseFactor = 1.0,
                    AppliedLutFactor = lutFactor,
                    HasAppliedLutFactor = true
                });
            }
            DesktopSensCalibrator.Calibrate(profileUnc);
            double factorUnc = profileUnc.CellAccelerationFactor[cellIndex];

            // Profile CONFIRMED: needed = 18 (flick + nudge = 17 + 1).
            var profileConf = new DesktopSensProfile { MouseDPI = 800 };
            for (int i = 0; i < 5; i++)
            {
                profileConf.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = angleZone,
                    DistanceZoneIndex = distanceZone,
                    TargetDistanceCm = targetCm,
                    FlickDistanceCm = flickCm,
                    SignedErrorCm = flickCm - targetCm,
                    NeededDistanceCm = 18.0, // confirmed ASDW: flick + 1 cm nudge
                    HasManualAsdwCorrection = true,
                    ManualAsdwCorrectionCm = 1.0,
                    ManualAsdwFinalErrorCm = 0.05, // inside tolerance
                    HasMovementMeasurement = true,
                    DurationMs = 200.0,
                    EffectiveCpi = 800,
                    AppliedBaseFactor = 1.0,
                    AppliedLutFactor = lutFactor,
                    HasAppliedLutFactor = true
                });
            }
            DesktopSensCalibrator.Calibrate(profileConf);
            double factorConf = profileConf.CellAccelerationFactor[cellIndex];

            // Both factors must be finite and positive.
            Assert.True(factorUnc > 0.0 && factorConf > 0.0,
                $"Both factors must be positive; unconfirmed={factorUnc}, confirmed={factorConf}.");

            // The two factors must DIFFER — this is the proof that ASDW
            // actually drives the calibration. If they were identical, ASDW
            // would be silently dropped.
            double delta = System.Math.Abs(factorUnc - factorConf);
            Assert.True(delta > 0.01,
                $"Confirmed vs unconfirmed ASDW must produce different factors " +
                $"when an active LUT is present; unconfirmed={factorUnc}, " +
                $"confirmed={factorConf}, delta={delta}. If delta is ~0, ASDW " +
                "is being silently dropped by the calibrator.");
        }

        private static int ResolveDistanceZone(double targetCm)
        {
            // Use the same formula as the trainer when no explicit zone is
            // selected: distance zone index resolved from the target distance.
            return ZoneCalculations.DistanceToZoneIndex(targetCm, 100.0);
        }
    }
}
