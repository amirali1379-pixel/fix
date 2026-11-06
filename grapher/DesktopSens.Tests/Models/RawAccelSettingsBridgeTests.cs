using System;
using System.Collections.Generic;
using grapher.DesktopSens.Models;
using Xunit;

namespace DesktopSens.Tests.Models
{
    public class RawAccelSettingsBridgeTests
    {
        [Fact]
        public void LookupActiveDirectionalBaseFactorFromProfile_UsesRawAccelAxisRatios()
        {
            var active = new DummyActiveProfile
            {
                outputDPI = 1200.0,
                lrOutputDPIRatio = 0.5,
                yxOutputDPIRatio = 0.8,
                udOutputDPIRatio = 1.5
            };

            Assert.Equal(1.2, RawAccelSettingsBridge.LookupActiveDirectionalBaseFactorFromProfile(
                active, 1000.0, "Right"), 12);
            Assert.Equal(0.6, RawAccelSettingsBridge.LookupActiveDirectionalBaseFactorFromProfile(
                active, 1000.0, "Left"), 12);
            Assert.Equal(0.96, RawAccelSettingsBridge.LookupActiveDirectionalBaseFactorFromProfile(
                active, 1000.0, "Down"), 12);
            Assert.Equal(1.44, RawAccelSettingsBridge.LookupActiveDirectionalBaseFactorFromProfile(
                active, 1000.0, "Up"), 12);
        }

        private sealed class DummyActiveProfile
        {
            public double outputDPI;
            public double lrOutputDPIRatio;
            public double yxOutputDPIRatio;
            public double udOutputDPIRatio;
        }

        [Fact]
        public void CombineSampleFitLutPointsForAxis_PreservesContinuousCurveOnSharedRange()
        {
            var right = new List<LutPoint>
            {
                new LutPoint(10.0, 1.0),
                new LutPoint(100.0, 2.0)
            };
            var left = new List<LutPoint>
            {
                new LutPoint(10.0, 1.0),
                new LutPoint(100.0, 4.0)
            };

            List<LutPoint> combined = RawAccelSettingsBridge.CombineSampleFitLutPointsForAxis(
                right, left, out string note, "Right", "Left");

            Assert.Equal(2, combined.Count);
            Assert.Equal(10.0, combined[0].InputSpeedCmPerSec, 10);
            Assert.Equal(1.0, combined[0].Factor, 10);
            Assert.Equal(100.0, combined[1].InputSpeedCmPerSec, 10);
            Assert.Equal(Math.Sqrt(8.0), combined[1].Factor, 10);
            Assert.Contains("Continuous sample-fit", note);

            // The output has only the fitted knots: no epsilon hold points or
            // staircase boundaries are inserted between the nodes.
            Assert.Equal(2, combined.Count);
        }

        [Fact]
        public void CombineSampleFitLutPointsForAxis_UsesOnlySharedSpeedDomain()
        {
            var right = new List<LutPoint>
            {
                new LutPoint(5.0, 1.0),
                new LutPoint(20.0, 1.5),
                new LutPoint(100.0, 2.0)
            };
            var left = new List<LutPoint>
            {
                new LutPoint(10.0, 1.0),
                new LutPoint(50.0, 2.0),
                new LutPoint(200.0, 3.0)
            };

            List<LutPoint> combined = RawAccelSettingsBridge.CombineSampleFitLutPointsForAxis(
                right, left, out _);

            Assert.NotEmpty(combined);
            Assert.Equal(10.0, combined[0].InputSpeedCmPerSec, 10);
            Assert.Equal(100.0, combined[combined.Count - 1].InputSpeedCmPerSec, 10);
            for (int i = 1; i < combined.Count; i++)
                Assert.True(combined[i].InputSpeedCmPerSec > combined[i - 1].InputSpeedCmPerSec);
        }

        [Fact]
        public void TryBuildLutValues_NeutralUpperBookendStartsImmediatelyAfterLastPoint()
        {
            var points = new List<LutPoint> { new LutPoint(100.0, 2.0) };
            Assert.True(RawAccelSettingsBridge.TryBuildLutValues(
                points, out Dictionary<string, object> values, out string error,
                LutUpperBookendMode.NeutralOne), error);

            var data = (float[])values["data"];
            int length = (int)values["length"];
            int pointCount = length / 2;
            double lastPointX = data[(pointCount - 2) * 2];
            double upperX = data[(pointCount - 1) * 2];
            Assert.Equal(2.0, data[(pointCount - 2) * 2 + 1], 5);
            Assert.Equal(1.0, data[(pointCount - 1) * 2 + 1], 5);
            Assert.True(upperX > lastPointX);
            Assert.True(upperX - lastPointX < lastPointX * 0.001,
                "Neutral upper bookend must not create a long untested interpolation ramp.");
        }

        [Fact]
        public void TryBuildLutValues_ExtendLastFactorKeepsLastFactorAtUpperBookend()
        {
            var points = new List<LutPoint> { new LutPoint(100.0, 1.7) };
            Assert.True(RawAccelSettingsBridge.TryBuildLutValues(
                points, out Dictionary<string, object> values, out string error,
                LutUpperBookendMode.ExtendLastFactor), error);

            var data = (float[])values["data"];
            int pointCount = (int)values["length"] / 2;
            Assert.Equal(1.7, data[(pointCount - 2) * 2 + 1], 5);
            Assert.Equal(1.7, data[(pointCount - 1) * 2 + 1], 5);
        }

        [Fact]
        public void CombineLutPointsForAxis_AppliesOneSidedCalibrationWithWarning()
        {
            var right = new List<LutPoint> { new LutPoint(100.0, 0.8) };

            List<LutPoint> combined = RawAccelSettingsBridge.CombineLutPointsForAxis(
                right, new List<LutPoint>(), out string note);

            Assert.NotEmpty(combined);
            Assert.Contains("Only positive calibrated", note);
            Assert.Contains("affect BOTH directions", note);
        }

        [Fact]
        public void CombineLutPointsForAxis_UsesGeometricMeanForEqualConfidenceConflict()
        {
            var positive = new List<LutPoint> { new LutPoint(100.0, 0.8) };
            var negative = new List<LutPoint> { new LutPoint(100.0, 0.9) };

            List<LutPoint> combined = RawAccelSettingsBridge.CombineLutPointsForAxis(
                positive, negative, out string note);

            double expected = Math.Sqrt(0.8 * 0.9);
            Assert.Contains(combined, p => Math.Abs(p.Factor - expected) < 1e-6);
            Assert.Contains("conflict", note);
            Assert.Contains("confidences tied", note);
        }

        [Fact]
        public void CombineLutPointsForAxis_EmitsNeutralStartAndStepHoldForMatchingDirections()
        {
            var positive = new List<LutPoint>
            {
                new LutPoint(100.0, 0.8),
                new LutPoint(200.0, 0.9)
            };
            var negative = new List<LutPoint>
            {
                new LutPoint(100.0, 0.8),
                new LutPoint(200.0, 0.9)
            };

            List<LutPoint> combined = RawAccelSettingsBridge.CombineLutPointsForAxis(
                positive, negative, out string note);

            Assert.True(combined.Count >= 4);
            Assert.Equal(49.995, combined[0].InputSpeedCmPerSec, 3);
            Assert.Equal(1.0, combined[0].Factor, 12);
            Assert.Equal(50.0, combined[1].InputSpeedCmPerSec, 6);
            Assert.Equal(0.8, combined[1].Factor, 12);
            Assert.Contains("within 5%", note);
            Assert.Contains("geometric mean applied", note);

            for (int i = 1; i < combined.Count; i++)
                Assert.True(combined[i].InputSpeedCmPerSec > combined[i - 1].InputSpeedCmPerSec);
        }
    }
}
