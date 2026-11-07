using grapher.DesktopSens.Models;
using Xunit;

namespace DesktopSens.Tests.Models
{
    public class CalibrationResidualCacheTests
    {
        [Fact]
        public void TryGet_RequiresExactDependencyKeyAndReturnsDefensiveCopies()
        {
            var profile = new DesktopSensProfile();
            var sourceKey = new byte[] { 1, 2, 3, 4 };
            var sourceResiduals = new[] { 0.1, -0.2, 0.3 };

            CalibrationResidualCache.Store(profile, 7, sourceKey, sourceResiduals);
            sourceKey[0] = 99;
            sourceResiduals[0] = 99.0;

            double[] cached;
            Assert.True(CalibrationResidualCache.TryGet(profile, 7, new byte[] { 1, 2, 3, 4 }, out cached));
            Assert.Equal(new[] { 0.1, -0.2, 0.3 }, cached);

            cached[0] = 55.0;
            Assert.True(CalibrationResidualCache.TryGet(profile, 7, new byte[] { 1, 2, 3, 4 }, out cached));
            Assert.Equal(0.1, cached[0], 12);
            Assert.False(CalibrationResidualCache.TryGet(profile, 7, new byte[] { 1, 2, 3, 5 }, out cached));
        }

        [Fact]
        public void Clear_RemovesEntriesAndResetsStatistics()
        {
            var profile = new DesktopSensProfile();
            CalibrationResidualCache.Store(profile, 2, new byte[] { 8 }, new[] { 0.4 });
            double[] cached;
            Assert.True(CalibrationResidualCache.TryGet(profile, 2, new byte[] { 8 }, out cached));

            CalibrationResidualCache.Clear(profile);

            Assert.False(CalibrationResidualCache.TryGet(profile, 2, new byte[] { 8 }, out cached));
            int hits, misses, cells;
            CalibrationResidualCache.GetStatistics(profile, out hits, out misses, out cells);
            Assert.Equal(0, hits);
            Assert.Equal(1, misses);
            Assert.Equal(0, cells);
        }
    }
}
