using System;
using grapher.DesktopSens.Models;
using Xunit;

namespace DesktopSens.Tests.Models
{
    public class DesktopSensCalibratorRecoveryTests
    {
        [Fact]
        public void Calibrate_RecoversFarDistanceAccelerationWithVariableTargetsAndOutliers()
        {
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            const double trueGain = 1.12;
            var random = new Random(1);
            var profile = new DesktopSensProfile { MouseDPI = 800 };

            for (int i = 0; i < 200; i++)
            {
                double target = 3.2 + (i % 10) * 0.18;
                double noise = Math.Exp(0.05 * NextGaussian(random));
                // Uniform-gain simulation (BaseZoneEndCm = 0.0): the gain
                // applies to the FULL target, not to the excess beyond 3 cm.
                double observed = target * trueGain * noise;
                if (i % 20 == 0) observed *= 1.35;

                profile.Trials.Add(new DesktopSensTrial
                {
                    AngleZoneIndex = 0,
                    DistanceZoneIndex = 1,
                    TargetDistanceCm = target,
                    SignedErrorCm = observed - target,
                    ProjectedDistanceCm = observed,
                    FlickDistanceCm = observed,
                    HasMovementMeasurement = true,
                    DurationMs = 300.0,
                    RawSampleCount = 10
                });
            }

            DesktopSensCalibrator.Calibrate(profile);

            int cell = DesktopSensProfile.GetCellIndex(0, 1);
            double expectedFactor = 1.0 / trueGain;
            Assert.InRange(profile.CellAccelerationFactor[cell],
                expectedFactor * 0.99, expectedFactor * 1.01);
        }

        private static double NextGaussian(Random random)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = 1.0 - random.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }
    }
}
