using System.Collections.Generic;
using grapher.DesktopSens.Models;
using Xunit;

namespace DesktopSens.Tests.Models
{
    public class DesktopSensProfileMergerSanityTests
    {
        [Fact]
        public void MergeAndApply_IgnoresFinderBaseFactorAndKeepsTrainBaseNeutral()
        {
            var profile = new DesktopSensProfile
            {
                CellTrainingStates = CreateFullyVerifiedStates(),
                RightSensitivityFactor = 3.0,
                RightSensitivityConfidence = 1.0
            };
            var applier = new CountingApplier();
            string reason;

            Assert.True(DesktopSensProfileMerger.MergeAndApply(profile, applier, out reason));
            Assert.Equal(1, applier.CallCount);
            Assert.True(profile.AllCardinalCellsVerified);
            Assert.Null(reason);
        }

        [Fact]
        public void BuildMergedProfile_RejectsOutOfRangeLutPointFactor()
        {
            var states = CreateFullyVerifiedStates();
            int cellIndex = DesktopSensProfile.GetCellIndex(
                DesktopSensTrainingState.CardinalAngleZoneIndices[0], 0);
            states[cellIndex].PrecisionVerified = true;
            states[cellIndex].AcceptedAttemptCount = 2;

            var profile = new DesktopSensProfile
            {
                CellTrainingStates = states,
                UseSampleFitLut = false
            };
            profile.CellAccelerationFactor[cellIndex] = 20.0;
            profile.CellAccelerationConfidence[cellIndex] = 1.0;
            profile.Trials.Add(new DesktopSensTrial
            {
                AngleZoneIndex = DesktopSensTrainingState.CardinalAngleZoneIndices[0],
                DistanceZoneIndex = 0,
                HasMovementMeasurement = true,
                FlickDistanceCm = 10.0,
                DurationMs = 100.0,
                AppliedLutFactor = 1.0,
                HasAppliedLutFactor = true
            });

            MergedCardinalProfile merged = DesktopSensProfileMerger.BuildMergedProfile(profile);

            Assert.False(merged.PassedSanityCheck);
            Assert.Contains("LUT factor", string.Join(" | ", merged.SanityErrors));
        }

        private static Dictionary<int, TrainingCellState> CreateFullyVerifiedStates()
        {
            var states = new Dictionary<int, TrainingCellState>();
            for (int axis = 0; axis < DesktopSensTrainingState.CardinalDirectionCount; axis++)
            {
                int angle = DesktopSensTrainingState.CardinalAngleZoneIndices[axis];
                int count = TrainingGeometry.Current.CardinalDirectionZoneCount(axis);
                for (int distance = 0; distance < count; distance++)
                {
                    states[DesktopSensTrainingState.GetCellIndex(angle, distance)] =
                        new TrainingCellState
                        {
                            AngleZoneIndex = angle,
                            DistanceZoneIndex = distance,
                            Verified = true
                        };
                }
            }
            return states;
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
