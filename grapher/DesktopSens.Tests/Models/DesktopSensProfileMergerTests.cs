using grapher.DesktopSens.Models;
using Xunit;

namespace DesktopSens.Tests.Models
{
    public class DesktopSensProfileMergerTests
    {
        [Fact]
        public void IsReady_ReturnsFalseForNullProfile()
        {
            Assert.False(DesktopSensProfileMerger.IsReady(null));
        }

        [Fact]
        public void BuildMergedProfile_RejectsNullProfile()
        {
            Assert.Throws<System.ArgumentNullException>(
                () => DesktopSensProfileMerger.BuildMergedProfile(null));
        }

        [Fact]
        public void BuildMergedProfile_LeavesFirst3CmNeutralAndKeepsFarZoneFactor()
        {
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            var profile = new DesktopSensProfile();
            int microCell = DesktopSensProfile.GetCellIndex(0, 0);
            int farCell = DesktopSensProfile.GetCellIndex(0, 1);
            profile.CellSensitivityFactor[microCell] = 0.5;
            profile.CellSensitivityConfidence[microCell] = 1.0;
            profile.CellAccelerationFactor[farCell] = 0.8;
            profile.CellAccelerationConfidence[farCell] = 1.0;
            profile.CellTrainingStates[farCell] = new TrainingCellState
            {
                AngleZoneIndex = 0,
                DistanceZoneIndex = 1,
                TotalAttemptCount = 1
            };

            MergedCardinalProfile merged = DesktopSensProfileMerger.BuildMergedProfile(profile);

            Assert.Equal(DesktopSensConstants.NeutralFactor, merged.Right.BaseSensitivityFactor, 12);
            Assert.Equal(1.0, merged.Right.AccelerationByDistanceZone[0], 12);
            Assert.Equal(0.8, merged.Right.AccelerationByDistanceZone[1], 12);
        }

        [Fact]
        public void BuildMergedProfile_PreservesAllFourSavedBaseSensitivityFactors()
        {
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            var profile = new DesktopSensProfile
            {
                RightSensitivityFactor = 1.10,
                DownSensitivityFactor = 0.90,
                LeftSensitivityFactor = 1.25,
                UpSensitivityFactor = 0.75
            };

            MergedCardinalProfile merged = DesktopSensProfileMerger.BuildMergedProfile(profile);

            Assert.Equal(1.10, merged.Right.BaseSensitivityFactor, 12);
            Assert.Equal(0.90, merged.Down.BaseSensitivityFactor, 12);
            Assert.Equal(1.25, merged.Left.BaseSensitivityFactor, 12);
            Assert.Equal(0.75, merged.Up.BaseSensitivityFactor, 12);
        }

        [Fact]
        public void BuildMergedProfile_UsesNeutralBaseFactorForInvalidSavedValue()
        {
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0);
            var profile = new DesktopSensProfile
            {
                RightSensitivityFactor = double.NaN,
                DownSensitivityFactor = 2.5,
                LeftSensitivityFactor = 0.5,
                UpSensitivityFactor = 2.0
            };

            MergedCardinalProfile merged = DesktopSensProfileMerger.BuildMergedProfile(profile);

            Assert.Equal(1.0, merged.Right.BaseSensitivityFactor, 12);
            Assert.Equal(1.0, merged.Down.BaseSensitivityFactor, 12);
            Assert.Equal(0.5, merged.Left.BaseSensitivityFactor, 12);
            Assert.Equal(2.0, merged.Up.BaseSensitivityFactor, 12);
        }

        [Fact]
        public void MergeAndApply_RefusesNullProfileAndNullApplier()
        {
            string reason;
            Assert.False(DesktopSensProfileMerger.MergeAndApply(
                null, new NeverCalledApplier(), out reason));
            Assert.Contains("Profile is null", reason);

            var profile = new DesktopSensProfile();
            Assert.False(DesktopSensProfileMerger.MergeAndApply(profile, null, out reason));
            Assert.Contains("No RawAccel profile applier", reason);
        }

        private sealed class NeverCalledApplier : ICardinalProfileApplier
        {
            public bool Apply(MergedCardinalProfile merged, DesktopSensProfile sourceProfile)
            {
                throw new System.InvalidOperationException("Applier must not be called.");
            }
        }
    }
}
