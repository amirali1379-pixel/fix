using System.Collections.Generic;
using grapher.DesktopSens.Models;
using Xunit;

namespace DesktopSens.Tests.Models
{
    public class AdaptiveTrainingScheduleTests
    {
        [Fact]
        public void NextAdaptiveDistanceZone_PrioritizesLargestConfidenceHalfWidth()
        {
            var states = new Dictionary<int, TrainingCellState>();
            int count = TrainingGeometry.Current.CardinalDirectionZoneCount(0);
            for (int d = 0; d < count; d++)
            {
                states[DesktopSensTrainingState.GetCellIndex(0, d)] = new TrainingCellState
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = d,
                    LogGainConfidenceHalfWidth95 = 0.01 + d * 0.001,
                    LogGainSampleCount = 10
                };
            }

            int selected = DesktopSensTrainingState.NextAdaptiveDistanceZone(
                states, 0, 0, allowOverride: true, random: new System.Random(7));

            Assert.Equal(count - 1, selected);
        }

        [Fact]
        public void NextAdaptiveDistanceZone_SkipsVerifiedCells()
        {
            var states = new Dictionary<int, TrainingCellState>();
            int count = TrainingGeometry.Current.CardinalDirectionZoneCount(0);
            for (int d = 0; d < count; d++)
            {
                states[DesktopSensTrainingState.GetCellIndex(0, d)] = new TrainingCellState
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = d,
                    Verified = true,
                    LogGainConfidenceHalfWidth95 = 0.5,
                    LogGainSampleCount = 5
                };
            }
            int expected = count - 1;
            states[DesktopSensTrainingState.GetCellIndex(0, expected)].Verified = false;

            Assert.Equal(expected, DesktopSensTrainingState.NextAdaptiveDistanceZone(
                states, 0, 0, allowOverride: true, random: new System.Random(11)));
        }
        
        [Fact]
        public void CountAttemptedCells_CountsDistinctCellsWithAcceptedAttempts()
        {
            var states = new Dictionary<int, TrainingCellState>();
            int axis0Count = TrainingGeometry.Current.CardinalDirectionZoneCount(0);
            int axis1Count = TrainingGeometry.Current.CardinalDirectionZoneCount(1);

            states[DesktopSensTrainingState.GetCellIndex(
                DesktopSensTrainingState.CardinalAngleZoneIndices[0], 0)] =
                new TrainingCellState { TotalAttemptCount = 1, Verified = false };
            states[DesktopSensTrainingState.GetCellIndex(
                DesktopSensTrainingState.CardinalAngleZoneIndices[0], 1)] =
                new TrainingCellState { TotalAttemptCount = 4, Verified = true };
            states[DesktopSensTrainingState.GetCellIndex(
                DesktopSensTrainingState.CardinalAngleZoneIndices[1], 0)] =
                new TrainingCellState { TotalAttemptCount = 0, Verified = false };

            // Both Right-axis cells (zone 0 and zone 1) have TotalAttemptCount
            // > 0, so CountAttemptedCells returns 2 (calibration coverage
            // counts every cell with at least one accepted attempt, whether
            // or not it is Verified). The Down cell has TotalAttemptCount = 0
            // so it does not count.
            Assert.Equal(2, DesktopSensTrainingState.CountAttemptedCells(states));
            Assert.Equal(1, DesktopSensTrainingState.CountVerifiedCells(states));
            Assert.True(axis0Count > 1);
            Assert.True(axis1Count > 0);
        }

        [Fact]
        public void CountAttemptedCells_NullOrEmptyStatesReturnsZero()
        {
            Assert.Equal(0, DesktopSensTrainingState.CountAttemptedCells(null));
            Assert.Equal(0, DesktopSensTrainingState.CountAttemptedCells(
                new Dictionary<int, TrainingCellState>()));
        }
    }
}
