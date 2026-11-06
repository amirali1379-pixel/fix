using System;
using System.Collections.Generic;
using grapher.DesktopSens.Models;
using Xunit;

namespace DesktopSens.Tests.Models
{
    public class DesktopSensCalibratorSessionBiasTests
    {
        private static DesktopSensTrial Anchor(string sessionId, double error)
        {
            return new DesktopSensTrial
            {
                SessionId = sessionId,
                IsSessionAnchor = true,
                TargetDistanceCm = 2.0,
                SignedErrorCm = error
            };
        }

        [Fact]
        public void SessionBias_UsesMedianOfAtLeastThreeValidAnchors()
        {
            var trials = new List<DesktopSensTrial>
            {
                Anchor("session-A", 0.2), Anchor("session-A", 0.2),
                Anchor("session-A", 0.2), Anchor("session-A", 1.8),
                Anchor("session-B", -0.1), Anchor("session-B", -0.1)
            };
            double bias;
            Assert.True(DesktopSensCalibrator.TryGetSessionBiasLogGain(trials, "session-A", out bias));
            Assert.Equal(Math.Log(1.1), bias, 10);
            Assert.False(DesktopSensCalibrator.TryGetSessionBiasLogGain(trials, "session-B", out bias));
        }

        [Fact]
        public void SessionBias_IgnoresRejectedNonAnchorAndWrongDistanceTrials()
        {
            var rejected = Anchor("session-A", 0.2);
            rejected.WasRejected = true;
            var nonAnchor = Anchor("session-A", 0.2);
            nonAnchor.IsSessionAnchor = false;
            var wrongDistance = Anchor("session-A", 0.2);
            wrongDistance.TargetDistanceCm = 5.0;
            var trials = new List<DesktopSensTrial> { rejected, nonAnchor, wrongDistance, Anchor("session-A", 0.2) };
            double bias;
            Assert.False(DesktopSensCalibrator.TryGetSessionBiasLogGain(trials, "session-A", out bias));
            Assert.Equal(0.0, bias);
        }

        [Fact]
        public void SessionBias_RejectsMissingSessionIdentifier()
        {
            double bias;
            Assert.False(DesktopSensCalibrator.TryGetSessionBiasLogGain(
                new List<DesktopSensTrial> { Anchor("session-A", 0.2) }, null, out bias));
            Assert.Equal(0.0, bias);
        }
    }
}
