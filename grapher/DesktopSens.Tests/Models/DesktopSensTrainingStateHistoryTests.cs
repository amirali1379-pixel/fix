using System;
using System.Collections.Generic;
using grapher.DesktopSens.Models;
using Xunit;

namespace DesktopSens.Tests.Models
{
    public class DesktopSensTrainingStateHistoryTests
    {
        [Fact]
        public void RecordAttempt_KeepsTargetErrorPairsAlignedAndCapsHistory()
        {
            var states = new Dictionary<int, TrainingCellState>();
            DateTime start = DateTime.UtcNow;

            for (int i = 4; i <= TrainingCellState.MaxStoredHistorySamples + 9; i++)
            {
                DesktopSensTrainingState.RecordAttempt(
                    states,
                    angleZoneIndex: 0,
                    distanceZoneIndex: 1,
                    signedErrorCm: i / 100.0,
                    timestampUtc: start.AddMilliseconds(i),
                    sessionId: "session-history",
                    targetDistanceCm: i);
            }

            int cellIndex = DesktopSensTrainingState.GetCellIndex(0, 1);
            TrainingCellState state = states[cellIndex];

            Assert.Equal(TrainingCellState.MaxStoredHistorySamples, state.SignedErrorHistoryCm.Count);
            Assert.Equal(state.SignedErrorHistoryCm.Count, state.TargetDistanceHistoryCm.Count);
            Assert.Equal(0.10, state.SignedErrorHistoryCm[0], 10);
            Assert.Equal(10.0, state.TargetDistanceHistoryCm[0], 10);
            Assert.Equal((TrainingCellState.MaxStoredHistorySamples + 9) / 100.0, state.SignedErrorHistoryCm[state.SignedErrorHistoryCm.Count - 1], 10);
            Assert.Equal((double)(TrainingCellState.MaxStoredHistorySamples + 9), state.TargetDistanceHistoryCm[state.TargetDistanceHistoryCm.Count - 1], 10);
        }

        [Fact]
        public void RecordRejectedAttempt_CountsAttemptWithoutAddingCalibrationSamples()
        {
            var states = new Dictionary<int, TrainingCellState>();
            DateTime timestamp = DateTime.UtcNow;

            DesktopSensTrainingState.RecordRejectedAttempt(
                states, 0, 3, 2.5, timestamp, "rejected-session");

            int cellIndex = DesktopSensTrainingState.GetCellIndex(0, 3);
            TrainingCellState state = states[cellIndex];

            Assert.Equal(1, state.TotalAttemptCount);
            Assert.Equal(0, state.SuccessfulAttemptCount);
            Assert.Empty(state.SignedErrorHistoryCm);
            Assert.Empty(state.TargetDistanceHistoryCm);
            Assert.Equal(2.5, state.LastSignedErrorCm, 10);
            Assert.Equal(timestamp, state.LastAttemptTimestampUtc);
            Assert.Equal("rejected-session", state.LastAttemptSessionId);
            Assert.Equal(1, DesktopSensTrainingState.CountAttemptedCells(states));
            Assert.False(state.Verified);
        }

        [Fact]
        public void RecordAttempt_DoesNotCreateCalibrationStateForMicroFlickZone()
        {
            // NOTE: after the user's directive to remove the 3 cm base-zone
            // exclusion, BaseZoneEndCm = 0.0 and every zone (including the
            // micro-flick zone 0) is a calibration target. RecordAttempt
            // therefore DOES create a TrainingCellState for zone 0 now.
            // The original test asserted `states` was empty after a
            // zone-0 RecordAttempt; that assertion only held under the
            // legacy 3 cm exclusion. This updated test verifies the new
            // behaviour: both zone 0 and zone 1 get a state, both with
            // TotalAttemptCount = 1.
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            var states = new Dictionary<int, TrainingCellState>();

            DesktopSensTrainingState.RecordAttempt(
                states, 0, 0, 0.2, DateTime.UtcNow, targetDistanceCm: 2.0);
            Assert.Contains(DesktopSensTrainingState.GetCellIndex(0, 0), states);
            Assert.Equal(1, states[DesktopSensTrainingState.GetCellIndex(0, 0)].TotalAttemptCount);

            DesktopSensTrainingState.RecordAttempt(
                states, 0, 1, 0.2, DateTime.UtcNow, targetDistanceCm: 5.0);
            Assert.Contains(DesktopSensTrainingState.GetCellIndex(0, 1), states);
            Assert.Equal(1, states[DesktopSensTrainingState.GetCellIndex(0, 1)].TotalAttemptCount);
        }

        [Fact]
        public void RecordAttempt_RejectsNonFiniteSignedErrorWithoutCreatingCell()
        {
            var states = new Dictionary<int, TrainingCellState>();

            DesktopSensTrainingState.RecordAttempt(
                states, 0, 0, double.NaN, DateTime.UtcNow, targetDistanceCm: 5.0);

            Assert.Empty(states);
        }
    }
}
