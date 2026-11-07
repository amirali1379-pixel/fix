using System;
using System.Collections.Generic;
using grapher.DesktopSens.Models;
using Xunit;

namespace DesktopSens.Tests.Models
{
    public class SessionRepeatabilityTests
    {
        [Fact]
        public void RecordAttempt_FlagsSessionGainSpreadAboveTwoPercent()
        {
            var states = new Dictionary<int, TrainingCellState>();
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 5; i++)
                DesktopSensTrainingState.RecordAttempt(states, 0, 0, 0.0,
                    start.AddSeconds(i), "session-A", targetDistanceCm: 2.0);

            int cell = DesktopSensTrainingState.GetCellIndex(0, 0);
            Assert.False(states[cell].SessionRepeatabilityFailed);

            for (int i = 0; i < 5; i++)
                DesktopSensTrainingState.RecordAttempt(states, 0, 0, 0.2,
                    start.AddMinutes(1).AddSeconds(i), "session-B", targetDistanceCm: 2.0);

            Assert.True(states[cell].SessionRepeatabilityFailed);
            Assert.True(states[cell].SessionGainSpreadLog > Math.Log(1.02));
        }
    }
}
