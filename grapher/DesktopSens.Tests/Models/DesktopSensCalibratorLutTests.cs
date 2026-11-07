using grapher.DesktopSens.Models;
using Xunit;

namespace DesktopSens.Tests.Models
{
    public class DesktopSensCalibratorLutTests
    {
        [Fact]
        public void ComputeChainAdjustedResidual_MultipliesPreDriverObservationByActiveLut()
        {
            var profile = new DesktopSensProfile();
            var trial = new DesktopSensTrial
            {
                TargetDistanceCm = 10.0,
                HasAppliedLutFactor = true,
                AppliedLutFactor = 1.5
            };

            double residual = DesktopSensCalibrator.ComputeChainAdjustedResidual(
                profile, trial, 2.0);

            // Raw input observed 12 cm; the active driver LUT turns it into
            // 18 cm. The remaining error is therefore +8 cm, not -4 cm.
            Assert.Equal(8.0, residual, 12);
        }

        [Fact]
        public void ComputeChainAdjustedResidual_ComposesBaseAndLutFactorsOnce()
        {
            var profile = new DesktopSensProfile();
            var trial = new DesktopSensTrial
            {
                TargetDistanceCm = 10.0,
                AppliedBaseFactor = 1.2,
                HasAppliedLutFactor = true,
                AppliedLutFactor = 1.5
            };

            double residual = DesktopSensCalibrator.ComputeChainAdjustedResidual(
                profile, trial, 2.0);

            // 12 cm raw input × 1.2 base × 1.5 LUT = 21.6 cm output.
            Assert.Equal(11.6, residual, 12);
        }

        [Fact]
        public void ComputeChainAdjustedResidual_LeavesPreLutTrialUnchanged()
        {
            var profile = new DesktopSensProfile();
            var trial = new DesktopSensTrial
            {
                TargetDistanceCm = 10.0,
                HasAppliedLutFactor = false,
                AppliedLutFactor = 1.0
            };

            double residual = DesktopSensCalibrator.ComputeChainAdjustedResidual(
                profile, trial, 2.0);

            Assert.Equal(2.0, residual, 12);
        }

        [Fact]
        public void ComputeChainAdjustedResidual_LeavesEvaluationOnlyTrialUnchanged()
        {
            var profile = new DesktopSensProfile();
            var trial = new DesktopSensTrial
            {
                TargetDistanceCm = 10.0,
                HasAppliedLutFactor = true,
                AppliedLutFactor = 1.5,
                IsEvaluationOnly = true
            };

            double residual = DesktopSensCalibrator.ComputeChainAdjustedResidual(
                profile, trial, 2.0);

            Assert.Equal(2.0, residual, 12);
        }
    }
}
