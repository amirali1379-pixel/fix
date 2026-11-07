using System.Collections.Generic;
using Newtonsoft.Json;
using grapher.DesktopSens.Models;
using Xunit;

namespace grapher.DesktopSens.Tests
{
    public class DesktopSensProfileMigrationTests
    {
        [Fact]
        public void Model12Migration_ClearsOldFreezes_MarksGreenForReview_AndRetainsTrials()
        {
            var profile = new DesktopSensProfile { ModelVersion = 12 };
            profile.FrozenSampleLutNodeFactorsByAngleZone[0] = new double[12];
            for (int i = 0; i < 12; i++) profile.FrozenSampleLutNodeFactorsByAngleZone[0][i] = 1.1;
            profile.CellTrainingStates = new Dictionary<int, TrainingCellState>
            {
                [DesktopSensProfile.GetCellIndex(0, 0)] = new TrainingCellState
                {
                    AngleZoneIndex = 0, DistanceZoneIndex = 0,
                    Verified = true, PrecisionVerified = true
                }
            };
            profile.Trials.Add(new DesktopSensTrial
            {
                TargetDistanceCm = 5.0, FlickDistanceCm = 1.0, DurationMs = 1.0,
                EffectiveCpi = 800.0, HasMovementMeasurement = true,
                RawSamples = new List<grapher.Models.Mouse.MouseSample>
                {
                    new grapher.Models.Mouse.MouseSample { Timestamp = 0, DX = 1, DY = 0 },
                    new grapher.Models.Mouse.MouseSample { Timestamp = 1, DX = 1, DY = 0 }
                }
            });
            profile.Trials.Add(new DesktopSensTrial
            {
                TargetDistanceCm = 2.0, FlickDistanceCm = 1.5,
                DurationMs = 100.0, HasMovementMeasurement = true
            });

            DesktopSensProfileMigration.MigrationResult result =
                DesktopSensProfileMigration.MigrateToVersion13(profile);

            Assert.Equal(13, profile.ModelVersion);
            Assert.Empty(profile.FrozenSampleLutNodeFactorsByAngleZone);
            Assert.True(profile.CellTrainingStates[DesktopSensProfile.GetCellIndex(0, 0)].NeedsReview);
            Assert.Equal(2, profile.Trials.Count);
            Assert.True(profile.Trials[0].WasRejected);
            Assert.Equal(TrialRejectionReason.DurationOutOfRange, profile.Trials[0].RejectionReason);
            Assert.Equal(1, result.CellsMarkedNeedsReview);
            Assert.Equal(1, result.TrialsMarkedRejected);
            Assert.Equal(1, result.TrialsWithoutRawSamples);
        }

        [Fact]
        public void Version13MigrationMapsOldCellsToEnd()
        {
            TrainingGeometry.Configure(10.0, 180.0, 89.0, 30.0,
                DesktopSensConstants.DefaultZone0SubMarkerCount, true);
            var profile = new DesktopSensProfile { ModelVersion = 13 };
            profile.CellTrainingStates = new Dictionary<int, TrainingCellState>
            {
                [DesktopSensProfile.GetCellIndex(0, 3)] = new TrainingCellState
                {
                    AngleZoneIndex = 0, DistanceZoneIndex = 3, Verified = true, PrecisionVerified = true
                }
            };
            profile.Trials.Add(new DesktopSensTrial { DistanceZoneIndex = 3 });
            DesktopSensProfileMigration.MigrateToVersion14(profile);
            Assert.Equal(14, profile.ModelVersion);
            Assert.True(profile.CellTrainingStates.TryGetValue(DesktopSensProfile.GetCellIndex(0, 7), out var migrated));
            Assert.True(migrated.NeedsReview);
            Assert.Equal(7, profile.Trials[0].DistanceZoneIndex);
        }

        [Fact]
        public void Version13Profile_RoundTripsWithoutLosingReviewStateOrTrials()
        {
            var profile = new DesktopSensProfile { ModelVersion = 13 };
            profile.CellTrainingStates = new Dictionary<int, TrainingCellState>
            {
                [DesktopSensProfile.GetCellIndex(0, 0)] = new TrainingCellState { NeedsReview = true }
            };
            profile.Trials.Add(new DesktopSensTrial { TargetDistanceCm = 3.0 });

            DesktopSensProfile copy = JsonConvert.DeserializeObject<DesktopSensProfile>(
                JsonConvert.SerializeObject(profile));

            Assert.Equal(13, copy.ModelVersion);
            Assert.True(copy.CellTrainingStates[DesktopSensProfile.GetCellIndex(0, 0)].NeedsReview);
            Assert.Single(copy.Trials);
        }
    }
}