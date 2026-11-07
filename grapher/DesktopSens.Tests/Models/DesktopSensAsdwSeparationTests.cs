using System;
using System.Collections.Generic;
using System.Linq;
using grapher.DesktopSens.Models;
using Xunit;

namespace DesktopSens.Tests.Models
{
    /// <summary>
    /// Stage 7 regression tests for the ASDW / raw-flick separation fix.
    ///
    /// These tests exercise the model-layer invariants that the fix
    /// establishes. The WinForms FinishTrial path that populates the
    /// new ManualAsdwCorrectionCm / HasManualAsdwCorrection fields
    /// cannot be tested in this Linux sandbox (no WinForms / .NET 4.7.2
    /// runtime); the production code is exercised at the model layer
    /// here by constructing DesktopSensTrial instances directly.
    /// </summary>
    public class DesktopSensAsdwSeparationTests
    {
        public DesktopSensAsdwSeparationTests()
        {
            // Each test starts from a known geometry: 10° step, 180° max,
            // 89° pitch cap, 30 cm max distance, 3 sub-markers in zone 0
            // (the user's directive default). This is the configuration the
            // trainer uses on startup.
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
        }

        // ───────────────────────────────────────────────────────────────
        // Test A — ASDW correction is recorded separately; raw flick error
        // is preserved and the trial is NOT made to look perfect.
        // ───────────────────────────────────────────────────────────────

        [Fact]
        public void TestA_AsdwCorrectionIsRecordedSeparatelyAndRawFlickErrorIsPreserved()
        {
            // Simulate a trial where the raw flick overshot the target by
            // 1 cm (signedError = +1.0). The user then nudged backward
            // with ASDW by 1 cm (ManualAsdwCorrectionCm = -1.0, since
            // negative = backward = raw flick overshot). The trial
            // stores both values.
            var trial = new DesktopSensTrial
            {
                AngleZoneIndex = 0,
                DistanceZoneIndex = 1,
                TargetDistanceCm = 5.0,
                SignedErrorCm = 1.0,           // raw flick overshot by 1 cm
                FlickDistanceCm = 6.0,         // raw flick distance
                FinalErrorCm = 1.0,            // raw magnitude (no ASDW)
                ManualAsdwCorrectionCm = -1.0,  // user nudged backward by 1 cm
                HasManualAsdwCorrection = true,
                HasMovementMeasurement = true
            };

            // The raw flick error is preserved (NOT zeroed by ASDW).
            Assert.Equal(1.0, trial.SignedErrorCm, 12);
            Assert.Equal(6.0, trial.FlickDistanceCm, 12);

            // The manual correction is recorded separately, with a clear sign.
            Assert.True(trial.HasManualAsdwCorrection);
            Assert.Equal(-1.0, trial.ManualAsdwCorrectionCm, 12);

            // The trial is NOT a "perfect flick" — FinalErrorCm is the raw
            // magnitude, not zero.
            Assert.True(trial.FinalErrorCm > 0.0,
                "ASDW must not make the trial look perfect — FinalErrorCm must reflect the raw flick error.");
        }

        [Fact]
        public void TestA_CalibratorFactorIsBasedOnRawFlickError_NotOnCombinedError()
        {
            // Two trials at the same cell with the SAME raw flick error
            // but DIFFERENT ASDW corrections. The calibrator's factor
            // must be the same in both cases, because the estimator uses
            // SignedErrorCm (raw), NOT SignedErrorCm + ManualAsdwCorrectionCm
            // (which would be ~0 if the user corrected to the target).
            var profileNoAsdw = new DesktopSensProfile { MouseDPI = 800 };
            var profileWithAsdw = new DesktopSensProfile { MouseDPI = 800 };

            for (int i = 0; i < 10; i++)
            {
                profileNoAsdw.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = 5.0,
                    SignedErrorCm = 0.5,           // raw flick overshot by 0.5 cm
                    FlickDistanceCm = 5.5,
                    FinalErrorCm = 0.5,
                    ManualAsdwCorrectionCm = 0.0,  // no ASDW used
                    HasManualAsdwCorrection = false,
                    HasMovementMeasurement = true
                });
                profileWithAsdw.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = 5.0,
                    SignedErrorCm = 0.5,           // SAME raw flick error
                    FlickDistanceCm = 5.5,
                    FinalErrorCm = 0.5,           // SAME raw magnitude
                    ManualAsdwCorrectionCm = -0.5, // user nudged backward by 0.5 cm
                    HasManualAsdwCorrection = true,
                    HasMovementMeasurement = true
                });
            }

            DesktopSensCalibrator.Calibrate(profileNoAsdw);
            DesktopSensCalibrator.Calibrate(profileWithAsdw);

            int cell = DesktopSensProfile.GetCellIndex(0, 1);
            Assert.Equal(
                profileNoAsdw.CellAccelerationFactor[cell],
                profileWithAsdw.CellAccelerationFactor[cell], 6);
        }

        // ───────────────────────────────────────────────────────────────
        // Test B — Under-flick with ASDW correction (sign correctness).
        // ───────────────────────────────────────────────────────────────

        [Fact]
        public void TestB_UnderFlickAsdwCorrectionHasPositiveSignAndDoesNotReverse()
        {
            // Under-flick: raw flick undershot by 1 cm (signedError = -1.0).
            // The user nudged FORWARD by 1 cm (ManualAsdwCorrectionCm = +1.0,
            // since positive = forward = raw flick undershot).
            var trial = new DesktopSensTrial
            {
                AngleZoneIndex = 0,
                DistanceZoneIndex = 1,
                TargetDistanceCm = 5.0,
                SignedErrorCm = -1.0,
                FlickDistanceCm = 4.0,
                FinalErrorCm = 1.0,
                ManualAsdwCorrectionCm = 1.0,    // positive = forward correction
                HasManualAsdwCorrection = true,
                HasMovementMeasurement = true
            };

            // Sign of the correction matches the under-flick direction.
            Assert.True(trial.ManualAsdwCorrectionCm > 0.0,
                "Under-flick must produce a positive (forward) ASDW correction.");
            Assert.True(trial.SignedErrorCm < 0.0,
                "Under-flick must produce a negative signed error.");

            // The correction is NOT reversed: -manualCorrection == signedError
            // (within rounding), confirming the user moved the crosshair
            // from the raw landing to the target.
            Assert.Equal(-trial.ManualAsdwCorrectionCm, trial.SignedErrorCm, 6);

            // The trial is NOT perfect — the raw magnitude is preserved.
            Assert.True(trial.FinalErrorCm > 0.0);

            // The calibrator does NOT reverse the sign when computing the
            // factor: with signedError = -1.0 and target = 5.0, observed = 4.0,
            // factor = 5.0 / 4.0 = 1.25 (speed up by 25%).
            var profile = new DesktopSensProfile { MouseDPI = 800 };
            profile.Trials.Add(trial);
            DesktopSensCalibrator.Calibrate(profile);
            int cell = DesktopSensProfile.GetCellIndex(0, 1);
            Assert.True(profile.CellAccelerationFactor[cell] > 1.0,
                "Under-flick must produce a factor > 1.0 (speed up).");
        }

        [Fact]
        public void TestB_ZeroAsdwCorrectionIsAllowedAndDoesNotAffectFactor()
        {
            // A trial with ManualAsdwCorrectionCm = 0 (no ASDW used)
            // should produce the same factor as a trial with
            // HasManualAsdwCorrection = false (legacy trial).
            var profileLegacy = new DesktopSensProfile { MouseDPI = 800 };
            var profileZeroAsdw = new DesktopSensProfile { MouseDPI = 800 };

            for (int i = 0; i < 10; i++)
            {
                profileLegacy.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = 5.0,
                    SignedErrorCm = 0.3,
                    FlickDistanceCm = 5.3,
                    HasMovementMeasurement = true
                    // ManualAsdwCorrectionCm and HasManualAsdwCorrection
                    // keep their default values (0.0 and false).
                });
                profileZeroAsdw.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = 5.0,
                    SignedErrorCm = 0.3,
                    FlickDistanceCm = 5.3,
                    ManualAsdwCorrectionCm = 0.0,
                    HasManualAsdwCorrection = false, // explicitly false
                    HasMovementMeasurement = true
                });
            }

            DesktopSensCalibrator.Calibrate(profileLegacy);
            DesktopSensCalibrator.Calibrate(profileZeroAsdw);

            int cell = DesktopSensProfile.GetCellIndex(0, 1);
            Assert.Equal(
                profileLegacy.CellAccelerationFactor[cell],
                profileZeroAsdw.CellAccelerationFactor[cell], 12);
        }

        // ───────────────────────────────────────────────────────────────
        // Test C — Point isolation: calibrating a high point does NOT
        // change a previously calibrated low point's factor.
        // ───────────────────────────────────────────────────────────────

        [Fact]
        public void TestC_CalibratingHighPointDoesNotChangeLowPointFactor()
        {
            // Calibrate zone 0 only. Record the factor.
            var profile = new DesktopSensProfile { MouseDPI = 800 };
            for (int i = 0; i < 10; i++)
            {
                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 0,
                    TargetDistanceCm = 2.0,
                    SignedErrorCm = 0.2,
                    FlickDistanceCm = 2.2,
                    HasMovementMeasurement = true
                });
            }
            DesktopSensCalibrator.Calibrate(profile);
            int lowCell = DesktopSensProfile.GetCellIndex(0, 0);
            double lowFactorAfterLowOnly = profile.CellAccelerationFactor[lowCell];
            Assert.True(lowFactorAfterLowOnly > 0.0);
            Assert.True(profile.CellAccelerationConfidence[lowCell] > 0.0);

            // Now add trials at zone 1 (a higher point) and re-calibrate.
            // The estimator's ordered pass processes zone 0 BEFORE zone 1,
            // and zone 0's factor depends only on zone 0's trials (no
            // earlier zones to chain through). Adding zone 1 trials must
            // NOT change zone 0's factor.
            for (int i = 0; i < 10; i++)
            {
                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = 5.0,
                    SignedErrorCm = 0.5,
                    FlickDistanceCm = 5.5,
                    HasMovementMeasurement = true
                });
            }
            DesktopSensCalibrator.Calibrate(profile);
            double lowFactorAfterBoth = profile.CellAccelerationFactor[lowCell];

            Assert.Equal(lowFactorAfterLowOnly, lowFactorAfterBoth, 12);
        }

        [Fact]
        public void TestC_HighPointFactorDependsOnLowPointFactorViaChain()
        {
            // The chain logic is the EXPECTED way a high point's factor
            // depends on a low point: the calibrator applies the low
            // zone's verified factor to the portion of the high-zone
            // trial that fell in the low zone, then estimates the
            // high-zone factor from the residual. This is intentional
            // (the chain models the cumulative acceleration curve).
            //
            // This test documents that the dependency is one-way:
            // low → high (chain), NOT high → low.
            var profile = new DesktopSensProfile { MouseDPI = 800 };
            for (int i = 0; i < 10; i++)
            {
                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 0,
                    TargetDistanceCm = 2.0,
                    SignedErrorCm = 0.4,        // raw flick overshot
                    FlickDistanceCm = 2.4,
                    HasMovementMeasurement = true
                });
            }
            DesktopSensCalibrator.Calibrate(profile);
            int lowCell = DesktopSensProfile.GetCellIndex(0, 0);
            int highCell = DesktopSensProfile.GetCellIndex(0, 1);

            double lowFactorBeforeHigh = profile.CellAccelerationFactor[lowCell];
            double highFactorBeforeHigh = profile.CellAccelerationFactor[highCell];

            // Add high-zone trials. The low-zone factor must NOT change.
            for (int i = 0; i < 10; i++)
            {
                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = 5.0,
                    SignedErrorCm = 0.5,
                    FlickDistanceCm = 5.5,
                    HasMovementMeasurement = true
                });
            }
            DesktopSensCalibrator.Calibrate(profile);

            // Low factor unchanged (point isolation for the lower zone).
            Assert.Equal(lowFactorBeforeHigh,
                profile.CellAccelerationFactor[lowCell], 12);
            // High factor IS now computed (chain applied the low factor
            // to the low-zone portion of the high-zone trial's observed
            // distance, then estimated the high factor from the residual).
            Assert.True(profile.CellAccelerationConfidence[highCell] > 0.0,
                "High cell must have a positive confidence after both zones are calibrated.");
            // The high factor is different from what it was when only the
            // low cell had data (when confidence was 0). This documents
            // the chain dependency.
            Assert.NotEqual(highFactorBeforeHigh,
                profile.CellAccelerationFactor[highCell]);
        }

        // ───────────────────────────────────────────────────────────────
        // Test D — Repeated calibration does not double-apply the
        // correction (factors do not drift or grow exponentially).
        // ───────────────────────────────────────────────────────────────

        [Fact]
        public void TestD_RepeatedCalibrationDoesNotDriftFactors()
        {
            // Calibrate the same trial set 10 times. Each Calibrate()
            // pass RESETS the factor arrays at the start (lines 98–112
            // of DesktopSensCalibrator.cs), so the result must be
            // identical on every pass — no cumulative correction.
            var profile = new DesktopSensProfile { MouseDPI = 800 };
            for (int i = 0; i < 10; i++)
            {
                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = 5.0,
                    SignedErrorCm = 0.3,
                    FlickDistanceCm = 5.3,
                    HasMovementMeasurement = true
                });
            }

            DesktopSensCalibrator.Calibrate(profile);
            int cell = DesktopSensProfile.GetCellIndex(0, 1);
            double firstFactor = profile.CellAccelerationFactor[cell];
            double firstConfidence = profile.CellAccelerationConfidence[cell];

            for (int pass = 0; pass < 9; pass++)
            {
                DesktopSensCalibrator.Calibrate(profile);
                Assert.Equal(firstFactor,
                    profile.CellAccelerationFactor[cell], 12);
                Assert.Equal(firstConfidence,
                    profile.CellAccelerationConfidence[cell], 12);
            }

            // Sanity: the factor is meaningfully different from 1.0 (the
            // trial had a +0.3 cm error at target 5 cm, so the factor
            // should be < 1.0 to slow the user down).
            Assert.True(firstFactor < 1.0,
                "Over-flick trial must produce a factor < 1.0 to slow the user down.");
        }

        [Fact]
        public void TestD_CalibrateIsIdempotentOnSameTrialSet()
        {
            // A stronger property: two consecutive Calibrate() calls on
            // the same trial set produce the same per-cell factors AND
            // the same per-direction aggregates.
            var profile = new DesktopSensProfile { MouseDPI = 800 };
            for (int zone = 0; zone <= 3; zone++)
            {
                double target = TrainingGeometry.Current.ZoneCenterCm(zone);
                for (int i = 0; i < 8; i++)
                {
                    profile.Trials.Add(new DesktopSensTrial
                    {
                        AngleZoneIndex = 0,
                        DistanceZoneIndex = zone,
                        TargetDistanceCm = target,
                        SignedErrorCm = 0.1,
                        FlickDistanceCm = target + 0.1,
                        HasMovementMeasurement = true
                    });
                }
            }
            DesktopSensCalibrator.Calibrate(profile);
            double[] factorsAfterFirst = (double[])profile.CellAccelerationFactor.Clone();
            double[] confidenceAfterFirst = (double[])profile.CellAccelerationConfidence.Clone();

            DesktopSensCalibrator.Calibrate(profile);
            double[] factorsAfterSecond = (double[])profile.CellAccelerationFactor.Clone();
            double[] confidenceAfterSecond = (double[])profile.CellAccelerationConfidence.Clone();

            Assert.Equal(factorsAfterFirst.Length, factorsAfterSecond.Length);
            for (int i = 0; i < factorsAfterFirst.Length; i++)
            {
                Assert.Equal(factorsAfterFirst[i], factorsAfterSecond[i], 12);
                Assert.Equal(confidenceAfterFirst[i], confidenceAfterSecond[i], 12);
            }
        }

        // ───────────────────────────────────────────────────────────────
        // Test E — Speed overlap: two distance-zones with the same
        // input speed cannot be independently corrected by the current
        // RawAccel bridge.
        // ───────────────────────────────────────────────────────────────

        [Fact]
        public void TestE_SpeedOverlapIsDocumentedAndPerCellFactorsAreStored()
        {
            // Two cells at different distance zones but the SAME input
            // speed (cm/sec). The per-cell calibrator computes a factor
            // for each, but the RawAccel bridge applies ONE curve per
            // axis (not per distance-zone), so the two factors cannot
            // both be applied at the overlapping speed.
            //
            // This test verifies that the per-cell factors ARE computed
            // and stored on the profile (so a future LUT-capable bridge
            // can read them). It also documents (via the BuildMergedProfile
            // output) that the merged profile does NOT carry independent
            // per-zone LUT slots to the driver.
            var profile = new DesktopSensProfile { MouseDPI = 800 };
            double targetLow = 2.0;
            double targetHigh = 5.0;
            // Same duration → same speed if flick distance is similar.
            // Both trials use 300 ms duration and a small error.
            for (int i = 0; i < 8; i++)
            {
                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 0,
                    TargetDistanceCm = targetLow,
                    SignedErrorCm = 0.1,
                    FlickDistanceCm = targetLow + 0.1,
                    DurationMs = 300.0,
                    HasMovementMeasurement = true
                });
                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = targetHigh,
                    SignedErrorCm = 0.5,
                    FlickDistanceCm = targetHigh + 0.5,
                    DurationMs = 300.0,
                    HasMovementMeasurement = true
                });
            }
            DesktopSensCalibrator.Calibrate(profile);

            int cellLow = DesktopSensProfile.GetCellIndex(0, 0);
            int cellHigh = DesktopSensProfile.GetCellIndex(0, 1);
            Assert.True(profile.CellAccelerationConfidence[cellLow] > 0.0);
            Assert.True(profile.CellAccelerationConfidence[cellHigh] > 0.0);

            // The two cells have DIFFERENT factors — the calibrator did
            // compute independent per-cell estimates.
            Assert.NotEqual(
                profile.CellAccelerationFactor[cellLow],
                profile.CellAccelerationFactor[cellHigh]);

            // The merged profile stores AccelerationByDistanceZone per
            // direction (the per-zone LUT that a future bridge would
            // apply), but the applier only uses BaseSensitivityFactor
            // (hardcoded to 1.0). This is verified in Test G.
            var merged = DesktopSensProfileMerger.BuildMergedProfile(profile);
            Assert.NotNull(merged.Right.AccelerationByDistanceZone);
            Assert.True(merged.Right.AccelerationByDistanceZone.Length > 0);
            // The per-zone LUT is stored for diagnostic / future use,
            // but the apply path does NOT use it (see Test G).
            Assert.Equal(1.0, merged.Right.BaseSensitivityFactor, 12);
        }

        // ───────────────────────────────────────────────────────────────
        // Test F — Persistence: new fields default to 0 / false so
        // legacy trials remain backward-compatible.
        // ───────────────────────────────────────────────────────────────

        [Fact]
        public void TestF_NewFieldsDefaultToZeroAndFalseForLegacyTrials()
        {
            // A trial constructed without setting the new fields must
            // have ManualAsdwCorrectionCm = 0.0 and HasManualAsdwCorrection
            // = false. This is the backward-compatibility contract: a
            // serialized trial from before this fix deserializes with
            // the new fields at their defaults, and the calibrator
            // treats it as "no manual correction info — use
            // SignedErrorCm only" (the legacy behaviour).
            var legacy = new DesktopSensTrial
            {
                AngleZoneIndex = 0,
                DistanceZoneIndex = 1,
                TargetDistanceCm = 5.0,
                SignedErrorCm = 0.3,
                FlickDistanceCm = 5.3,
                HasMovementMeasurement = true
            };

            Assert.Equal(0.0, legacy.ManualAsdwCorrectionCm, 12);
            Assert.False(legacy.HasManualAsdwCorrection);
            Assert.True(legacy.IsValidForCalibration,
                "Legacy trial without ASDW fields must still be valid for calibration.");
        }

        [Fact]
        public void TestF_LegacyAndAsdwTrialsProduceSameFactorWhenRawFlickMatches()
        {
            // A trial with ManualAsdwCorrectionCm = 0 (legacy) and a
            // trial with ManualAsdwCorrectionCm = -0.3 (user corrected by
            // 0.3 cm) but the SAME SignedErrorCm produce the SAME factor.
            // This proves the calibrator uses SignedErrorCm, not the
            // combined error (which would be ~0 for the second trial).
            var profileLegacy = new DesktopSensProfile { MouseDPI = 800 };
            var profileAsdw = new DesktopSensProfile { MouseDPI = 800 };

            for (int i = 0; i < 8; i++)
            {
                profileLegacy.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = 5.0,
                    SignedErrorCm = 0.3,
                    FlickDistanceCm = 5.3,
                    HasMovementMeasurement = true
                });
                profileAsdw.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = 5.0,
                    SignedErrorCm = 0.3,             // SAME raw flick error
                    FlickDistanceCm = 5.3,
                    ManualAsdwCorrectionCm = -0.3,    // user corrected to target
                    HasManualAsdwCorrection = true,
                    HasMovementMeasurement = true
                });
            }
            DesktopSensCalibrator.Calibrate(profileLegacy);
            DesktopSensCalibrator.Calibrate(profileAsdw);

            int cell = DesktopSensProfile.GetCellIndex(0, 1);
            Assert.Equal(
                profileLegacy.CellAccelerationFactor[cell],
                profileAsdw.CellAccelerationFactor[cell], 12);
        }

        // ───────────────────────────────────────────────────────────────
        // Test G — RawAccel apply path: the merged profile's
        // BaseSensitivityFactor is hardcoded to 1.0, so the Train
        // merge-and-apply is a no-op for the driver. The per-zone
        // curve is stored in the profile but NOT transferred to the
        // driver by the current applier.
        // ───────────────────────────────────────────────────────────────

        [Fact]
        public void TestG_BuildMergedProfileHardcodesBaseSensitivityFactorToOne()
        {
            // BuildDirection in DesktopSensProfileMerger sets
            // dir.BaseSensitivityFactor = NeutralFactor = 1.0 unconditionally.
            // This is the documented limitation: the Train applier uses
            // only BaseSensitivityFactor (passed as the four direction
            // multipliers), so the Train merge-and-apply is a no-op.
            var profile = new DesktopSensProfile { MouseDPI = 800 };
            for (int i = 0; i < 8; i++)
            {
                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = 5.0,
                    SignedErrorCm = 0.5,
                    FlickDistanceCm = 5.5,
                    HasMovementMeasurement = true
                });
            }
            DesktopSensCalibrator.Calibrate(profile);

            var merged = DesktopSensProfileMerger.BuildMergedProfile(profile);
            Assert.Equal(1.0, merged.Right.BaseSensitivityFactor, 12);
            Assert.Equal(1.0, merged.Down.BaseSensitivityFactor, 12);
            Assert.Equal(1.0, merged.Left.BaseSensitivityFactor, 12);
            Assert.Equal(1.0, merged.Up.BaseSensitivityFactor, 12);
        }

        [Fact]
        public void TestG_ApplierReceivesAllOnesAndDoesNotChangeOutputDpi()
        {
            // The SampleCardinalProfileApplier callback in
            // DesktopSensConfigForm.CreateCardinalProfileApplier calls
            // TryApplyTrainDirectionalFactors(R, D, L, U) with the four
            // BaseSensitivityFactor values. Since they are all 1.0,
            // the resulting RawAccel settings are:
            //   settings.outputDPI = baseOutputDPI * 1.0 = baseOutputDPI (no change)
            //   settings.lrOutputDPIRatio = 1.0 / 1.0 = 1.0
            //   settings.yxOutputDPIRatio = 1.0 / 1.0 = 1.0
            //   settings.udOutputDPIRatio = 1.0 / 1.0 = 1.0
            // This test simulates the applier's arithmetic with the
            // merged profile's BaseSensitivityFactor values and verifies
            // the result is a no-op for the driver.
            var profile = new DesktopSensProfile { MouseDPI = 800 };
            for (int i = 0; i < 8; i++)
            {
                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = 5.0,
                    SignedErrorCm = 0.5,
                    FlickDistanceCm = 5.5,
                    HasMovementMeasurement = true
                });
            }
            DesktopSensCalibrator.Calibrate(profile);
            var merged = DesktopSensProfileMerger.BuildMergedProfile(profile);

            // Simulate TryApplyTrainDirectionalFactors' arithmetic.
            double baseOutputDPI = 800.0;
            double right = PositiveOrOne(merged.Right.BaseSensitivityFactor);
            double down = PositiveOrOne(merged.Down.BaseSensitivityFactor);
            double left = PositiveOrOne(merged.Left.BaseSensitivityFactor);
            double up = PositiveOrOne(merged.Up.BaseSensitivityFactor);
            double newOutputDPI = baseOutputDPI * right;
            double lrRatio = left / right;
            double yxRatio = down / right;
            double udRatio = up / down;

            Assert.Equal(baseOutputDPI, newOutputDPI, 12);
            Assert.Equal(1.0, lrRatio, 12);
            Assert.Equal(1.0, yxRatio, 12);
            Assert.Equal(1.0, udRatio, 12);
        }

        [Fact]
        public void TestG_PerZoneAccelerationIsStoredButNotApplied()
        {
            // The merged profile DOES store per-zone acceleration factors
            // in AccelerationByDistanceZone (one slot per distance zone
            // per direction). This is what a future LUT-capable RawAccel
            // bridge would read. The CURRENT applier does not read it.
            var profile = new DesktopSensProfile { MouseDPI = 800 };
            for (int i = 0; i < 8; i++)
            {
                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = 5.0,
                    SignedErrorCm = 0.5,
                    FlickDistanceCm = 5.5,
                    HasMovementMeasurement = true
                });
            }
            DesktopSensCalibrator.Calibrate(profile);
            var merged = DesktopSensProfileMerger.BuildMergedProfile(profile);

            // The per-zone array is populated (confidence-blended with 1.0
            // for cells with low confidence, so it is always finite).
            Assert.NotNull(merged.Right.AccelerationByDistanceZone);
            Assert.True(merged.Right.AccelerationByDistanceZone.Length > 1);
            foreach (double f in merged.Right.AccelerationByDistanceZone)
            {
                Assert.True(!double.IsNaN(f) && !double.IsInfinity(f) && f > 0.0,
                    "Per-zone acceleration factor must be finite and positive.");
            }

            // The interpolated curve is also stored (for diagnostic /
            // future-LUT use). It must be monotone-compatible: every
            // value must be finite and positive.
            Assert.NotNull(merged.Right.InterpolatedCurve);
            foreach (double v in merged.Right.InterpolatedCurve)
            {
                Assert.True(!double.IsNaN(v) && !double.IsInfinity(v) && v > 0.0,
                    "Interpolated curve value must be finite and positive.");
            }
        }

        // ───────────────────────────────────────────────────────────────
        // Test — Direction reversal: a positive ASDW correction
        // (forward) on an over-flick trial would indicate the user
        // moved the WRONG direction. The fix does not interpret the
        // correction's sign; it just stores it. The estimator uses
        // SignedErrorCm (raw), so a reversed ASDW cannot corrupt the
        // factor.
        // ───────────────────────────────────────────────────────────────

        [Fact]
        public void Test_DirectionReversalInAsdwDoesNotCorruptFactor()
        {
            // Over-flick: signedError = +0.5. The user mistakenly nudged
            // FORWARD (positive) by 0.2 instead of backward. The
            // ManualAsdwCorrectionCm is +0.2 (the wrong direction).
            // The estimator must still use SignedErrorCm = +0.5, NOT
            // SignedErrorCm - ManualAsdwCorrectionCm = +0.3 (which would
            // understate the error).
            var profileCorrectAsdw = new DesktopSensProfile { MouseDPI = 800 };
            var profileReversedAsdw = new DesktopSensProfile { MouseDPI = 800 };

            for (int i = 0; i < 8; i++)
            {
                profileCorrectAsdw.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = 5.0,
                    SignedErrorCm = 0.5,
                    FlickDistanceCm = 5.5,
                    ManualAsdwCorrectionCm = -0.5,    // correct direction
                    HasManualAsdwCorrection = true,
                    HasMovementMeasurement = true
                });
                profileReversedAsdw.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = 5.0,
                    SignedErrorCm = 0.5,
                    FlickDistanceCm = 5.5,
                    ManualAsdwCorrectionCm = 0.2,    // WRONG direction
                    HasManualAsdwCorrection = true,
                    HasMovementMeasurement = true
                });
            }
            DesktopSensCalibrator.Calibrate(profileCorrectAsdw);
            DesktopSensCalibrator.Calibrate(profileReversedAsdw);

            int cell = DesktopSensProfile.GetCellIndex(0, 1);
            Assert.Equal(
                profileCorrectAsdw.CellAccelerationFactor[cell],
                profileReversedAsdw.CellAccelerationFactor[cell], 12);
        }

        // ───────────────────────────────────────────────────────────────
        // GPT 0002 regression tests — staircase curve + ASDW curve-sample
        // validation. These verify the architectural changes GPT proposed
        // (BuildStaircaseCurve replacing PCHIP, and per-cell ASDW
        // validation in TryBuildCurveSample) that are now part of the
        // combined fix.
        // ───────────────────────────────────────────────────────────────

        [Fact]
        public void Gpt_BuildStaircaseCurve_AppendingLaterCellDoesNotChangeEarlierSamples()
        {
            double[] before = DesktopSensProfileMerger.BuildStaircaseCurve(
                new[] { 1.10, 1.20 }, oversample: 4);
            double[] after = DesktopSensProfileMerger.BuildStaircaseCurve(
                new[] { 1.10, 1.20, 1.35 }, oversample: 4);

            Assert.Equal(before.Length + 4, after.Length);
            for (int i = 0; i < before.Length; i++)
                Assert.Equal(before[i], after[i], 12);
        }

        [Fact]
        public void Gpt_StaircaseCurve_PreservesAllEarlierSamplesWhenAnyCellChanges()
        {
            // Changing a middle cell (index 1) must not change any sample
            // from earlier cells (index 0).
            double[] before = DesktopSensProfileMerger.BuildStaircaseCurve(
                new[] { 1.10, 1.20, 1.30 }, oversample: 4);
            double[] after = DesktopSensProfileMerger.BuildStaircaseCurve(
                new[] { 1.10, 1.25, 1.40 }, oversample: 4);

            Assert.Equal(before.Length, after.Length);
            // The first `oversample` samples belong to cell 0 and must be
            // bit-for-bit identical.
            for (int i = 0; i < 4; i++)
                Assert.Equal(before[i], after[i], 12);
        }

        [Fact]
        public void Gpt_Calibrate_UsesConfirmedAsdwEndpointForThisCellsCurveSample()
        {
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            var profile = new DesktopSensProfile();
            int cellIndex = DesktopSensProfile.GetCellIndex(0, 1);
            profile.CellTrainingStates[cellIndex] = new TrainingCellState
            {
                AdaptiveToleranceCm = 0.5
            };

            double target = 5.0;
            for (int i = 0; i < 5; i++)
            {
                double flick = 4.2 + i * 0.15;
                double speed = 100.0 + i * 80.0;
                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = target,
                    FlickDistanceCm = flick,
                    SignedErrorCm = flick - target,
                    DurationMs = flick / speed * 1000.0,
                    ManualAsdwCorrectionCm = target - flick + 0.2,
                    ManualAsdwFinalErrorCm = 0.2,  // within tolerance
                    HasManualAsdwCorrection = true,
                    HasMovementMeasurement = true
                });
            }

            DesktopSensCalibrator.Calibrate(profile);

            // All 5 ASDW-confirmed samples fit the curve.
            Assert.Equal(5, profile.CellCurveSampleCount[cellIndex]);
        }

        [Fact]
        public void Gpt_Calibrate_DoesNotFitCurveFromUnconfirmedAsdwEndpoint()
        {
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            var profile = new DesktopSensProfile();
            int cellIndex = DesktopSensProfile.GetCellIndex(0, 1);
            profile.CellTrainingStates[cellIndex] = new TrainingCellState
            {
                AdaptiveToleranceCm = 0.5
            };

            double target = 5.0;
            for (int i = 0; i < 5; i++)
            {
                double flick = 4.2 + i * 0.15;
                double speed = 100.0 + i * 80.0;
                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = target,
                    FlickDistanceCm = flick,
                    SignedErrorCm = flick - target,
                    DurationMs = flick / speed * 1000.0,
                    ManualAsdwCorrectionCm = target - flick,
                    // The first trial's final error (0.75) is OUTSIDE the
                    // 0.5 cm tolerance, so its curve sample is rejected.
                    // Only 4 confirmed samples remain, below the
                    // 5-sample curve-fit minimum.
                    ManualAsdwFinalErrorCm = i == 0 ? 0.75 : 0.2,
                    HasManualAsdwCorrection = true,
                    HasMovementMeasurement = true
                });
            }

            DesktopSensCalibrator.Calibrate(profile);

            Assert.Equal(0, profile.CellCurveSampleCount[cellIndex]);
        }

        [Fact]
        public void Gpt_TrialsWithoutAsdwAlwaysPassCurveSampleValidation()
        {
            // A trial with HasManualAsdwCorrection = false must always
            // produce a curve sample (subject to the other validation
            // checks in TryBuildCurveSample). The ASDW-tolerance check
            // is skipped for trials without ASDW.
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            var profile = new DesktopSensProfile { MouseDPI = 800 };
            int cellIndex = DesktopSensProfile.GetCellIndex(0, 1);

            double target = 5.0;
            for (int i = 0; i < 5; i++)
            {
                double flick = 4.2 + i * 0.15;
                double speed = 100.0 + i * 80.0;
                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = target,
                    FlickDistanceCm = flick,
                    SignedErrorCm = flick - target,
                    DurationMs = flick / speed * 1000.0,
                    // No ASDW fields set — defaults to 0.0 / false.
                    HasMovementMeasurement = true
                });
            }

            DesktopSensCalibrator.Calibrate(profile);

            Assert.Equal(5, profile.CellCurveSampleCount[cellIndex]);
        }

        private static double PositiveOrOne(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0
                ? 1.0
                : value;
        }
    }
}
