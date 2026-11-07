using System;

namespace grapher.DesktopSens.Models
{
    /// <summary>
    /// Pure profile lookup/conversion helpers. This class does not inject input or modify the
    /// Raw Accel driver; it provides the calibrated multiplier for a future/explicit runtime hook.
    /// </summary>
    public static class DesktopSensTranslator
    {
        public static double GetMultiplier(DesktopSensProfile profile, double angleDegrees, double distanceCm)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (distanceCm < 0.0 || double.IsNaN(distanceCm) || double.IsInfinity(distanceCm))
                throw new ArgumentOutOfRangeException(nameof(distanceCm));

            int angleIndex = ZoneCalculations.AngleToZoneIndex(angleDegrees);
            int distanceIndex = ZoneCalculations.DistanceToZoneIndex(distanceCm);
            int cellIndex = DesktopSensProfile.GetCellIndex(angleIndex, distanceIndex);

            // Directional base sensitivity is cardinal: 0=right, 1=down, 2=left, 3=up.
            // The 3 cm short-circuit that previously returned baseFactor alone for
            // micro-flicks has been REMOVED (user request): every distance now
            // also receives its per-cell acceleration factor, so micro-flicks are
            // corrected by the calibration instead of being left neutral.
            double baseFactor = GetDirectionalFactor(profile, angleDegrees);
            double accelerationFactor = ReadFactor(profile.CellAccelerationFactor, cellIndex, 1.0);
            return baseFactor * accelerationFactor;
        }

        public static double GetRotationCorrectionDegrees(DesktopSensProfile profile, double angleDegrees, double distanceCm)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (distanceCm < 0.0 || double.IsNaN(distanceCm) || double.IsInfinity(distanceCm))
                throw new ArgumentOutOfRangeException(nameof(distanceCm));

            int angleIndex = ZoneCalculations.AngleToZoneIndex(angleDegrees);
            int distanceIndex = ZoneCalculations.DistanceToZoneIndex(distanceCm);
            int cellIndex = DesktopSensProfile.GetCellIndex(angleIndex, distanceIndex);
            if (profile.CellRotationOffsetDegrees == null ||
                cellIndex >= profile.CellRotationOffsetDegrees.Length)
                return 0.0;

            double correction = profile.CellRotationOffsetDegrees[cellIndex];
            return double.IsNaN(correction) || double.IsInfinity(correction) ? 0.0 : correction;
        }

        private static double GetDirectionalFactor(DesktopSensProfile profile, double angleDegrees)
        {
            if (double.IsNaN(angleDegrees) || double.IsInfinity(angleDegrees)) return 1.0;
            angleDegrees %= 360.0;
            if (angleDegrees < 0.0) angleDegrees += 360.0;
            int direction = ((int)Math.Floor((angleDegrees + 45.0) / 90.0)) % 4;
            switch (direction)
            {
                case 0: return ReadPositive(profile.RightSensitivityFactor);
                case 1: return ReadPositive(profile.DownSensitivityFactor);
                case 2: return ReadPositive(profile.LeftSensitivityFactor);
                default: return ReadPositive(profile.UpSensitivityFactor);
            }
        }

        private static double ReadPositive(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0 ? 1.0 : value;
        }

        private static double ReadFactor(double[] values, int index, double fallback)
        {
            if (values == null || index < 0 || index >= values.Length)
                return fallback;
            double value = values[index];
            return double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0 ? fallback : value;
        }
    }
}
