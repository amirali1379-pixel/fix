using System;

namespace grapher.DesktopSens.Models
{
    /// <summary>
    /// Central, single source of truth for the cardinal-training geometry.
    ///
    /// The user's directive (verbatim, paraphrased from Persian):
    ///   "Training is the central authority for the points, the distance
    ///    between them, and the real zone count. Every downstream component
    ///    (Arena, Zone, Calibration, Profile, Merger) must read its zone
    ///    count from Training — nobody is allowed to keep a parallel
    ///    definition or a hardcoded 46."
    ///
    /// Concretely this class owns four numbers:
    ///   • MarkerStepDegrees  — angular spacing between two consecutive
    ///                          training markers along a cardinal axis
    ///                          (default 10°; was hardcoded in
    ///                          DesktopSensTrainerForm.Training.cs before).
    ///   • MaxAxisDegrees     — span of the horizontal axes (Right/Left),
    ///                          i.e. half of a full 360° turn (default 180°).
    ///   • MaxPitchDegrees    — physical camera pitch limit the trainer
    ///                          cannot exceed (default 89°). Vertical axes
    ///                          (Down/Up) stop at min(MaxAxisDegrees,
    ///                          MaxPitchDegrees) so unreachable markers are
    ///                          never generated.
    ///   • MaxDistanceCm      — physical mouse travel (cm) that maps to
    ///                          MaxAxisDegrees. Computed from DPI +
    ///                          game sensitivity in the trainer constructor
    ///                          via ZoneCalculations.MaxDistanceCmForTurnDegrees.
    ///                          Stored here so every downstream call site
    ///                          (DistanceToZoneIndex, ZoneCenterCm, etc.)
    ///                          can resolve the physical scale without
    ///                          each caller re-deriving it.
    ///
    /// Why a singleton rather than passing instances around?
    ///   1. The geometry is a global property of the running trainer
    ///      session — there is exactly one trainer, exactly one geometry.
    ///   2. The legacy code already referenced DesktopSensConstants
    ///      statically everywhere; swapping one static for another is the
    ///      minimum-blast-radius refactor.
    ///   3. The profile arrays can be sized lazily by the calibrator's
    ///      EnsureArray helper, so the singleton not being set at field
    ///      initializer time is fine — the calibrator resizes on first
    ///      call once the trainer has set the geometry.
    ///
    /// Migration / backward compatibility:
    ///   • The default 10°/180°/89° geometry has 17 horizontal markers
    ///     (10°..170°) and 8 vertical markers (10°..80°). The shared
    ///     180° endpoint is not duplicated as a calibration cell.
    ///   • The trainer detects legacy 46-zone array strides and stale
    ///     cell-state keys, clears geometry-dependent verification,
    ///     re-bins retained trials by their physical target distance,
    ///     and recalculates factors rather than copying misaligned prefixes.
    /// </summary>
    public class TrainingGeometry
    {
        // ───────────────────────────────────────────────────────────────
        // Singleton.
        // The trainer sets this once at startup via Configure(...).
        // All downstream code reads from TrainingGeometry.Current.
        // ───────────────────────────────────────────────────────────────
        public static TrainingGeometry Current { get; private set; } = new TrainingGeometry();

        // ───────────────────────────────────────────────────────────────
        // Editable geometry parameters.
        // ───────────────────────────────────────────────────────────────
        /// <summary>Angular spacing (degrees) between consecutive training markers.</summary>
        public double MarkerStepDegrees { get; set; } = 10.0;

        /// <summary>Angular span (degrees) of the horizontal axes (Right, Left).</summary>
        public double MaxAxisDegrees { get; set; } = 180.0;

        /// <summary>Camera pitch limit (degrees). Vertical axes stop at min(MaxAxis, MaxPitch).</summary>
        public double MaxPitchDegrees { get; set; } = 89.0;

        /// <summary>
        /// Physical mouse travel (cm) that maps to MaxAxisDegrees. Set by
        /// the trainer from ZoneCalculations.MaxDistanceCmForTurnDegrees
        /// using the profile's DPI and game sensitivity. Zero means
        /// "not yet configured — fall back to the legacy default (30 cm)".
        /// </summary>
        public double MaxDistanceCm { get; set; } = 0.0;
        public bool UseMidEndPoints { get; set; } = false;

        // ───────────────────────────────────────────────────────────────
        // Zone-0 sub-marker density.
        //
        // User directive (Persian, paraphrased): "3 ta noghteh calib be
        //   zone avval ezafeh kon baraye har 16 mehvar" — add 3
        //   calibration points in the first zone for each of the 16 axes.
        //
        // The first zone used to be a single marker at MarkerStepDegrees
        // (e.g. 10° → ~1.667 cm for a 30 cm / 180° setup). That left the
        // most common flick length (micro-flicks under one step) covered
        // by exactly ONE calibration cell per axis, which is the same
        // "weak micro-flick calibration" problem that motivated setting
        // BaseZoneEndCm to 0.0.
        //
        // Zone0SubMarkerCount subdivides the angular range [0, step] into
        // `Zone0SubMarkerCount` equal slices and places one marker at
        // each slice boundary (step/N, 2*step/N, ..., N*step/N = step).
        // The Nth sub-marker coincides with the original zone-0 marker,
        // so existing per-axis calibration data at the old zone-0 cm
        // distance is preserved at index N-1 in the new layout.
        //
        // Default = 3 (matches the user's directive). Set to 1 to
        // restore the legacy single-marker behaviour.
        // ───────────────────────────────────────────────────────────────
        public int Zone0SubMarkerCount { get; set; } = DesktopSensConstants.DefaultZone0SubMarkerCount;

        // ───────────────────────────────────────────────────────────────
        // Derived counts — the ONLY place where "how many zones" is answered.
        // Every call site that previously read DesktopSensConstants.DistanceZoneCount
        // now reads one of these. There is no parallel definition anywhere.
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Number of markers on a HORIZONTAL axis (Right or Left).
        /// Marker i (0-based) sits at angular position MarkerAngularDegrees(i).
        /// The exact MaxAxisDegrees endpoint is excluded because it is shared
        /// with the opposing cardinal axis.
        ///
        /// With Zone0SubMarkerCount > 1, the first (Zone0SubMarkerCount - 1)
        /// markers are sub-step markers inside the original zone-0 range
        /// [0, step], so the total count is
        ///     baseCount + (Zone0SubMarkerCount - 1)
        /// where baseCount is the legacy (i+1)*step count.
        /// </summary>
        public int HorizontalMarkerCount
        {
            get
            {
                int baseCount = CalculateHorizontalMarkerCount(MarkerStepDegrees, MaxAxisDegrees);
                if (baseCount <= 0) return 0;
                int extra = Math.Max(0, EffectiveZone0SubMarkerCount - 1);
                return (baseCount + extra) * (UseMidEndPoints ? 2 : 1);
            }
        }

        /// <summary>
        /// Computes the horizontal marker count using the exact same rule as
        /// the configured geometry. Used by startup migration checks before
        /// the global geometry is replaced.
        /// </summary>
        public static int CalculateHorizontalMarkerCount(
            double markerStepDegrees,
            double maxAxisDegrees)
        {
            if (!IsFinitePositive(markerStepDegrees) || !IsFinitePositive(maxAxisDegrees))
                return 0;
            return Math.Max(0,
                (int)Math.Ceiling(maxAxisDegrees / markerStepDegrees - 1e-9) - 1);
        }

        /// <summary>
        /// Number of markers on a VERTICAL axis (Down or Up), capped by
        /// the camera pitch limit. Unreachable vertical zones are excluded
        /// from completion and merge readiness.
        ///
        /// With Zone0SubMarkerCount > 1, the same sub-step expansion is
        /// applied as for HorizontalMarkerCount.
        /// </summary>
        public int VerticalMarkerCount
        {
            get
            {
                if (MarkerStepDegrees <= 0.0) return 0;
                double effective = Math.Min(MaxPitchDegrees, MaxAxisDegrees);
                int baseCount = Math.Max(0, (int)Math.Ceiling(effective / MarkerStepDegrees - 1e-9) - 1);
                if (baseCount <= 0) return 0;
                int extra = Math.Max(0, EffectiveZone0SubMarkerCount - 1);
                return (baseCount + extra) * (UseMidEndPoints ? 2 : 1);
            }
        }

        /// <summary>
        /// Maximum zone count / array stride used by the legacy rectangular
        /// per-angle arrays. It equals HorizontalMarkerCount. Cardinal
        /// completion and merge readiness must instead use
        /// CardinalDirectionZoneCount(axis), because vertical axes are
        /// pitch-limited and have fewer reachable markers.
        /// </summary>
        public int DistanceZoneCount => HorizontalMarkerCount;

        /// <summary>
        /// Number of reachable zones for a cardinal axis. Right/Left use
        /// the horizontal marker count; Down/Up use the pitch-limited count.
        /// The profile arrays keep a max-width stride, but completion and
        /// merge readiness must use only markers that can actually be reached.
        /// </summary>
        public int CardinalDirectionZoneCount(int cardinalDirectionIndex)
        {
            return cardinalDirectionIndex == 1 || cardinalDirectionIndex == 3
                ? VerticalMarkerCount
                : HorizontalMarkerCount;
        }

        /// <summary>
        /// FIX (8/16-way): number of reachable markers for an axis in the
        /// active direction-count mode. The four cardinal axes use the
        /// same rule as CardinalDirectionZoneCount; the diagonal axes
        /// (8-way: indices 0,2,4,6 in eight-way order; 16-way: similar
        /// half-step indices) are bounded by the smaller of the horizontal
        /// and vertical marker counts because a diagonal flick combines
        /// both yaw and pitch, and the pitch limit is the tighter one.
        ///
        /// The `axisIndex` is the position in the active mode's axis array
        /// (AngleZoneIndicesForCount(directionCount)[axisIndex]).
        /// </summary>
        public int MultiDirectionZoneCount(int axisIndex, int directionCount)
        {
            if (directionCount != 8 && directionCount != 16)
                return CardinalDirectionZoneCount(axisIndex);

            int[] zones = DesktopSensTrainingState.AngleZoneIndicesForCount(directionCount);
            if (axisIndex < 0 || axisIndex >= zones.Length)
                return HorizontalMarkerCount;
            int zone = zones[axisIndex];

            // The 4 cardinal axes (zone 0, 8, 16, 24) follow the legacy
            // rule: horizontal axes use HorizontalMarkerCount, vertical
            // axes use VerticalMarkerCount.
            if (zone == 0 || zone == 16)
                return HorizontalMarkerCount;
            if (zone == 8 || zone == 24)
                return VerticalMarkerCount;

            // Diagonal axes: bounded by the smaller of the two, because a
            // 45° diagonal flick needs both yaw and pitch to reach its
            // endpoint; whichever limit is hit first stops the marker.
            return Math.Min(HorizontalMarkerCount, VerticalMarkerCount);
        }

        /// <summary>
        /// Effective max distance. If the trainer hasn't been opened yet
        /// (MaxDistanceCm still 0), fall back to a sensible default so
        /// legacy serialized profiles can still be read without crashing.
        /// 30 cm is a typical CS2 180° turn at 800 DPI / sens 1.
        /// </summary>
        public double EffectiveMaxDistanceCm
            => MaxDistanceCm > 0.0 ? MaxDistanceCm : 30.0;

        // ───────────────────────────────────────────────────────────────
        // Distance ↔ zone mapping. These REPLACE the legacy 4-band
        // (micro/short/medium/long) model in ZoneCalculations. There is
        // exactly ONE zone per training marker — the two concepts are
        // the same thing now.
        //
        // With Zone0SubMarkerCount > 1 the first N markers subdivide the
        // original zone-0 angular range [0, step] into N equal slices:
        //   marker 0   → step / N
        //   marker 1   → 2 * step / N
        //   ...
        //   marker N-1 → step          (coincides with the legacy zone 0)
        //   marker N   → 2 * step      (legacy zone 1)
        //   marker N+1 → 3 * step      (legacy zone 2)
        //   ...
        // The mapping stays linear in degrees and therefore linear in cm.
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Effective value of Zone0SubMarkerCount, clamped to >= 1 so a
        /// misconfigured 0 or negative never produces an empty zone 0.
        /// </summary>
        public int EffectiveZone0SubMarkerCount
            => Zone0SubMarkerCount > 1 ? Zone0SubMarkerCount : 1;

        /// <summary>
        /// Angular position (degrees) of marker `markerIndex` along a
        /// cardinal axis. This is the single source of truth for the
        /// marker→angle mapping; MarkerDistanceCm and DistanceToMarkerIndex
        /// both delegate to it.
        ///
        /// For Zone0SubMarkerCount = 1 (legacy), this collapses to
        ///     (markerIndex + 1) * MarkerStepDegrees
        /// i.e. the original (i+1)*step formula.
        ///
        /// For Zone0SubMarkerCount = N > 1:
        ///   • marker 0 .. N-1 subdivide [0, step] into N slices at
        ///     (i+1) * step / N
        ///   • marker N onward sit at (i - N + 2) * step, i.e. the legacy
        ///     positions shifted right by (N - 1).
        /// </summary>
        public double MarkerAngularDegrees(int markerIndex)
        {
            int n = EffectiveZone0SubMarkerCount;
            if (n <= 1)
                return (markerIndex + 1.0) * MarkerStepDegrees;
            if (markerIndex < n)
                return (markerIndex + 1.0) * MarkerStepDegrees / n;
            return (markerIndex - n + 2.0) * MarkerStepDegrees;
        }

        /// <summary>
        /// Physical distance (cm) of marker `markerIndex` along a
        /// cardinal axis. Marker 0 is at MarkerAngularDegrees(0) / MaxAxis°
        /// * maxDistanceCm; the final marker is the last full step below
        /// MaxAxisDegrees. The mapping is linear in degrees and therefore
        /// linear in cm.
        /// </summary>
        public double MarkerDistanceCm(int markerIndex, double maxDistanceCm)
        {
            int count = DistanceZoneCount;
            if (count <= 0 ||
                !IsFinitePositive(maxDistanceCm) ||
                !IsFinitePositive(MaxAxisDegrees) ||
                !IsFinitePositive(MarkerStepDegrees))
                return 0.0;
            int i = Math.Max(0, Math.Min(count - 1, markerIndex));
            double degrees;
            if (UseMidEndPoints)
            {
                int physicalMarker = i / 2;
                double endDegrees = MarkerAngularDegrees(physicalMarker);
                double startDegrees = physicalMarker == 0 ? 0.0 : MarkerAngularDegrees(physicalMarker - 1);
                degrees = (i & 1) == 0 ? (startDegrees + endDegrees) * 0.5 : endDegrees;
            }
            else degrees = MarkerAngularDegrees(i);
            return Math.Min(MaxAxisDegrees, degrees) / MaxAxisDegrees * maxDistanceCm;
        }

        /// <summary>
        /// Inverse of MarkerDistanceCm: given an observed distance in cm,
        /// return the marker index whose canonical position is closest.
        /// Used by the trainer's click handler and by the calibrator's
        /// trial-to-zone resolver.
        ///
        /// We ROUND (not floor) so that a flick that landed halfway
        /// between two markers is attributed to the nearer one — this
        /// matches the user's expectation that a flick aimed at marker 5
        /// is recorded against marker 5, not marker 4.
        ///
        /// With Zone0SubMarkerCount > 1, the inversion has two regimes:
        ///   • angular < step: solve i from (i+1) * step / N = angular
        ///     → i = round(angular * N / step) - 1
        ///   • angular >= step: solve i from (i - N + 2) * step = angular
        ///     → i = round(angular / step) + N - 2
        /// </summary>
        public int DistanceToMarkerIndex(double distanceCm, double maxDistanceCm)
        {
            if (!IsFinitePositive(distanceCm) || !IsFinitePositive(maxDistanceCm)) return -1;
            int count = DistanceZoneCount;
            if (count <= 0 || !IsFinitePositive(MaxAxisDegrees) || !IsFinitePositive(MarkerStepDegrees)) return -1;
            if (UseMidEndPoints)
            {
                int nearest = 0;
                double nearestError = double.PositiveInfinity;
                for (int marker = 0; marker < count; marker++)
                {
                    double error = Math.Abs(MarkerDistanceCm(marker, maxDistanceCm) - distanceCm);
                    if (error < nearestError) { nearestError = error; nearest = marker; }
                }
                return nearest;
            }
            double angularDeg = distanceCm / maxDistanceCm * MaxAxisDegrees;
            int n = EffectiveZone0SubMarkerCount;
            double raw = n <= 1 ? (angularDeg / MarkerStepDegrees) - 1.0 :
                angularDeg < MarkerStepDegrees ? (angularDeg * n / MarkerStepDegrees) - 1.0 :
                (angularDeg / MarkerStepDegrees) + (n - 2.0);
            int idx = (int)Math.Floor(raw + 0.5);
            return Math.Max(0, Math.Min(count - 1, idx));
        }

        /// <summary>
        /// The canonical "center" of a zone — which, since one zone =
        /// one marker, is just MarkerDistanceCm using the singleton's
        /// own MaxDistanceCm. Callers that know a per-trial maxDistanceCm
        /// should call MarkerDistanceCm(zone, maxDistanceCm) instead.
        /// </summary>
        public double ZoneCenterCm(int zoneIndex)
            => MarkerDistanceCm(zoneIndex, EffectiveMaxDistanceCm);

        // ───────────────────────────────────────────────────────────────
        // One-shot configuration. The trainer calls this in its
        // constructor (and again whenever the user changes the step in
        // the UI — not currently exposed, but the API supports it).
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Replace the global geometry. The trainer calls this once at
        /// startup with the user's current step setting and the
        /// DPI/sensitivity-derived maxDistanceCm. Every downstream
        /// caller (calibrator, merger, arena, profile arrays) then
        /// sees the new numbers via TrainingGeometry.Current.
        /// </summary>
        public static void Configure(
            double markerStepDegrees,
            double maxAxisDegrees,
            double maxPitchDegrees,
            double maxDistanceCm)
        {
            Configure(markerStepDegrees, maxAxisDegrees, maxPitchDegrees, maxDistanceCm,
                Current.Zone0SubMarkerCount, Current.UseMidEndPoints);
        }

        /// <summary>
        /// Full Configure overload that also sets Zone0SubMarkerCount.
        /// Use this when the user wants to change the zone-0 sub-marker
        /// density (e.g. switch between 1 = legacy single marker and 3 =
        /// the user's directive of 3 calibration points in zone 0 for
        /// each of the 16 axes).
        /// </summary>
        public static void Configure(
            double markerStepDegrees,
            double maxAxisDegrees,
            double maxPitchDegrees,
            double maxDistanceCm,
            int zone0SubMarkerCount)
        {
            Configure(markerStepDegrees, maxAxisDegrees, maxPitchDegrees, maxDistanceCm,
                zone0SubMarkerCount, false);
        }

        public static void Configure(double markerStepDegrees, double maxAxisDegrees,
            double maxPitchDegrees, double maxDistanceCm, int zone0SubMarkerCount, bool useMidEndPoints)
        {
            Current = new TrainingGeometry
            {
                MarkerStepDegrees = IsFinitePositive(markerStepDegrees) ? markerStepDegrees : 10.0,
                MaxAxisDegrees = IsFinitePositive(maxAxisDegrees) ? maxAxisDegrees : 180.0,
                MaxPitchDegrees = IsFinitePositive(maxPitchDegrees) ? maxPitchDegrees : 89.0,
                MaxDistanceCm = IsFinitePositive(maxDistanceCm) ? maxDistanceCm : 0.0,
                Zone0SubMarkerCount = zone0SubMarkerCount > 1 ? zone0SubMarkerCount : 1,
                UseMidEndPoints = useMidEndPoints
            };
        }

        /// <summary>
        /// Convenience overload for the trainer: it already knows
        /// MarkerStepDegrees, MaxAxisDegrees, MaxPitchDegrees as private
        /// consts — pass them in one call so they cannot drift.
        /// </summary>
        public static void Configure(
            double markerStepDegrees,
            double maxAxisDegrees,
            double maxPitchDegrees)
        {
            Configure(markerStepDegrees, maxAxisDegrees, maxPitchDegrees, Current.MaxDistanceCm);
        }

        /// <summary>
        /// Update only the per-session maxDistanceCm (computed from the
        /// profile's DPI/sensitivity in the trainer constructor). Called
        /// after Configure(...) if the user loads a different profile.
        /// </summary>
        public static void SetMaxDistanceCm(double maxDistanceCm)
        {
            if (IsFinitePositive(maxDistanceCm))
                Current.MaxDistanceCm = maxDistanceCm;
        }

        /// <summary>
        /// Determines whether persisted training state is still tied to the
        /// same physical geometry. A changed max-distance scale (e.g. DPI or
        /// sensitivity change) invalidates verification just like a changed
        /// marker spacing or legacy array stride.
        /// </summary>
        public static bool HasGeometryChanged(
            double appliedMarkerStepDegrees,
            double currentMarkerStepDegrees,
            double appliedMaxDistanceCm,
            double currentMaxDistanceCm,
            bool legacyStrideDetected)
        {
            if (legacyStrideDetected) return true;
            if (!IsFinitePositive(appliedMarkerStepDegrees) ||
                !IsFinitePositive(currentMarkerStepDegrees) ||
                !IsFinitePositive(appliedMaxDistanceCm) ||
                !IsFinitePositive(currentMaxDistanceCm))
                return true;

            return Math.Abs(appliedMarkerStepDegrees - currentMarkerStepDegrees) > 1e-9 ||
                Math.Abs(appliedMaxDistanceCm - currentMaxDistanceCm) >
                    Math.Max(1e-9, Math.Abs(currentMaxDistanceCm) * 1e-9);
        }

        private static bool IsFinitePositive(double value)
        {
            return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
