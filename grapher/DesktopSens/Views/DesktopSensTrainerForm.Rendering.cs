using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using OpenTK;
using grapher.DesktopSens.Models;
using grapher.Models.Mouse;

namespace grapher.DesktopSens.Views
{
    public partial class DesktopSensTrainerForm
    {
        /// <summary>
        /// Pushes training-mode properties into the arena control so it can
        /// render the colored axes / yellow / green markers and pulse the
        /// next-eligible cell on each axis.
        ///
        /// In Sens Finder mode this is a no-op — Sens Finder doesn't use
        /// the cardinal-trainer overlay (no yellow/green markers, no
        /// next-eligible pulse). The arena just renders the floor grid,
        /// the start/target markers, and the crosshair, which is all
        /// handled by the existing DrawIndoorRange / DrawAzimuthTicks /
        /// DrawZoneGrid path that runs regardless of training mode.
        /// </summary>
        private void UpdateTrainingArenaProperties()
        {
            // Sens Finder mode = no cardinal-trainer overlay.
            if (_sensitivityFinderMode) return;

            if (_arena3D == null || _arena3D.IsDisposed) return;
            // FIX 4 (from AI review): use _showTrainingMarkers (the
            // overlay-visibility flag toggled by T) instead of
            // _isTrainingMode (which is now always true in training
            // mode and only flipped by the Sens Finder/Trainer
            // constructor). This decouples the on-screen marker overlay
            // from the recording mode so the user can hide the visual
            // clutter while still recording calibration attempts.
            _arena3D.ShowTrainingMarkers = _showTrainingMarkers;
            _arena3D.TrainingStates = _profile.CellTrainingStates;
            // FIX (8/16-way): push the active direction-count mode and the
            // currently-selected axis into the arena so the renderer can
            // pick the right color palette and brighten the selected axis.
            int directionCount = DesktopSensTrainingState.ResolveDirectionCount(_profile);
            _arena3D.ActiveDirectionCount = directionCount;
            _arena3D.SelectedAxisIndex = _selectedAxisIndex;
            _arena3D.HighlightSelectedAxis = true;
            // Rebuild canonical markers whenever the direction-count mode
            // changes so the arena draws the right number of axes.
            EnsureCanonicalTrainingPoints();
            _arena3D.CanonicalTrainingMarkerPositions = _canonicalTrainingMarkerPositions;
            _arena3D.CanonicalTrainingMarkerDistanceCm = _canonicalTrainingMarkerDistanceCm;

            // Compute the next eligible distance zone for each axis in the
            // active direction-count mode so the arena can pulse the right
            // marker on each axis.
            int axisCount = DesktopSensTrainingState.AngleZoneIndicesForCount(directionCount).Length;
            int[] nextEligible = new int[axisCount];
            for (int i = 0; i < axisCount; i++)
            {
                nextEligible[i] = DesktopSensTrainingState.NextAdaptiveDistanceZone(
                    _profile.CellTrainingStates,
                    DesktopSensTrainingState.AngleZoneIndicesForCount(directionCount)[i],
                    i,
                    allowOverride: _profile.AllowSequentialOverride,
                    random: null); // Stable indicator; randomize only on explicit axis selection.
            }
            _arena3D.NextEligibleDistanceZone = nextEligible;
            _arena3D.PulsePhase = _trainingPulsePhase;
        }

        /// <summary>
        /// Builds the one authoritative set of angular training points.
        /// Points are spaced using the profile's TrainingMarkerStepDegrees up to
        /// TrainingGeometry.Current.MaxAxisDegrees horizontally. Vertical axes stop at
        /// the trainer's real pitch limit (about ±89°), so unreachable
        /// points are not generated beyond the camera limit.
        ///
        /// REFACTORED: this method now drives TrainingGeometry.Current
        /// as the single source of truth. The step / max-axis / max-pitch
        /// are the trainer's geometry settings, but they are also pushed
        /// into TrainingGeometry via InitializeTrainingMode so every
        /// downstream component (calibrator, merger, profile arrays,
        /// arena) reads the same numbers. If the user later wants to
        /// expose the step in the UI, change TrainingMarkerStepDegrees
        /// (or read it from a UI control) and call
        /// TrainingGeometry.Configure(...) — every consumer follows.
        ///
        /// FIX (8/16-way): the array now has one entry per axis in the
        /// active direction-count mode (4, 8, or 16). The diagonal axes
        /// (8-way indices 1, 3, 5, 7; 16-way indices 1, 3, 5, ...) get
        /// their yaw and pitch combined so the marker sits at 45° in
        /// both axes; their marker count is bounded by the smaller of
        /// the horizontal and vertical limits so unreachable markers
        /// are never generated.
        /// </summary>
        private void EnsureCanonicalTrainingPoints()
        {
            // Determine which direction-count mode is active. The cached
            // arrays are tied to a specific count so a switch rebuilds
            // them from scratch.
            int directionCount = DesktopSensTrainingState.ResolveDirectionCount(_profile);

            if (_canonicalTrainingMarkerPositions != null &&
                _canonicalTrainingMarkerDistanceCm != null &&
                _canonicalTrainingMarkerPositions.Length == directionCount &&
                _canonicalDirectionCountCached == directionCount)
                return;

            double maxYawDegrees = TrainingGeometry.Current.MaxAxisDegrees;
            double maxDistanceCm = Math.Max(0.001, _maxDistanceCm);
            int axisCount = directionCount;
            _canonicalTrainingMarkerPositions = new Vector3[axisCount][];
            _canonicalTrainingMarkerDistanceCm = new double[axisCount][];
            _canonicalDirectionCountCached = directionCount;

            int[] angleZones = DesktopSensTrainingState.AngleZoneIndicesForCount(directionCount);
            for (int axis = 0; axis < axisCount; axis++)
            {
                int zone = angleZones[axis];
                // The axis direction in the calibration frame: 0° = Right,
                // 90° = Down, 180° = Left, 270° = Up. Each axis is at
                // zone * (360 / 32) = zone * 11.25 degrees.
                double axisAngleDeg = zone * (360.0 / 32.0);
                axisAngleDeg = ((axisAngleDeg % 360.0) + 360.0) % 360.0;

                // Decompose into yaw (horizontal) and pitch (vertical).
                // The cardinal axes (0, 90, 180, 270) are pure yaw or
                // pure pitch; the diagonals are 50/50.
                bool isPureRight = Math.Abs(axisAngleDeg - 0.0) < 0.01;
                bool isPureLeft = Math.Abs(axisAngleDeg - 180.0) < 0.01;
                bool isPureDown = Math.Abs(axisAngleDeg - 90.0) < 0.01;
                bool isPureUp = Math.Abs(axisAngleDeg - 270.0) < 0.01;

                double axisMaxDegrees;
                if (isPureRight || isPureLeft)
                    axisMaxDegrees = TrainingGeometry.Current.MaxAxisDegrees;
                else if (isPureDown || isPureUp)
                    axisMaxDegrees = Math.Min(TrainingGeometry.Current.MaxPitchDegrees, TrainingGeometry.Current.MaxAxisDegrees);
                else
                    axisMaxDegrees = Math.Min(TrainingGeometry.Current.MaxPitchDegrees, TrainingGeometry.Current.MaxAxisDegrees);

                int markerCount = TrainingGeometry.Current.MultiDirectionZoneCount(axis, directionCount);
                markerCount = Math.Max(0, markerCount);
                _canonicalTrainingMarkerPositions[axis] = new Vector3[markerCount];
                _canonicalTrainingMarkerDistanceCm[axis] = new double[markerCount];

                for (int i = 0; i < markerCount; i++)
                {
                    // Delegate to TrainingGeometry.MarkerAngularDegrees so the
                    // renderer honours Zone0SubMarkerCount: when > 1, the first
                    // N markers subdivide the original zone-0 range [0, step]
                    // into N slices at step/N, 2*step/N, ..., step. This is the
                    // user's "3 calibration points in zone 0 per axis" directive.
                    double degrees = TrainingGeometry.Current.MarkerAngularDegrees(i);
                    degrees = Math.Min(axisMaxDegrees, Math.Max(0.0, degrees));
                    // Keep the existing distance-based calibration model in sync
                    // with the angular marker: 180° maps to _maxDistanceCm.
                    double distanceCm = degrees / Math.Max(0.001, maxYawDegrees) * maxDistanceCm;

                    // Decompose the angular travel into yaw and pitch
                    // according to the axis's direction in the
                    // calibration frame. Pure cardinal axes travel only
                    // in yaw or only in pitch; diagonal axes split the
                    // angular travel between them (e.g., DownRight at
                    // 45° puts 0.707 * degrees into yaw and 0.707 *
                    // degrees into -pitch).
                    double yawDeg, pitchDeg;
                    if (isPureRight) { yawDeg = degrees; pitchDeg = 0.0; }
                    else if (isPureLeft) { yawDeg = -degrees; pitchDeg = 0.0; }
                    else if (isPureDown) { yawDeg = 0.0; pitchDeg = -degrees; }
                    else if (isPureUp) { yawDeg = 0.0; pitchDeg = degrees; }
                    else
                    {
                        // Diagonal: split degrees into yaw and pitch
                        // proportional to the axis's cardinal-frame
                        // projection. A 45° diagonal puts cos(45) = 0.707
                        // of the magnitude into each axis.
                        double yawFraction = Math.Cos(axisAngleDeg * Math.PI / 180.0);
                        double pitchFraction = Math.Sin(axisAngleDeg * Math.PI / 180.0);
                        // The angular magnitude is the hypotenuse, so each
                        // component is the fraction of `degrees` along its
                        // axis. Pitch follows the Windows convention
                        // (positive = up; negative = down).
                        yawDeg = degrees * yawFraction;
                        pitchDeg = -degrees * pitchFraction;
                    }

                    // Store the exact same world position that the target-placement
                    // path will use; Arena receives this position and only renders it.
                    _canonicalTrainingMarkerPositions[axis][i] =
                        WorldPointFromAngles(NormalizeAngle(yawDeg), pitchDeg, distanceCm);
                    _canonicalTrainingMarkerDistanceCm[axis][i] = distanceCm;
                }
            }
        }

        /// <summary>
        /// Updates the training HUD overlay with: verified count / total,
        /// overall %, current selection (axis name + zone index + zone
        /// center distance), and attempt counter for the selected cell.
        ///
        /// In Sens Finder mode this is a no-op — the cardinal-trainer
        /// HUD (with verified-cell count and 1/2/3/4/T/M/R hotkey list)
        /// is not created in Sens Finder mode (see InitializeTrainingMode).
        /// Sens Finder shows its own progress in the existing _hud label
        /// via the SENS FINDER banner that UpdateHud already adds.
        /// </summary>
        private void UpdateTrainingHud()
        {
            // Sens Finder has no training HUD — skip to avoid touching a
            // null _trainingHud.
            if (_sensitivityFinderMode) return;
            if (_trainingHud == null) return;
            // PATCH 6 from review: use direction-count-aware counts so the
            // HUD's "X/total" denominator AND numerator both match the
            // active mode. The legacy CountAttemptedCells /
            // CountVerifiedCells only iterate the 4-way axes, so in
            // 8-way / 16-way mode the HUD showed "50/82 verified" even
            // when only the 4 cardinal axes were green.
            int directionCount = DesktopSensTrainingState.ResolveDirectionCount(_profile);
            int calibrated = DesktopSensTrainingState.CountAttemptedCellsForCount(_profile.CellTrainingStates, directionCount);
            int verified = DesktopSensTrainingState.CountVerifiedCellsForCount(_profile.CellTrainingStates, directionCount);
            // FIX (8/16-way): use the direction-count-aware total so the
            // HUD's "X/total" denominator matches the active mode.
            int total = DesktopSensTrainingState.TotalCardinalCellsForCount(directionCount);
            double pct = total > 0 ? 100.0 * calibrated / total : 0.0;

            // FIX (8/16-way): show the axis name in the active mode so
            // 8-way / 16-way users see "DownRight", "UpLeft", etc.
            string axisName;
            if (_selectedAxisIndex >= 0)
                axisName = DesktopSensTrainingState.GetAxisName(_selectedAxisIndex, directionCount).ToUpperInvariant();
            else
                axisName = "—";

            string cellLine;
            if (_selectedAxisIndex >= 0 && _selectedDistanceZone >= 0)
            {
                int angleZone = DesktopSensTrainingState.AngleZoneIndicesForCount(directionCount)[_selectedAxisIndex];
                int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, _selectedDistanceZone);
                double zoneCenterCm = DesktopSensTrainingState.DistanceZoneCenterCm(_selectedDistanceZone);
                int successCount = 0, totalCount = 0;
                bool firstRound = false, verifiedFlag = false, lowPrecision = false;
                bool sessionMismatch = false;
                double sessionSpreadLog = 0.0;
                double lastError = 0.0;
                double ciHalfWidth = double.PositiveInfinity;
                int precisionSamples = 0;
                if (_profile.CellTrainingStates.TryGetValue(cellIndex, out var st) && st != null)
                {
                    successCount = st.SuccessfulAttemptCount;
                    totalCount = st.TotalAttemptCount;
                    firstRound = st.FirstRoundComplete;
                    verifiedFlag = st.Verified;
                    lowPrecision = st.LowPrecision;
                    sessionMismatch = st.SessionRepeatabilityFailed;
                    sessionSpreadLog = st.SessionGainSpreadLog;
                    lastError = st.LastSignedErrorCm;
                    ciHalfWidth = st.LogGainConfidenceHalfWidth95;
                    precisionSamples = st.LogGainSampleCount;
                }
                string cellState = verifiedFlag ? "GREEN"
                                  : sessionMismatch ? "SESSION-RETEST"
                                  : lowPrecision ? "LOW-PRECISION"
                                  : firstRound ? "YELLOW"
                                  : "WHITE";
                // FIX (AI review root-cause #3): show WHY the cell is
                // still yellow so the user knows it's not a bug — they
                // just need more flicks. The precision gate requires
                // CI95 half-width ≤ PrecisionToleranceLog (now 5%).
                // Before this fix, the HUD showed "YELLOW" with no
                // explanation and the user thought calibration was
                // broken. Now it shows the current CI and the target.
                string ciText = ciHalfWidth < 0.0 || double.IsNaN(ciHalfWidth) || double.IsInfinity(ciHalfWidth)
                    ? "CI95 n/a"
                    : $"CI95 ±{(Math.Exp(ciHalfWidth) - 1.0) * 100.0:0.00}% (n={precisionSamples})";
                // Add a precision-gate hint for yellow cells so the
                // user knows what's needed to go green.
                string precisionHint = "";
                if (firstRound && !verifiedFlag && !lowPrecision &&
                    ciHalfWidth > 0.0 && ciHalfWidth < 100.0)
                {
                    double targetPct = (Math.Exp(DesktopSensTrainingState.PrecisionToleranceLog) - 1.0) * 100.0;
                    double currentPct = (Math.Exp(ciHalfWidth) - 1.0) * 100.0;
                    precisionHint = $"  | NEEDS CI ≤ {targetPct:0.0}% (now {currentPct:0.0}%) — flick more to tighten";
                }
                string sessionText = sessionMismatch
                    ? $" | session Δ {(Math.Exp(sessionSpreadLog) - 1.0) * 100.0:0.00}% — retest"
                    : "";
                cellLine = $"SEL: {axisName} zone {_selectedDistanceZone} ({zoneCenterCm:0.0}cm) " +
                           $"→ {cellState}  | attempts {successCount}/{DesktopSensTrainingState.MinSuccessfulAttemptsForFirstRound} " +
                           $"(total {totalCount})  | last err {lastError:+0.00;-0.00;0.00}cm  | {ciText}{sessionText}{precisionHint}";
            }
            else if (_selectedAxisIndex >= 0 && _selectedDistanceZone < 0)
            {
                // The user finished a trial on the selected axis but
                // hasn't picked the next zone yet. The axis stays
                // selected (per the user's revised directive), so we
                // show the axis name and prompt them to pick the next
                // zone via click or hotkey.
                string zoneHint;
                if (directionCount == 8)
                    zoneHint = "press 1-8 (or click a marker) for the next zone";
                else if (directionCount == 16)
                    zoneHint = "press 1-9,0,Q,E,F,G,H,J (or click) for the next zone";
                else
                    zoneHint = "press 1/2/3/4 (or click a marker) for the next zone";
                cellLine = $"SEL: {axisName} axis  →  pick next zone  ({zoneHint})";
            }
            else
            {
                // Match the keys bound in HandleTrainingModeKey for the
                // active mode so the hint is always accurate.
                if (directionCount == 8)
                    cellLine = "SEL: none  (press 1-8 to pick an axis)";
                else if (directionCount == 16)
                    cellLine = "SEL: none  (press 1-9,0,Q,E,F,G,H,J to pick an axis)";
                else
                    cellLine = "SEL: none  (press 1/2/3/4 to pick an axis)";
            }

            // FIX (8/16-way): the hotkey list grows with the mode. The
            // 16-way list must match the actual keys bound in
            // HandleTrainingModeKey — 1-9 + 0 + Q, E, F, G, H, J — NOT
            // "Q-P" (which would include W and R, both reserved).
            string hotkeyLine;
            if (directionCount == 8)
                hotkeyLine = "1-8=axes  |  T=overlay  |  M=merge  |  U=force-merge  |  R=reset  |  V=A/B  |  O=seq-override";
            else if (directionCount == 16)
                hotkeyLine = "1-9,0,Q,E,F,G,H,J=16 axes  |  T=overlay  |  M=merge  |  U=force-merge  |  R=reset  |  V=A/B";
            else
                hotkeyLine = "1=Right  2=Down  3=Left  4=Up  |  T=overlay  |  M=merge  |  U=force-merge  |  R=reset  |  V=A/B";

            string modeBanner = directionCount == 4
                ? "4-WAY"
                : (directionCount == 8 ? "8-WAY" : "16-WAY");
            _trainingHud.Text =
                $"TRAINING [{modeBanner}]  |  {calibrated}/{total} attempted ({pct:0.0}%)  |  {verified}/{total} verified  |  all-green: {(_profile.AllCardinalCellsVerified ? "YES" : "no")}\n" +
                cellLine + "\n" +
                hotkeyLine;

            // PATCHED: also update the calib-factor HUD so the user sees
            // the calibrator's per-cell factor and confidence update in real
            // time as they ASDW into a marker and flick.
            try { UpdateCalibFactorHud(); } catch { }
        }
    }
}
