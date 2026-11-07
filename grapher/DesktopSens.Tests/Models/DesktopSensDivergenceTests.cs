using System;
using System.Collections.Generic;
using grapher.DesktopSens.Models;
using Xunit;

namespace grapher.DesktopSens.Tests.Models
{
    public class DesktopSensDivergenceTests
    {
        [Fact]
        public void ThreeConsecutiveRobustErrorWorsenings_MarkCellDivergingAndNeverVerified()
        {
            var states = new Dictionary<int, TrainingCellState>();
            double[] errors = { 1.0, 1.2, 1.5, 2.0 };
            for (int i = 0; i < errors.Length; i++)
                DesktopSensTrainingState.RecordAttempt(
                    states, 0, 0, -errors[i], DateTime.UtcNow,
                    sessionId: "divergence-test", targetDistanceCm: 5.0);

            int cellIndex = DesktopSensProfile.GetCellIndex(0, 0);
            Assert.True(states.TryGetValue(cellIndex, out TrainingCellState state));
            Assert.True(state.IsDiverging);
            Assert.True(state.DivergenceWorseningStreak >= DesktopSensConstants.DivergenceWorseningStreakLimit);
            Assert.False(state.Verified);
            Assert.False(state.PrecisionVerified);
        }
    }
}
