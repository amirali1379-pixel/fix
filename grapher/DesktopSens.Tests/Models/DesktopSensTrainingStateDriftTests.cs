using System;
using System.Collections.Generic;
using grapher.DesktopSens.Models;
using Xunit;

namespace DesktopSens.Tests.Models
{
    public class DesktopSensTrainingStateDriftTests
    {
        [Fact]
        public void DetectDrift_DowngradesVerifiedCellAndClearsOldSessionHistory()
        {
            var state = new TrainingCellState
            {
                AngleZoneIndex = 0,
                DistanceZoneIndex = 0,
                Verified = true,
                VerifiedSessionId = "session-old",
                AdaptiveToleranceCm = 0.5,
                RobustSigmaCm = 0.1,
                SignedErrorHistoryCm = new List<double> { 0.05, -0.02, 0.03 },
                TargetDistanceHistoryCm = new List<double> { 5.0, 5.0, 5.0 },
                AngularErrorHistoryDegrees = new List<double> { 0.2, -0.1 },
                LogGainSampleCount = 3,
                LogGainEstimate = 0.02,
                LogGainStandardError = 0.01,
                LogGainConfidenceHalfWidth95 = 0.02,
                PrecisionVerified = true,
                LowPrecision = false,
                RobustSignedErrorCm = 0.03
            };
            var states = new Dictionary<int, TrainingCellState>
            {
                { DesktopSensTrainingState.GetCellIndex(0, 0), state }
            };

            bool drifted = DesktopSensTrainingState.DetectDrift(
                states, 0, 0, 1.0, DateTime.UtcNow, "session-new");

            Assert.True(drifted);
            Assert.True(state.DriftDetected);
            Assert.Equal(1.0, state.DriftSignedErrorCm);
            Assert.False(state.Verified);
            Assert.Null(state.VerifiedTimestampUtc);
            Assert.Null(state.VerifiedSessionId);
            Assert.Empty(state.SignedErrorHistoryCm);
            Assert.Empty(state.TargetDistanceHistoryCm);
            Assert.Empty(state.AngularErrorHistoryDegrees);
            Assert.Equal(0, state.LogGainSampleCount);
            Assert.Equal(0.0, state.LogGainEstimate);
            Assert.Equal(-1.0, state.LogGainStandardError);
            Assert.Equal(-1.0, state.LogGainConfidenceHalfWidth95);
            Assert.False(state.PrecisionVerified);
            Assert.False(state.LowPrecision);
            Assert.Equal(0.0, state.RobustSignedErrorCm);
        }

        [Fact]
        public void DetectDrift_DoesNotDowngradeVerifiedCellInsideThreshold()
        {
            var state = new TrainingCellState
            {
                AngleZoneIndex = 0,
                DistanceZoneIndex = 0,
                Verified = true,
                AdaptiveToleranceCm = 0.5,
                RobustSigmaCm = 0.1,
                SignedErrorHistoryCm = new List<double> { 0.05 },
                TargetDistanceHistoryCm = new List<double> { 5.0 }
            };
            var states = new Dictionary<int, TrainingCellState>
            {
                { DesktopSensTrainingState.GetCellIndex(0, 0), state }
            };

            bool drifted = DesktopSensTrainingState.DetectDrift(
                states, 0, 0, 0.2, DateTime.UtcNow, "session-new");

            Assert.False(drifted);
            Assert.True(state.Verified);
            Assert.False(state.DriftDetected);
            Assert.Single(state.SignedErrorHistoryCm);
        }
    }
}
