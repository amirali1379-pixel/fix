using System.Collections.Generic;
using grapher.DesktopSens.Models;
using Xunit;

namespace DesktopSens.Tests.Models
{
    public class CalibrationCurveCacheTests
    {
        [Fact]
        public void GetOrFit_ReusesOnlyExactOrderedSampleInput()
        {
            var profile = new DesktopSensProfile();
            var samples = new List<SensitivitySample>();
            for (int i = 1; i <= 12; i++)
            {
                samples.Add(new SensitivitySample
                {
                    InputSpeedCmPerSec = i * 10.0,
                    ObservedRatio = 1.0 + i * 0.002,
                    Weight = 1.0
                });
            }

            CurveFitResult first = CalibrationCurveCache.GetOrFit(profile, 4, samples);
            int hits, misses, cells;
            CalibrationCurveCache.GetStatistics(profile, out hits, out misses, out cells);
            Assert.Equal(0, hits);
            Assert.Equal(1, misses);
            Assert.Equal(1, cells);

            CurveFitResult second = CalibrationCurveCache.GetOrFit(profile, 4, samples);
            CalibrationCurveCache.GetStatistics(profile, out hits, out misses, out cells);
            Assert.Equal(1, hits);
            Assert.Equal(1, misses);
            Assert.Equal(first.Mode, second.Mode);
            Assert.Equal(first.IsValid, second.IsValid);

            samples[0] = new SensitivitySample
            {
                InputSpeedCmPerSec = 10.0,
                ObservedRatio = 1.25,
                Weight = 1.0
            };
            CalibrationCurveCache.GetOrFit(profile, 4, samples);
            CalibrationCurveCache.GetStatistics(profile, out hits, out misses, out cells);
            Assert.Equal(1, hits);
            Assert.Equal(2, misses);
        }

        [Fact]
        public void Invalidate_RemovesProfileCacheAndStartsFreshStatistics()
        {
            var profile = new DesktopSensProfile();
            var samples = new List<SensitivitySample>();
            for (int i = 1; i <= 8; i++)
                samples.Add(new SensitivitySample { InputSpeedCmPerSec = i * 5.0, ObservedRatio = 1.0 + i * 0.01, Weight = 1.0 });

            CalibrationCurveCache.GetOrFit(profile, 0, samples);
            CalibrationCurveCache.Invalidate(profile);
            CalibrationCurveCache.GetOrFit(profile, 0, samples);

            int hits, misses, cells;
            CalibrationCurveCache.GetStatistics(profile, out hits, out misses, out cells);
            Assert.Equal(0, hits);
            Assert.Equal(1, misses);
            Assert.Equal(1, cells);
        }
    }
}
