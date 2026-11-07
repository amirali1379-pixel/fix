using System;
using System.Collections.Generic;
using grapher.DesktopSens.Models;
using Xunit;

namespace DesktopSens.Tests.Models
{
    public class RobustStatisticsTests
    {
        [Fact]
        public void TryLogGain_RecoversKnownMultiplicativeGain()
        {
            var targets = new List<double> { 10.0, 20.0, 30.0, 40.0 };
            var errors = new List<double> { 1.0, 2.0, 3.0, 4.0 };

            double logGain;
            double standardError;
            int sampleCount;
            bool valid = RobustStatistics.TryLogGain(
                targets, errors, out logGain, out standardError, out sampleCount);

            Assert.True(valid);
            Assert.Equal(4, sampleCount);
            Assert.Equal(Math.Log(1.1), logGain, 10);
            Assert.True(standardError > 0.0);
        }

        [Fact]
        public void TryLogGain_RejectsInsufficientValidPairs()
        {
            double logGain;
            double standardError;
            int sampleCount;
            bool valid = RobustStatistics.TryLogGain(
                new List<double> { 10.0, double.NaN },
                new List<double> { 1.0, 1.0 },
                out logGain, out standardError, out sampleCount);

            Assert.False(valid);
            Assert.Equal(1, sampleCount);
            Assert.True(double.IsPositiveInfinity(standardError));
        }

        [Fact]
        public void TryLogGainConfidence_ReturnsFinite95PercentHalfWidth()
        {
            double logGain;
            double standardError;
            double halfWidth;
            int sampleCount;
            bool valid = RobustStatistics.TryLogGainConfidence(
                new List<double> { 10.0, 20.0, 30.0 },
                new List<double> { 1.0, 2.0, 3.0 },
                out logGain, out standardError, out halfWidth, out sampleCount);

            Assert.True(valid);
            Assert.Equal(3, sampleCount);
            Assert.Equal(1.96 * standardError, halfWidth, 12);
        }
    }
}
