using System;
using System.Collections.Generic;
using grapher.DesktopSens.Models;
using Xunit;

namespace DesktopSens.Tests.Models
{
    public class CalibrationResidualOrderTests
    {
        [Fact]
        public void Calibrate_ProducesSameCellFactorsWhenTrialInputOrderIsReversed()
        {
            var trials = new List<DesktopSensTrial>();
            for (int zone = 0; zone <= 3; zone++)
            {
                double target = TrainingGeometry.Current.ZoneCenterCm(zone);
                for (int sample = 0; sample < 8; sample++)
                {
                    double gain = 1.025 + (sample - 3.5) * 0.0008;
                    double observed = target * gain;
                    trials.Add(new DesktopSensTrial
                    {
                        AngleZoneIndex = 0,
                        DistanceZoneIndex = zone,
                        TargetDistanceCm = target,
                        SignedErrorCm = observed - target,
                        ProjectedDistanceCm = observed,
                        FlickDistanceCm = observed,
                        HasMovementMeasurement = true
                    });
                }
            }

            var forward = BuildProfile(trials);
            var reversedTrials = new List<DesktopSensTrial>(trials);
            reversedTrials.Reverse();
            var reversed = BuildProfile(reversedTrials);

            DesktopSensCalibrator.Calibrate(forward);
            DesktopSensCalibrator.Calibrate(reversed);

            for (int zone = 0; zone <= 3; zone++)
            {
                int cell = DesktopSensProfile.GetCellIndex(0, zone);
                Assert.Equal(forward.CellSensitivityFactor[cell], reversed.CellSensitivityFactor[cell], 10);
                Assert.Equal(forward.CellAccelerationFactor[cell], reversed.CellAccelerationFactor[cell], 10);
                Assert.Equal(forward.CellSensitivityConfidence[cell], reversed.CellSensitivityConfidence[cell], 10);
                Assert.Equal(forward.CellAccelerationConfidence[cell], reversed.CellAccelerationConfidence[cell], 10);
            }
        }

        private static DesktopSensProfile BuildProfile(List<DesktopSensTrial> trials)
        {
            return new DesktopSensProfile
            {
                MouseDPI = 800,
                Trials = new List<DesktopSensTrial>(trials)
            };
        }
    }
}
