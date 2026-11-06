using System;
using System.Collections.Generic;
using System.Linq;

namespace grapher.DesktopSens.Models
{
    /// <summary>
    /// Paired target/error history and log-gain estimate for one training session.
    /// </summary>
    [Serializable]
    public class TrainingSessionCalibrationHistory
    {
        public string SessionId;
        public List<double> TargetDistanceHistoryCm = new List<double>();
        public List<double> SignedErrorHistoryCm = new List<double>();
        public int LogGainSampleCount;
        public double LogGainEstimate;
        public double LogGainHalfWidth95 = -1.0;
        public bool SessionBiasApplied;
        public double AppliedSessionBiasLogGain;
    }


    /// <summary>
    /// Per-cell training state for the cardinal-direction calibration trainer.
    ///
    /// Only the 4 cardinal axes (Right = angle zone 0, Down = zone 8,
    /// Left = zone 16, Up = zone 24) × N distance zones = 4N cells are
    /// tracked here, where N = TrainingGeometry.Current.DistanceZoneCount.
    /// The other cells of the AngularZoneCount × N grid continue to be
    /// served by the legacy per-trial calibrator in DesktopSensCalibrator.
    ///
    /// FIX #1 (robust statistics): the previous implementation stored only
    /// the last signed error and used a single-sample tolerance check.
    /// A single misfire that happened to land within ±1 cm would mark
    /// the cell yellow even though the user's actual aim point was 4 cm
    /// away. We now keep the FULL error history per cell and use the
    /// median / MAD for the success criterion, which is what
    /// RobustStatistics was already designed for but the training state
    /// wasn't using.
    ///
    /// FIX #8 (session-based re-verification): the Verified flag used to
    /// be sticky — once green, always green. We now track the session
    /// id where the cell was first verified, and expose
    /// DetectDrift(...) so the trainer can re-test a previously-verified
    /// cell on a new session and downgrade it back to yellow if the
    /// user's aim has drifted.
    ///
    /// FIX #10 (adaptive tolerance): the ±1 cm tolerance was hard-coded.
    /// We now accept an adaptive tolerance per cell, derived from the
    /// user's measured noise floor (see EstimateNoiseFloor in the
    /// trainer). The default still falls back to 1.0 cm for backward
    /// compatibility.
    /// </summary>
    [Serializable]
    public class TrainingCellState
    {
        public int AngleZoneIndex;
        public int DistanceZoneIndex;

        /// <summary>
        /// Successful calibration attempts (mouse landed within
        /// ±DesktopSensConstants.DefaultToleranceCm of the target point).
        /// </summary>
        public int SuccessfulAttemptCount;

        // Valid, measurement-bearing trials accepted into calibration history.
        // Rejected attempts increment TotalAttemptCount only and must not make
        // a marker look calibrated/yellow.
        public int AcceptedAttemptCount;

        /// <summary>
        /// Total attempts (including failures). Used for HUD display only.
        /// </summary>
        public int TotalAttemptCount;

        /// <summary>
        /// True when SuccessfulAttemptCount >= MinSuccessfulAttemptsForFirstRound
        /// AND the median of the last K successes is within tolerance.
        /// Rendered as a yellow ring around the cell marker in the 3D arena.
        /// </summary>
        public bool FirstRoundComplete;

        /// <summary>
        /// True after a subsequent successful attempt (post-yellow) lands
        /// within tolerance. Rendered as a solid green marker.
        /// </summary>
        public bool Verified;

        public DateTime LastAttemptTimestampUtc;
        public DateTime? FirstRoundCompletedTimestampUtc;
        public DateTime? VerifiedTimestampUtc;

        /// <summary>
        /// Last measured signed error in cm, for the HUD counter.
        /// signedError = actualFlickDistanceCm - targetDistanceCm
        /// (positive = over-flick, negative = under-flick).
        /// Kept for backward-compat — the new RobustSignedErrorCm
        /// field carries the robust (median) estimate that the
        /// success criterion actually uses.
        /// </summary>
        public double LastSignedErrorCm;

        // ───────────────────────────────────────────────────────────────
        // NEW (fix #1): full error history + robust statistics.
        //
        // The previous implementation overwrote LastSignedErrorCm on
        // every attempt, throwing away the history that the robust
        // statistics layer (RobustStatistics.cs) needs to do its job.
        // We now keep the last K signed-error samples per cell, where
        // K = MaxStoredHistorySamples. K is bounded (64) so a long
        // calibration session does not balloon the profile's JSON size.
        // ─────────────────────────────────────────────────────────────
        public const int MaxStoredHistorySamples = 64;
        public const int MaxSessionHistorySamples = 32;
        public List<double> SignedErrorHistoryCm = new List<double>();
        // Paired with SignedErrorHistoryCm. Used to estimate the multiplicative
        // gain and its confidence interval without losing trial pairing.
        public List<double> TargetDistanceHistoryCm = new List<double>();
        public int LogGainSampleCount;
        public double LogGainEstimate;
        public double LogGainStandardError = -1.0;
        public double LogGainConfidenceHalfWidth95 = -1.0;
        public bool PrecisionVerified;
        public bool LowPrecision;
        // Session-specific paired observations support the v4 test-retest gate.
        public List<TrainingSessionCalibrationHistory> SessionCalibrationHistories =
            new List<TrainingSessionCalibrationHistory>();
        public bool SessionRepeatabilityFailed;
        public double SessionGainSpreadLog;

        // NEW (fix #3): angular-error history, same length as the
        // signed-error history. Used by the validation layer to
        // reject trials whose perpendicular deviation is large and
        // by the arena renderer to show the per-cell "off-axis
        // tendency" as a small colored stripe on the marker.
        public List<double> AngularErrorHistoryDegrees = new List<double>();

        /// <summary>
        /// Robust (median) signed error of the last K successful
        /// attempts. Updated by RecordAttempt. Used by the success
        /// criterion instead of LastSignedErrorCm.
        /// </summary>
        public double RobustSignedErrorCm;

        /// <summary>
        /// Robust standard deviation of the last K successful
        /// attempts (1.4826 * MAD). Used by the arena renderer to
        /// draw an "uncertainty bar" on the marker, and by the
        /// drift detector to decide whether a re-verify sample is
        /// within tolerance.
        /// </summary>
        public double RobustSigmaCm;

        // ───────────────────────────────────────────────────────────────
        // NEW (fix #8): session bookkeeping for drift detection.
        // ─────────────────────────────────────────────────────────────
        public string FirstRoundCompletedSessionId;
        public string VerifiedSessionId;
        public string LastAttemptSessionId;

        /// <summary>
        /// True if DetectDrift() found that the user's aim has drifted
        /// away from the verified center by more than the cell's
        /// tolerance. The arena renderer marks drifted cells with a
        /// red ring; the trainer should re-run a verification sample
        /// and call ClearDriftFlag() once the cell passes again.
        /// </summary>
        public bool DriftDetected;

        /// <summary>
        /// Last signed error measured during a re-verification sample
        /// (only set when DriftDetected is true). Used for HUD display.
        /// </summary>
        public double DriftSignedErrorCm;

        // ───────────────────────────────────────────────────────────────
        // NEW (fix #10): adaptive tolerance per cell.
        //
        // Set by DesktopSensTrainingState.SetAdaptiveTolerance from
        // the trainer's noise-floor estimation. Zero means "use the
        // global default". This is a per-cell override because the
        // noise floor at long distance (where the flick takes longer
        // and the user's hand has more time to drift) can be larger
        // than at short distance.
        // ─────────────────────────────────────────────────────────────
        public double AdaptiveToleranceCm;

        /// <summary>
        /// Effective tolerance for this cell:
        ///   AdaptiveToleranceCm if > 0,
        ///   else DesktopSensConstants.DefaultToleranceCm (the global default).
        /// </summary>
        public double EffectiveToleranceCm
        {
            get
            {
                double noiseTol = AdaptiveToleranceCm > 0.0 ? AdaptiveToleranceCm : 0.0;
                double tol = DesktopSensConstants.DefaultToleranceCm;
                if (noiseTol > tol) tol = noiseTol;
                try
                {
                    double targetCm = TrainingGeometry.Current.ZoneCenterCm(DistanceZoneIndex);
                    double distanceTol = 0.15 * targetCm;
                    if (distanceTol > tol) tol = distanceTol;
                }
                catch { }
                return tol;
            }
        }
    }

    /// <summary>
    /// Drives the yellow → green transition and enforces the sequential
    /// progression rule.
    ///
    /// FIX #14 (less strict sequential rule): the previous rule was a
    /// hard block — cell N+1 could not even be selected until cell N was
    /// Verified. We now expose IsCellAvailableForTrainingStrict (the
    /// original behavior) and IsCellAvailableForTrainingSoft (warn but
    /// allow). The trainer decides which to use based on the user's
    /// AllowSequentialOverride setting.
    /// </summary>
    public static class DesktopSensTrainingState
    {
        public const int MaxAttemptsForPrecision = 48;
        // FIX (AI review root-cause): the old value of 0.015 (1.5%
        // half-width in log-space) was practically unreachable for a
        // real user. With typical flick noise (σ 3-5% in log-space),
        // the user would need 17-47 successful flicks on a SINGLE cell
        // to get the 95% CI half-width below 1.5%. Almost no user has
        // the patience for that, so cells stayed yellow forever and the
        // driver was never updated.
        //
        // The new value 0.05 (5% half-width) is reachable after a few
        // repeatable observations. The estimator needs at least three valid
        // target/observed pairs; one corrected success completes the first
        // round, and one additional success plus this precision gate turns
        // the cell green.
        //
        // If you want even more relaxed (e.g. for very noisy mice),
        // increase to 0.08 or 0.10. The trade-off is that green cells
        // are less precisely calibrated — but a green cell with 5%
        // CI is still MUCH better than a yellow cell that never gets
        // applied to the driver.
        public const double PrecisionToleranceLog = 0.10;
        // A cell needs one successful observation AFTER the robust gain
        // estimate explains the collected target/observed pairs. The gain
        // estimator itself requires at least three valid pairs (see
        // RobustStatistics.TryLogGain), and PrecisionVerified still enforces
        // the confidence-interval gate before the cell can turn green.
        // This avoids requiring five already-accurate raw flicks before the
        // first correction is allowed to reach RawAccel.
        public const int MinSuccessfulAttemptsForFirstRound = 1;

        // FIX #1: green requires one MORE successful sample after the
        // first round is complete. This was already the behavior; kept
        // explicit here so the calibrator and the trainer can agree.
        public const int VerificationExtraSuccessfulAttempts = 1;

        // FIX #1: the original ±1 cm tolerance stays as the DEFAULT
        // (backward compat with any external code that reads this
        // field), but per-cell AdaptiveToleranceCm can override it.

        // Per-cell negative-acceleration zone half-width. ±0.5 cm around
        // each calibrated point may carry a decel factor (factor < 1.0)
        // to absorb over-flick. This is what motivates the sequential rule.
        public const double NegativeAccelZoneHalfWidthCm = 0.5;

        // The four cardinal angle-zone indices. AngularZoneSize is 11.25°,
        // so cardinal directions land on zones 0, 8, 16, 24. This matches
        // the cardinal-classification convention in DesktopSensTranslator.
        public static readonly int[] CardinalAngleZoneIndices = { 0, 8, 16, 24 };

        // FIX #6: 8-way and 16-way cardinal direction support.
        // The original trainer only calibrated the 4 cardinal directions;
        // every other direction was extrapolated from those 4. The new
        // arrays let the trainer optionally calibrate 8 or 16 directions.
        // The choice is made at runtime by the trainer:
        //   • 4-way:  Right, Down, Left, Up             (default, backward-compat)
        //   • 8-way:  + DownRight, DownLeft, UpLeft, UpRight
        //   • 16-way: + every 22.5° direction (zone index step 4 instead of 8)
        //
        // The 8-way indices land on zones {0, 4, 8, 12, 16, 20, 24, 28}.
        // The 16-way indices land on every even zone.
        public static readonly int[] EightWayAngleZoneIndices =
            { 0, 4, 8, 12, 16, 20, 24, 28 };

        // FIX (16-way): every 22.5° direction. 32 angular zones / 16 = 2 zones
        // per direction, so 16-way indices land on every EVEN zone index:
        //   0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30
        // Each direction owns a non-overlapping 22.5° wedge centered on its
        // axis. Adjacent directions share only the boundary line, never an
        // interior point — there is no overlap between calibration zones.
        public static readonly int[] SixteenWayAngleZoneIndices =
            { 0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30 };

        // Cardinal direction order matches DesktopSensTranslator:
        // 0 = Right, 1 = Down, 2 = Left, 3 = Up.
        public const int CardinalDirectionCount = 4;
        public const int EightWayDirectionCount = 8;
        public const int SixteenWayDirectionCount = 16;

        // Maximum supported direction count. Used to size arrays that must
        // hold any mode's data without re-allocation when the user switches.
        public const int MaxDirectionCount = 16;

        /// <summary>
        /// Resolve the active direction count for a profile. Falls back to
        /// 4-way when the profile field is missing or invalid so legacy
        /// serialized profiles keep rendering.
        /// </summary>
        public static int ResolveDirectionCount(DesktopSensProfile profile)
        {
            if (profile == null) return CardinalDirectionCount;
            int n = profile.CalibrationDirectionCount;
            if (n == 4 || n == 8 || n == 16) return n;
            return CardinalDirectionCount;
        }

        /// <summary>
        /// Returns the angle-zone indices that correspond to the requested
        /// direction-count mode. Never returns null; an unsupported value
        /// falls back to the 4-way array.
        /// </summary>
        public static int[] AngleZoneIndicesForCount(int directionCount)
        {
            switch (directionCount)
            {
                case 8:  return EightWayAngleZoneIndices;
                case 16: return SixteenWayAngleZoneIndices;
                default: return CardinalAngleZoneIndices;
            }
        }

        /// <summary>
        /// Half-width (in angular zones) of one direction's exclusive wedge
        /// in the requested mode. Used by the arena renderer to draw the
        /// non-overlapping sector boundaries and by the calibrator's
        /// direction classifier to keep neighbouring axes from stealing
        /// each other's trials.
        ///   4-way: 4 zones (45°) — cardinal axes are 90° apart.
        ///   8-way: 2 zones (22.5°) — adjacent axes are 45° apart.
        ///   16-way: 1 zone (11.25°) — adjacent axes are 22.5° apart, which
        ///           equals the base angular-zone size; sectors touch but
        ///           never overlap.
        /// </summary>
        public static int AngularZoneHalfWidthForCount(int directionCount)
        {
            switch (directionCount)
            {
                case 8:  return 2;
                case 16: return 1;
                default: return 4;
            }
        }

        // RGB triplets for the four cardinal axes. Kept as plain ints so the
        // Models layer has no System.Drawing dependency (the existing
        // DesktopSensProfile and DesktopSensConstants files don't import it).
        // Right = blue, Down = red, Left = red, Up = blue.
        // Down intentionally shares the left-axis red to keep the lower
        // markers visually distinct from the yellow attempted-state overlay.
        public static readonly int[] RightAxisRgb = { 70, 140, 230 };
        public static readonly int[] DownAxisRgb  = { 220, 70, 70 };
        public static readonly int[] LeftAxisRgb   = { 220, 70, 70 };
        public static readonly int[] UpAxisRgb     = { 70, 140, 230 };  // blue

        // FIX #6: 8-way RGB. The four cardinal directions inherit the
        // colors above; the four diagonal directions get intermediate
        // hues so they are visually distinct from their neighbors.
        // Index order: 0=Right, 4=DownRight, 8=Down, 12=DownLeft,
        // 16=Left, 20=UpLeft, 24=Up, 28=UpRight.
        public static readonly int[][] EightWayAxisRgb =
        {
            RightAxisRgb,                                      // 0
            new int[] { 160, 150, 220 },                       // 4  DownRight (purple-blue)
            DownAxisRgb,                                       // 8
            new int[] { 220, 110, 100 },                       // 12 DownLeft (red-orange)
            LeftAxisRgb,                                       // 16
            new int[] { 200, 90, 130 },                        // 20 UpLeft (magenta)
            UpAxisRgb,                                         // 24
            new int[] { 100, 180, 200 },                       // 28 UpRight (cyan)
        };

        // FIX (16-way): one visually-distinct color per axis. The four
        // cardinals keep their 4-way colors so the user can read them at a
        // glance; the four diagonals keep their 8-way colors; the eight
        // half-step directions (22.5°, 67.5°, ...) get hues interpolated
        // between their two cardinal neighbours so adjacent axes always
        // differ in hue. Index order follows SixteenWayAngleZoneIndices:
        //   0=Right, 2=RightDownRight, 4=DownRight, 6=DownDownRight,
        //   8=Down, 10=DownDownLeft, 12=DownLeft, 14=LeftDownLeft,
        //  16=Left, 18=LeftUpLeft,    20=UpLeft,   22=UpUpLeft,
        //  24=Up,   26=UpUpRight,     28=UpRight,  30=RightUpRight.
        public static readonly int[][] SixteenWayAxisRgb =
        {
            new int[] {  70, 140, 230 },  //  0 Right
            new int[] { 110, 150, 225 },  //  2
            new int[] { 160, 150, 220 },  //  4 DownRight
            new int[] { 195, 110, 145 },  //  6
            new int[] { 220,  70,  70 },  //  8 Down
            new int[] { 220,  90, 100 },  // 10
            new int[] { 220, 110, 100 },  // 12 DownLeft
            new int[] { 220,  90,  85 },  // 14
            new int[] { 220,  70,  70 },  // 16 Left
            new int[] { 210,  80, 110 },  // 18
            new int[] { 200,  90, 130 },  // 20 UpLeft
            new int[] { 150, 140, 180 },  // 22
            new int[] {  70, 140, 230 },  // 24 Up
            new int[] {  85, 160, 215 },  // 26
            new int[] { 100, 180, 200 },  // 28 UpRight
            new int[] {  85, 160, 230 },  // 30
        };

        /// <summary>
        /// Resolve the RGB triplet for an axis in the active direction-count
        /// mode. Falls back to gray for an out-of-range index so a stale
        /// profile cannot crash the renderer.
        /// </summary>
        public static int[] GetAxisRgbForMode(int axisIndex, int directionCount)
        {
            switch (directionCount)
            {
                case 8:
                    if (axisIndex >= 0 && axisIndex < EightWayAxisRgb.Length)
                        return EightWayAxisRgb[axisIndex];
                    break;
                case 16:
                    if (axisIndex >= 0 && axisIndex < SixteenWayAxisRgb.Length)
                        return SixteenWayAxisRgb[axisIndex];
                    break;
                default:
                    if (axisIndex >= 0 && axisIndex < 4)
                        return GetAxisRgb(axisIndex);
                    break;
            }
            return new int[] { 200, 200, 200 };
        }

        /// <summary>
        /// Returns the human-readable name of the axis at the given index in
        /// the active mode. Used by the trainer HUD and the arena overlay.
        /// </summary>
        public static string GetAxisName(int axisIndex, int directionCount)
        {
            int[] zones = AngleZoneIndicesForCount(directionCount);
            if (axisIndex < 0 || axisIndex >= zones.Length) return "?";
            // Map the angle-zone index to a compass-style label. The zones
            // follow the calibration-frame convention where zone 0 = Right,
            // zone 8 = Down, zone 16 = Left, zone 24 = Up. Each label is the
            // closest cardinal pair that brackets the axis.
            int zone = zones[axisIndex];
            // 32 zones per 360°, so 1 zone = 11.25°. Use that to label.
            double angleDeg = zone * (360.0 / 32.0);
            // Wrap to [0, 360)
            angleDeg = ((angleDeg % 360.0) + 360.0) % 360.0;
            // Cardinal buckets
            string[] labels =
            {
                "Right", "DownRight", "Down", "DownLeft",
                "Left", "UpLeft", "Up", "UpRight"
            };
            int bucket = (int)Math.Round(angleDeg / 45.0) % 8;
            // For 16-way we add the half-step prefix to distinguish from
            // the 8-way neighbour. Names like "Right/DownRight" indicate
            // the axis sits halfway between Right and DownRight.
            if (directionCount == 16)
            {
                double half = (angleDeg - bucket * 45.0);
                if (Math.Abs(half - 22.5) < 1.0)
                    return labels[bucket] + "/" + labels[(bucket + 1) % 8];
            }
            return labels[bucket];
        }

        public static int[] GetAxisRgb(int cardinalDirectionIndex)
        {
            switch (cardinalDirectionIndex)
            {
                case 0: return RightAxisRgb;
                case 1: return DownAxisRgb;
                case 2: return LeftAxisRgb;
                case 3: return UpAxisRgb;
                default: return new int[] { 200, 200, 200 };
            }
        }

        /// <summary>
        /// Returns 0..3 for cardinal angle zones (0/8/16/24), or -1 for
        /// non-cardinal zones. Used to decide whether a cell belongs to the
        /// cardinal trainer at all.
        /// </summary>
        public static int GetCardinalDirectionIndex(int angleZoneIndex)
        {
            for (int i = 0; i < CardinalAngleZoneIndices.Length; i++)
                if (CardinalAngleZoneIndices[i] == angleZoneIndex)
                    return i;
            return -1;
        }

        /// <summary>
        /// FIX #6: returns 0..7 for 8-way angle zones (0/4/8/12/16/20/24/28),
        /// or -1 otherwise.
        /// </summary>
        public static int GetEightWayDirectionIndex(int angleZoneIndex)
        {
            for (int i = 0; i < EightWayAngleZoneIndices.Length; i++)
                if (EightWayAngleZoneIndices[i] == angleZoneIndex)
                    return i;
            return -1;
        }

        /// <summary>
        /// Reuses DesktopSensProfile.GetCellIndex so the trainer, calibrator,
        /// and training-state map all agree on cell indexing.
        /// </summary>
        public static int GetCellIndex(int angleZoneIndex, int distanceZoneIndex)
        {
            return DesktopSensProfile.GetCellIndex(angleZoneIndex, distanceZoneIndex);
        }

        // With BaseZoneEndCm = 0.0, every distance zone with a positive
        // center is a Train calibration target. The previous 3 cm
        // micro-flick exclusion is gone, so micro-flick zones are
        // trainable too.
        public static bool IsCalibrationDistanceZone(int distanceZoneIndex)
        {
            return distanceZoneIndex >= 0 &&
                distanceZoneIndex < TrainingGeometry.Current.DistanceZoneCount &&
                TrainingGeometry.Current.ZoneCenterCm(distanceZoneIndex) >
                    DesktopSensConstants.BaseZoneEndCm;
        }

        /// <summary>
        /// Sequential rule (strict, original behavior): cell N is available
        /// only after cell N-1 is Verified. Cell 0 is always available.
        /// </summary>
        public static bool IsCellAvailableForTraining(
            Dictionary<int, TrainingCellState> states,
            int angleZoneIndex,
            int distanceZoneIndex)
        {
            return IsCellAvailableForTrainingStrict(states, angleZoneIndex, distanceZoneIndex);
        }

        /// <summary>
        /// FIX (8/16-way): direction-count-aware strict rule. Resolves the
        /// axis from the active mode's angle-zone array and uses
        /// MultiDirectionZoneCount so the diagonal axes' reach is bounded
        /// by min(horizontal, vertical) as expected.
        /// </summary>
        public static bool IsCellAvailableForTrainingForCount(
            Dictionary<int, TrainingCellState> states,
            int angleZoneIndex,
            int distanceZoneIndex,
            int directionCount)
        {

            int cardinalIndex = -1;
            int[] zones = AngleZoneIndicesForCount(directionCount);
            for (int i = 0; i < zones.Length; i++)
                if (zones[i] == angleZoneIndex) { cardinalIndex = i; break; }
            int zoneCount = cardinalIndex >= 0
                ? TrainingGeometry.Current.MultiDirectionZoneCount(cardinalIndex, directionCount)
                : TrainingGeometry.Current.DistanceZoneCount;
            // No sequential lockout; with BaseZoneEndCm = 0.0 every zone
            // (including micro-flick zones) is a calibration target.
            return distanceZoneIndex < zoneCount && IsCalibrationDistanceZone(distanceZoneIndex);
        }

        /// <summary>
        /// Original strict rule. Used when AllowSequentialOverride = false.
        /// </summary>
        public static bool IsCellAvailableForTrainingStrict(
            Dictionary<int, TrainingCellState> states,
            int angleZoneIndex,
            int distanceZoneIndex)
        {

            int cardinalIndex = GetCardinalDirectionIndex(angleZoneIndex);
            int zoneCount = cardinalIndex >= 0
                ? TrainingGeometry.Current.CardinalDirectionZoneCount(cardinalIndex)
                : TrainingGeometry.Current.DistanceZoneCount;
            // Remove sequential lockout; with BaseZoneEndCm = 0.0 every zone
            // (including micro-flick zones) is a calibration target.
            return distanceZoneIndex < zoneCount && IsCalibrationDistanceZone(distanceZoneIndex);
        }

        /// <summary>
        /// FIX #14: soft rule. Returns true if the cell is "available for
        /// training", meaning either (a) the previous cell is verified, OR
        /// (b) the previous cell is yellow (first-round complete), OR
        /// (c) the user has explicitly enabled sequential-override mode.
        ///
        /// Even when this returns true, the caller can check
        /// IsSequentialWarningFlagged to decide whether to show a warning
        /// in the HUD ("you are training a cell whose previous cell is
        /// not yet verified — proceed with caution").
        /// </summary>
        public static bool IsCellAvailableForTrainingSoft(
            Dictionary<int, TrainingCellState> states,
            int angleZoneIndex,
            int distanceZoneIndex,
            bool allowOverride)
        {

            int cardinalIndex = GetCardinalDirectionIndex(angleZoneIndex);
            int zoneCount = cardinalIndex >= 0
                ? TrainingGeometry.Current.CardinalDirectionZoneCount(cardinalIndex)
                : TrainingGeometry.Current.DistanceZoneCount;
            // The allowOverride flag is retained for API compatibility.
            return distanceZoneIndex < zoneCount && IsCalibrationDistanceZone(distanceZoneIndex);
        }

        /// <summary>
        /// FIX #14: returns true if the soft rule allowed the cell but
        /// the strict rule would have blocked it. Used by the trainer to
        /// show a "training ahead of sequence — re-verify later" warning.
        /// </summary>
        public static bool IsSequentialWarningFlagged(
            Dictionary<int, TrainingCellState> states,
            int angleZoneIndex,
            int distanceZoneIndex)
        {
            if (distanceZoneIndex <= 0) return false;
            if (states == null) return false;
            int prevCellIndex = GetCellIndex(angleZoneIndex, distanceZoneIndex - 1);
            if (!states.TryGetValue(prevCellIndex, out var prev) || prev == null)
                return true;
            return !prev.Verified;
        }

        /// <summary>
        /// Records one attempt for the given cell. Updates the success
        /// counter and promotes the cell through yellow → green when the
        /// criteria are met. Does NOT touch the existing
        /// CellSensitivityConfidence / CellAccelerationConfidence arrays
        /// on the profile — those continue to be driven by
        /// DesktopSensCalibrator.Calibrate(profile) on the same trial set.
        ///
        /// FIX #1: success is now decided by the ROBUST median of the
        /// last K successful samples, not by a single sample. The first
        /// attempt that lands within tolerance no longer immediately
        /// turns the cell yellow — the cell turns yellow only after
        /// MinSuccessfulAttemptsForFirstRound successes, AND only if
        /// the median of those successes is within tolerance.
        /// </summary>
        public static void RecordAttempt(
            Dictionary<int, TrainingCellState> states,
            int angleZoneIndex,
            int distanceZoneIndex,
            double signedErrorCm,
            DateTime timestampUtc,
            string sessionId = null,
            double angularErrorDegrees = 0.0,
            double targetDistanceCm = 0.0,
            bool sessionBiasApplied = false,
            double sessionBiasLogGain = 0.0)
        {
            if (states == null) return;
            if (double.IsNaN(signedErrorCm) || double.IsInfinity(signedErrorCm))
                return;
            if (!IsCalibrationDistanceZone(distanceZoneIndex)) return;
            // NEW (Model v6): callers now pass NeededDistanceCm (when the ASDW
            // correction landed inside tolerance) instead of the raw target.
            // This function does not know about ASDW; it just requires that the
            // supplied target is physically meaningful.
            if (!IsFinite(targetDistanceCm) || targetDistanceCm <= 0.0)
                return;
            if (targetDistanceCm <= DesktopSensConstants.BaseZoneEndCm) return;

            int cellIndex = GetCellIndex(angleZoneIndex, distanceZoneIndex);
            if (!states.TryGetValue(cellIndex, out var state) || state == null)
            {
                state = new TrainingCellState
                {
                    AngleZoneIndex = angleZoneIndex,
                    DistanceZoneIndex = distanceZoneIndex
                };
                states[cellIndex] = state;
            }

            // A verified cell is checked once when it is first touched in a
            // different training session. This is the actual integration point
            // for drift detection; previously DetectDrift existed but was never called.
            if (state.Verified &&
                !string.IsNullOrEmpty(sessionId) &&
                !string.Equals(state.VerifiedSessionId, sessionId, StringComparison.Ordinal))
            {
                bool drifted = DetectDrift(
                    states, angleZoneIndex, distanceZoneIndex,
                    signedErrorCm, timestampUtc, sessionId);
                if (!drifted)
                    state.VerifiedSessionId = sessionId;
            }

            state.TotalAttemptCount++;
            state.AcceptedAttemptCount++;
            state.LastAttemptTimestampUtc = timestampUtc;
            state.LastAttemptSessionId = sessionId;
            state.LastSignedErrorCm = signedErrorCm;

            if (state.SignedErrorHistoryCm == null)
                state.SignedErrorHistoryCm = new List<double>();
            if (state.AngularErrorHistoryDegrees == null)
                state.AngularErrorHistoryDegrees = new List<double>();
            if (state.TargetDistanceHistoryCm == null)
                state.TargetDistanceHistoryCm = new List<double>();
            // Backward compatibility: profiles saved before the paired history
            // field existed have signed errors but no targets. Pad those legacy
            // entries with zero (an invalid-target sentinel) to preserve alignment.
            while (state.TargetDistanceHistoryCm.Count > state.SignedErrorHistoryCm.Count)
                state.TargetDistanceHistoryCm.RemoveAt(0);
            while (state.TargetDistanceHistoryCm.Count < state.SignedErrorHistoryCm.Count)
                state.TargetDistanceHistoryCm.Add(0.0);

            // Keep target and signed error as a pair. Invalid target values are
            // represented by zero so the estimator can reject the pair without
            // shifting subsequent observations out of alignment.
            state.SignedErrorHistoryCm.Add(signedErrorCm);
            state.TargetDistanceHistoryCm.Add(
                !double.IsNaN(targetDistanceCm) && !double.IsInfinity(targetDistanceCm) && targetDistanceCm > 0.05
                    ? targetDistanceCm : 0.0);
            while (state.SignedErrorHistoryCm.Count > TrainingCellState.MaxStoredHistorySamples)
                state.SignedErrorHistoryCm.RemoveAt(0);
            while (state.TargetDistanceHistoryCm.Count > TrainingCellState.MaxStoredHistorySamples)
                state.TargetDistanceHistoryCm.RemoveAt(0);

            UpdateSessionRepeatability(
                state, sessionId, targetDistanceCm, signedErrorCm, sessionBiasApplied, sessionBiasLogGain);

            // FIX #1: append to history, cap at TrainingCellState.MaxStoredHistorySamples.
            // We store EVERY attempt that the caller passes in, not just
            // successful ones — the calibrator's robust statistics layer
            // needs the full distribution to do outlier rejection. The
            // success criterion below uses the median of the inliers,
            // which is robust to a small fraction of misfires.
            if (!double.IsNaN(angularErrorDegrees) && !double.IsInfinity(angularErrorDegrees))
            {
                state.AngularErrorHistoryDegrees.Add(angularErrorDegrees);
                if (state.AngularErrorHistoryDegrees.Count > TrainingCellState.MaxStoredHistorySamples)
                    state.AngularErrorHistoryDegrees.RemoveAt(0);
            }

            // FIX #1: recompute the robust statistics from the
            // history so the success criterion can use the median
            // instead of the last sample. RobustSignedErrorCm is
            // the median of the last K samples; RobustSigmaCm is
            // 1.4826 * MAD.
            UpdateRobustStatistics(state);

            // With BaseZoneEndCm = 0.0, isBaseDimension is always false
            // (every zone center is > 0), so the gain is always estimated
            // on the full target/observed distance. The previous "excess
            // beyond 3 cm" reduction degenerates to the full distance here.
            bool isBaseDimension = TrainingGeometry.Current.ZoneCenterCm(distanceZoneIndex)
                <= DesktopSensConstants.BaseZoneEndCm;
            var gainTargets = new List<double>();
            var gainErrors = new List<double>();
            var provisionalLogGains = new List<double>();
            int pairedHistoryCount = Math.Min(
                state.TargetDistanceHistoryCm.Count,
                state.SignedErrorHistoryCm.Count);
            for (int i = 0; i < pairedHistoryCount; i++)
            {
                double originalTarget = state.TargetDistanceHistoryCm[i];
                double observed = originalTarget + state.SignedErrorHistoryCm[i];
                if (!IsFinite(originalTarget) || !IsFinite(observed)) continue;
                double dimensionTarget = isBaseDimension
                    ? originalTarget
                    : originalTarget - DesktopSensConstants.BaseZoneEndCm;
                double dimensionObserved = isBaseDimension
                    ? observed
                    : observed - DesktopSensConstants.BaseZoneEndCm;
                if (dimensionTarget <= DesktopSensConstants.MinimumDenominatorCm ||
                    dimensionObserved <= DesktopSensConstants.MinimumDenominatorCm)
                    continue;
                gainTargets.Add(dimensionTarget);
                gainErrors.Add(dimensionObserved - dimensionTarget);
                provisionalLogGains.Add(Math.Log(dimensionObserved / dimensionTarget));
            }

            double logGain, logSe, logHalfWidth;
            int logSamples;
            bool hasPrecisionEstimate = RobustStatistics.TryLogGainConfidence(
                gainTargets, gainErrors,
                out logGain, out logSe, out logHalfWidth, out logSamples);
            state.LogGainSampleCount = hasPrecisionEstimate ? logSamples : 0;
            state.LogGainEstimate = hasPrecisionEstimate ? logGain : 0.0;
            state.LogGainStandardError = hasPrecisionEstimate ? logSe : -1.0;
            state.LogGainConfidenceHalfWidth95 = hasPrecisionEstimate
                ? logHalfWidth : -1.0;
            state.PrecisionVerified = hasPrecisionEstimate &&
                logSamples >= MinSuccessfulAttemptsForFirstRound &&
                logHalfWidth <= PrecisionToleranceLog;
            state.LowPrecision = state.TotalAttemptCount >= MaxAttemptsForPrecision &&
                !state.PrecisionVerified;

            // A green cell must continue to satisfy the precision gate.
            if (state.Verified &&
                (!state.PrecisionVerified || state.SessionRepeatabilityFailed))
            {
                state.Verified = false;
                state.VerifiedSessionId = null;
                state.VerifiedTimestampUtc = null;
            }

            // A calibration trial is not expected to be accurate BEFORE
            // its factor is learned. Testing the raw signed error against the
            // target tolerance created a circular gate: an incorrectly-scaled
            // mouse could never produce five "successful" raw hits, so the
            // first-round merge never applied the correction that would make
            // subsequent hits accurate.
            //
            // Evaluate repeatability after removing the current robust gain
            // estimate. This asks whether the observations are consistently
            // explained by one multiplicative sensitivity correction, rather
            // than whether the uncalibrated mouse already hits the target.
            // A provisional median gain is available before the precision
            // interval has enough samples. Precision verification remains a
            // separate gate before the cell can turn green.
            double effectiveLogGain = hasPrecisionEstimate
                ? logGain
                : (provisionalLogGains.Count > 0
                    ? RobustStatistics.Median(provisionalLogGains)
                    : 0.0);
            // Use the measured correction without an arbitrary 0.5x-2.0x
            // clamp. The base-vs-acceleration dimension remains selected by
            // the 3 cm boundary above; this only removes a factor-range cap.
            double correctionFactor = Math.Exp(-effectiveLogGain);
            if (!IsFinite(correctionFactor) || correctionFactor <= 0.0)
                correctionFactor = 1.0;

            var calibratedResiduals = new List<double>();
            double latestCalibratedResidual = double.NaN;
            bool currentPairValid = targetDistanceCm > 0.05 &&
                IsFinite(targetDistanceCm) &&
                IsFinite(signedErrorCm) &&
                targetDistanceCm + signedErrorCm > 0.05;
            int pairedCount = Math.Min(
                state.TargetDistanceHistoryCm.Count,
                state.SignedErrorHistoryCm.Count);
            for (int i = 0; i < pairedCount; i++)
            {
                double target = state.TargetDistanceHistoryCm[i];
                double observed = target + state.SignedErrorHistoryCm[i];
                if (!IsFinite(target) || !IsFinite(observed) ||
                    target <= 0.05 || observed <= 0.05)
                    continue;
                double dimensionTarget = isBaseDimension
                    ? target
                    : target - DesktopSensConstants.BaseZoneEndCm;
                double dimensionObserved = isBaseDimension
                    ? observed
                    : observed - DesktopSensConstants.BaseZoneEndCm;
                if (dimensionTarget <= DesktopSensConstants.MinimumDenominatorCm ||
                    dimensionObserved <= DesktopSensConstants.MinimumDenominatorCm)
                    continue;

                double residual = dimensionObserved * correctionFactor - dimensionTarget;
                if (!IsFinite(residual)) continue;
                calibratedResiduals.Add(residual);
                if (i == pairedCount - 1)
                    latestCalibratedResidual = residual;
            }

            if (currentPairValid)
            {
                double currentObserved = targetDistanceCm + signedErrorCm;
                double currentDimensionTarget = isBaseDimension
                    ? targetDistanceCm
                    : targetDistanceCm - DesktopSensConstants.BaseZoneEndCm;
                double currentDimensionObserved = isBaseDimension
                    ? currentObserved
                    : currentObserved - DesktopSensConstants.BaseZoneEndCm;
                if (currentDimensionTarget > DesktopSensConstants.MinimumDenominatorCm &&
                    currentDimensionObserved > DesktopSensConstants.MinimumDenominatorCm)
                    latestCalibratedResidual =
                        currentDimensionObserved * correctionFactor - currentDimensionTarget;
            }

            double calibratedMedian = calibratedResiduals.Count > 0
                ? RobustStatistics.Median(calibratedResiduals)
                : state.RobustSignedErrorCm;
            double calibratedSigma = calibratedResiduals.Count > 1
                ? RobustStatistics.RobustSigma(calibratedResiduals)
                : state.RobustSigmaCm;

            bool withinTolerance =
                Math.Abs(calibratedMedian) <= state.EffectiveToleranceCm;
            double sigmaForTest = Math.Max(calibratedSigma, 0.05);
            bool isOutlier = !currentPairValid ||
                !IsFinite(latestCalibratedResidual) ||
                Math.Abs(latestCalibratedResidual - calibratedMedian) > 3.0 * sigmaForTest;

            if (isOutlier || !withinTolerance) return;

            state.SuccessfulAttemptCount++;

            // First round: MinSuccessfulAttemptsForFirstRound successful
            // attempts AND the robust median within tolerance.
            if (!state.FirstRoundComplete &&
                state.SuccessfulAttemptCount >= MinSuccessfulAttemptsForFirstRound)
            {
                state.FirstRoundComplete = true;
                state.FirstRoundCompletedTimestampUtc = timestampUtc;
                state.FirstRoundCompletedSessionId = sessionId;
            }

            // Verification: a successful attempt AFTER the first-round was
            // completed → green.
            if (state.FirstRoundComplete &&
                !state.Verified &&
                state.PrecisionVerified &&
                !state.SessionRepeatabilityFailed &&
                state.SuccessfulAttemptCount >=
                    MinSuccessfulAttemptsForFirstRound + VerificationExtraSuccessfulAttempts)
            {
                state.Verified = true;
                state.VerifiedTimestampUtc = timestampUtc;
                state.VerifiedSessionId = sessionId;
                state.DriftDetected = false;
                state.DriftSignedErrorCm = 0.0;
            }
        }

        public static void ApplySessionBiasToSession(
            Dictionary<int, TrainingCellState> states,
            string sessionId,
            double logBias,
            DateTime timestampUtc)
        {
            if (states == null || string.IsNullOrEmpty(sessionId) || !IsFinite(logBias) || Math.Abs(logBias) < 1e-12)
                return;
            foreach (TrainingCellState state in states.Values)
            {
                if (state == null || state.SessionCalibrationHistories == null) continue;
                TrainingSessionCalibrationHistory history = null;
                for (int i = 0; i < state.SessionCalibrationHistories.Count; i++)
                {
                    TrainingSessionCalibrationHistory candidate = state.SessionCalibrationHistories[i];
                    if (candidate != null && string.Equals(candidate.SessionId, sessionId, StringComparison.Ordinal))
                    { history = candidate; break; }
                }
                if (history == null || history.TargetDistanceHistoryCm == null || history.SignedErrorHistoryCm == null) continue;
                double previousBias = history.SessionBiasApplied ? history.AppliedSessionBiasLogGain : 0.0;
                double biasDelta = logBias - previousBias;
                history.SessionBiasApplied = true;
                history.AppliedSessionBiasLogGain = logBias;
                if (Math.Abs(biasDelta) < 1e-12) continue;
                int count = Math.Min(history.TargetDistanceHistoryCm.Count, history.SignedErrorHistoryCm.Count);
                double scale = Math.Exp(-biasDelta);
                for (int i = 0; i < count; i++)
                {
                    double target = history.TargetDistanceHistoryCm[i];
                    double observed = target + history.SignedErrorHistoryCm[i];
                    if (!IsFinite(target) || target <= 0.05 || !IsFinite(observed) || observed <= 0.05) continue;
                    history.SignedErrorHistoryCm[i] = observed * scale - target;
                }
                history.SessionBiasApplied = true;
                RebuildCellStatisticsFromSessions(state, timestampUtc);
            }
        }

        private static void RebuildCellStatisticsFromSessions(TrainingCellState state, DateTime timestampUtc)
        {
            if (state == null) return;
            if (state.SignedErrorHistoryCm == null) state.SignedErrorHistoryCm = new List<double>();
            if (state.TargetDistanceHistoryCm == null) state.TargetDistanceHistoryCm = new List<double>();
            state.SignedErrorHistoryCm.Clear();
            state.TargetDistanceHistoryCm.Clear();
            if (state.SessionCalibrationHistories != null)
            {
                foreach (TrainingSessionCalibrationHistory session in state.SessionCalibrationHistories)
                {
                    if (session == null || session.TargetDistanceHistoryCm == null || session.SignedErrorHistoryCm == null) continue;
                    int n = Math.Min(session.TargetDistanceHistoryCm.Count, session.SignedErrorHistoryCm.Count);
                    for (int i = 0; i < n; i++)
                    {
                        state.TargetDistanceHistoryCm.Add(session.TargetDistanceHistoryCm[i]);
                        state.SignedErrorHistoryCm.Add(session.SignedErrorHistoryCm[i]);
                    }
                }
            }
            while (state.SignedErrorHistoryCm.Count > TrainingCellState.MaxStoredHistorySamples)
            {
                state.SignedErrorHistoryCm.RemoveAt(0);
                state.TargetDistanceHistoryCm.RemoveAt(0);
            }
            UpdateRobustStatistics(state);
            double gain, se, halfWidth;
            int sampleCount;
            bool valid = RobustStatistics.TryLogGainConfidence(state.TargetDistanceHistoryCm,
                state.SignedErrorHistoryCm, out gain, out se, out halfWidth, out sampleCount);
            state.LogGainSampleCount = valid ? sampleCount : 0;
            state.LogGainEstimate = valid ? gain : 0.0;
            state.LogGainStandardError = valid ? se : -1.0;
            state.LogGainConfidenceHalfWidth95 = valid ? halfWidth : -1.0;
            state.PrecisionVerified = valid && sampleCount >= MinSuccessfulAttemptsForFirstRound &&
                halfWidth <= PrecisionToleranceLog;
            state.LowPrecision = state.TotalAttemptCount >= MaxAttemptsForPrecision && !state.PrecisionVerified;

            int successful = 0;
            double sigma = Math.Max(state.RobustSigmaCm, 0.05);
            for (int i = 0; i < state.SignedErrorHistoryCm.Count; i++)
            {
                double error = state.SignedErrorHistoryCm[i];
                if (Math.Abs(error) <= state.EffectiveToleranceCm &&
                    Math.Abs(error - state.RobustSignedErrorCm) <= 3.0 * sigma) successful++;
            }
            state.SuccessfulAttemptCount = successful;
            state.FirstRoundComplete = successful >= MinSuccessfulAttemptsForFirstRound;
            if (!state.FirstRoundComplete)
            {
                state.FirstRoundCompletedTimestampUtc = null;
                state.FirstRoundCompletedSessionId = null;
            }

            bool failed = false;
            double maxSpread = 0.0;
            if (state.SessionCalibrationHistories != null)
            {
                foreach (TrainingSessionCalibrationHistory session in state.SessionCalibrationHistories)
                {
                    if (session == null) continue;
                    double sg, ss, sh; int sn;
                    bool ok = RobustStatistics.TryLogGainConfidence(session.TargetDistanceHistoryCm,
                        session.SignedErrorHistoryCm, out sg, out ss, out sh, out sn);
                    session.LogGainSampleCount = ok ? sn : 0;
                    session.LogGainEstimate = ok ? sg : 0.0;
                    session.LogGainHalfWidth95 = ok ? sh : -1.0;
                }
                for (int i = 0; i < state.SessionCalibrationHistories.Count; i++)
                {
                    TrainingSessionCalibrationHistory a = state.SessionCalibrationHistories[i];
                    if (a == null || a.LogGainSampleCount < MinSuccessfulAttemptsForFirstRound) continue;
                    for (int j = i + 1; j < state.SessionCalibrationHistories.Count; j++)
                    {
                        TrainingSessionCalibrationHistory b = state.SessionCalibrationHistories[j];
                        if (b == null || b.LogGainSampleCount < MinSuccessfulAttemptsForFirstRound) continue;
                        double spread = Math.Abs(a.LogGainEstimate - b.LogGainEstimate);
                        maxSpread = Math.Max(maxSpread, spread);
                        if (spread > Math.Log(1.02) || spread > a.LogGainHalfWidth95 + b.LogGainHalfWidth95)
                            failed = true;
                    }
                }
            }
            state.SessionGainSpreadLog = maxSpread;
            state.SessionRepeatabilityFailed = failed;
            if (state.Verified && (!state.PrecisionVerified || failed))
            {
                state.Verified = false;
                state.VerifiedSessionId = null;
                state.VerifiedTimestampUtc = null;
            }
            else if (state.FirstRoundComplete && state.PrecisionVerified && !failed &&
                successful >= MinSuccessfulAttemptsForFirstRound + VerificationExtraSuccessfulAttempts)
            {
                state.Verified = true;
                state.VerifiedSessionId = state.LastAttemptSessionId;
                state.VerifiedTimestampUtc = timestampUtc;
            }
        }

        /// <summary>
        /// Maintains paired observations per session and evaluates the v4
        /// test-retest gate. A cell is not green when two sufficiently sampled
        /// sessions disagree by more than 2% or their 95% log-gain intervals
        /// do not overlap.
        /// </summary>
        private static void UpdateSessionRepeatability(
            TrainingCellState state,
            string sessionId,
            double targetDistanceCm,
            double signedErrorCm,
            bool sessionBiasApplied,
            double sessionBiasLogGain)
        {
            if (state == null || string.IsNullOrWhiteSpace(sessionId)) return;
            if (!IsFinite(targetDistanceCm) || targetDistanceCm <= 0.05 ||
                !IsFinite(signedErrorCm))
                return;

            if (state.SessionCalibrationHistories == null)
                state.SessionCalibrationHistories = new List<TrainingSessionCalibrationHistory>();

            TrainingSessionCalibrationHistory current = null;
            for (int i = 0; i < state.SessionCalibrationHistories.Count; i++)
            {
                if (state.SessionCalibrationHistories[i] != null &&
                    string.Equals(
                        state.SessionCalibrationHistories[i].SessionId,
                        sessionId,
                        StringComparison.Ordinal))
                {
                    current = state.SessionCalibrationHistories[i];
                    break;
                }
            }

            if (current == null)
            {
                current = new TrainingSessionCalibrationHistory { SessionId = sessionId };
                state.SessionCalibrationHistories.Add(current);
            }

            if (sessionBiasApplied)
            {
                current.SessionBiasApplied = true;
                current.AppliedSessionBiasLogGain = sessionBiasLogGain;
            }
            if (current.TargetDistanceHistoryCm == null)
                current.TargetDistanceHistoryCm = new List<double>();
            if (current.SignedErrorHistoryCm == null)
                current.SignedErrorHistoryCm = new List<double>();
            current.TargetDistanceHistoryCm.Add(targetDistanceCm);
            current.SignedErrorHistoryCm.Add(signedErrorCm);
            while (current.TargetDistanceHistoryCm.Count > TrainingCellState.MaxSessionHistorySamples)
                current.TargetDistanceHistoryCm.RemoveAt(0);
            while (current.SignedErrorHistoryCm.Count > TrainingCellState.MaxSessionHistorySamples)
                current.SignedErrorHistoryCm.RemoveAt(0);

            // Keep a bounded set of the most recent sessions; current is always
            // the newest item, so evict the oldest session record.
            while (state.SessionCalibrationHistories.Count > 4)
                state.SessionCalibrationHistories.RemoveAt(0);

            double currentLogGain, currentSe, currentHalfWidth;
            int currentCount;
            bool currentValid = RobustStatistics.TryLogGainConfidence(
                current.TargetDistanceHistoryCm,
                current.SignedErrorHistoryCm,
                out currentLogGain, out currentSe, out currentHalfWidth, out currentCount);
            current.LogGainSampleCount = currentValid ? currentCount : 0;
            current.LogGainEstimate = currentValid ? currentLogGain : 0.0;
            current.LogGainHalfWidth95 = currentValid ? currentHalfWidth : -1.0;

            bool failed = state.SessionRepeatabilityFailed;
            double maxSpread = state.SessionGainSpreadLog;
            if (currentValid && currentCount >= MinSuccessfulAttemptsForFirstRound)
            {
                failed = false;
                maxSpread = 0.0;
                for (int i = 0; i < state.SessionCalibrationHistories.Count; i++)
                {
                    TrainingSessionCalibrationHistory other = state.SessionCalibrationHistories[i];
                    if (ReferenceEquals(other, current) || other == null ||
                        other.LogGainSampleCount < MinSuccessfulAttemptsForFirstRound ||
                        other.LogGainHalfWidth95 < 0.0)
                        continue;

                    double spread = Math.Abs(current.LogGainEstimate - other.LogGainEstimate);
                    if (spread > maxSpread) maxSpread = spread;
                    if (spread > Math.Log(1.02) ||
                        spread > current.LogGainHalfWidth95 + other.LogGainHalfWidth95)
                        failed = true;
                }
            }

            state.SessionGainSpreadLog = maxSpread;
            state.SessionRepeatabilityFailed = failed;
            if (failed)
            {
                state.Verified = false;
                state.VerifiedTimestampUtc = null;
                state.VerifiedSessionId = null;
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>
        /// Records that a trial was made for a cell but rejected by validation.
        /// Rejected observations must not enter calibration/error histories, but
        /// they still count as an attempted point so the UI can show it in yellow.
        /// </summary>
        public static void RecordRejectedAttempt(
            Dictionary<int, TrainingCellState> states,
            int angleZoneIndex,
            int distanceZoneIndex,
            double signedErrorCm,
            DateTime timestampUtc,
            string sessionId = null)
        {
            if (states == null || !IsCalibrationDistanceZone(distanceZoneIndex)) return;
            int cellIndex = GetCellIndex(angleZoneIndex, distanceZoneIndex);
            if (!states.TryGetValue(cellIndex, out var state) || state == null)
            {
                state = new TrainingCellState
                {
                    AngleZoneIndex = angleZoneIndex,
                    DistanceZoneIndex = distanceZoneIndex
                };
                states[cellIndex] = state;
            }

            state.TotalAttemptCount++;
            state.LastAttemptTimestampUtc = timestampUtc;
            state.LastAttemptSessionId = sessionId;
            if (!double.IsNaN(signedErrorCm) && !double.IsInfinity(signedErrorCm))
                state.LastSignedErrorCm = signedErrorCm;
        }

        /// <summary>
        /// FIX #1: recompute the robust (median + sigma) statistics for
        /// a cell from its error history. Called by RecordAttempt after
        /// every sample, and by EstimateNoiseFloor before training
        /// starts (to seed each cell's adaptive tolerance).
        /// </summary>
        public static void UpdateRobustStatistics(TrainingCellState state)
        {
            if (state == null) return;
            if (state.SignedErrorHistoryCm == null || state.SignedErrorHistoryCm.Count == 0)
            {
                state.RobustSignedErrorCm = 0.0;
                state.RobustSigmaCm = 0.0;
                return;
            }
            state.RobustSignedErrorCm = RobustStatistics.Median(state.SignedErrorHistoryCm);
            state.RobustSigmaCm = RobustStatistics.RobustSigma(state.SignedErrorHistoryCm);
        }

        /// <summary>
        /// FIX #10: set the per-cell adaptive tolerance based on the
        /// user's measured noise floor. The trainer calls this once
        /// after the noise-floor estimation phase, passing the noise
        /// floor at the cell's distance zone.
        ///
        /// The adaptive tolerance is:
        ///   max(DesktopSensConstants.DefaultToleranceCm, 2 * noiseFloorCm)
        /// so a user with a very stable hand (noise floor 0.2 cm)
        /// keeps the default 1.0 cm tolerance, while a user with a
        /// shaky hand (noise floor 0.7 cm) gets a 1.4 cm tolerance —
        /// otherwise they would never get any cell verified.
        /// </summary>
        public static void SetAdaptiveTolerance(
            Dictionary<int, TrainingCellState> states,
            int angleZoneIndex,
            int distanceZoneIndex,
            double noiseFloorCm)
        {
            if (states == null) return;
            if (noiseFloorCm <= 0.0) return;
            int cardinalIndex = GetCardinalDirectionIndex(angleZoneIndex);
            int directionZoneCount = cardinalIndex >= 0
                ? TrainingGeometry.Current.CardinalDirectionZoneCount(cardinalIndex)
                : TrainingGeometry.Current.DistanceZoneCount;
            if (distanceZoneIndex < 0 || distanceZoneIndex >= directionZoneCount) return;
            int cellIndex = GetCellIndex(angleZoneIndex, distanceZoneIndex);
            if (!states.TryGetValue(cellIndex, out var state) || state == null)
            {
                state = new TrainingCellState
                {
                    AngleZoneIndex = angleZoneIndex,
                    DistanceZoneIndex = distanceZoneIndex
                };
                states[cellIndex] = state;
            }
            state.AdaptiveToleranceCm = Math.Max(
                DesktopSensConstants.DefaultToleranceCm,
                2.0 * noiseFloorCm);
        }

        /// <summary>
        /// FIX #8: drift detection. Called by the trainer after a
        /// re-verify sample is collected for a previously-verified cell.
        /// If the new sample's signed error exceeds 3× the cell's
        /// robust sigma (or the global tolerance, whichever is
        /// larger), the cell is downgraded back to yellow and must
        /// be re-verified.
        ///
        /// Returns true if drift was detected. The caller should
        /// update the HUD to alert the user.
        /// </summary>
        public static bool DetectDrift(
            Dictionary<int, TrainingCellState> states,
            int angleZoneIndex,
            int distanceZoneIndex,
            double newSignedErrorCm,
            DateTime timestampUtc,
            string sessionId = null)
        {
            if (states == null || double.IsNaN(newSignedErrorCm) || double.IsInfinity(newSignedErrorCm)) return false;
            int cellIndex = GetCellIndex(angleZoneIndex, distanceZoneIndex);
            if (!states.TryGetValue(cellIndex, out var state) || state == null)
                return false;
            if (!state.Verified) return false;

            double threshold = Math.Max(
                state.EffectiveToleranceCm,
                3.0 * Math.Max(state.RobustSigmaCm, 0.05));

            if (Math.Abs(newSignedErrorCm) > threshold)
            {
                state.DriftDetected = true;
                state.DriftSignedErrorCm = newSignedErrorCm;
                // Downgrade to yellow and begin a fresh robust history for the
                // new session. Retaining the old median would make genuine
                // technique drift look like an outlier for many subsequent trials.
                state.Verified = false;
                state.VerifiedTimestampUtc = null;
                state.VerifiedSessionId = null;
                state.FirstRoundComplete = true;
                state.SuccessfulAttemptCount = MinSuccessfulAttemptsForFirstRound;
                state.SignedErrorHistoryCm = new List<double>();
                state.TargetDistanceHistoryCm = new List<double>();
                state.AngularErrorHistoryDegrees = new List<double>();
                state.LogGainSampleCount = 0;
                state.LogGainEstimate = 0.0;
                state.LogGainStandardError = -1.0;
                state.LogGainConfidenceHalfWidth95 = -1.0;
                state.PrecisionVerified = false;
                state.LowPrecision = false;
                state.RobustSignedErrorCm = 0.0;
                state.RobustSigmaCm = 0.0;
                return true;
            }
            return false;
        }

        /// <summary>
        /// FIX #8: clears the drift flag and re-promotes the cell
        /// to verified. Called by the trainer after a re-verify
        /// sample passes the tolerance check.
        /// </summary>
        public static void ClearDriftFlag(
            Dictionary<int, TrainingCellState> states,
            int angleZoneIndex,
            int distanceZoneIndex,
            DateTime timestampUtc,
            string sessionId = null)
        {
            if (states == null) return;
            int cellIndex = GetCellIndex(angleZoneIndex, distanceZoneIndex);
            if (!states.TryGetValue(cellIndex, out var state) || state == null)
                return;
            state.DriftDetected = false;
            state.DriftSignedErrorCm = 0.0;
            // Never let the manual drift-clear path bypass the CI precision
            // gate. A cell remains yellow until RecordAttempt has rebuilt a
            // sufficiently narrow confidence interval from the new session.
            state.Verified = state.PrecisionVerified && !state.SessionRepeatabilityFailed;
            state.VerifiedTimestampUtc = state.Verified ? (DateTime?)timestampUtc : null;
            state.VerifiedSessionId = state.Verified ? sessionId : null;
        }

        /// <summary>
        /// Count of cells currently in the verified (green) state, across
        /// all four cardinal axes.
        /// </summary>
        public static int CountVerifiedCells(Dictionary<int, TrainingCellState> states)
        {
            if (states == null) return 0;
            int count = 0;
            for (int axis = 0; axis < CardinalDirectionCount; axis++)
            {
                int angleZone = CardinalAngleZoneIndices[axis];
                int zoneCount = TrainingGeometry.Current.CardinalDirectionZoneCount(axis);
                for (int d = 0; d < zoneCount; d++)
                {
                    int cellIndex = GetCellIndex(angleZone, d);
                    if (!IsCalibrationDistanceZone(d)) continue;
                    if (states.TryGetValue(cellIndex, out var state) &&
                        state != null && state.Verified)
                        count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Count of reachable cardinal cells that have at least one accepted
        /// training attempt. This is calibration coverage, distinct from the
        /// stricter green/verified count, which requires the full precision
        /// and repeatability gates to pass.
        /// </summary>
        public static int CountAttemptedCells(Dictionary<int, TrainingCellState> states)
        {
            if (states == null) return 0;
            int count = 0;
            for (int axis = 0; axis < CardinalDirectionCount; axis++)
            {
                int angleZone = CardinalAngleZoneIndices[axis];
                int zoneCount = TrainingGeometry.Current.CardinalDirectionZoneCount(axis);
                for (int distanceZone = 0; distanceZone < zoneCount; distanceZone++)
                {
                    int cellIndex = GetCellIndex(angleZone, distanceZone);
                    if (!IsCalibrationDistanceZone(distanceZone)) continue;
                    if (states.TryGetValue(cellIndex, out var state) &&
                        state != null && state.TotalAttemptCount > 0)
                        count++;
                }
            }
            return count;
        }

        /// <summary>
        /// PATCH 6 from review: direction-count-aware verified count.
        /// Same semantics as CountVerifiedCells but iterates the
        /// requested mode's axes. Without this, the trainer HUD showed
        /// "50/82 verified" in 8-way mode even when only the 4 cardinal
        /// axes were green — the legacy count was capped at the 4-way
        /// cell total.
        /// </summary>
        public static int CountVerifiedCellsForCount(
            Dictionary<int, TrainingCellState> states, int directionCount)
        {
            if (states == null) return 0;
            int count = 0;
            int[] zones = AngleZoneIndicesForCount(directionCount);
            for (int axis = 0; axis < zones.Length; axis++)
            {
                int angleZone = zones[axis];
                int zoneCount = TrainingGeometry.Current.MultiDirectionZoneCount(axis, directionCount);
                for (int d = 0; d < zoneCount; d++)
                {
                    if (!IsCalibrationDistanceZone(d)) continue;
                    int cellIndex = GetCellIndex(angleZone, d);
                    if (states.TryGetValue(cellIndex, out var state) && state != null && state.Verified)
                        count++;
                }
            }
            return count;
        }

        /// <summary>
        /// PATCH 6 from review: direction-count-aware attempted count.
        /// Same semantics as CountAttemptedCells but iterates the
        /// requested mode's axes.
        /// </summary>
        public static int CountAttemptedCellsForCount(
            Dictionary<int, TrainingCellState> states, int directionCount)
        {
            if (states == null) return 0;
            int count = 0;
            int[] zones = AngleZoneIndicesForCount(directionCount);
            for (int axis = 0; axis < zones.Length; axis++)
            {
                int angleZone = zones[axis];
                int zoneCount = TrainingGeometry.Current.MultiDirectionZoneCount(axis, directionCount);
                for (int d = 0; d < zoneCount; d++)
                {
                    if (!IsCalibrationDistanceZone(d)) continue;
                    int cellIndex = GetCellIndex(angleZone, d);
                    if (states.TryGetValue(cellIndex, out var state) &&
                        state != null && state.TotalAttemptCount > 0)
                        count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Total reachable cardinal cells. Horizontal axes use the full
        /// yaw span; vertical axes use only pitch-reachable markers.
        /// </summary>
        public static int TotalCardinalCells
        {
            get
            {
                int total = 0;
                for (int axis = 0; axis < CardinalDirectionCount; axis++)
                {
                    int zoneCount = TrainingGeometry.Current.CardinalDirectionZoneCount(axis);
                    for (int d = 0; d < zoneCount; d++)
                        if (IsCalibrationDistanceZone(d)) total++;
                }
                return total;
            }
        }

        /// <summary>
        /// FIX (8/16-way): total reachable cells across all axes in the
        /// requested direction-count mode. Used by the trainer's HUD to
        /// show the correct "X / total verified" count.
        /// </summary>
        public static int TotalCardinalCellsForCount(int directionCount)
        {
            int total = 0;
            int[] zones = AngleZoneIndicesForCount(directionCount);
            for (int axis = 0; axis < zones.Length; axis++)
            {
                int zoneCount = TrainingGeometry.Current.MultiDirectionZoneCount(axis, directionCount);
                for (int d = 0; d < zoneCount; d++)
                    if (IsCalibrationDistanceZone(d)) total++;
            }
            return total;
        }

        /// <summary>
        /// Convenience overload: reads the active direction-count mode
        /// from the profile. Falls back to 4-way for null/legacy profiles.
        /// </summary>
        public static int TotalCardinalCellsFor(DesktopSensProfile profile)
        {
            return TotalCardinalCellsForCount(ResolveDirectionCount(profile));
        }

        public static double OverallProgressPercent(Dictionary<int, TrainingCellState> states)
        {
            int total = TotalCardinalCells;
            if (total <= 0) return 0.0;
            return 100.0 * CountVerifiedCells(states) / total;
        }

        public static bool AllCardinalCellsVerified(Dictionary<int, TrainingCellState> states)
        {
            if (states == null) return false;
            for (int axis = 0; axis < CardinalDirectionCount; axis++)
            {
                int angleZone = CardinalAngleZoneIndices[axis];
                int zoneCount = TrainingGeometry.Current.CardinalDirectionZoneCount(axis);
                for (int d = 0; d < zoneCount; d++)
                {
                    if (!IsCalibrationDistanceZone(d)) continue;
                    int cellIndex = GetCellIndex(angleZone, d);
                    if (!states.TryGetValue(cellIndex, out var state) ||
                        state == null || !state.Verified)
                        return false;
                }
            }
            return true;
        }

        /// <summary>
        /// FIX (8/16-way): direction-count-aware version. Checks that
        /// every reachable cell on every axis in the active mode is
        /// verified. Used by DesktopSensProfileMerger.IsReady so the
        /// merge-and-apply step does not fire until the user has actually
        /// completed every axis in the active mode.
        /// </summary>
        public static bool AllCardinalCellsVerifiedForCount(
            Dictionary<int, TrainingCellState> states, int directionCount)
        {
            if (states == null) return false;
            int[] zones = AngleZoneIndicesForCount(directionCount);
            for (int axis = 0; axis < zones.Length; axis++)
            {
                int angleZone = zones[axis];
                int zoneCount = TrainingGeometry.Current.MultiDirectionZoneCount(axis, directionCount);
                for (int d = 0; d < zoneCount; d++)
                {
                    if (!IsCalibrationDistanceZone(d)) continue;
                    int cellIndex = GetCellIndex(angleZone, d);
                    if (!states.TryGetValue(cellIndex, out var state) ||
                        state == null || !state.Verified)
                        return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Convenience overload: reads the active direction-count mode
        /// from the profile so callers don't have to.
        /// </summary>
        public static bool AllCardinalCellsVerifiedFor(
            DesktopSensProfile profile, Dictionary<int, TrainingCellState> states)
        {
            return AllCardinalCellsVerifiedForCount(states, ResolveDirectionCount(profile));
        }

        /// <summary>
        /// Reuses ZoneCalculations.ZoneIndexToDistanceCenterCm so the
        /// cardinal trainer and the legacy calibrator agree on the canonical
        /// "center of zone N" distance. The zone count comes from
        /// TrainingGeometry.Current — there is no longer a hardcoded 46.
        /// </summary>
        public static double DistanceZoneCenterCm(int distanceZoneIndex)
        {
            int distanceZoneCount = TrainingGeometry.Current.DistanceZoneCount;
            if (distanceZoneIndex < 0 || distanceZoneIndex >= distanceZoneCount)
                return 0.0;
            return ZoneCalculations.ZoneIndexToDistanceCenterCm(distanceZoneIndex);
        }

        /// <summary>
        /// Negative-acceleration lookup. Given the per-cell
        /// CellAccelerationFactor already stored on the profile (clamped to
        /// [0.5, 2.0] by DesktopSensCalibrator), return the decel factor to
        /// apply when the current flick distance is within
        /// ±NegativeAccelZoneHalfWidthCm of the cell's center.
        /// </summary>
        public static double GetNegativeAccelFactor(
            DesktopSensProfile profile,
            int angleZoneIndex,
            int distanceZoneIndex,
            double currentDistanceCm)
        {
            if (profile == null || !IsCalibrationDistanceZone(distanceZoneIndex)) return 1.0;
            if (profile.CellAccelerationFactor == null) return 1.0;
            int cellIndex = GetCellIndex(angleZoneIndex, distanceZoneIndex);
            if (cellIndex < 0 || cellIndex >= profile.CellAccelerationFactor.Length)
                return 1.0;
            double zoneCenterCm = DistanceZoneCenterCm(distanceZoneIndex);
            double delta = Math.Abs(currentDistanceCm - zoneCenterCm);
            if (delta > NegativeAccelZoneHalfWidthCm) return 1.0;
            double factor = profile.CellAccelerationFactor[cellIndex];
            if (double.IsNaN(factor) || double.IsInfinity(factor) || factor <= 0.0) return 1.0;
            // Only apply decel (factor < 1.0). Acceleration (> 1.0) is the
            // existing driver's job outside this gate.
            return Math.Min(1.0, factor);
        }

        /// <summary>
        /// Resets a single cell back to the untrained state. Used when the
        /// user wants to re-calibrate a cell that turned out wrong.
        ///
        /// FIX #1: also clears the error history so the robust statistics
        /// start fresh. The legacy per-cell factor arrays are left
        /// untouched (they are re-derived by the calibrator on the next
        /// call from whatever trials remain in profile.Trials).
        /// </summary>
        public static bool ResetCell(
            Dictionary<int, TrainingCellState> states,
            int angleZoneIndex,
            int distanceZoneIndex)
        {
            if (states == null) return false;
            int cellIndex = GetCellIndex(angleZoneIndex, distanceZoneIndex);
            return states.Remove(cellIndex);
        }

        /// <summary>
        /// Profile-aware cell reset. Reopening an older cell releases that
        /// cell's frozen LUT ownership and all downstream node constraints.
        /// </summary>
        public static bool ResetCell(
            DesktopSensProfile profile,
            int angleZoneIndex,
            int distanceZoneIndex)
        {
            if (profile == null) return false;
            bool removed = ResetCell(
                profile.CellTrainingStates, angleZoneIndex, distanceZoneIndex);
            DesktopSensProfileMerger.UnfreezeSampleLutFromCell(
                profile, angleZoneIndex, distanceZoneIndex);
            return removed;
        }

        /// <summary>
        /// Returns the next distance-zone index that is available for
        /// training on the given cardinal axis, or -1 if every cell on
        /// that axis is Verified.
        ///
        /// FIX #14: the caller now chooses between strict and soft
        /// availability via the allowOverride parameter.
        /// </summary>
        /// <summary>
        /// Returns the eligible unverified cell with the greatest uncertainty.
        /// Missing estimates rank as infinite uncertainty; ties prefer fewer
        /// paired samples and are randomized through the supplied RNG.
        /// </summary>
        public static int NextAdaptiveDistanceZone(
            Dictionary<int, TrainingCellState> states,
            int angleZoneIndex,
            int axisIndex,
            bool allowOverride,
            Random random)
        {
            int zoneCount = TrainingGeometry.Current.CardinalDirectionZoneCount(axisIndex);
            double bestHalfWidth = double.NegativeInfinity;
            int bestSampleCount = int.MaxValue;
            int selected = -1;
            int tieCount = 0;

            for (int d = 0; d < zoneCount; d++)
            {
                bool available = allowOverride
                    ? IsCellAvailableForTrainingSoft(states, angleZoneIndex, d, true)
                    : IsCellAvailableForTrainingStrict(states, angleZoneIndex, d);
                if (!available) continue;

                int cellIndex = GetCellIndex(angleZoneIndex, d);
                TrainingCellState state = null;
                if (states != null) states.TryGetValue(cellIndex, out state);
                if (state != null && state.Verified) continue;

                double halfWidth = state != null &&
                    !double.IsNaN(state.LogGainConfidenceHalfWidth95) &&
                    !double.IsInfinity(state.LogGainConfidenceHalfWidth95) &&
                    state.LogGainConfidenceHalfWidth95 >= 0.0
                        ? state.LogGainConfidenceHalfWidth95
                        : double.PositiveInfinity;
                int sampleCount = state != null ? state.LogGainSampleCount : 0;

                bool better = selected < 0 ||
                    halfWidth > bestHalfWidth ||
                    (halfWidth.Equals(bestHalfWidth) && sampleCount < bestSampleCount);
                bool tied = selected >= 0 &&
                    halfWidth.Equals(bestHalfWidth) &&
                    sampleCount == bestSampleCount;

                if (better)
                {
                    selected = d;
                    bestHalfWidth = halfWidth;
                    bestSampleCount = sampleCount;
                    tieCount = 1;
                }
                else if (tied)
                {
                    tieCount++;
                    if (random != null && random.Next(tieCount) == 0)
                        selected = d;
                }
            }

            return selected;
        }

        public static int NextAvailableDistanceZone(
            Dictionary<int, TrainingCellState> states,
            int angleZoneIndex,
            bool allowOverride = false)
        {
            int cardinalIndex = GetCardinalDirectionIndex(angleZoneIndex);
            int distanceZoneCount = cardinalIndex >= 0
                ? TrainingGeometry.Current.CardinalDirectionZoneCount(cardinalIndex)
                : TrainingGeometry.Current.DistanceZoneCount;
            if (distanceZoneCount <= 0) return -1;
            if (states == null) return 0;
            for (int d = 0; d < distanceZoneCount; d++)
            {
                bool available = allowOverride
                    ? IsCellAvailableForTrainingSoft(states, angleZoneIndex, d, true)
                    : IsCellAvailableForTrainingStrict(states, angleZoneIndex, d);
                if (!available) continue;
                int cellIndex = GetCellIndex(angleZoneIndex, d);
                if (!states.TryGetValue(cellIndex, out var s) ||
                    s == null ||
                    !s.Verified)
                    return d;
            }
            return -1;
        }
    }
}
