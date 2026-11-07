using System;
using System.Collections.Generic;
using System.Diagnostics;
using grapher.Models.Mouse;
using grapher.DesktopSens.Models;
using Xunit;

namespace grapher.DesktopSens.Tests
{
    public class DesktopSensTrialGateTests
    {
        private static DesktopSensTrial MakeTrial(double targetCm, double flickCm, double durationMs,
            long[] intervalsMs, int dxCounts = 10, double cpi = 80.0)
        {
            var samples = new List<MouseSample>();
            long t = 0;
            samples.Add(new MouseSample { Timestamp = t, DX = dxCounts, DY = 0 });
            for (int i = 0; i < intervalsMs.Length; i++)
            {
                t += (long)(Stopwatch.Frequency * intervalsMs[i] / 1000.0);
                samples.Add(new MouseSample { Timestamp = t, DX = dxCounts, DY = 0 });
            }
            return new DesktopSensTrial
            {
                TargetDistanceCm = targetCm,
                FlickDistanceCm = flickCm,
                DurationMs = durationMs,
                EffectiveCpi = cpi,
                RawSamples = samples,
                RawSampleCount = samples.Count,
                HasMovementMeasurement = true
            };
        }

        [Fact]
        public void RejectsVeryLongTrial()
        {
            var trial = MakeTrial(5.0, 3.0, 9500.0, new long[] { 100, 100 });
            var result = DesktopSensTrialGate.Evaluate(trial, 0.0, true);
            Assert.False(result.Accepted);
            Assert.Equal(TrialRejectionReason.DurationOutOfRange, result.Reason);
        }

        [Fact]
        public void RejectsExtremeOvershoot()
        {
            var trial = MakeTrial(5.0, 13.5, 300.0, new long[] { 100, 100 });
            var result = DesktopSensTrialGate.Evaluate(trial, 0.0, true);
            Assert.False(result.Accepted);
            Assert.Equal(TrialRejectionReason.OvershootOutOfRange, result.Reason);
        }

        [Fact]
        public void RejectsTrialThatDidNotReachZoneStart()
        {
            var trial = MakeTrial(5.0, 1.0, 300.0, new long[] { 100, 100 });
            var result = DesktopSensTrialGate.Evaluate(trial, 2.0, false);
            Assert.False(result.Accepted);
            Assert.Equal(TrialRejectionReason.DidNotEnterZone, result.Reason);
        }

        [Fact]
        public void RejectsLongPauseInsideFlick()
        {
            var trial = MakeTrial(5.0, 3.0, 1200.0, new long[] { 100, 1000, 100 });
            var result = DesktopSensTrialGate.Evaluate(trial, 0.0, true);
            Assert.False(result.Accepted);
            Assert.Equal(TrialRejectionReason.LongPause, result.Reason);
        }

        [Fact]
        public void AcceptsCleanContinuousFlick()
        {
            var trial = MakeTrial(5.0, 1.5, 200.0, new long[] { 100, 100 });
            var result = DesktopSensTrialGate.Evaluate(trial, 0.1, false);
            Assert.True(result.Accepted, result.Reason);
            Assert.Equal(TrialRejectionReason.None, result.Reason);
        }

        [Fact]
        public void RejectsFlickWithInsufficientPeakSpeed()
        {
            var trial = MakeTrial(5.0, 1.5, 200.0, new long[] { 100, 100 }, dxCounts: 1);
            var result = DesktopSensTrialGate.Evaluate(trial, 0.1, false);
            Assert.False(result.Accepted);
            Assert.Equal(TrialRejectionReason.PeakVelocityTooLow, result.Reason);
        }
    }
}
