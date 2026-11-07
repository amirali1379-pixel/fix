using grapher.DesktopSens.Models;
using Xunit;

namespace DesktopSens.Tests.Models
{
    public class DesktopSensCalibratorResidualTests
    {
        [Fact]
        public void ComputeChainAdjustedResidual_PreservesRawErrorForMissingInputs()
        {
            var trial = new DesktopSensTrial();
            Assert.Equal(0.75, DesktopSensCalibrator.ComputeChainAdjustedResidual(null, trial, 0.75), 12);
            Assert.Equal(0.75, DesktopSensCalibrator.ComputeChainAdjustedResidual(new DesktopSensProfile(), null, 0.75), 12);
        }

        [Fact]
        public void Calibrate_CalibratesMicroAndFarZonesUniformly()
        {
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            var profile = new DesktopSensProfile();
            profile.Trials.Add(new DesktopSensTrial
            {
                AngleZoneIndex = 0,
                DistanceZoneIndex = 0,
                TargetDistanceCm = 2.0,
                SignedErrorCm = 1.0,
                HasMovementMeasurement = true
            });
            profile.Trials.Add(new DesktopSensTrial
            {
                AngleZoneIndex = 0,
                DistanceZoneIndex = 1,
                TargetDistanceCm = 5.0,
                SignedErrorCm = 1.0,
                HasMovementMeasurement = true
            });

            DesktopSensCalibrator.Calibrate(profile);

            // After the user's directive to remove the 3 cm base-zone
            // exclusion, BaseZoneEndCm is 0.0. Both the micro-flick
            // (target 2 cm) and the far flick (target 5 cm) feed the
            // SAME CellAccelerationFactor path.
            int microCell = DesktopSensProfile.GetCellIndex(0, 0);
            int farCell = DesktopSensProfile.GetCellIndex(0, 1);

            // CellSensitivityFactor / Confidence keep their defaults
            // (the calibrator no longer writes them with BaseZoneEndCm=0).
            Assert.Equal(1.0, profile.CellSensitivityFactor[microCell], 12);
            Assert.Equal(0.0, profile.CellSensitivityConfidence[microCell], 12);
            Assert.Equal(1.0, profile.CellSensitivityFactor[farCell], 12);
            Assert.Equal(0.0, profile.CellSensitivityConfidence[farCell], 12);

            Assert.True(profile.CellAccelerationConfidence[microCell] > 0.0);
            Assert.True(profile.CellAccelerationConfidence[farCell] > 0.0);

            // Micro cell (zone 0) has no earlier zone, so no chain — factor
            // is exactly 2 / (2 + 1) = 2/3.
            Assert.Equal(2.0 / 3.0, profile.CellAccelerationFactor[microCell], 6);

            // Spatial-chain replay has been removed. Each cell is
            // calibrated from its own observation, so the far cell uses
            // the direct target/observed ratio: 5 / 6 = 0.833333...
            Assert.Equal(0.8333333333333333, profile.CellAccelerationFactor[farCell], 6);
        }

        [Fact]
        public void ComputeChainAdjustedResidual_DoesNotApplyCalibrationToEvaluationOnlyTrial()
        {
            var profile = new DesktopSensProfile();
            var trial = new DesktopSensTrial { IsEvaluationOnly = true };
            Assert.Equal(-0.4, DesktopSensCalibrator.ComputeChainAdjustedResidual(profile, trial, -0.4), 12);
        }
    }
}
