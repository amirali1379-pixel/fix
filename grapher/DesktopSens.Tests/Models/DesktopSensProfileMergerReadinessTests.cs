using System.Collections.Generic;
using grapher.DesktopSens.Models;
using Xunit;

namespace DesktopSens.Tests.Models
{
    public class DesktopSensProfileMergerReadinessTests
    {
        [Fact]
        public void IsReady_ReturnsFalseWhenAnyRequiredCellIsMissing()
        {
            var profile = new DesktopSensProfile
            {
                CellTrainingStates = new Dictionary<int, TrainingCellState>()
            };
            Assert.False(DesktopSensProfileMerger.IsReady(profile));
        }

        [Fact]
        public void MergeAndApply_DoesNotCallAdapterBeforeAllCellsAreVerified()
        {
            var profile = new DesktopSensProfile
            {
                CellTrainingStates = new Dictionary<int, TrainingCellState>()
            };
            var adapter = new CountingApplier();
            string reason;

            Assert.False(DesktopSensProfileMerger.MergeAndApply(profile, adapter, out reason));
            Assert.Equal(0, adapter.CallCount);
            Assert.Contains("No Train cell has completed its first calibration round yet", reason);
        }

        private sealed class CountingApplier : ICardinalProfileApplier
        {
            public int CallCount;
            public bool Apply(MergedCardinalProfile merged, DesktopSensProfile sourceProfile)
            {
                CallCount++;
                return true;
            }
        }
    }
}
