using System;

namespace grapher.DesktopSens.Models
{
    /// <summary>
    /// Distance ↔ zone-index helpers.
    ///
    /// REFACTORED: the legacy 4-band (micro/short/medium/long, total 46)
    /// model has been REPLACED by the training-derived model. There is
    /// exactly one zone per training marker; the marker count is
    ///     TrainingGeometry.Current.DistanceZoneCount
    /// and the marker positions are linear in degrees (therefore linear
    /// in cm) along the [0, MaxDistanceCm] span.
    ///
    /// Why this refactor:
    ///   The user explained that the old code split distance into 4
    ///   differently-sized bands (0.5cm, 1cm, 2cm, ~2.86cm) and hardcoded
    ///   the total at 46. Meanwhile Training generated markers every
    ///   10° (= 18 markers over 180°) using a SEPARATE definition. The
    ///   two definitions were not connected: clicking on training marker
    ///   N would map it into one of the 46 legacy zones via a fallback
    ///   formula, and if the user changed the 10° step in Training, the
    ///   46-cell arrays did not follow. This file is the connection
    ///   point: every distance↔zone question is now answered by the same
    ///   TrainingGeometry that the trainer itself uses to lay out
    ///   markers.
    /// </summary>
    public static class ZoneCalculations
    {
        public static int AngleToZoneIndex(double angleDegrees)
        {
            angleDegrees = NormalizeAngle(angleDegrees);
            int idx = (int)Math.Floor(angleDegrees / DesktopSensConstants.AngularZoneSizeDegrees);
            if (idx < 0) idx = 0;
            if (idx >= DesktopSensConstants.AngularZoneCount) idx = DesktopSensConstants.AngularZoneCount - 1;
            return idx;
        }

        /// <summary>
        /// One-arg distance → zone lookup. Uses TrainingGeometry.Current
        /// with the singleton's stored MaxDistanceCm (or the fallback
        /// default of 30 cm if the trainer hasn't been opened yet).
        ///
        /// Prefer the two-arg overload when the caller already knows the
        /// per-trial maxDistanceCm — e.g. the trainer and the config
        /// form both compute it from DPI/sensitivity and pass it in.
        /// The one-arg form is kept for callers that don't have that
        /// context (the translator, the flick recorder).
        /// </summary>
        public static int DistanceToZoneIndex(double distanceCm)
        {
            if (!IsFinite(distanceCm)) distanceCm = 0.0;
            if (distanceCm < 0.0) distanceCm = 0.0;
            var g = TrainingGeometry.Current;
            return g.DistanceToMarkerIndex(distanceCm, g.EffectiveMaxDistanceCm);
        }

        /// <summary>
        /// Two-arg distance → zone lookup. The caller passes the
        /// per-trial maxDistanceCm (typically
        /// ZoneCalculations.MaxDistanceCmForTurnDegrees(dpi, sens)).
        ///
        /// In the new model there is exactly one zone per training
        /// marker, so this returns the index of the nearest marker to
        /// the supplied distance.
        /// </summary>
        public static int DistanceToZoneIndex(double distanceCm, double maxDistanceCm)
        {
            if (!IsFinite(distanceCm)) distanceCm = 0.0;
            if (distanceCm < 0.0) distanceCm = 0.0;
            if (!IsFinite(maxDistanceCm) || maxDistanceCm <= 0.0)
                return DistanceToZoneIndex(distanceCm);
            return TrainingGeometry.Current.DistanceToMarkerIndex(distanceCm, maxDistanceCm);
        }

        // Convert CS2 turn range to physical mouse travel for the current DPI and sensitivity.
        public static double MaxDistanceCmForTurnDegrees(double mouseDpi, double gameSensitivity)
        {
            if (!IsFinite(mouseDpi) || mouseDpi <= 0.0 ||
                !IsFinite(gameSensitivity) || gameSensitivity <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(gameSensitivity), "DPI and game sensitivity must be positive and finite.");

            double countsPerCm = mouseDpi / DesktopSensConstants.InchesToCm;
            double degreesPerCount = gameSensitivity * DesktopSensConstants.Cs2YawDegreesPerCount;
            return DesktopSensConstants.MaxGameTurnDegrees / (countsPerCm * degreesPerCount);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>
        /// Returns the canonical center distance (cm) of zone N.
        ///
        /// In the new model, the zone center IS the training marker's
        /// distance. The marker positions are linear in degrees (and
        /// therefore linear in cm) along [0, MaxDistanceCm]. We use
        /// TrainingGeometry.Current.EffectiveMaxDistanceCm so this
        /// function remains callable without per-trial context.
        ///
        /// If you have a per-trial maxDistanceCm, prefer the overload
        /// ZoneIndexToDistanceCenterCm(zoneIndex, maxDistanceCm).
        /// </summary>
        public static double ZoneIndexToDistanceCenterCm(int zoneIndex)
        {
            if (zoneIndex < 0) return 0.0;
            return TrainingGeometry.Current.ZoneCenterCm(zoneIndex);
        }

        /// <summary>
        /// Per-trial overload: returns the canonical center distance of
        /// zone N given the per-trial maxDistanceCm. Use this whenever
        /// the caller already knows the maxDistanceCm — e.g. the
        /// trainer's HUD display.
        /// </summary>
        public static double ZoneIndexToDistanceCenterCm(int zoneIndex, double maxDistanceCm)
        {
            if (zoneIndex < 0) return 0.0;
            if (!IsFinite(maxDistanceCm) || maxDistanceCm <= 0.0)
                return ZoneIndexToDistanceCenterCm(zoneIndex);
            return TrainingGeometry.Current.MarkerDistanceCm(zoneIndex, maxDistanceCm);
        }

        public static double AngleZoneCenterDegrees(int zoneIndex)
        {
            return (zoneIndex + 0.5) * DesktopSensConstants.AngularZoneSizeDegrees;
        }

        public static double NormalizeAngle(double angleDegrees)
        {
            while (angleDegrees < 0) angleDegrees += 360;
            while (angleDegrees >= 360) angleDegrees -= 360;
            return angleDegrees;
        }
    }
}
