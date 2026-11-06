using grapher.DesktopSens.Models;
using Xunit;

namespace DesktopSens.Tests.Models
{
    public class DesktopSensTrialValidationTests
    {
        [Fact]
        public void Validate_AllowsLowSampleCountShortDurationHighDispersionAndLargeAngularDeviation()
        {
            var trial = new DesktopSensTrial
            {
                RawSampleCount = 1,
                DurationMs = 10.0,
                MeanVelocityPxPerSec = 100.0,
                VelocityStdDevPxPerSec = 10000.0,
                HasAngularMeasurement = true,
                AngularErrorDegrees = 170.0
            };

            Assert.False(trial.Validate(
                minRawSamples: 3,
                minDurationMs: 40.0,
                maxDurationMs: 4500.0,
                maxAngularErrorDegrees: 15.0));
            Assert.False(trial.WasRejected);
            Assert.Equal(TrialRejectionReason.None, trial.RejectionReason);
        }

        [Fact]
        public void Validate_RejectsNonFiniteAngularSensorMeasurement()
        {
            var trial = new DesktopSensTrial
            {
                HasAngularMeasurement = true,
                AngularErrorDegrees = double.NaN
            };

            Assert.True(trial.Validate());
            Assert.True(trial.WasRejected);
            Assert.Equal(TrialRejectionReason.SensorGlitch, trial.RejectionReason);
        }

        [Fact]
        public void Validate_AcceptsLegacyTrialWithoutRawSampleMetadata()
        {
            var trial = new DesktopSensTrial
            {
                TargetDistanceCm = 10,
                SignedErrorCm = 0.5,
                HasMovementMeasurement = true
            };

            Assert.False(trial.Validate());
            Assert.False(trial.WasRejected);
            Assert.Equal(TrialRejectionReason.None, trial.RejectionReason);
        }
    }
}
