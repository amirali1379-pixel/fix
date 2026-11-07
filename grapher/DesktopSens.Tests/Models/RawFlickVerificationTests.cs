using System;
using System.Collections.Generic;
using grapher.DesktopSens.Models;
using Xunit;

namespace grapher.DesktopSens.Tests.Models
{
    public class RawFlickVerificationTests
    {
        [Fact]
        public void ASDWConfirmedSmallResidualCannotGreenCellWhenRawFlickMissIsLarge()
        {
            var states = new Dictionary<int, TrainingCellState>();
            for (int i = 0; i < 12; i++)
            {
                // Calibration/state residual is near zero after ASDW/normalization,
                // but the original pre-ASDW flick missed by 3 cm.
                DesktopSensTrainingState.RecordAttempt(
                    states, 0, 0, 0.05, DateTime.UtcNow,
                    sessionId: "raw-flick-verification",
                    targetDistanceCm: 5.0,
                    rawFlickErrorCm: -3.0);
            }

            int cellIndex = DesktopSensProfile.GetCellIndex(0, 0);
            Assert.True(states.TryGetValue(cellIndex, out TrainingCellState state));
            Assert.True(state.FirstRoundComplete);
            Assert.False(state.Verified,
                "A cell must not be green when robust raw pre-ASDW error exceeds the fixed tolerance.");
            Assert.True(Math.Abs(state.RobustRawFlickErrorCm) >
                DesktopSensConstants.TrainingGreenRawFlickToleranceCm);
        }
    }
}
