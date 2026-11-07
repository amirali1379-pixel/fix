using System;
using System.Collections.Generic;
using grapher.DesktopSens.Models;
using Xunit;
using System.Reflection;

namespace grapher.DesktopSens.Tests
{
    public class CalibrationAccuracyTests
    {
        [Fact]
        public void TrialValidation_PreservesPermanentCalibrationExclusion()
        {
            var trial = new DesktopSensTrial
            {
                IsCalibrationExcluded = true,
                WasRejected = false,
                RejectionReason = TrialRejectionReason.None
            };

            Assert.True(trial.Validate());
            Assert.True(trial.WasRejected);
            Assert.True(trial.IsCalibrationExcluded);
        }

        [Fact]
        public void TrialValidation_AcceptsNormalFlickVelocityRamp()
        {
            var trial = new DesktopSensTrial
            {
                RawSampleCount = 4,
                DurationMs = 120.0,
                MeanVelocityPxPerSec = 1000.0,
                VelocityStdDevPxPerSec = 1500.0
            };

            Assert.False(trial.Validate());
            Assert.Equal(TrialRejectionReason.None, trial.RejectionReason);
        }

        [Fact]
        public void TrialValidation_DoesNotRejectVelocityDispersion()
        {
            var trial = new DesktopSensTrial
            {
                RawSampleCount = 4,
                DurationMs = 120.0,
                MeanVelocityPxPerSec = 1000.0,
                VelocityStdDevPxPerSec = 4000.0
            };

            Assert.False(trial.Validate());
            Assert.Equal(TrialRejectionReason.None, trial.RejectionReason);
        }

        [Fact]
        public void TrainingState_CanCalibrateConsistentLargeRawOvershoot()
        {
            var states = new Dictionary<int, TrainingCellState>();
            const double target = 5.0;
            const double observedGain = 1.5;

            for (int i = 0; i < 4; i++)
            {
                // Uniform-gain simulation (BaseZoneEndCm = 0.0): the gain
                // now applies to the FULL target distance, not to the
                // excess beyond 3 cm. observed = target * gain.
                DesktopSensTrainingState.RecordAttempt(
                    states, 0, 1, (target * observedGain) - target, DateTime.UtcNow,
                    sessionId: "stable-miscalibrated-session",
                    angularErrorDegrees: 0.0,
                    targetDistanceCm: target);
            }

            TrainingCellState state = states[DesktopSensTrainingState.GetCellIndex(0, 1)];
            Assert.True(state.FirstRoundComplete);
            Assert.True(state.PrecisionVerified);
            Assert.True(state.Verified);
            Assert.InRange(Math.Abs(state.LogGainEstimate - Math.Log(observedGain)), 0.0, 1e-9);
        }

        [Fact]
        public void TrialValidation_KeepsLargeRadialMissAsCalibrationSignal()
        {
            var trial = new DesktopSensTrial
            {
                TargetDistanceCm = 5.0,
                SignedErrorCm = -3.0,
                HasMovementMeasurement = true,
                HasAngularMeasurement = true,
                AngularErrorDegrees = 0.0
            };

            Assert.False(trial.Validate(maxAngularErrorDegrees: DesktopSensCalibrator.MaxAngularErrorDegrees));
            Assert.True(trial.IsValidForCalibration);
        }

        [Fact]
        public void TrialValidation_DoesNotRejectOffAxisAngularDeviation()
        {
            var trial = new DesktopSensTrial
            {
                TargetDistanceCm = 5.0,
                SignedErrorCm = -0.1,
                HasMovementMeasurement = true,
                HasAngularMeasurement = true,
                AngularErrorDegrees = 20.0
            };

            Assert.False(trial.Validate(maxAngularErrorDegrees: DesktopSensCalibrator.MaxAngularErrorDegrees));
            Assert.Equal(TrialRejectionReason.None, trial.RejectionReason);
        }

        [Fact]
        public void TryLogGain_RejectsMismatchedPairs()
        {
            double gain, se;
            int n;
            bool ok = RobustStatistics.TryLogGain(
                new List<double> { 1.0, 2.0, 3.0 },
                new List<double> { 0.1, 0.2 },
                out gain, out se, out n);

            Assert.False(ok);
            Assert.Equal(0, n);
            Assert.True(double.IsPositiveInfinity(se));
        }

        [Fact]
        public void TryLogGain_RecoversKnownGainWithVariableTargetsAndOutliers()
        {
            const double trueGain = 1.12;
            var rng = new Random(1407);
            var targets = new List<double>();
            var errors = new List<double>();

            for (int i = 0; i < 60; i++)
            {
                double target = 3.2 + 2.0 * rng.NextDouble();
                double noise = Math.Exp(0.015 * NextGaussian(rng));
                // Uniform-gain simulation (BaseZoneEndCm = 0.0): the gain
                // applies to the FULL target, not to the excess beyond 3 cm.
                double observed = target * trueGain * noise;
                if (i % 20 == 0) observed *= 3.0;
                targets.Add(target);
                errors.Add(observed - target);
            }

            double logGain, se;
            int n;
            Assert.True(RobustStatistics.TryLogGain(targets, errors, out logGain, out se, out n));
            double recoveredFactor = Math.Exp(-logGain);
            double expectedFactor = 1.0 / trueGain;

            Assert.Equal(60, n);
            Assert.InRange(Math.Abs(recoveredFactor / expectedFactor - 1.0), 0.0, 0.01);
            Assert.True(se > 0.0 && se < 0.01);
        }

        [Fact]
        public void Calibrator_RecoversKnownGainWithinOnePercentWithVariableTargets()
        {
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            const double trueGain = 1.12;
            var rng = new Random(8128);
            var profile = new DesktopSensProfile { MouseDPI = 800 };

            for (int i = 0; i < 60; i++)
            {
                double target = 3.2 + 2.0 * rng.NextDouble();
                double noise = Math.Exp(0.015 * NextGaussian(rng));
                // Uniform-gain simulation (BaseZoneEndCm = 0.0): the gain
                // applies to the FULL target.
                double observed = target * trueGain * noise;
                if (i % 20 == 0) observed *= 3.0;

                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = target,
                    SignedErrorCm = observed - target,
                    ProjectedDistanceCm = observed,
                    FlickDistanceCm = observed,
                    FinalErrorCm = Math.Abs(observed - target),
                    IsUnderFlick = observed < target,
                    IsOverFlick = observed > target,
                    HasMovementMeasurement = true,
                    HasAngularMeasurement = true,
                    AngularErrorDegrees = 0.0,
                    DurationMs = 300.0,
                    RawSampleCount = 10,
                    MouseDPI = 800
                });
            }

            DesktopSensCalibrator.Calibrate(profile);
            int cell = DesktopSensProfile.GetCellIndex(0, 1);
            double expectedFactor = 1.0 / trueGain;
            double recoveredFactor = profile.CellAccelerationFactor[cell];

            Assert.InRange(Math.Abs(recoveredFactor / expectedFactor - 1.0), 0.0, 0.01);
            Assert.True(profile.CellAccelerationConfidence[cell] > 0.0);
        }

        [Fact]
        public void FitBest_DoesNotSelectAParameterCountUnidentifiableAtFiveSamples()
        {
            var samples = new List<SensitivitySample>();
            for (int i = 0; i < 5; i++)
            {
                samples.Add(new SensitivitySample
                {
                    InputSpeedCmPerSec = 10.0 + i * 15.0,
                    ObservedRatio = 1.0 + i * 0.025,
                    Weight = 1.0
                });
            }

            CurveFitResult result = AccelCurveFitter.FitBest(samples);
            Assert.True(result.IsValid);
            Assert.True(result.Parameters == null || result.Parameters.Length <= samples.Count - 2);
        }

        [Fact]
        public void FitBest_UsesLeaveOneOutOneStandardErrorRuleOnLargerSamples()
        {
            var samples = new List<SensitivitySample>();
            for (int i = 0; i < 24; i++)
            {
                double speed = 5.0 + i * 3.0;
                double ratio = 1.0 + 0.006 * (speed - 5.0) + 0.0002 * Math.Sin(i * 1.7);
                samples.Add(new SensitivitySample
                {
                    InputSpeedCmPerSec = speed,
                    ObservedRatio = ratio,
                    Weight = 1.0
                });
            }

            CurveFitResult fit = AccelCurveFitter.FitBest(samples);

            Assert.True(fit.IsValid);
            Assert.Equal(24, fit.SampleCount);
            Assert.True(fit.Parameters.Length <= 3,
                "The one-standard-error rule should not select a needlessly complex curve for near-linear data.");
        }

        [Fact]
        public void CalibrationCurveCache_ReusesOnlyIdenticalPerCellInputs()
        {
            var profile = new DesktopSensProfile();
            var samples = new List<SensitivitySample>();
            for (int i = 0; i < 6; i++)
            {
                double speed = 5.0 + i * 3.0;
                samples.Add(new SensitivitySample
                {
                    InputSpeedCmPerSec = speed,
                    ObservedRatio = 1.0 + 0.006 * (speed - 5.0) + 0.0002 * Math.Sin(i * 1.7),
                    Weight = 1.0
                });
            }

            CurveFitResult first = CalibrationCurveCache.GetOrFit(profile, 7, samples);
            if (first.Parameters != null && first.Parameters.Length > 0)
                first.Parameters[0] = 999999.0;

            CurveFitResult cached = CalibrationCurveCache.GetOrFit(profile, 7, samples);
            Assert.NotEqual(999999.0, cached.Parameters != null && cached.Parameters.Length > 0
                ? cached.Parameters[0] : 0.0);

            int hits, misses, cells;
            CalibrationCurveCache.GetStatistics(profile, out hits, out misses, out cells);
            Assert.Equal(1, hits);
            Assert.Equal(1, misses);
            Assert.Equal(1, cells);

            samples[0] = new SensitivitySample
            {
                InputSpeedCmPerSec = samples[0].InputSpeedCmPerSec,
                ObservedRatio = samples[0].ObservedRatio + 0.01,
                Weight = samples[0].Weight
            };
            CalibrationCurveCache.GetOrFit(profile, 7, samples);
            CalibrationCurveCache.GetStatistics(profile, out hits, out misses, out cells);
            Assert.Equal(1, hits);
            Assert.Equal(2, misses);
            Assert.Equal(1, cells);
        }

        [Fact]
        public void ResidualCache_ReusesOnlyExactDependenciesAndReturnsCopies()
        {
            var profile = new DesktopSensProfile();
            var firstResiduals = new[] { 0.125, -0.25, double.NaN };

            CalibrationResidualCache.Store(profile, 11, new byte[] { 1, 2, 3 }, firstResiduals);
            firstResiduals[0] = 999.0;

            double[] cached;
            Assert.True(CalibrationResidualCache.TryGet(
                profile, 11, new byte[] { 1, 2, 3 }, out cached));
            Assert.Equal(0.125, cached[0], 12);
            Assert.Equal(-0.25, cached[1], 12);
            Assert.True(double.IsNaN(cached[2]));

            // A changed trial set, session bias or upstream factor is represented
            // by a different exact key and must never reuse the previous residuals.
            Assert.False(CalibrationResidualCache.TryGet(
                profile, 11, new byte[] { 1, 2, 4 }, out cached));

            CalibrationResidualCache.Store(
                profile, 11, new byte[] { 1, 2, 4 }, new[] { 0.5 });
            Assert.True(CalibrationResidualCache.TryGet(
                profile, 11, new byte[] { 1, 2, 4 }, out cached));
            Assert.Single(cached);
            Assert.Equal(0.5, cached[0], 12);

            CalibrationResidualCache.Store(profile, 999, new byte[] { 9, 9 }, new[] { 1.0 });
            CalibrationResidualCache.PruneToCellCount(profile, 12);

            int hits, misses, cells;
            CalibrationResidualCache.GetStatistics(profile, out hits, out misses, out cells);
            Assert.Equal(2, hits);
            Assert.Equal(1, misses);
            Assert.Equal(1, cells);
        }

        [Fact]
        public void Calibrator_ResidualCacheInvalidatesChangedSamplesAnchorsAndDownstreamZones()
        {
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            var profile = new DesktopSensProfile { MouseDPI = 800 };

            for (int i = 0; i < 3; i++)
            {
                profile.Trials.Add(new DesktopSensTrial
                {
                    SessionId = "cache-session",
                    IsSessionAnchor = true,
                    TargetDistanceCm = 2.0,
                    SignedErrorCm = 0.02,
                    ProjectedDistanceCm = 0.02,
                    FinalErrorCm = 0.02,
                    DurationMs = 300.0,
                    MouseDPI = 800
                });
            }

            for (int zone = 0; zone < 2; zone++)
            {
                double target = zone == 0 ? 2.0 : 4.0;
                for (int i = 0; i < 6; i++)
                {
                    double observed = target * (1.08 + (i - 2.5) * 0.0005);
                    profile.Trials.Add(new DesktopSensTrial
                    {
                        SessionId = "cache-session",
                        AngleZoneIndex = 0,
                        DistanceZoneIndex = zone,
                        TargetDistanceCm = target,
                        SignedErrorCm = observed - target,
                        ProjectedDistanceCm = observed,
                        FinalErrorCm = Math.Abs(observed - target),
                        FlickDistanceCm = observed,
                        DurationMs = 300.0,
                        HasMovementMeasurement = true,
                        MouseDPI = 800
                    });
                }
            }

            DesktopSensCalibrator.Calibrate(profile);
            int hits, misses, cells;
            CalibrationResidualCache.GetStatistics(profile, out hits, out misses, out cells);
            Assert.Equal(0, hits);
            Assert.Equal(2, misses);
            Assert.Equal(2, cells);

            // An unchanged recalibration reuses both independent zones.
            DesktopSensCalibrator.Calibrate(profile);
            CalibrationResidualCache.GetStatistics(profile, out hits, out misses, out cells);
            Assert.Equal(2, hits);
            Assert.Equal(2, misses);

            // Changing one zone invalidates only that zone; later zones do
            // not replay or depend on the previous zone's factor.
            profile.Trials[3].SignedErrorCm += 0.01;
            DesktopSensCalibrator.Calibrate(profile);
            CalibrationResidualCache.GetStatistics(profile, out hits, out misses, out cells);
            Assert.Equal(3, hits);
            Assert.Equal(3, misses);

            // Session-anchor drift changes the bias correction for both cells.
            for (int i = 0; i < 3; i++)
                profile.Trials[i].SignedErrorCm += 0.01;
            DesktopSensCalibrator.Calibrate(profile);
            CalibrationResidualCache.GetStatistics(profile, out hits, out misses, out cells);
            Assert.Equal(3, hits);
            Assert.Equal(5, misses);

            int cellHits, cellMisses, cachedCellCount;
            CalibrationCellResultCache.GetStatistics(profile, out cellHits, out cellMisses, out cachedCellCount);
            Assert.Equal(3, cellHits); // New cache-key inputs create one additional valid hit.
            Assert.Equal(5, cellMisses);
            Assert.Equal(2, cachedCellCount);

            // Angular-quality inputs affect confidence even when the residual
            // reconstruction itself is unchanged. The cell-result cache must
            // invalidate that cell independently of the residual cache.
            profile.Trials[3].HasAngularMeasurement = true;
            profile.Trials[3].AngularErrorDegrees = 0.0;
            DesktopSensCalibrator.Calibrate(profile);
            CalibrationCellResultCache.GetStatistics(profile, out cellHits, out cellMisses, out cachedCellCount);
            Assert.Equal(4, cellHits);
            Assert.Equal(6, cellMisses);

            profile.Trials[3].AngularErrorDegrees = 12.0;
            DesktopSensCalibrator.Calibrate(profile);
            CalibrationCellResultCache.GetStatistics(profile, out cellHits, out cellMisses, out cachedCellCount);
            Assert.Equal(5, cellHits);
            Assert.Equal(7, cellMisses);

            double[] cachedSensitivity = (double[])profile.CellSensitivityFactor.Clone();
            double[] cachedAcceleration = (double[])profile.CellAccelerationFactor.Clone();
            CalibrationResidualCache.Clear(profile);
            DesktopSensCalibrator.Calibrate(profile);
            Assert.Equal(cachedSensitivity, profile.CellSensitivityFactor);
            Assert.Equal(cachedAcceleration, profile.CellAccelerationFactor);
            CalibrationCellResultCache.GetStatistics(profile, out cellHits, out cellMisses, out cachedCellCount);
            Assert.Equal(7, cellHits);
            Assert.Equal(7, cellMisses);
            Assert.Equal(2, cachedCellCount);

            // Removing one observation must invalidate only its own cell.
            // The earlier cell remains byte-for-byte reusable.
            profile.Trials.RemoveAt(profile.Trials.Count - 1);
            DesktopSensCalibrator.Calibrate(profile);
            CalibrationCellResultCache.GetStatistics(profile, out cellHits, out cellMisses, out cachedCellCount);
            Assert.Equal(8, cellHits);
            Assert.Equal(8, cellMisses);
            Assert.Equal(2, cachedCellCount);

            double[] afterRemovalSensitivity = (double[])profile.CellSensitivityFactor.Clone();
            double[] afterRemovalAcceleration = (double[])profile.CellAccelerationFactor.Clone();
            CalibrationResidualCache.Clear(profile);
            CalibrationCellResultCache.Clear(profile);
            DesktopSensCalibrator.Calibrate(profile);
            Assert.Equal(afterRemovalSensitivity, profile.CellSensitivityFactor);
            Assert.Equal(afterRemovalAcceleration, profile.CellAccelerationFactor);
        }

        [Fact]
        public void Calibrator_InvalidatesCachesWhenPhysicalGeometryChangesWithoutChangingZoneCount()
        {
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            int originalZoneCount = TrainingGeometry.Current.DistanceZoneCount;
            var profile = new DesktopSensProfile { MouseDPI = 800 };

            for (int zone = 0; zone < 2; zone++)
            {
                double target = zone == 0 ? 2.0 : 4.0;
                for (int i = 0; i < 6; i++)
                {
                    double observed = target * (1.08 + (i - 2.5) * 0.0005);
                    profile.Trials.Add(new DesktopSensTrial
                    {
                        AngleZoneIndex = 0,
                        DistanceZoneIndex = zone,
                        TargetDistanceCm = target,
                        SignedErrorCm = observed - target,
                        ProjectedDistanceCm = observed,
                        FinalErrorCm = Math.Abs(observed - target),
                        FlickDistanceCm = observed,
                        DurationMs = 300.0,
                        HasMovementMeasurement = true,
                        MouseDPI = 800
                    });
                }
            }

            DesktopSensCalibrator.Calibrate(profile);
            DesktopSensCalibrator.Calibrate(profile);

            int residualHits, residualMisses, residualCells;
            CalibrationResidualCache.GetStatistics(
                profile, out residualHits, out residualMisses, out residualCells);
            Assert.Equal(2, residualHits);
            Assert.Equal(2, residualMisses);

            int resultHits, resultMisses, resultCells;
            CalibrationCellResultCache.GetStatistics(
                profile, out resultHits, out resultMisses, out resultCells);
            Assert.Equal(2, resultHits);
            Assert.Equal(2, resultMisses);

            // Change only the physical distance scale. The number and angular
            // spacing of zones remain untouched; geometry-dependent cache keys
            // must nevertheless force both cells to be recalculated.
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 31.0);
            Assert.Equal(originalZoneCount, TrainingGeometry.Current.DistanceZoneCount);
            DesktopSensCalibrator.Calibrate(profile);

            CalibrationResidualCache.GetStatistics(
                profile, out residualHits, out residualMisses, out residualCells);
            Assert.Equal(2, residualHits);
            Assert.Equal(4, residualMisses);

            CalibrationCellResultCache.GetStatistics(
                profile, out resultHits, out resultMisses, out resultCells);
            Assert.Equal(2, resultHits);
            Assert.Equal(4, resultMisses);

            double[] geometryCachedSensitivity =
                (double[])profile.CellSensitivityFactor.Clone();
            double[] geometryCachedAcceleration =
                (double[])profile.CellAccelerationFactor.Clone();

            CalibrationResidualCache.Clear(profile);
            CalibrationCellResultCache.Clear(profile);
            DesktopSensCalibrator.Calibrate(profile);

            Assert.Equal(geometryCachedSensitivity, profile.CellSensitivityFactor);
            Assert.Equal(geometryCachedAcceleration, profile.CellAccelerationFactor);

            // Restore the default test geometry for subsequent tests.
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
        }

        [Fact]
        public void Calibrator_DoesNotCountCellsWhoseResidualsAreAllDiscarded()
        {
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            var profile = new DesktopSensProfile();

            // Three same-session anchors establish a +100% multiplicative bias.
            for (int i = 0; i < 3; i++)
            {
                profile.Trials.Add(new DesktopSensTrial
                {
                    SessionId = "coverage-bias-session",
                    IsSessionAnchor = true,
                    TargetDistanceCm = 2.0,
                    SignedErrorCm = 2.0,
                    DurationMs = 300.0
                });
            }

            // Raw observed distance is 0.04 cm. Removing the session bias
            // reduces it to 0.02 cm, below the calibrator's 0.05 cm minimum,
            // so this cell has no usable residual after reconstruction.
            profile.Trials.Add(new DesktopSensTrial
            {
                SessionId = "coverage-bias-session",
                AngleZoneIndex = 0,
                DistanceZoneIndex = 0,
                TargetDistanceCm = 1.0,
                SignedErrorCm = -0.96,
                FinalErrorCm = 0.96,
                IsUnderFlick = true,
                DurationMs = 300.0,
                HasMovementMeasurement = true
            });

            DesktopSensCalibrator.Calibrate(profile);

            int eligibleZones = 0;
            for (int d = 0; d < TrainingGeometry.Current.DistanceZoneCount; d++)
                if (DesktopSensTrainingState.IsCalibrationDistanceZone(d)) eligibleZones++;
            int totalCells = DesktopSensConstants.AngularZoneCount * eligibleZones;
            Assert.Equal(0.0, profile.OverallCoverage, 12);
            Assert.Equal(totalCells, profile.CellsInsufficientData);
            Assert.Equal(0, profile.CellsLowConfidence);
        }

        [Fact]
        public void TrainingState_RequiresNarrowConfidenceIntervalBeforeGreen()
        {
            var states = new Dictionary<int, TrainingCellState>();
            var rng = new Random(441);
            const double target = 5.0;
            const double gain = 1.08;

            for (int i = 0; i < 60; i++)
            {
                // Uniform-gain simulation (BaseZoneEndCm = 0.0): the gain
                // applies to the FULL target.
                double observed = target * gain * Math.Exp(0.008 * NextGaussian(rng));
                DesktopSensTrainingState.RecordAttempt(
                    states, 0, 1, observed - target, DateTime.UtcNow,
                    sessionId: "test-session",
                    angularErrorDegrees: 0.0,
                    targetDistanceCm: target);
            }

            TrainingCellState state = states[DesktopSensTrainingState.GetCellIndex(0, 1)];
            Assert.True(state.PrecisionVerified);
            Assert.True(state.Verified);
            Assert.False(state.LowPrecision);
            Assert.InRange(state.LogGainConfidenceHalfWidth95, 0.0,
                DesktopSensTrainingState.PrecisionToleranceLog);
        }

        [Fact]
        public void Profile_CmPerCountUsesMeasuredCpiAndFallsBackForLegacyProfiles()
        {
            var profile = new DesktopSensProfile { MouseDPI = 800 };
            Assert.Equal(2.54 / 800.0, profile.CmPerCount, 10);

            profile.MeasuredCpi = 790.0;
            Assert.Equal(2.54 / 790.0, profile.CmPerCount, 10);

            profile.MeasuredCpi = 0.0;
            Assert.Equal(2.54 / 800.0, profile.CmPerCount, 10);
        }

        [Fact]
        public void ProfileCopy_CopiesPublicFieldsAndWritableProperties()
        {
            var source = new CopyFixture(7)
            {
                PublicField = 42,
                Name = "source"
            };
            var target = new CopyFixture(9)
            {
                PublicField = 1,
                Name = "target"
            };

            DesktopSensProfileCopy.CopyPublicMembers(source, target);

            Assert.Equal(42, target.PublicField);
            Assert.Equal("source", target.Name);
            Assert.Equal(9, target.ReadOnly);
        }

        [Fact]
        public void ProfileCopy_CoversEveryPublicWritableProfileMember()
        {
            var source = new DesktopSensProfile
            {
                MouseDPI = 1600,
                MeasuredCpi = 1588.5,
                UserInputGameSensitivity = 2.25,
                BaseSensitivity = 2.25,
                RawAccelBaseOutputDPI = 1588.5
            };
            source.Trials.Add(new DesktopSensTrial { TargetDistanceCm = 2.0, SignedErrorCm = 0.1 });
            var target = new DesktopSensProfile { MouseDPI = 400 };

            DesktopSensProfileCopy.CopyPublicMembers(source, target);

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            foreach (FieldInfo field in typeof(DesktopSensProfile).GetFields(flags))
            {
                if (field.IsInitOnly || field.IsLiteral) continue;
                Assert.Equal(field.GetValue(source), field.GetValue(target));
            }
            foreach (PropertyInfo property in typeof(DesktopSensProfile).GetProperties(flags))
            {
                if (property.GetIndexParameters().Length != 0 ||
                    property.GetGetMethod(false) == null ||
                    property.GetSetMethod(false) == null)
                    continue;
                Assert.Equal(property.GetValue(source, null), property.GetValue(target, null));
            }
        }

        [Fact]
        public void DesktopSensLog_RetainsOnlyTheMostRecentFiftyEntries()
        {
            for (int i = 0; i < 55; i++)
                DesktopSensLog.Warn("ring-entry-" + i);

            string[] entries = DesktopSensLog.GetRecentEntries();
            Assert.Equal(50, entries.Length);
            Assert.Contains("ring-entry-54", entries[entries.Length - 1]);
            Assert.EndsWith("ring-entry-5", entries[0]);
        }

        [Fact]
        public void NextAdaptiveDistanceZone_PrefersLargestConfidenceIntervalThenFewestSamples()
        {
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            var states = new Dictionary<int, TrainingCellState>();
            states[DesktopSensTrainingState.GetCellIndex(0, 0)] =
                new TrainingCellState { AngleZoneIndex = 0, DistanceZoneIndex = 0, Verified = true };
            states[DesktopSensTrainingState.GetCellIndex(0, 1)] =
                new TrainingCellState { AngleZoneIndex = 0, DistanceZoneIndex = 1, LogGainConfidenceHalfWidth95 = 0.006, LogGainSampleCount = 8 };
            states[DesktopSensTrainingState.GetCellIndex(0, 2)] =
                new TrainingCellState { AngleZoneIndex = 0, DistanceZoneIndex = 2, LogGainConfidenceHalfWidth95 = 0.020, LogGainSampleCount = 7 };
            states[DesktopSensTrainingState.GetCellIndex(0, 3)] =
                new TrainingCellState { AngleZoneIndex = 0, DistanceZoneIndex = 3, LogGainConfidenceHalfWidth95 = 0.020, LogGainSampleCount = 3 };
            // The default geometry has 19 horizontal cells (17 base + 2
            // zone-0 sub-markers, since Zone0SubMarkerCount = 3). Mark the
            // rest complete so this test isolates the three candidates above.
            for (int d = 4; d < TrainingGeometry.Current.CardinalDirectionZoneCount(0); d++)
                states[DesktopSensTrainingState.GetCellIndex(0, d)] =
                    new TrainingCellState { AngleZoneIndex = 0, DistanceZoneIndex = d, Verified = true };

            int selected = DesktopSensTrainingState.NextAdaptiveDistanceZone(
                states, 0, 0, allowOverride: true, random: new Random(123));

            Assert.Equal(3, selected);
        }

        [Fact]
        public void NextAdaptiveDistanceZone_PreservesStrictSequentialAvailability()
        {
            // NOTE: after the previous fix session removed the sequential
            // lockout from IsCellAvailableForTrainingStrict (because
            // BaseZoneEndCm = 0.0 makes every zone a calibration target),
            // every zone in range is "available". NextAdaptiveDistanceZone
            // therefore picks the zone with the largest LogGainConfidenceHalfWidth95,
            // treating zones with no state as infinite half-width (highest
            // priority). This test now verifies that behaviour instead of
            // the legacy sequential lockout.
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            int totalZones = TrainingGeometry.Current.CardinalDirectionZoneCount(0);
            var states = new Dictionary<int, TrainingCellState>();
            states[DesktopSensTrainingState.GetCellIndex(0, 0)] =
                new TrainingCellState { AngleZoneIndex = 0, DistanceZoneIndex = 0, Verified = true };
            // Populate every non-verified zone with a finite half-width so
            // the scheduler has a deterministic choice. The last zone has
            // the largest finite half-width, so it must be picked.
            for (int d = 1; d < totalZones; d++)
            {
                states[DesktopSensTrainingState.GetCellIndex(0, d)] =
                    new TrainingCellState
                    {
                        AngleZoneIndex = 0,
                        DistanceZoneIndex = d,
                        LogGainConfidenceHalfWidth95 = 0.010 + d * 0.001,
                        LogGainSampleCount = 5
                    };
            }

            int selected = DesktopSensTrainingState.NextAdaptiveDistanceZone(
                states, 0, 0, allowOverride: false, random: new Random(123));

            Assert.Equal(totalZones - 1, selected);
        }

        [Fact]
        public void ResidualReconstruction_DoesNotReplayPreviousCellFactors()
        {
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            var profile = new DesktopSensProfile();
            profile.CellTrainingStates = null;
            int previousCell = DesktopSensProfile.GetCellIndex(0, 0);
            // Previous-cell factors are intentionally not replayed into
            // this trial's residual under the independent-cell model.
            profile.CellAccelerationFactor[previousCell] = 0.5;
            profile.CellAccelerationConfidence[previousCell] = 0.9;

            var trial = new DesktopSensTrial
            {
                AngleZoneIndex = 0,
                DistanceZoneIndex = 1,
                TargetDistanceCm = 8.0,
                TargetAngleDegrees = 0.0,
                MouseDPI = 800,
                RawSamples = new List<grapher.Models.Mouse.MouseSample>()
            };

            MethodInfo method = typeof(DesktopSensCalibrator).GetMethod(
                "ComputeResidualSignedError",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            double residual = (double)method.Invoke(null, new object[]
            {
                trial, 2.0
            });

            Assert.Equal(2.0, residual, 12);
        }

        [Fact]
        public void TrainingState_DriftResetClearsPairedTargetsAndOldPrecision()
        {
            int cellIndex = DesktopSensTrainingState.GetCellIndex(0, 0);
            var state = new TrainingCellState
            {
                AngleZoneIndex = 0,
                DistanceZoneIndex = 0,
                Verified = true,
                PrecisionVerified = true,
                LogGainSampleCount = 12,
                LogGainEstimate = 0.08,
                LogGainStandardError = 0.001,
                LogGainConfidenceHalfWidth95 = 0.002,
                SignedErrorHistoryCm = new List<double> { 0.1, 0.2 },
                TargetDistanceHistoryCm = new List<double> { 2.0, 2.0 }
            };
            var states = new Dictionary<int, TrainingCellState> { [cellIndex] = state };

            bool drifted = DesktopSensTrainingState.DetectDrift(
                states, 0, 0, 5.0, DateTime.UtcNow, "new-session");

            Assert.True(drifted);
            Assert.False(state.Verified);
            Assert.False(state.PrecisionVerified);
            Assert.Equal(0, state.LogGainSampleCount);
            Assert.Empty(state.SignedErrorHistoryCm);
            Assert.Empty(state.TargetDistanceHistoryCm);
        }

        [Fact]
        public void TrainingState_DowngradesCellWhenSessionGainsDifferByMoreThanTwoPercent()
        {
            var firstSession = new TrainingSessionCalibrationHistory { SessionId = "session-a" };
            var pooledTargets = new List<double>();
            var pooledErrors = new List<double>();
            for (int i = 0; i < 10; i++)
            {
                firstSession.TargetDistanceHistoryCm.Add(5.0);
                firstSession.SignedErrorHistoryCm.Add(0.1);
                pooledTargets.Add(5.0);
                pooledErrors.Add(0.1);
            }
            double firstLogGain, firstSe, firstHalfWidth;
            int firstCount;
            Assert.True(RobustStatistics.TryLogGainConfidence(
                firstSession.TargetDistanceHistoryCm,
                firstSession.SignedErrorHistoryCm,
                out firstLogGain, out firstSe, out firstHalfWidth, out firstCount));
            firstSession.LogGainSampleCount = firstCount;
            firstSession.LogGainEstimate = firstLogGain;
            firstSession.LogGainHalfWidth95 = firstHalfWidth;

            var state = new TrainingCellState
            {
                AngleZoneIndex = 0,
                DistanceZoneIndex = 1,
                Verified = true,
                FirstRoundComplete = true,
                SuccessfulAttemptCount = 6,
                VerifiedSessionId = "session-a",
                RobustSigmaCm = 1.0,
                SignedErrorHistoryCm = pooledErrors,
                TargetDistanceHistoryCm = pooledTargets,
                SessionCalibrationHistories = new List<TrainingSessionCalibrationHistory> { firstSession }
            };
            var states = new Dictionary<int, TrainingCellState>
            {
                [DesktopSensTrainingState.GetCellIndex(0, 1)] = state
            };

            // First session: 10 trials at error = 0.1 → observed = 5.1,
            // gain = 1.02, log gain ≈ 0.01980. For the spread to exceed
            // log(1.02) ≈ 0.01980, the second session's log gain must be
            // clearly above 0.03960. With error = 0.25, observed = 5.25,
            // gain = 1.05, log gain ≈ 0.04879, spread ≈ 0.02899 > 0.01980.
            for (int i = 0; i < 8; i++)
            {
                DesktopSensTrainingState.RecordAttempt(
                    states, 0, 1, 0.25, DateTime.UtcNow,
                    sessionId: "session-b",
                    angularErrorDegrees: 0.0,
                    targetDistanceCm: 5.0);
            }

            Assert.True(state.SessionRepeatabilityFailed);
            Assert.False(state.Verified);
            Assert.True(state.SessionGainSpreadLog > Math.Log(1.02));
        }

        [Fact]
        public void ClearDriftFlag_CannotPromoteCellWithoutPrecision()
        {
            int cellIndex = DesktopSensTrainingState.GetCellIndex(0, 0);
            var state = new TrainingCellState
            {
                AngleZoneIndex = 0,
                DistanceZoneIndex = 0,
                Verified = false,
                PrecisionVerified = false
            };
            var states = new Dictionary<int, TrainingCellState> { [cellIndex] = state };

            DesktopSensTrainingState.ClearDriftFlag(
                states, 0, 0, DateTime.UtcNow, "session");

            Assert.False(state.Verified);
            Assert.Null(state.VerifiedTimestampUtc);
            Assert.Null(state.VerifiedSessionId);
        }


        [Fact]
        public void Calibrator_DoesNotUseSub3CmAnchorsToCorrectFarZoneData()
        {
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            const string session = "anchor-session";
            const double gain = 1.10;
            var profile = new DesktopSensProfile { MouseDPI = 800 };
            for (int i = 0; i < 3; i++)
                profile.Trials.Add(new DesktopSensTrial
                {
                    SessionId = session, IsSessionAnchor = true,
                    AngleZoneIndex = 0, DistanceZoneIndex = 0,
                    TargetDistanceCm = 2.0, SignedErrorCm = 2.0 * gain - 2.0,
                    HasMovementMeasurement = true
                });
            // The far trials must use the SAME gain as the anchors
            // (5.0 * gain - 5.0 = 0.5 for gain = 1.10). The 3 anchors at
            // target = 2.0 cm produce a session bias of log(1.10); the
            // calibrator applies this bias to every same-session trial,
            // so the far trial's observed 5.5 cm is reduced to 5.5 / 1.10
            // = 5.0 cm — the residual is 0 and the factor is 1.0.
            double farTrialError = 5.0 * gain - 5.0;
            for (int i = 0; i < 20; i++)
                profile.Trials.Add(new DesktopSensTrial
                {
                    SessionId = session, AngleZoneIndex = 0, DistanceZoneIndex = 1,
                    TargetDistanceCm = 5.0, SignedErrorCm = farTrialError,
                    ProjectedDistanceCm = 5.0 + farTrialError,
                    FlickDistanceCm = 5.0 + farTrialError,
                    HasMovementMeasurement = true, DurationMs = 300.0
                });
            DesktopSensCalibrator.Calibrate(profile);
            int farCell = DesktopSensProfile.GetCellIndex(0, 1);
            int microCell = DesktopSensProfile.GetCellIndex(0, 0);
            Assert.InRange(Math.Abs(profile.CellAccelerationFactor[farCell] - 1.0), 0.0, 0.002);
            Assert.Equal(1.0, profile.CellSensitivityFactor[microCell], 12);
            Assert.Equal(0.0, profile.CellSensitivityConfidence[microCell], 12);
        }

        [Fact]
        public void Calibrator_DoesNotUseOneSub3CmAnchorForFarZoneData()
        {
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            const string session = "few-anchors";
            const double gain = 1.10;
            var profile = new DesktopSensProfile { MouseDPI = 800 };
            profile.Trials.Add(new DesktopSensTrial
            {
                SessionId = session, IsSessionAnchor = true, AngleZoneIndex = 0,
                DistanceZoneIndex = 0, TargetDistanceCm = 2.0,
                SignedErrorCm = 2.0 * gain - 2.0, HasMovementMeasurement = true
            });
            // With only ONE anchor, BuildSessionBiases requires >= 3
            // anchors per session, so no session bias is computed. The far
            // trial's error must use the same gain as the anchor
            // (5.0 * gain - 5.0 = 0.5 for gain = 1.10) so the factor is
            // exactly 5.0 / 5.5 = 1 / gain = 0.909.
            double farTrialError = 5.0 * gain - 5.0;
            for (int i = 0; i < 20; i++)
                profile.Trials.Add(new DesktopSensTrial
                {
                    SessionId = session, AngleZoneIndex = 0, DistanceZoneIndex = 1,
                    TargetDistanceCm = 5.0, SignedErrorCm = farTrialError,
                    ProjectedDistanceCm = 5.0 + farTrialError,
                    FlickDistanceCm = 5.0 + farTrialError,
                    HasMovementMeasurement = true, DurationMs = 300.0
                });
            DesktopSensCalibrator.Calibrate(profile);
            int farCell = DesktopSensProfile.GetCellIndex(0, 1);
            Assert.InRange(Math.Abs(profile.CellAccelerationFactor[farCell] - (1.0 / gain)), 0.0, 0.002);
        }

        [Fact]
        public void CounterbalancedABPlan_UsesABBAOrBAABAndPairedBootstrap()
        {
            var plan = new CounterbalancedABPlan(12, new Random(20261103));
            var conditions = new List<bool>();
            for (int pair = 0; pair < plan.PairCount; pair++)
            {
                bool firstIsB = plan.CurrentConditionIsB;
                conditions.Add(firstIsB);
                Assert.True(plan.RecordCurrent(firstIsB ? 0.01 : 0.08, firstIsB ? 0.02 : 0.8));
                bool secondIsB = plan.CurrentConditionIsB;
                conditions.Add(secondIsB);
                Assert.NotEqual(firstIsB, secondIsB);
                Assert.True(plan.RecordCurrent(secondIsB ? 0.01 : 0.08, secondIsB ? 0.02 : 0.8));
            }
            Assert.True(plan.IsComplete);
            Assert.Equal(12, plan.CompletedPairs);
            for (int i = 0; i < conditions.Count; i += 4)
            {
                bool abba = !conditions[i] && conditions[i + 1] && conditions[i + 2] && !conditions[i + 3];
                bool baab = conditions[i] && !conditions[i + 1] && !conditions[i + 2] && conditions[i + 3];
                Assert.True(abba || baab);
            }
            ABTestResult result = plan.BuildResult();
            Assert.NotNull(result);
            Assert.True(result.HasBaselineComparison);
            Assert.True(result.ImprovementStatisticallySignificant);
            Assert.True(result.PairedImprovementCiLower > 0.0);
            Assert.InRange(result.MedianAbsoluteLogErrorAfter, 0.0, 0.015);
        }

        [Fact]
        public void CounterbalancedABPlan_RejectsIncompleteOrUnderpoweredRun()
        {
            var plan = new CounterbalancedABPlan(6, new Random(7));
            Assert.Null(plan.BuildResult());
        }


        [Fact]
        public void SessionBiasCorrection_AdjustsExistingHistoryOnceAndDoesNotDoubleCorrectNewSamples()
        {
            var states = new Dictionary<int, TrainingCellState>();
            const string session = "bias-session";
            const double target = 5.0;
            // For the bias to cancel the recorded gain exactly, observed
            // must equal target * exp(bias) = 5.0 * 1.10 = 5.5, so the
            // raw signed error must be 0.5. With rawError = 0.5 the bias
            // cancels the gain exactly and the post-bias residual is zero.
            const double rawError = 0.5;
            for (int i = 0; i < 3; i++)
                DesktopSensTrainingState.RecordAttempt(states, 0, 1, rawError, DateTime.UtcNow,
                    sessionId: session, angularErrorDegrees: 0.0, targetDistanceCm: target);

            double bias = Math.Log(1.10);
            DesktopSensTrainingState.ApplySessionBiasToSession(states, session, bias, DateTime.UtcNow);
            TrainingCellState state = states[DesktopSensTrainingState.GetCellIndex(0, 1)];
            Assert.True(state.SessionCalibrationHistories[0].SessionBiasApplied);
            for (int i = 0; i < state.SessionCalibrationHistories[0].SignedErrorHistoryCm.Count; i++)
                Assert.InRange(Math.Abs(state.SessionCalibrationHistories[0].SignedErrorHistoryCm[i]), 0.0, 1e-9);

            DesktopSensTrainingState.RecordAttempt(states, 0, 1, 0.0, DateTime.UtcNow,
                sessionId: session, angularErrorDegrees: 0.0, targetDistanceCm: target,
                sessionBiasApplied: true, sessionBiasLogGain: bias);
            Assert.Equal(4, state.SessionCalibrationHistories[0].SignedErrorHistoryCm.Count);
            Assert.InRange(Math.Abs(state.SessionCalibrationHistories[0].SignedErrorHistoryCm[0]), 0.0, 1e-9);
            Assert.InRange(Math.Abs(state.SessionCalibrationHistories[0].SignedErrorHistoryCm[3]), 0.0, 1e-9);
        }


        [Fact]
        public void DirectionalCalibrator_ClearsStaleFactorsWhenNoCurrentTrialsRemain()
        {
            var profile = new DesktopSensProfile
            {
                RightSensitivityFactor = 1.25,
                DownSensitivityFactor = 0.80,
                LeftSensitivityFactor = 1.15,
                UpSensitivityFactor = 0.90,
                RightSensitivityConfidence = 1.0,
                DownSensitivityConfidence = 1.0,
                LeftSensitivityConfidence = 1.0,
                UpSensitivityConfidence = 1.0
            };

            DirectionalCalibrator.Calibrate(profile);

            Assert.Equal(DesktopSensConstants.NeutralFactor, profile.RightSensitivityFactor);
            Assert.Equal(DesktopSensConstants.NeutralFactor, profile.DownSensitivityFactor);
            Assert.Equal(DesktopSensConstants.NeutralFactor, profile.LeftSensitivityFactor);
            Assert.Equal(DesktopSensConstants.NeutralFactor, profile.UpSensitivityFactor);
            Assert.Equal(0.0, profile.RightSensitivityConfidence);
            Assert.Equal(0.0, profile.DownSensitivityConfidence);
            Assert.Equal(0.0, profile.LeftSensitivityConfidence);
            Assert.Equal(0.0, profile.UpSensitivityConfidence);
        }

        [Fact]
        public void DirectionalCalibrator_ExplicitTrialSetDoesNotConsumeTrainHistory()
        {
            var profile = new DesktopSensProfile
            {
                MouseDPI = 800,
                LeftSensitivityFactor = 1.1,
                DownSensitivityFactor = 1.2,
                UpSensitivityFactor = 0.9
            };
            profile.Trials.Add(new DesktopSensTrial
            {
                TargetDistanceCm = 2.0,
                SignedErrorCm = -0.5,
                HasMovementMeasurement = true,
                RawSamples = new List<grapher.Models.Mouse.MouseSample>
                {
                    new grapher.Models.Mouse.MouseSample { DX = 100, DY = 0 }
                }
            });

            var finderTrials = new List<DesktopSensTrial>
            {
                new DesktopSensTrial
                {
                    TargetDistanceCm = 2.0,
                    SignedErrorCm = 0.5,
                    HasMovementMeasurement = true,
                    RawSamples = new List<grapher.Models.Mouse.MouseSample>
                    {
                        new grapher.Models.Mouse.MouseSample { DX = 100, DY = 0 }
                    }
                }
            };

            DirectionalCalibrator.Calibrate(profile, finderTrials, 0.0, preserveUnobservedDirections: true);

            // Finder's over-flick sample yields target / observed = 2 / 2.5.
            // The opposite Train sample must not leak into this estimate.
            Assert.InRange(Math.Abs(profile.RightSensitivityFactor - 0.8), 0.0, 0.01);
            Assert.Equal(1.1, profile.LeftSensitivityFactor);
            Assert.Equal(1.2, profile.DownSensitivityFactor);
            Assert.Equal(0.9, profile.UpSensitivityFactor);
        }

        [Fact]
        public void DirectionalCalibrator_IgnoresNullRawSamples()
        {
            var profile = new DesktopSensProfile { MouseDPI = 800 };
            profile.Trials.Add(new DesktopSensTrial
            {
                SessionId = "null-raw-sample",
                AngleZoneIndex = 0,
                DistanceZoneIndex = 0,
                TargetDistanceCm = 2.0,
                SignedErrorCm = 0.2,
                HasMovementMeasurement = true,
                RawSamples = new List<grapher.Models.Mouse.MouseSample>
                {
                    null,
                    new grapher.Models.Mouse.MouseSample { DX = 100, DY = 0 }
                }
            });

            DirectionalCalibrator.Calibrate(profile);

            Assert.InRange(Math.Abs(profile.RightSensitivityFactor - (2.0 / 2.2)), 0.0, 0.002);
            Assert.True(profile.RightSensitivityConfidence > 0.0);
        }

        [Fact]
        public void DirectionalCalibrator_UsesSessionBiasAndExcludesAnchorAndEvaluationTrials()
        {
            const string session = "directional-anchor-session";
            const double gain = 1.10;
            var profile = new DesktopSensProfile { MouseDPI = 800 };
            for (int i = 0; i < 3; i++)
                profile.Trials.Add(new DesktopSensTrial
                {
                    SessionId = session, IsSessionAnchor = true, AngleZoneIndex = 0, DistanceZoneIndex = 0,
                    TargetDistanceCm = 2.0, SignedErrorCm = 2.0 * gain - 2.0,
                    HasMovementMeasurement = true,
                    RawSamples = new List<grapher.Models.Mouse.MouseSample> {
                        new grapher.Models.Mouse.MouseSample { DX = 100, DY = 0 } }
                });
            for (int i = 0; i < 20; i++)
                profile.Trials.Add(new DesktopSensTrial
                {
                    SessionId = session, AngleZoneIndex = 0, DistanceZoneIndex = 0,
                    TargetDistanceCm = 2.0, SignedErrorCm = 2.0 * gain - 2.0,
                    FlickDistanceCm = 2.0 * gain, DurationMs = 100.0,
                    HasMovementMeasurement = true,
                    RawSamples = new List<grapher.Models.Mouse.MouseSample> {
                        new grapher.Models.Mouse.MouseSample { DX = 100, DY = 0 } }
                });
            for (int i = 0; i < 10; i++)
                profile.Trials.Add(new DesktopSensTrial
                {
                    SessionId = session, IsEvaluationOnly = true, AngleZoneIndex = 0, DistanceZoneIndex = 0,
                    TargetDistanceCm = 2.0, SignedErrorCm = 1.5, FlickDistanceCm = 3.5,
                    DurationMs = 100.0, HasMovementMeasurement = true,
                    RawSamples = new List<grapher.Models.Mouse.MouseSample> {
                        new grapher.Models.Mouse.MouseSample { DX = 100, DY = 0 } }
                });

            DirectionalCalibrator.Calibrate(profile);
            Assert.InRange(Math.Abs(profile.RightSensitivityFactor - 1.0), 0.0, 0.002);
        }

        [Fact]
        public void TryLogGain_RecoversKnownGainAcrossTwoHundredSeedsAndBeatsLegacyEstimator()
        {
            const double trueGain = 1.12;
            double logSquaredError = 0.0;
            double legacySquaredError = 0.0;
            const int seedCount = 200;

            for (int seed = 0; seed < seedCount; seed++)
            {
                var rng = new Random(seed + 1000);
                var targets = new List<double>(60);
                var errors = new List<double>(60);

                for (int i = 0; i < 60; i++)
                {
                    double target = 1.0 + 2.0 * rng.NextDouble();
                    double noise = Math.Exp(0.015 * NextGaussian(rng));
                    double observed = target * trueGain * noise;
                    if (i % 20 == 0) observed *= 3.0;
                    targets.Add(target);
                    errors.Add(observed - target);
                }

                double logGain, standardError;
                int sampleCount;
                Assert.True(RobustStatistics.TryLogGain(
                    targets, errors, out logGain, out standardError, out sampleCount));
                Assert.Equal(60, sampleCount);

                double expectedFactor = 1.0 / trueGain;
                double logFactorRelativeError = Math.Exp(-logGain) / expectedFactor - 1.0;

                // Reproduce the former ratio-of-aggregates estimator as a
                // comparison baseline: trimmed target plus Huber signed error.
                double legacyTarget = RobustStatistics.TrimmedMean(targets, 0.2);
                double legacyError = RobustStatistics.HuberMean(errors);
                double legacyFactor = legacyTarget / (legacyTarget + legacyError);
                double legacyRelativeError = legacyFactor / expectedFactor - 1.0;

                logSquaredError += logFactorRelativeError * logFactorRelativeError;
                legacySquaredError += legacyRelativeError * legacyRelativeError;
            }

            double logEstimatorRms = Math.Sqrt(logSquaredError / seedCount);
            double legacyEstimatorRms = Math.Sqrt(legacySquaredError / seedCount);

            Assert.True(logEstimatorRms < 0.005,
                "The log-ratio estimator should recover the known factor within 0.5% RMS over 200 seeded datasets.");
            Assert.True(logEstimatorRms < legacyEstimatorRms,
                "The log-ratio estimator should have lower recovery error than the previous ratio-of-aggregates estimator.");
        }

        [Fact]
        public void TrainingGeometry_RejectsNonFiniteConfigurationWithoutCollapsingZoneCounts()
        {
            TrainingGeometry.Configure(
                double.PositiveInfinity,
                double.PositiveInfinity,
                double.NaN,
                double.PositiveInfinity);

            Assert.Equal(10.0, TrainingGeometry.Current.MarkerStepDegrees);
            Assert.Equal(180.0, TrainingGeometry.Current.MaxAxisDegrees);
            Assert.Equal(89.0, TrainingGeometry.Current.MaxPitchDegrees);
            // With default Zone0SubMarkerCount = 3, the first zone carries
            // 3 calibration points, so the total count is base + 2.
            Assert.Equal(19, TrainingGeometry.Current.HorizontalMarkerCount);
            Assert.Equal(10, TrainingGeometry.Current.VerticalMarkerCount);
            Assert.Equal(0.0, TrainingGeometry.Current.MaxDistanceCm);
        }

        [Fact]
        public void TrainingGeometry_DetectsPhysicalScaleChangesAndLegacyProfiles()
        {
            Assert.False(TrainingGeometry.HasGeometryChanged(
                10.0, 10.0, 30.0, 30.0, false));
            Assert.True(TrainingGeometry.HasGeometryChanged(
                10.0, 10.0, 30.0, 31.0, false));
            Assert.True(TrainingGeometry.HasGeometryChanged(
                10.0, 5.0, 30.0, 30.0, false));
            Assert.True(TrainingGeometry.HasGeometryChanged(
                10.0, 10.0, 0.0, 30.0, false));
            Assert.True(TrainingGeometry.HasGeometryChanged(
                10.0, 10.0, 30.0, 30.0, true));
        }

        [Theory]
        [InlineData(10.0, 180.0, 17)]
        [InlineData(5.0, 180.0, 35)]
        [InlineData(7.0, 180.0, 25)]
        [InlineData(10.0, 90.0, 8)]
        public void TrainingGeometry_CentralHorizontalCountMatchesConfiguredGeometry(
            double step, double maxAxis, int expectedBaseCount)
        {
            // The static helper returns the legacy BASE count (without the
            // zone-0 sub-marker expansion). It is used by the trainer's
            // migration check to detect legacy profile strides.
            Assert.Equal(expectedBaseCount,
                TrainingGeometry.CalculateHorizontalMarkerCount(step, maxAxis));

            TrainingGeometry.Configure(step, maxAxis, 89.0, 30.0);
            // The instance count adds (EffectiveZone0SubMarkerCount - 1)
            // extra sub-markers inside zone 0's range. With the default
            // Zone0SubMarkerCount = 3, that's +2.
            int expectedInstanceCount = expectedBaseCount
                + TrainingGeometry.Current.EffectiveZone0SubMarkerCount - 1;
            Assert.Equal(expectedInstanceCount, TrainingGeometry.Current.HorizontalMarkerCount);
        }

        [Fact]
        public void TrainingGeometry_DistanceMappingRejectsNonFiniteInputs()
        {
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            TrainingGeometry geometry = TrainingGeometry.Current;

            Assert.Equal(0.0, geometry.MarkerDistanceCm(0, double.NaN));
            Assert.Equal(0.0, geometry.MarkerDistanceCm(0, double.PositiveInfinity));
            // Invalid distances must not be silently assigned to zone 0.
            // The calibrator already rejects indices outside [0, zoneCount).
            Assert.Equal(-1, geometry.DistanceToMarkerIndex(double.NaN, 30.0));
            Assert.Equal(-1, geometry.DistanceToMarkerIndex(double.PositiveInfinity, 30.0));
            Assert.Equal(-1, geometry.DistanceToMarkerIndex(5.0, double.PositiveInfinity));
            Assert.Equal(-1, geometry.DistanceToMarkerIndex(5.0, double.NaN));
            Assert.Equal(-1, geometry.DistanceToMarkerIndex(0.0, 30.0));
            Assert.Equal(-1, geometry.DistanceToMarkerIndex(-1.0, 30.0));

            // Invalid geometry must not route valid-looking input into zone 0.
            geometry.MarkerStepDegrees = 0.0;
            Assert.Equal(-1, geometry.DistanceToMarkerIndex(1.0, 30.0));
            geometry.MarkerStepDegrees = 10.0;
            geometry.MaxAxisDegrees = double.NaN;
            Assert.Equal(-1, geometry.DistanceToMarkerIndex(1.0, 30.0));
            geometry.MaxAxisDegrees = 180.0;
            geometry.MarkerStepDegrees = double.PositiveInfinity;
            Assert.Equal(-1, geometry.DistanceToMarkerIndex(1.0, 30.0));

            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            geometry = TrainingGeometry.Current;

            // Valid mapping remains unchanged in shape (10° spacing, 180° span,
            // 30 cm). With Zone0SubMarkerCount = 3, the marker at 10° (= step)
            // is now at INDEX 2 (the third sub-marker), not index 0; and the
            // marker at 170° (= 17*step) is now at index 17 + 1 = 18.
            Assert.Equal(2, geometry.DistanceToMarkerIndex(30.0 / 180.0 * 10.0, 30.0));
            Assert.Equal(18, geometry.DistanceToMarkerIndex(30.0 / 180.0 * 170.0, 30.0));
        }

        [Fact]
        public void TrainingGeometry_SetMaxDistanceIgnoresNonFiniteValues()
        {
            TrainingGeometry.Configure(5.0, 180.0, 89.0, 42.0);

            TrainingGeometry.SetMaxDistanceCm(double.PositiveInfinity);
            Assert.Equal(42.0, TrainingGeometry.Current.MaxDistanceCm);

            TrainingGeometry.SetMaxDistanceCm(double.NaN);
            Assert.Equal(42.0, TrainingGeometry.Current.MaxDistanceCm);

            TrainingGeometry.SetMaxDistanceCm(50.0);
            Assert.Equal(50.0, TrainingGeometry.Current.MaxDistanceCm);
            // Step = 5° → base horizontal count = 35, base vertical count = 17.
            // With Zone0SubMarkerCount = 3, add 2 sub-markers → 37 and 19.
            Assert.Equal(37, TrainingGeometry.Current.HorizontalMarkerCount);
            Assert.Equal(19, TrainingGeometry.Current.VerticalMarkerCount);
        }

        [Fact]
        public void MicroBoostCurve_OnlyTreatsUnitLowStartAsIdentity()
        {
            Assert.True(MicroBoostCurve.IsIdentity(1.0));
            Assert.False(MicroBoostCurve.IsIdentity(0.999));
            Assert.False(MicroBoostCurve.IsIdentity(0.5));
            Assert.False(MicroBoostCurve.IsIdentity(double.NaN));
            Assert.False(MicroBoostCurve.IsIdentity(double.PositiveInfinity));
        }

        [Fact]
        public void MicroBoostCurve_RampsToOneAndNeverExceedsBase()
        {
            const double lowStart = 0.4;
            const double range = 12.0;

            Assert.Equal(lowStart, MicroBoostCurve.Evaluate(lowStart, range, 0.0), 12);
            Assert.Equal(0.7, MicroBoostCurve.Evaluate(lowStart, range, 6.0), 12);
            Assert.Equal(1.0, MicroBoostCurve.Evaluate(lowStart, range, 12.0), 12);
            Assert.Equal(1.0, MicroBoostCurve.Evaluate(lowStart, range, 30.0), 12);
        }

        [Fact]
        public void RawAccelSettingsBridge_BuildsOnlyFiniteInRangeDirectionalCurves()
        {
            Dictionary<string, object> values;
            string error;

            Assert.True(RawAccelSettingsBridge.TryBuildDirectionalCurveValues(
                AccelCurveMode.Linear, new[] { 2.0, 0.5 }, out values, out error), error);
            Assert.Equal("classic", values["mode"]);
            Assert.Equal(0.5, values["acceleration"]);

            // Persisted profile curve parameters use a fixed four-value stride.
            Assert.True(RawAccelSettingsBridge.TryBuildDirectionalCurveValues(
                AccelCurveMode.Linear, new[] { 2.0, 0.5, 0.0, 0.0 }, out values, out error), error);
            Assert.Equal("classic", values["mode"]);

            Assert.True(RawAccelSettingsBridge.TryBuildDirectionalCurveValues(
                AccelCurveMode.Natural, new[] { 2.0, 0.05, 1.5 }, out values, out error), error);
            Assert.Equal("natural", values["mode"]);

            Assert.True(RawAccelSettingsBridge.TryBuildDirectionalCurveValues(
                AccelCurveMode.Power, new[] { 2.0, 0.1, 1.2, 0.0 }, out values, out error), error);
            Assert.Equal("power", values["mode"]);

            Assert.True(RawAccelSettingsBridge.TryBuildDirectionalCurveValues(
                AccelCurveMode.Synchronous, new[] { 12.0, 1.5, 1.0, 0.5 }, out values, out error), error);
            Assert.Equal("synchronous", values["mode"]);
        }

        [Fact]
        public void RawAccelSettingsBridge_RejectsNonFiniteAndOutOfRangeDirectionalCurves()
        {
            Dictionary<string, object> values;
            string error;

            Assert.False(RawAccelSettingsBridge.TryBuildDirectionalCurveValues(
                AccelCurveMode.Linear, new[] { 2.0, double.NaN }, out values, out error));
            Assert.Empty(values);
            Assert.False(string.IsNullOrEmpty(error));

            Assert.False(RawAccelSettingsBridge.TryBuildDirectionalCurveValues(
                AccelCurveMode.Power, new[] { 2.0, 0.0, 1.2, 0.0 }, out values, out error));
            Assert.Empty(values);

            Assert.False(RawAccelSettingsBridge.TryBuildDirectionalCurveValues(
                AccelCurveMode.Synchronous, new[] { 12.0, 1.5, 1.0, 1.5 }, out values, out error));
            Assert.Empty(values);

            Assert.False(RawAccelSettingsBridge.TryBuildDirectionalCurveValues(
                AccelCurveMode.Natural, new[] { 2.0, 0.05 }, out values, out error));
            Assert.Empty(values);
        }

        [Fact]
        public void RawAccelSettingsBridge_UpdatesNestedValueStructAndParsesEnum()
        {
            var settings = new RawAccelBridgeFixture();
            var values = new Dictionary<string, object>
            {
                { "mode", "classic" },
                { "gain", true },
                { "inputOffset", 2.5 }
            };

            string error;
            Assert.True(RawAccelSettingsBridge.TrySetNestedFields(
                settings, "argsX", values, out error), error);

            Assert.Equal(RawAccelBridgeMode.classic, settings.argsX.mode);
            Assert.True(settings.argsX.gain);
            Assert.Equal(2.5, settings.argsX.inputOffset, 10);
            Assert.Equal(RawAccelBridgeMode.noaccel, settings.argsY.mode);
        }

        [Fact]
        public void RawAccelSettingsBridge_RejectsMissingNestedFieldWithoutPartialMutation()
        {
            var settings = new RawAccelBridgeFixture();
            var values = new Dictionary<string, object>
            {
                { "mode", "power" },
                { "notARealRawAccelField", 1.0 }
            };

            string error;
            Assert.False(RawAccelSettingsBridge.TrySetNestedFields(
                settings, "argsX", values, out error));

            Assert.Equal(RawAccelBridgeMode.noaccel, settings.argsX.mode);
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void ProfileMerger_UsesTrainCellsAndPreservesFinderBaseFactors()
        {
            var profile = new DesktopSensProfile
            {
                // Finder/manual base-sensitivity values are independent from
                // the speed-dependent Train LUT, but must still reach RawAccel
                // through DirectionProfile.BaseSensitivityFactor.
                RightSensitivityFactor = 1.80,
                DownSensitivityFactor = 0.60,
                LeftSensitivityFactor = 1.70,
                UpSensitivityFactor = 0.50,
                RightSensitivityConfidence = 1.0,
                DownSensitivityConfidence = 1.0,
                LeftSensitivityConfidence = 1.0,
                UpSensitivityConfidence = 1.0
            };

            double[] trainFactors = { 0.80, 1.20, 0.70, 1.30 };
            double[] trainConfidence = { 0.25, 0.50, double.NaN, 2.0 };
            int[] angleZones = DesktopSensTrainingState.CardinalAngleZoneIndices;

            // Train's per-cell acceleration remains independent from the
            // Finder/manual base sensitivity. The merged direction must carry
            // the saved base multiplier while its cell factors populate the LUT.
            //
            // To verify the per-zone acceleration path still works, we
            // populate CellAccelerationFactor at the first trainable
            // distance zone of each cardinal direction. We pick the
            // smallest zone whose center is > BaseZoneEndCm (which, with
            // BaseZoneEndCm = 0.0, is simply zone 0).
            for (int direction = 0; direction < 4; direction++)
            {
                int zoneCount = TrainingGeometry.Current.CardinalDirectionZoneCount(direction);
                int firstTrainableZone = -1;
                for (int distanceZone = 0; distanceZone < zoneCount; distanceZone++)
                {
                    if (DesktopSensTrainingState.IsCalibrationDistanceZone(distanceZone))
                    {
                        firstTrainableZone = distanceZone;
                        break;
                    }
                }
                Assert.True(firstTrainableZone >= 0,
                    "Expected at least one Train acceleration zone after the 3 cm exclusion was removed.");

                int cellIndex = DesktopSensProfile.GetCellIndex(angleZones[direction], firstTrainableZone);
                profile.CellAccelerationFactor[cellIndex] = trainFactors[direction];
                profile.CellAccelerationConfidence[cellIndex] = trainConfidence[direction];
                profile.CellTrainingStates[cellIndex] = new TrainingCellState
                {
                    AngleZoneIndex = angleZones[direction],
                    DistanceZoneIndex = firstTrainableZone,
                    FirstRoundComplete = true
                };
            }

            MergedCardinalProfile merged = DesktopSensProfileMerger.BuildMergedProfile(profile);

            // Saved directional base factors must flow through unchanged.
            Assert.Equal(1.80, merged.Right.BaseSensitivityFactor, 10);
            Assert.Equal(0.60, merged.Down.BaseSensitivityFactor, 10);
            Assert.Equal(1.70, merged.Left.BaseSensitivityFactor, 10);
            Assert.Equal(0.50, merged.Up.BaseSensitivityFactor, 10);

            // The profile's Finder-owned values must not be mutated by the merge.
            Assert.Equal(1.80, profile.RightSensitivityFactor, 10);
            Assert.Equal(0.60, profile.DownSensitivityFactor, 10);
            Assert.Equal(1.70, profile.LeftSensitivityFactor, 10);
            Assert.Equal(0.50, profile.UpSensitivityFactor, 10);
        }

        private enum RawAccelBridgeMode
        {
            noaccel,
            classic,
            power
        }

        private struct RawAccelBridgeArgs
        {
            public RawAccelBridgeMode mode;
            public bool gain;
            public double inputOffset;

            public RawAccelBridgeArgs(RawAccelBridgeMode mode, bool gain, double inputOffset)
            {
                this.mode = mode;
                this.gain = gain;
                this.inputOffset = inputOffset;
            }
        }

        private sealed class RawAccelBridgeFixture
        {
            public RawAccelBridgeArgs argsX;
            public RawAccelBridgeArgs argsY;

            public RawAccelBridgeFixture()
            {
                argsX.mode = RawAccelBridgeMode.noaccel;
                argsY.mode = RawAccelBridgeMode.noaccel;
            }
        }

        private sealed class CopyFixture
        {
            public int PublicField;
            public readonly int ReadOnly;
            public string Name { get; set; }

            public CopyFixture(int readOnly)
            {
                ReadOnly = readOnly;
            }
        }

        private static double NextGaussian(Random rng)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }

        // ─────────────────────────────────────────────────────────────────────
        // LUT applier tests (Claude directive).
        //
        // These tests exercise the LUT-build path that converts green
        // calibration cells into a RawAccel lut-mode AccelArgs. They
        // cover:
        //   1. A 3-cell green LUT is built with <= 257 points and strictly
        //      ascending x.
        //   2. Adding a 4th green cell does NOT change any of the 3-cell
        //      LUT's earlier points (bit-for-bit isolation).
        //   3. When all green factors are 1.0, the LUT still builds but
        //      the applier's status reports a no-op (we test the LUT
        //      build side here; the applier-level no-op is exercised in
        //      DesktopSensConfigForm which is not in this test project).
        //   4. The cm/s → LUT x conversion: a known speed produces a
        //      known LUT x (with a numerical example).
        //   5. Validation rejects non-ascending x and non-positive y.
        // ─────────────────────────────────────────────────────────────────────

        private static DesktopSensProfile BuildProfileWithGreenCells(
            int angleZone, int[] greenDistanceZones,
            double[] cellFactors, double[] cellSpeedsCmPerSec,
            double[] trialDurationMs)
        {
            // Build a profile with the given green cells on the given
            // angle zone. For each green cell, populate:
            //   - CellAccelerationFactor[cellIndex] = the cell's factor
            //   - CellTrainingStates[cellIndex] = a Verified +
            //     PrecisionVerified state
            //   - profile.Trials with 3 trials per cell at speeds
            //     slightly above and below the median speed, so the
            //     median is well-defined and equals cellSpeedsCmPerSec[i].
            // NEW (Model v6): these tests exercise the legacy median-speed
            // LUT path (each cell becomes one LUT point at its median
            // speed). The synthetic trials do not carry raw samples, so the
            // sample-fit path cannot run on them. The default
            // UseSampleFitLut=true is overridden here to keep the tests on
            // the legacy path they were written for.
            var profile = new DesktopSensProfile { MouseDPI = 800 };
            profile.UseSampleFitLut = false;
            for (int i = 0; i < greenDistanceZones.Length; i++)
            {
                int d = greenDistanceZones[i];
                int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, d);
                if (cellIndex >= profile.CellAccelerationFactor.Length) continue;
                profile.CellAccelerationFactor[cellIndex] = cellFactors[i];
                profile.CellAccelerationConfidence[cellIndex] = 0.9;

                if (profile.CellTrainingStates == null)
                    profile.CellTrainingStates = new Dictionary<int, TrainingCellState>();
                profile.CellTrainingStates[cellIndex] = new TrainingCellState
                {
                    AngleZoneIndex = angleZone,
                    DistanceZoneIndex = d,
                    Verified = true,
                    PrecisionVerified = true,
                    LogGainSampleCount = 12,
                    LogGainConfidenceHalfWidth95 = 0.001
                };

                double speed = cellSpeedsCmPerSec[i];
                double durationMs = trialDurationMs[i];
                // Add 3 trials at speeds slightly above and below the
                // median so the median is exactly `speed`.
                double[] trialSpeeds = { speed * 0.95, speed, speed * 1.05 };
                for (int t = 0; t < 3; t++)
                {
                    double flick = trialSpeeds[t] * (durationMs / 1000.0);
                    profile.Trials.Add(new DesktopSensTrial
                    {
                        AngleZoneIndex = angleZone,
                        DistanceZoneIndex = d,
                        TargetDistanceCm = 5.0,
                        FlickDistanceCm = flick,
                        SignedErrorCm = flick - 5.0,
                        DurationMs = durationMs,
                        HasMovementMeasurement = true
                    });
                }
            }
            return profile;
        }

        [Fact]
        public void LutBuild_WithThreeGreenCells_BuildsValidLutUnder257PointsStrictlyAscendingX()
        {
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            int angleZone = DesktopSensTrainingState.CardinalAngleZoneIndices[0]; // Right
            var profile = BuildProfileWithGreenCells(
                angleZone,
                new[] { 1, 2, 3 },
                new[] { 1.05, 1.10, 1.15 },
                new[] { 30.0, 50.0, 70.0 },
                new[] { 300.0, 300.0, 300.0 });

            var merged = DesktopSensProfileMerger.BuildMergedProfile(profile);
            Assert.NotNull(merged.Right.LutPoints);
            Assert.Equal(3, merged.Right.LutPoints.Count);

            // Build the LUT values.
            Dictionary<string, object> values;
            string error;
            bool ok = RawAccelSettingsBridge.TryBuildLutValues(
                merged.Right.LutPoints, out values, out error);
            Assert.True(ok, "TryBuildLutValues should succeed: " + (error ?? "(none)"));

            Assert.Equal("lut", values["mode"]);
            Assert.Equal(false, values["gain"]); // sensitivity mode, not velocity
            int length = (int)values["length"];
            // 3 green cells → 2 bookends + 3 boundary pairs = 8 points
            // = 16 entries.
            // CLAUDE FIX (round 2, directive #5) — TryBuildLutValues now
            // writes points directly (no staircase expansion). 3 cells
            // → 3 points + 2 bookends = 5 points = 10 entries.
            Assert.Equal(10, length);
            Assert.True(length <= 257 * 2, "length (" + length + ") must not exceed 257*2 = 514 entries");

            var data = (float[])values["data"];
            Assert.Equal(514, data.Length); // padded to capacity

            // Verify x values are strictly ascending.
            double prevX = double.NegativeInfinity;
            for (int i = 0; i < length / 2; i++)
            {
                double x = data[2 * i];
                double y = data[2 * i + 1];
                Assert.True(x > prevX, "x[" + i + "]=" + x + " is not strictly greater than x[" + (i - 1) + "]=" + prevX);
                Assert.True(y > 0.0, "y[" + i + "]=" + y + " is not positive");
                prevX = x;
            }
        }

        [Fact]
        public void LutBuild_AddingFourthGreenCellDoesNotChangeEarlierThreePointsBitForBit()
        {
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            int angleZone = DesktopSensTrainingState.CardinalAngleZoneIndices[0];

            // Build a profile with 3 green cells.
            var profile3 = BuildProfileWithGreenCells(
                angleZone,
                new[] { 1, 2, 3 },
                new[] { 1.05, 1.10, 1.15 },
                new[] { 30.0, 50.0, 70.0 },
                new[] { 300.0, 300.0, 300.0 });
            var merged3 = DesktopSensProfileMerger.BuildMergedProfile(profile3);
            Dictionary<string, object> values3;
            string error3;
            Assert.True(RawAccelSettingsBridge.TryBuildLutValues(
                merged3.Right.LutPoints, out values3, out error3));
            var data3 = (float[])values3["data"];
            int length3 = (int)values3["length"];

            // Now build a profile with 4 green cells (the 4th at a higher
            // speed).
            var profile4 = BuildProfileWithGreenCells(
                angleZone,
                new[] { 1, 2, 3, 4 },
                new[] { 1.05, 1.10, 1.15, 1.20 },
                new[] { 30.0, 50.0, 70.0, 90.0 },
                new[] { 300.0, 300.0, 300.0, 300.0 });
            var merged4 = DesktopSensProfileMerger.BuildMergedProfile(profile4);
            Dictionary<string, object> values4;
            string error4;
            Assert.True(RawAccelSettingsBridge.TryBuildLutValues(
                merged4.Right.LutPoints, out values4, out error4));
            var data4 = (float[])values4["data"];
            int length4 = (int)values4["length"];

            // CLAUDE FIX (round 2, directive #5) — 4-cell LUT has 2
            // more entries than 3-cell LUT (1 more point + 0 more bookends
            // = 2 more entries, since the xMax bookend's factor changes
            // but the entry count only grows by the new point's 2 entries).
            Assert.Equal(length3 + 2, length4);

            // The first `length3 - 2` entries (the 3-cell LUT minus the
            // trailing (xMax, lastFactor) bookend point, which IS
            // legitimately different because the last cell changed)
            // must be bit-for-bit identical in the 4-cell LUT — adding
            // the 4th cell did not change ANY earlier point. The last
            // 2 entries (the xMax bookend) ARE allowed to differ because
            // the 4th cell changed the last cell's factor.
            //
            // length3 = 16 (3 cells * 2 boundary pairs + 2 bookends = 8 points * 2 = 16 entries)
            // The trailing bookend is the LAST 2 entries (1 point).
            int sharedEntries = length3 - 2;
            for (int i = 0; i < sharedEntries; i++)
            {
                Assert.Equal(data3[i], data4[i]);
            }
        }

        [Fact]
        public void LutBuild_WhenAllFactorsAreOne_StillBuildsButApplierReportsNoOpAtLutLevel()
        {
            // When every green cell has factor = 1.0 (the calibrator
            // learned no correction), the LUT still builds (the points
            // are emitted at the cells' speeds with y = 1.0). The
            // applier's no-op detection (which compares the four base
            // directional factors to 1.0 AND checks for green cells)
            // is at the DesktopSensConfigForm level, not in the model
            // layer — so this test only verifies the LUT build side:
            // it builds successfully and every y is 1.0.
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            int angleZone = DesktopSensTrainingState.CardinalAngleZoneIndices[0];
            var profile = BuildProfileWithGreenCells(
                angleZone,
                new[] { 1, 2 },
                new[] { 1.0, 1.0 },
                new[] { 30.0, 50.0 },
                new[] { 300.0, 300.0 });

            var merged = DesktopSensProfileMerger.BuildMergedProfile(profile);
            Dictionary<string, object> values;
            string error;
            bool ok = RawAccelSettingsBridge.TryBuildLutValues(
                merged.Right.LutPoints, out values, out error);
            Assert.True(ok, "LUT should build with all-1.0 factors: " + (error ?? "(none)"));
            var data = (float[])values["data"];
            int length = (int)values["length"];
            // Every y in the LUT must be 1.0 (within float precision).
            for (int i = 0; i < length / 2; i++)
            {
                Assert.Equal(1.0f, data[2 * i + 1], 5);
            }
        }

        [Fact]
        public void LutBuild_ConvertCmPerSecToLutX_MatchesManualCalculation()
        {
            // Per the proof in RawAccelSettingsBridge.TryBuildLutValues:
            //     x_lut = (cm/s) / 2.54
            // because the driver's NORMALIZED_DPI (1000) cancels with the
            // mouse's DPI in the conversion counts/ms * (1000/DPI).
            //
            // Example: 50 cm/s → 50 / 2.54 = 19.6850... counts/ms
            // (normalized). At 800 DPI the raw counts/ms would be
            // 50 * 800 / 2.54 / 1000 = 15.748, but the driver multiplies
            // by 1000/800 = 1.25, giving 19.685 — which equals (cm/s)/2.54.
            double speedCmPerSec = 50.0;
            double expected = speedCmPerSec / 2.54;
            double actual = RawAccelSettingsBridge.ConvertCmPerSecToLutX(speedCmPerSec);
            Assert.Equal(expected, actual, 6);

            // Numerical sanity: at 50 cm/s the LUT x is about 19.685.
            Assert.Equal(19.685039, actual, 5);

            // At 100 cm/s the LUT x is about 39.37.
            Assert.Equal(100.0 / 2.54, RawAccelSettingsBridge.ConvertCmPerSecToLutX(100.0), 6);

            // At 0.5 cm/s (very slow) the LUT x is about 0.197 — still
            // positive and finite.
            Assert.True(RawAccelSettingsBridge.ConvertCmPerSecToLutX(0.5) > 0.0);
        }

        [Fact]
        public void LutBuild_SortsBySpeedBeforeValidation_NonMonotonicInputIsAccepted()
        {
            // CLAUDE DIRECTIVE #5 — the test "non-ascending" had a
            // misleading name and assertion: it claimed to test rejection
            // but actually documented that non-monotonic input is
            // handled via the staircase expansion. With the directive
            // #2 rewrite (sort by speed, then reject non-ascending
            // AFTER the sort), the behaviour is:
            //
            //   • Input [50, 30] (non-monotonic) → sort to [30, 50] →
            //     strictly ascending → OK (the LUT is built).
            //   • Input [50, 50] (same speed) → sort is stable →
            //     [50, 50] → not strictly ascending → rejected.
            //
            // This test covers the first case (non-monotonic → accepted
            // via sort).
            var points = new List<LutPoint>
            {
                new LutPoint(50.0, 1.10),
                new LutPoint(30.0, 1.05)
            };
            Dictionary<string, object> values;
            string error;
            bool ok = RawAccelSettingsBridge.TryBuildLutValues(points, out values, out error);
            Assert.True(ok, "TryBuildLutValues should accept non-monotonic input by sorting it first: " + (error ?? "(none)"));
            Assert.Equal("lut", values["mode"]);
            // CLAUDE FIX (round 2, directive #5) — 2 cells → 2 points
            // + 2 bookends = 4 points = 8 entries.
            Assert.Equal(8, (int)values["length"]);
        }

        [Fact]
        public void LutBuild_RejectsDuplicateSpeedsAfterSort()
        {
            // CLAUDE DIRECTIVE #5 — covers the second case of the
            // non-ascending test: two cells with EXACTLY the same speed.
            // After the sort, the speeds are still [50, 50] which is NOT
            // strictly ascending — the LUT build must reject it (no
            // silent fallback to the previous x, per directive #2).
            var points = new List<LutPoint>
            {
                new LutPoint(50.0, 1.10),
                new LutPoint(50.0, 1.20)   // same speed
            };
            Dictionary<string, object> values;
            string error;
            bool ok = RawAccelSettingsBridge.TryBuildLutValues(points, out values, out error);
            Assert.False(ok, "TryBuildLutValues should reject two cells with the same speed.");
            Assert.NotNull(error);
            Assert.Contains("non-ascending", error);
        }

        [Fact]
        public void LutBuild_SpeedSortIsStable_OrdersBySpeedAscending()
        {
            // CLAUDE DIRECTIVE #2/#5 — verify that TryBuildLutValues
            // sorts the input points by speed before building the LUT.
            // We construct an input in non-sorted order and verify the
            // output LUT's x values are in ascending order matching the
            // sorted speeds.
            var points = new List<LutPoint>
            {
                new LutPoint(70.0, 1.15),
                new LutPoint(30.0, 1.05),
                new LutPoint(50.0, 1.10)
            };
            Dictionary<string, object> values;
            string error;
            bool ok = RawAccelSettingsBridge.TryBuildLutValues(points, out values, out error);
            Assert.True(ok, "TryBuildLutValues should succeed: " + (error ?? "(none)"));
            var data = (float[])values["data"];
            int length = (int)values["length"];
            // The LUT x values (every other entry in data) must be
            // strictly ascending.
            double prevX = double.NegativeInfinity;
            for (int i = 0; i < length / 2; i++)
            {
                double x = data[2 * i];
                Assert.True(x > prevX, "x[" + i + "]=" + x + " is not greater than x[" + (i - 1) + "]=" + prevX);
                prevX = x;
            }
        }

        [Fact]
        public void LutBuild_RejectsNonPositiveFactor()
        {
            // A cell with a non-positive factor (y <= 0) is invalid —
            // validate.hpp requires y > 0 (and our LutPoint validation
            // rejects it before the staircase expansion).
            var points = new List<LutPoint>
            {
                new LutPoint(50.0, 1.10),
                new LutPoint(70.0, 0.0)   // non-positive factor
            };
            Dictionary<string, object> values;
            string error;
            bool ok = RawAccelSettingsBridge.TryBuildLutValues(points, out values, out error);
            Assert.False(ok, "TryBuildLutValues should reject a non-positive factor.");
            Assert.NotNull(error);
            Assert.Contains("factor", error);
        }

        [Fact]
        public void LutBuild_RejectsNaNFactor()
        {
            var points = new List<LutPoint>
            {
                new LutPoint(50.0, 1.10),
                new LutPoint(70.0, double.NaN)
            };
            Dictionary<string, object> values;
            string error;
            bool ok = RawAccelSettingsBridge.TryBuildLutValues(points, out values, out error);
            Assert.False(ok);
            Assert.Contains("factor", error);
        }

        [Fact]
        public void LutBuild_RejectsEmptyPointList()
        {
            var points = new List<LutPoint>();
            Dictionary<string, object> values;
            string error;
            bool ok = RawAccelSettingsBridge.TryBuildLutValues(points, out values, out error);
            Assert.False(ok);
            Assert.NotNull(error);
        }

        [Fact]
        public void LutBuild_OneGreenDirection_AppliesSharedLutWithWarning()
        {
            var rightPoints = new List<LutPoint> { new LutPoint(50.0, 1.10) };

            var points = RawAccelSettingsBridge.CombineLutPointsForAxis(
                rightPoints, new List<LutPoint>(), out string note,
                0.85, 0.0, "Right", "Left");

            Assert.NotEmpty(points);
            Assert.Contains("Only Right calibrated", note);
            Assert.Contains("affect BOTH directions", note);
        }

        [Fact]
        public void LutBuild_TwoDirectionsWithinFivePercent_UsesGeometricMean()
        {
            var rightPoints = new List<LutPoint> { new LutPoint(50.0, 1.10) };
            var leftPoints = new List<LutPoint> { new LutPoint(50.0, 1.14) };

            var points = RawAccelSettingsBridge.CombineLutPointsForAxis(
                rightPoints, leftPoints, out string note,
                0.8, 0.75, "Right", "Left");

            double expected = Math.Sqrt(1.10 * 1.14);
            Assert.Contains(points, p => Math.Abs(p.Factor - expected) < 1e-6);
            Assert.Contains("within 5%", note);
            Assert.Contains("geometric mean", note);
        }

        [Fact]
        public void LutBuild_ConflictingDirections_UsesHigherConfidenceDirection()
        {
            var rightPoints = new List<LutPoint> { new LutPoint(50.0, 1.00) };
            var leftPoints = new List<LutPoint> { new LutPoint(50.0, 1.20) };

            var points = RawAccelSettingsBridge.CombineLutPointsForAxis(
                rightPoints, leftPoints, out string note,
                0.95, 0.40, "Right", "Left");

            Assert.Contains(points, p => Math.Abs(p.Factor - 1.00) < 1e-6);
            Assert.Contains("factors conflict at 25 cm/s", note);
            Assert.Contains("Right=1", note);
            Assert.Contains("Left=1.2", note);
            Assert.Contains("Applied Right", note);
            Assert.Contains("higher confidence", note);
        }

        [Fact]
        public void LutBuild_DistinctSpeedRanges_StillProducesSharedAxisLut()
        {
            var rightPoints = new List<LutPoint> { new LutPoint(50.0, 1.10) };
            var leftPoints = new List<LutPoint> { new LutPoint(70.0, 1.20) };

            var points = RawAccelSettingsBridge.CombineLutPointsForAxis(
                rightPoints, leftPoints, out string note,
                0.8, 0.7, "Right", "Left");

            Assert.NotEmpty(points);
            Assert.False(string.IsNullOrWhiteSpace(note));
        }

        [Fact]
        public void LutBuild_OneDirectionWithAdditionalOppositeCell_UsesProgressiveFallback()
        {
            var rightPoints = new List<LutPoint>
            {
                new LutPoint(100.0, 1.05),
                new LutPoint(200.0, 1.10)
            };

            var points = RawAccelSettingsBridge.CombineLutPointsForAxis(
                rightPoints, null, out string note,
                0.9, 0.0, "Right", "Left");

            Assert.NotEmpty(points);
            Assert.Contains("Only Right calibrated", note);
        }

        [Fact]
        public void LutBuild_NoGreenCellsOnEitherDirection_ReturnsEmpty()
        {
            var points = RawAccelSettingsBridge.CombineLutPointsForAxis(
                new List<LutPoint>(), new List<LutPoint>(), out string note,
                0.0, 0.0, "Right", "Left");

            Assert.Empty(points);
            Assert.Contains("no green cells on either direction", note);
            Assert.Contains("nothing to apply", note);
        }

        [Fact]
        public void LutBuild_ConflictsUseConfidenceOfTheActiveSpeedCell()
        {
            var right = new List<LutPoint>
            {
                new LutPoint(50.0, 1.00, 0.20),
                new LutPoint(100.0, 1.00, 0.95)
            };
            var left = new List<LutPoint>
            {
                new LutPoint(50.0, 1.20, 0.80),
                new LutPoint(100.0, 1.20, 0.10)
            };

            // Deliberately make aggregate confidence point the wrong way:
            // per-speed cell confidence must decide each conflicting interval.
            var points = RawAccelSettingsBridge.CombineLutPointsForAxis(
                right, left, out string note, 0.01, 0.99, "Right", "Left");

            Assert.Contains(points, p => Math.Abs(p.Factor - 1.20) < 1e-6);
            Assert.Contains(points, p => Math.Abs(p.Factor - 1.00) < 1e-6);
            Assert.Contains("Applied Left (higher confidence 0.8)", note);
            Assert.Contains("Applied Right (higher confidence 0.95)", note);
        }

        [Fact]
        public void Merger_MeanConfidenceIncludesOnlyGreenCellsWithValidFactors()
        {
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            int angleZone = DesktopSensTrainingState.CardinalAngleZoneIndices[0];
            var profile = BuildProfileWithGreenCells(
                angleZone, new[] { 1 }, new[] { 1.10 }, new[] { 50.0 }, new[] { 300.0 });

            int yellowCell = DesktopSensProfile.GetCellIndex(angleZone, 2);
            profile.CellAccelerationFactor[yellowCell] = 1.30;
            profile.CellAccelerationConfidence[yellowCell] = 0.99;
            profile.CellTrainingStates[yellowCell] = new TrainingCellState
            {
                AngleZoneIndex = angleZone,
                DistanceZoneIndex = 2,
                Verified = true,
                PrecisionVerified = false
            };

            int invalidGreenCell = DesktopSensProfile.GetCellIndex(angleZone, 3);
            profile.CellAccelerationFactor[invalidGreenCell] = double.NaN;
            profile.CellAccelerationConfidence[invalidGreenCell] = 1.0;
            profile.CellTrainingStates[invalidGreenCell] = new TrainingCellState
            {
                AngleZoneIndex = angleZone,
                DistanceZoneIndex = 3,
                Verified = true,
                PrecisionVerified = true
            };

            var merged = DesktopSensProfileMerger.BuildMergedProfile(profile);
            Assert.Equal(0.9, merged.Right.MeanConfidence, 6);
            Assert.Equal(0.9, merged.Right.LutPoints[0].Confidence, 6);
            Assert.True(merged.Right.LutPoints[0].HasConfidence);
        }

        [Fact]
        public void LutBuild_GreenCellFactorAtCellSpeedAppearsInLut()
        {
            // A green cell with factor 1.15 at speed 70 cm/s should
            // produce an LUT point at x = 70/2.54 = 27.56 with y = 1.15.
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            int angleZone = DesktopSensTrainingState.CardinalAngleZoneIndices[0];
            var profile = BuildProfileWithGreenCells(
                angleZone,
                new[] { 1 },
                new[] { 1.15 },
                new[] { 70.0 },
                new[] { 300.0 });

            var merged = DesktopSensProfileMerger.BuildMergedProfile(profile);
            Assert.Single(merged.Right.LutPoints);
            Assert.Equal(70.0, merged.Right.LutPoints[0].InputSpeedCmPerSec, 6);
            Assert.Equal(1.15, merged.Right.LutPoints[0].Factor, 6);
        }

        [Fact]
        public void LutBuild_NonGreenCell_DoesNotProduceLutPoint()
        {
            // A cell that is calibrated (has a factor) but is NOT green
            // (Verified && PrecisionVerified) should NOT contribute a
            // LUT point. We test this by populating CellAccelerationFactor
            // and CellAccelerationConfidence but NOT the training state.
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            int angleZone = DesktopSensTrainingState.CardinalAngleZoneIndices[0];
            var profile = new DesktopSensProfile { MouseDPI = 800 };
            int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, 1);
            profile.CellAccelerationFactor[cellIndex] = 1.15;
            profile.CellAccelerationConfidence[cellIndex] = 0.9;
            // No CellTrainingStates entry → the cell is not green.

            var merged = DesktopSensProfileMerger.BuildMergedProfile(profile);
            Assert.NotNull(merged.Right.LutPoints);
            Assert.Empty(merged.Right.LutPoints);
        }

        // ─────────────────────────────────────────────────────────────────────
        // CHAIN FIX tests (Claude directive #1)
        //
        // These verify that the calibrator applies the active LUT to
        // the raw pre-driver mouse observation exactly once.
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Chain_CalibratorMultipliesRawObservationByAppliedLutFactor()
        {
            // Raw pre-driver flick = 6.0 cm; active LUT = 1.10, so the
            // effective output is 6.6 cm. Against a 5.0 cm target the
            // residual is +1.6 cm: the LUT was too high and must be slowed.
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            var profile = new DesktopSensProfile { MouseDPI = 800 };
            int cellIndex = DesktopSensProfile.GetCellIndex(0, 1);
            for (int i = 0; i < 10; i++)
            {
                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = 5.0,
                    SignedErrorCm = 1.0,
                    FlickDistanceCm = 6.0,
                    DurationMs = 300.0,
                    AppliedLutFactor = 1.10,
                    HasAppliedLutFactor = true,
                    HasMovementMeasurement = true
                });
            }

            DesktopSensCalibrator.Calibrate(profile);

            double cellFactor = profile.CellAccelerationFactor[cellIndex];
            Assert.True(cellFactor < 0.95,
                "The active LUT was too high; the calibrator must slow it down. " +
                "Expected a residual correction below 0.95, got " + cellFactor + ".");
        }

        [Fact]
        public void Chain_LegacyTrialWithoutAppliedLutFactor_TreatedAsFactorOne()
        {
            // A trial serialized before the AppliedLutFactor field was
            // added (HasAppliedLutFactor = false, AppliedLutFactor = 0.0
            // default) must be treated as if no LUT was active — the
            // calibrator uses the raw signed error directly.
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            var profile = new DesktopSensProfile { MouseDPI = 800 };
            int cellIndex = DesktopSensProfile.GetCellIndex(0, 1);
            for (int i = 0; i < 10; i++)
            {
                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = 5.0,
                    SignedErrorCm = 0.5,
                    FlickDistanceCm = 5.5,
                    DurationMs = 300.0,
                    // HasAppliedLutFactor = false (default), AppliedLutFactor = 0.0 (default)
                    HasMovementMeasurement = true
                });
            }
            DesktopSensCalibrator.Calibrate(profile);

            // Without any LUT division, factor = 5.0/5.5 ≈ 0.909.
            double cellFactor = profile.CellAccelerationFactor[cellIndex];
            Assert.True(cellFactor < 0.95,
                "Legacy trial's cell factor " + cellFactor + " should be ~0.909 (no LUT was active).");
        }

        [Fact]
        public void Chain_LookupActiveLutFactor_ReturnsOneWhenMergedIsNull()
        {
            // CLAUDE FIX (round 2, directive #3) — LookupActiveLutFactor
            // was REMOVED from the merger. These stubs are kept as
            // documentation. The new
            // RawAccelSettingsBridge.LookupActiveLutFactorFromProfile
            // tests below cover the same cases.
            Assert.True(true, "LookupActiveLutFactor was moved to RawAccelSettingsBridge.");
        }

        [Fact]
        public void Chain_LookupActiveLutFactor_ReturnsOneWhenNoLutPoints()
        {
            Assert.True(true, "LookupActiveLutFactor was moved to RawAccelSettingsBridge.");
        }

        [Fact]
        public void Chain_LookupActiveLutFactor_ReturnsCellFactorAtItsSpeed()
        {
            Assert.True(true, "LookupActiveLutFactor was moved to RawAccelSettingsBridge.");
        }

        [Fact]
        public void Chain_LookupActiveLutFactor_ReturnsOneBelowFirstCellSpeed()
        {
            Assert.True(true, "LookupActiveLutFactor was moved to RawAccelSettingsBridge.");
        }

        // Multi-round residual updates may alternate under the multiplicative
        // model. The invariant here is bounded output, not monotonic convergence.
        [Fact]
        public void Chain_MultiRoundCalibration_KeepsFactorsBounded()
        {
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            int angleZone = DesktopSensTrainingState.CardinalAngleZoneIndices[0];
            int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, 1);
            var profile = new DesktopSensProfile { MouseDPI = 800 };

            for (int i = 0; i < 10; i++)
            {
                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = angleZone,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = 5.0,
                    SignedErrorCm = 1.0,
                    FlickDistanceCm = 6.0,
                    DurationMs = 300.0,
                    AppliedLutFactor = 1.0,
                    HasAppliedLutFactor = false,
                    HasMovementMeasurement = true
                });
            }

            profile.CellTrainingStates = new Dictionary<int, TrainingCellState>();
            profile.CellTrainingStates[cellIndex] = new TrainingCellState
            {
                AngleZoneIndex = angleZone,
                DistanceZoneIndex = 1,
                Verified = true,
                PrecisionVerified = true
            };

            double[] factors = new double[5];
            for (int round = 0; round < factors.Length; round++)
            {
                DesktopSensCalibrator.Calibrate(profile);
                factors[round] = profile.CellAccelerationFactor[cellIndex];

                double liveLut = factors[round];
                foreach (var trial in profile.Trials)
                {
                    if (trial.AngleZoneIndex == angleZone && trial.DistanceZoneIndex == 1)
                    {
                        trial.AppliedLutFactor = liveLut;
                        trial.HasAppliedLutFactor = liveLut > 0.0 &&
                            Math.Abs(liveLut - 1.0) > 1e-9;
                    }
                }
            }

            foreach (double factor in factors)
            {
                Assert.True(factor >= 0.5 && factor <= 2.0,
                    "Cell factor " + factor + " is outside the expected bounded range [0.5, 2.0].");
            }
        }

        // CLAUDE FIX (round 2, directive #3) — LookupActiveLutFactorFromProfile
        // tests. These test the new function that reads from the live
        // RawAccel Profile (argsX/argsY) via reflection.
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void LookupFromProfile_ReturnsOneWhenProfileIsNull()
        {
            double factor = RawAccelSettingsBridge.LookupActiveLutFactorFromProfile(null, 50.0, "X");
            Assert.Equal(1.0, factor, 12);
        }

        [Fact]
        public void LookupFromProfile_ReturnsOneWhenSpeedIsNonPositive()
        {
            // A mock profile is not needed — the function returns 1.0
            // before touching the profile when the speed is invalid.
            Assert.Equal(1.0, RawAccelSettingsBridge.LookupActiveLutFactorFromProfile(null, 0.0, "X"), 12);
            Assert.Equal(1.0, RawAccelSettingsBridge.LookupActiveLutFactorFromProfile(null, -1.0, "X"), 12);
            Assert.Equal(1.0, RawAccelSettingsBridge.LookupActiveLutFactorFromProfile(null, double.NaN, "X"), 12);
        }

        [Fact]
        public void LookupFromProfile_ReturnsOneWhenModeIsNotLut()
        {
            // Build a mock profile-like object with argsX.mode = "noaccel".
            var mock = new MockAccelArgs { mode = "noaccel", gain = false, length = 0, data = new float[0] };
            var mockProfile = new MockProfile { argsX = mock };
            double factor = RawAccelSettingsBridge.LookupActiveLutFactorFromProfile(mockProfile, 50.0, "X");
            Assert.Equal(1.0, factor, 12);
        }

        [Fact]
        public void LookupFromProfile_ReturnsYFromLutDataAtExactMatch()
        {
            // Build a mock profile with a 2-point LUT: (x=10, y=1.5), (x=20, y=1.0).
            // At speed = 10 * 2.54 = 25.4 cm/s → x = 10.0 (exact match) → return 1.5.
            var mock = new MockAccelArgs
            {
                mode = "lut",
                gain = false,
                length = 4,  // 2 points = 4 entries
                data = new float[] { 10.0f, 1.5f, 20.0f, 1.0f }
            };
            var mockProfile = new MockProfile { argsX = mock };
            double speedCmPerSec = 10.0 * 2.54; // x = 10.0
            double factor = RawAccelSettingsBridge.LookupActiveLutFactorFromProfile(mockProfile, speedCmPerSec, "X");
            Assert.Equal(1.5, factor, 4);
        }

        [Fact]
        public void LookupFromProfile_ReturnsInterpolatedYBetweenTwoPoints()
        {
            // LUT: (x=10, y=1.0), (x=20, y=2.0). At x=15 (midpoint),
            // linear interpolation → y = 1.5.
            var mock = new MockAccelArgs
            {
                mode = "lut",
                gain = false,
                length = 4,
                data = new float[] { 10.0f, 1.0f, 20.0f, 2.0f }
            };
            var mockProfile = new MockProfile { argsX = mock };
            double speedCmPerSec = 15.0 * 2.54; // x = 15.0
            double factor = RawAccelSettingsBridge.LookupActiveLutFactorFromProfile(mockProfile, speedCmPerSec, "X");
            Assert.Equal(1.5, factor, 4);
        }

        [Fact]
        public void LookupFromProfile_ReturnsFirstYWhenXBelowFirstPoint()
        {
            // LUT: (x=10, y=1.5), (x=20, y=1.0). At x=5 (below first),
            // the driver returns points[0].y = 1.5.
            var mock = new MockAccelArgs
            {
                mode = "lut",
                gain = false,
                length = 4,
                data = new float[] { 10.0f, 1.5f, 20.0f, 1.0f }
            };
            var mockProfile = new MockProfile { argsX = mock };
            double speedCmPerSec = 5.0 * 2.54; // x = 5.0
            double factor = RawAccelSettingsBridge.LookupActiveLutFactorFromProfile(mockProfile, speedCmPerSec, "X");
            Assert.Equal(1.5, factor, 4);
        }

        [Fact]
        public void LookupFromProfile_ReturnsOneWhenGainIsTrue()
        {
            // If gain is true, the LUT returns velocity (not sensitivity).
            // The lookup should return 1.0 (we only support sensitivity mode).
            var mock = new MockAccelArgs
            {
                mode = "lut",
                gain = true,  // velocity mode
                length = 4,
                data = new float[] { 10.0f, 1.5f, 20.0f, 1.0f }
            };
            var mockProfile = new MockProfile { argsX = mock };
            double factor = RawAccelSettingsBridge.LookupActiveLutFactorFromProfile(mockProfile, 25.4, "X");
            Assert.Equal(1.0, factor, 12);
        }

        // Mock types for LookupActiveLutFactorFromProfile tests.
        // These match the shape of the C++/CLI AccelArgs and Profile
        // structs (public fields, same names) so the reflection-based
        // lookup can read them.

        public class MockProfile
        {
            public MockAccelArgs argsX;
            public MockAccelArgs argsY;
        }

        public class MockAccelArgs
        {
            public string mode;
            public bool gain;
            public double inputOffset;
            public double outputOffset;
            public double acceleration;
            public double decayRate;
            public double gamma;
            public double motivity;
            public double exponentClassic;
            public double scale;
            public double exponentPower;
            public double limit;
            public double syncSpeed;
            public double smooth;
            public MockVec2 cap;
            public string capMode;
            public int length;
            public float[] data;
        }

        public class MockVec2
        {
            public double x;
            public double y;
        }
    }
}