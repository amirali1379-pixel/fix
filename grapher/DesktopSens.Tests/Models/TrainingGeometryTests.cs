using grapher.DesktopSens.Models;
using Xunit;

namespace DesktopSens.Tests.Models
{
    public class TrainingGeometryTests
    {
        [Fact]
        public void DefaultGeometryHasExpectedReachableCounts()
        {
            var geometry = new TrainingGeometry
            {
                MarkerStepDegrees = 10.0,
                MaxAxisDegrees = 180.0,
                MaxPitchDegrees = 89.0
                // Zone0SubMarkerCount defaults to 3: the first zone is
                // subdivided into 3 calibration points per the user's
                // directive. HorizontalMarkerCount = 17 + 2 = 19;
                // VerticalMarkerCount = 8 + 2 = 10.
            };

            Assert.Equal(19, geometry.HorizontalMarkerCount);
            Assert.Equal(10, geometry.VerticalMarkerCount);
            Assert.Equal(10, geometry.CardinalDirectionZoneCount(1));
            Assert.Equal(19, geometry.CardinalDirectionZoneCount(0));
        }

        [Fact]
        public void MidEndModeDoublesMarkersAndRoundTrips()
        {
            var geometry = new TrainingGeometry
            {
                MarkerStepDegrees = 10.0, MaxAxisDegrees = 180.0, MaxPitchDegrees = 89.0,
                MaxDistanceCm = 30.0, UseMidEndPoints = true
            };
            Assert.Equal(38, geometry.HorizontalMarkerCount);
            Assert.Equal(20, geometry.VerticalMarkerCount);
            Assert.Equal(geometry.MarkerDistanceCm(1, 30.0) * 0.5,
                geometry.MarkerDistanceCm(0, 30.0), 10);
            Assert.Equal(1, geometry.DistanceToMarkerIndex(geometry.MarkerDistanceCm(1, 30.0), 30.0));
            Assert.Equal(2, geometry.DistanceToMarkerIndex(geometry.MarkerDistanceCm(2, 30.0), 30.0));
        }

        [Fact]
        public void DistanceMappingRoundTripsCanonicalMarker()
        {
            var geometry = new TrainingGeometry
            {
                MarkerStepDegrees = 10.0,
                MaxAxisDegrees = 180.0,
                MaxPitchDegrees = 89.0
            };

            double distance = geometry.MarkerDistanceCm(4, 30.0);
            Assert.Equal(4, geometry.DistanceToMarkerIndex(distance, 30.0));
        }
    }
}
