using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using OpenTK;
using grapher.DesktopSens.Models;
using grapher.Models.Mouse;

namespace grapher.DesktopSens.Views
{
    /// <summary>
    /// Cardinal-training state machine and hotkey orchestration for the trainer partial.
    /// Rendering, backup, noise-floor, A/B and fixes helpers live in focused partial files.
    /// All partial files share this class's private state and compile into one form.
    /// </summary>
    public partial class DesktopSensTrainerForm
    {
        // =========================================================================
        // Training-mode state (added).
        // =========================================================================

        private bool _isTrainingMode = true; // Training mode is on by default.
        // FIX 4 (from AI review): T now only toggles the on-screen overlay
        // (the coloured markers), NOT the recording mode. _isTrainingMode
        // stays true so the user can hide the visual clutter while still
        // recording calibration attempts. The previous behaviour was
        // dangerous: one accidental press of T silently disabled
        // recording, and the user could flick for 10 minutes before
        // realising nothing was being calibrated.
        private bool _showTrainingMarkers = true;  // overlay visibility, toggled by T
        private ICardinalProfileApplier _cardinalApplier;
        // CLAUDE FIX (round 2, directive #3) — callback that returns
        // the LIVE RawAccel Profile (_accelGUI.Settings.ActiveProfile)
        // from the config form, so FinishTrial can call
        // RawAccelSettingsBridge.LookupActiveLutFactorFromProfile on
        // the actual active settings — NOT the merged profile cache.
        // May be null (e.g. in test builds); the lookup returns 1.0 in
        // that case.
        private Func<object> _activeProfileProvider;
        private int _selectedAxisIndex = -1;     // 0=Right, 1=Down, 2=Left, 3=Up
        private int _selectedDistanceZone = -1;  // 0..N-1 on the selected axis,
                                                 // where N = TrainingGeometry.Current.DistanceZoneCount
        // The EXACT distance in cm that the user clicked. Used for target
        // placement so the target appears exactly where the user clicked,
        // not at the nearest zone center (which can be up to ~1.5 cm away
        // for the long zones). The trial is still RECORDED under the
        // nearest zone (DistanceToZoneIndex) so the existing calibrator
        // keeps working — but visually the target sits on the marker.
        private double _selectedDistanceCm = 0.0;
        // ───────────────────────────────────────────────────────────────────
        // Central training-geometry constants.
        //
        // These three numbers used to live only in this file as private
        // consts. They are now also pushed into TrainingGeometry.Current
        // (see InitializeTrainingMode) so every downstream component
        // (calibrator, merger, profile arrays, arena) reads the SAME
        // values — the trainer is the central authority, no parallel
        // definitions allowed.
        // ───────────────────────────────────────────────────────────────────
        private const double TrainingMaxAxisDegrees = 180.0;
        private Vector3[][] _canonicalTrainingMarkerPositions;
        private double[][] _canonicalTrainingMarkerDistanceCm;
        // FIX (8/16-way): cache the direction-count mode that the
        // canonical-marker arrays were built for, so a mode switch
        // invalidates and rebuilds them.
        private int _canonicalDirectionCountCached = -1;
        private Timer _trainingPulseTimer;
        private float _trainingPulsePhase;

        // ----------------------------------------------------------------------
        // Chained-correction state.
        //
        // The user explained: when point N becomes green, its per-cell factor
        // must be APPLIED during the calibration flick for point N+1, so the
        // signed error measured at point N+1 is the RESIDUAL error (after
        // point N's correction). Otherwise point N+1's factor would
        // compensate for point N's residual too, and the merged curve would
        // double-correct.
        //
        // ----------------------------------------------------------------------
        // HUD overlay for training progress (verified count, current selection,
        // attempt counter for the currently selected cell).
        private Label _trainingHud;

        // Replacement render timer. The existing _renderTimer is `readonly`
        // so we can't reassign it, but we CAN stop it. We then start our own
        // timer that checks Form.ActiveForm before doing anything — this is
        // what lets the user Alt+Tab away without the cursor getting yanked
        // back to the center of the form.
        private Timer _trainingRenderTimer;

        // =========================================================================
        // TEST MODE: F1 auto-sweep. When the user presses F1, the trainer
        // automatically advances through every marker on the Left axis
        // (axis 2), one zone per ~600 ms, so the user can SEE exactly where
        // calibration stops working. This bypasses the 3D click-on-marker
        // hit-test (which fails for markers behind the camera).
        // Press F1 again or Esc to stop the sweep.
        // =========================================================================
        private Timer _sweepTimer;
        private int _sweepCurrentZone;
        private int _sweepAxis = 2; // 0=Right, 1=Down, 2=Left, 3=Up. Default: Left.

        // =========================================================================
        // CALIBRATOR TEST: F2. Synthesizes a "perfect" flick to each zone on
        // the selected axis (or Left axis by default) and records it as a
        // trial, then triggers DirectionalCalibrator.Calibrate. After the
        // sweep, dumps a per-zone factor log so the user can SEE exactly
        // what the calibrator computed for each zone.
        //
        // This bypasses mouse input entirely. We construct a synthetic
        // trial with raw samples whose cumulative projected distance
        // EXACTLY equals the zone's target distance. The calibrator should
        // compute factor = 1.0 for every zone (because the flick was
        // perfect). If any zone returns factor = NaN, 0, or stays at the
        // default 1.0 with confidence = 0, the calibrator has a bug.
        // =========================================================================
        private Timer _calibTestTimer;
        private int _calibTestCurrentZone;
        private int _calibTestAxis = 2;
        private readonly List<string> _calibTestLog = new List<string>();

        // =========================================================================
        // FIXES (added): noise-floor, AB-test, backup, sequential-override state.
        // =========================================================================
        private Label _fixesHud;
        // PATCHED: a separate HUD label that shows the calibrator's
        // per-cell factor and confidence for the currently selected cell.
        // The user requested this so they can SEE whether ASDW is actually
        // calibrating the cell they are aiming at.
        private Label _calibFactorHud;
        // PATCHED: a small canvas that draws the fitted acceleration curve
        // for the currently selected cell. The user requested this so they
        // can see the SHAPE of the curve (not just the factor) for each zone.
        private CurvePanel _calibCurvePanel;
        private bool _isInNoiseFloorEstimation = false;
        private int _noiseFloorFlickCount = 0;
        private const int NoiseFloorTargetFlicks = 10;
        private List<double> _noiseFloorErrors = new List<double>();
        private bool _isInABTestMode = false;
        private List<double> _abTestErrors = new List<double>(); // legacy reset compatibility
        private int _abTestCurrentFlick = 0;
        private CounterbalancedABPlan _abPlan;

        /// <summary>
        /// PATCHED: update the calib-factor HUD. Called from UpdateTrainingHud
        /// so the user sees the current cell's calibrator factor update in
        /// real time as they ASDW into a marker and flick.
        /// </summary>
        private void UpdateCalibFactorHud()
        {
            if (_calibFactorHud == null || _calibFactorHud.IsDisposed) return;
            if (_profile == null || _selectedAxisIndex < 0 || _selectedDistanceZone < 0)
            {
                _calibFactorHud.Text = "CALIB: —";
                try { _calibCurvePanel?.Clear(); } catch { }
                return;
            }
            try
            {
                int directionCount = DesktopSensTrainingState.ResolveDirectionCount(_profile);
                int[] angleZones = DesktopSensTrainingState.AngleZoneIndicesForCount(directionCount);
                if (_selectedAxisIndex >= angleZones.Length) { _calibFactorHud.Text = "CALIB: —"; return; }
                int angleZone = angleZones[_selectedAxisIndex];
                int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, _selectedDistanceZone);

                // Read per-cell factors the calibrator has stored.
                double sensFactor = 1.0, sensConf = 0.0;
                double accelFactor = 1.0, accelConf = 0.0;
                if (_profile.CellSensitivityFactor != null && cellIndex < _profile.CellSensitivityFactor.Length)
                    sensFactor = _profile.CellSensitivityFactor[cellIndex];
                if (_profile.CellSensitivityConfidence != null && cellIndex < _profile.CellSensitivityConfidence.Length)
                    sensConf = _profile.CellSensitivityConfidence[cellIndex];
                if (_profile.CellAccelerationFactor != null && cellIndex < _profile.CellAccelerationFactor.Length)
                    accelFactor = _profile.CellAccelerationFactor[cellIndex];
                if (_profile.CellAccelerationConfidence != null && cellIndex < _profile.CellAccelerationConfidence.Length)
                    accelConf = _profile.CellAccelerationConfidence[cellIndex];

                // Read training-state info too.
                string cellState = "WHITE";
                int successCount = 0, totalCount = 0;
                double lastErr = 0.0;
                if (_profile.CellTrainingStates.TryGetValue(cellIndex, out var st) && st != null)
                {
                    successCount = st.SuccessfulAttemptCount;
                    totalCount = st.TotalAttemptCount;
                    lastErr = st.LastSignedErrorCm;
                    cellState = st.Verified ? "GREEN"
                              : st.FirstRoundComplete ? "YELLOW"
                              : "WHITE";
                }

                string axisName = DesktopSensTrainingState.GetAxisName(_selectedAxisIndex, directionCount).ToUpperInvariant();
                string zoneCm = DesktopSensTrainingState.DistanceZoneCenterCm(_selectedDistanceZone).ToString("F2",
                    System.Globalization.CultureInfo.InvariantCulture);

                _calibFactorHud.Text =
                    "CALIB  " + axisName + " zone " + _selectedDistanceZone + " (" + zoneCm + " cm)\n" +
                    "  state:       " + cellState + "   (attempts " + successCount + "/" + totalCount + ")\n" +
                    "  sens factor: " + sensFactor.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    "   conf " + sensConf.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "\n" +
                    "  accel factor:" + accelFactor.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    "   conf " + accelConf.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "\n" +
                    "  last err:    " + (lastErr >= 0 ? "+" : "") + lastErr.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " cm";

                // Color cue: green if verified, yellow if first-round, red if low confidence.
                _calibFactorHud.ForeColor = cellState == "GREEN" ? Color.LightGreen
                                            : cellState == "YELLOW" ? Color.Khaki
                                            : Color.Salmon;

                // PATCHED: also push the curve data to the curve panel so the
                // user sees the SHAPE of the fitted acceleration curve.
                if (_calibCurvePanel != null && !_calibCurvePanel.IsDisposed)
                {
                    try
                    {
                        AccelCurveMode mode = (AccelCurveMode)
                            (cellIndex < _profile.CellCurveMode.Length
                                ? _profile.CellCurveMode[cellIndex]
                                : (int)AccelCurveMode.NoAccel);
                        double[] parameters = _profile.GetCellCurveParameters(cellIndex);
                        double rmse = cellIndex < _profile.CellCurveRmse.Length
                            ? _profile.CellCurveRmse[cellIndex]
                            : 0.0;
                        int sampleCount = cellIndex < _profile.CellCurveSampleCount.Length
                            ? _profile.CellCurveSampleCount[cellIndex]
                            : 0;
                        string label = axisName + " z" + _selectedDistanceZone;
                        _calibCurvePanel.SetCell(label, mode, parameters, sampleCount, rmse);
                    }
                    catch { _calibCurvePanel.Clear(); }
                }
            }
            catch (Exception ex)
            {
                try { _calibFactorHud.Text = "CALIB err: " + ex.Message; } catch { }
            }
        }
        private int _abPairIndex = -1;
        private int _abPairAxis;
        private int _abPairDistanceZone;
        private double _abPairTargetCm;
        private bool _isABTestTrialInProgress;

        /// <summary>
        /// Called from the existing constructor to wire up training mode.
        /// The applier is optional — if null, the trainer still runs and
        /// marks cells green, but the final merge-and-apply step is a no-op
        /// (the user can wire it later from their existing ApplyDirectionalProfile
        /// method by implementing ICardinalProfileApplier).
        /// </summary>
        private void InitializeTrainingMode(ICardinalProfileApplier applier)
        {
            InitializeTrainingMode(applier, null);
        }

        /// <summary>
        /// CLAUDE FIX (round 2, directive #3) — overload that also
        /// accepts a callback returning the LIVE RawAccel Profile
        /// (typically _accelGUI.Settings.ActiveProfile). FinishTrial
        /// calls this callback to look up the active LUT factor at
        /// the trial's speed using
        /// RawAccelSettingsBridge.LookupActiveLutFactorFromProfile,
        /// which reads argsX/argsY directly via reflection.
        /// </summary>
        private void InitializeTrainingMode(ICardinalProfileApplier applier,
            Func<object> activeProfileProvider)
        {
            _cardinalApplier = applier;
            _activeProfileProvider = activeProfileProvider;
            if (_profile.CellTrainingStates == null)
                _profile.CellTrainingStates = new Dictionary<int, TrainingCellState>();
            if (_profile.Trials == null)
                _profile.Trials = new List<DesktopSensTrial>();
            // FIX 1 (from AI review): every trainer opening — Sens Finder
            // OR Training — starts a fresh session id. The previous code
            // only bumped the session id in training mode, which meant
            // that running Sens Finder immediately after a training
            // session reused the same CurrentSessionId. BuildSessionBiases
            // then saw a mix of Sens Finder + Training trials under one
            // session and computed a single shared session-bias gain from
            // the mixture; ApplySessionBiasToSession rewrote Training's
            // per-cell histories with that mixed bias; SessionRepeatability-
            // Failed fired on the Training cells because two "sessions"
            // with different biases shared one id; and state.Verified
            // stayed false forever, even after 6 successful attempts.
            // Starting a fresh session for every opening breaks that
            // cross-contamination.
            _profile.CurrentSessionId = Guid.NewGuid().ToString("N");
            _profile.SessionStartedAtUtc = DateTime.UtcNow;

            // ───────────────────────────────────────────────────────────────
            // Push the trainer's geometry constants into the global
            // TrainingGeometry so every downstream component (calibrator,
            // merger, profile arrays, arena) reads the SAME values.
            //
            // Before this call, TrainingGeometry.Current still has its
            // default values (10°/180°/89°/MaxDistanceCm=0). After this
            // call, all callers see the trainer's actual step setting
            // and the per-session maxDistanceCm derived from the profile's
            // DPI/sensitivity. The calibrator's EnsureArray helper will
            // resize every per-cell array on the next Calibrate() call.
            //
            // This runs in BOTH modes (cardinal trainer AND Sens Finder)
            // because the calibrator needs TrainingGeometry to be
            // configured even in Sens Finder mode (the calibrator sizes
            // the per-cell arrays using DistanceZoneCount, which comes
            // from TrainingGeometry).
            // ───────────────────────────────────────────────────────────────
            double markerStepDegrees = _profile.TrainingMarkerStepDegrees;
            if (double.IsNaN(markerStepDegrees) || double.IsInfinity(markerStepDegrees) ||
                markerStepDegrees < 1.0 || markerStepDegrees > 45.0)
                markerStepDegrees = 10.0;
            _profile.TrainingMarkerStepDegrees = markerStepDegrees;

            // Use the same central geometry formula as the runtime. The old
            // floor(180 / step) count was one too large for exact divisors
            // (10° -> 18 instead of 17), causing every startup to look like
            // a geometry migration and clear verification state.
            //
            // The expected array length must include the zone-0 sub-marker
            // expansion (Zone0SubMarkerCount - 1 extra markers). Without
            // this correction, every startup would falsely detect a legacy
            // stride and clear all verification state, because the actual
            // array length (base + sub-markers) would never match the base
            // count alone.
            int baseZoneCountBeforeConfigure =
                TrainingGeometry.CalculateHorizontalMarkerCount(
                    markerStepDegrees, TrainingMaxAxisDegrees);
            int subMarkerExtra =
                Math.Max(0, TrainingGeometry.Current.EffectiveZone0SubMarkerCount - 1);
            int configuredZoneCountBeforeConfigure =
                baseZoneCountBeforeConfigure + subMarkerExtra;
            int expectedCellCountBeforeConfigure =
                DesktopSensConstants.AngularZoneCount * configuredZoneCountBeforeConfigure;
            bool legacyDistanceStrideDetected =
                (_profile.CellSensitivityFactor != null &&
                 _profile.CellSensitivityFactor.Length != expectedCellCountBeforeConfigure) ||
                (_profile.CellAccelerationFactor != null &&
                 _profile.CellAccelerationFactor.Length != expectedCellCountBeforeConfigure) ||
                (_profile.CellRotationOffsetDegrees != null &&
                 _profile.CellRotationOffsetDegrees.Length != expectedCellCountBeforeConfigure) ||
                (_profile.CellCurveMode != null &&
                 _profile.CellCurveMode.Length != expectedCellCountBeforeConfigure);

            // Also detect a prior run that resized the arrays but left the
            // serialized cell-state dictionary using the old stride.
            if (_profile.CellTrainingStates != null)
            {
                foreach (var entry in _profile.CellTrainingStates)
                {
                    if (entry.Value == null) continue;
                    try
                    {
                        int expectedKey = DesktopSensProfile.GetCellIndex(
                            entry.Value.AngleZoneIndex, entry.Value.DistanceZoneIndex);
                        if (entry.Key != expectedKey)
                        {
                            legacyDistanceStrideDetected = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        DesktopSensLog.Warn("Could not validate legacy training-cell keys during geometry migration", ex);
                        legacyDistanceStrideDetected = true;
                        break;
                    }
                }
            }

            bool trainingGeometryChanged = TrainingGeometry.HasGeometryChanged(
                _profile.AppliedTrainingMarkerStepDegrees,
                markerStepDegrees,
                _profile.AppliedTrainingMaxDistanceCm,
                _maxDistanceCm,
                legacyDistanceStrideDetected);

            TrainingGeometry.Configure(
                markerStepDegrees,
                TrainingMaxAxisDegrees,
                MaxPitchDegrees,
                _maxDistanceCm);
            TrainingGeometry.SetMaxDistanceCm(_maxDistanceCm);

            int configuredZoneCount = TrainingGeometry.Current.DistanceZoneCount;
            if (_distanceZoneCounts == null || _distanceZoneCounts.Length != configuredZoneCount)
                _distanceZoneCounts = new int[configuredZoneCount];

            bool geometryMigrationSucceeded = !trainingGeometryChanged;
            if (trainingGeometryChanged)
            {
                // Zone indices and array strides are geometry-dependent.
                // Old profiles may still contain the legacy 46-zone stride
                // even though the marker step is already 10 degrees. Re-keying
                // old states under the new geometry would assign green cells
                // to different physical targets, so clear verification state,
                // re-bin retained raw trials from their physical target distance,
                // and rebuild all factors from those raw observations.
                _profile.CellTrainingStates.Clear();
                if (_profile.Trials != null)
                {
                    foreach (var oldTrial in _profile.Trials)
                    {
                        if (oldTrial == null) continue;
                        oldTrial.DistanceZoneIndex = TrainingGeometry.Current.DistanceToMarkerIndex(
                            oldTrial.TargetDistanceCm, _maxDistanceCm);
                        oldTrial.HasChainAdjustedSignedError = false;
                    }
                }
                _profile.AllCardinalCellsVerified = false;
                _profile.LastMergeAppliedTimestampUtc = null;
                try
                {
                    DesktopSensCalibrator.Calibrate(_profile);
                    geometryMigrationSucceeded = true;
                }
                catch (Exception ex)
                {
                    geometryMigrationSucceeded = false;
                    DesktopSensLog.Error("Geometry migration recalibration failed; persisted geometry marker was retained for retry", ex);
                }
            }

            // Do not mark a failed migration as complete. Keeping the old
            // persisted geometry forces the next startup to retry instead of
            // silently treating stale factors as migrated.
            if (geometryMigrationSucceeded)
            {
                _profile.AppliedTrainingMarkerStepDegrees = markerStepDegrees;
                _profile.AppliedTrainingMaxDistanceCm = _maxDistanceCm;
                _profile.ModifiedTimestamp = DateTime.UtcNow;
            }
            if (trainingGeometryChanged && geometryMigrationSucceeded)
                OnCellVerifiedPersistProfile();

            // ── Focus-aware render timer (replaces the existing _renderTimer) ──
            // The existing _renderTimer is readonly so we can't reassign it,
            // but we CAN stop it. We start our own timer with the same 16ms
            // cadence that:
            //   • Calls RecenterCursorIfNeeded() + UpdateArena3D() when the
            //     form is the active window (same behavior as before).
            //   • Skips BOTH when the form is not active — this is what lets
            //     the user Alt+Tab away. Without this, the existing timer
            //     keeps running and yanks the cursor back to center on every
            //     tick, making it impossible to take screenshots or interact
            //     with the desktop.
            //
            // The existing Deactivate/Activated events fire unreliably for
            // borderless maximized forms, so we don't rely on them. The
            // Form.ActiveForm check is polled every 16ms and is reliable.
            //
            // This runs in BOTH modes — Sens Finder also needs the 16ms
            // render tick to redraw the arena after the user flicks.
            try { _renderTimer?.Stop(); } catch (Exception ex) { DesktopSensLog.Warn("Could not stop the legacy render timer", ex); }
            _trainingRenderTimer = new Timer { Interval = 16 };
            _trainingRenderTimer.Tick += (s, e) =>
            {
                // If our form is not the active window (user Alt+Tabbed
                // away, switched focus, etc.), skip everything and ensure
                // the OS cursor is visible.
                if (Form.ActiveForm != this)
                {
                    // The OS cursor must be visible while the user is outside
                    // the trainer (for example after Alt+Tab).
                    RestoreTrainerCursor();
                    return;
                }

                // Keep the Windows pointer hidden while it is over the trainer
                // surface, including marker-selection mode. Marker selection is
                // driven by the in-app center crosshair, so the OS pointer is not
                // needed and must not be drawn over the arena.
                bool captureMouseForFlick =
                    _state == TrialState.Cooldown ||
                    _state == TrialState.Flicking ||
                    _state == TrialState.ASDWAdjusting;
                bool pointerInsideTrainer =
                    ClientRectangle.Contains(PointToClient(Cursor.Position));

                if (captureMouseForFlick)
                {
                    HideTrainerCursor();
                    RecenterCursorIfNeeded();
                }
                else if (pointerInsideTrainer)
                {
                    HideTrainerCursor();
                }
                else
                {
                    RestoreTrainerCursor();
                }
                UpdateArena3D();
            };
            _trainingRenderTimer.Start();

            // ───────────────────────────────────────────────────────────────
            // CARDINAL-TRAINER-ONLY SETUP.
            //
            // The user's directive: Sens Finder must have its OWN
            // environment, separate from the cardinal trainer. The
            // cardinal-trainer-specific UI (training HUD with verified-
            // cell count, pulse timer for the next-eligible marker,
            // click-on-marker hit-testing) must NOT be wired up in
            // Sens Finder mode. Sens Finder just collects micro-zone
            // trials via its own auto-advance path
            // (PlaceSensitivityFinderTarget) and never touches the
            // per-cell training state.
            //
            // We therefore skip the entire block below when in Sens
            // Finder mode. The geometry push and render timer above
            // still run because the calibrator and the arena renderer
            // need them.
            // ───────────────────────────────────────────────────────────────
            if (_sensitivityFinderMode)
            {
                // Make sure the arena is not showing training markers
                // (the cardinal trainer overlay) in Sens Finder mode.
                // _isTrainingMode was set to false in the constructor
                // when sens finder mode is active, but we double-check
                // here so a stray toggle can't re-enable the overlay
                // during a sens finder session.
                if (_arena3D != null && !_arena3D.IsDisposed)
                {
                    _arena3D.ShowTrainingMarkers = false;
                }
                return;
            }

            _trainingPulseTimer = new Timer { Interval = 50 };
            _trainingPulseTimer.Tick += (s, e) =>
            {
                _trainingPulsePhase = (_trainingPulsePhase + 0.04f) % 1.0f;
                if (_arena3D != null && !_arena3D.IsDisposed)
                {
                    _arena3D.PulsePhase = _trainingPulsePhase;
                    _arena3D.Invalidate();
                }
            };
            _trainingPulseTimer.Start();

            _trainingHud = new Label
            {
                AutoSize = true,
                Location = new Point(20, 110),
                Padding = new Padding(8),
                BackColor = Color.FromArgb(220, 12, 16, 22),
                ForeColor = Color.Gainsboro,
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                Text = "TRAINING: idle"
            };
            ForwardMouseInput(_trainingHud);
            Controls.Add(_trainingHud);
            _trainingHud.BringToFront();

            // PATCHED: a separate HUD label (positioned right above the
            // training HUD, at the top-right corner of the form) that shows
            // the calibrator's per-cell factor and confidence for the
            // currently selected cell. The user requested this so they can
            // SEE whether ASDW is actually calibrating the cell they are
            // aiming at — without having to dig through diagnostic logs.
            _calibFactorHud = new Label
            {
                AutoSize = true,
                // Anchor to the top-right corner so it stays visible even
                // when the user resizes the window.
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(ClientSize.Width - 360, 20),
                Padding = new Padding(8),
                BackColor = Color.FromArgb(230, 20, 10, 30),
                ForeColor = Color.LightGreen,
                Font = new Font("Consolas", 9f, FontStyle.Bold),
                Text = "CALIB: —"
            };
            try { ForwardMouseInput(_calibFactorHud); } catch { }
            Controls.Add(_calibFactorHud);
            _calibFactorHud.BringToFront();
            // PATCHED: also create the curve panel. It sits just below the
            // calib-factor HUD in the top-right corner.
            _calibCurvePanel = new CurvePanel
            {
                Location = new Point(ClientSize.Width - 340, 130),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            try { ForwardMouseInput(_calibCurvePanel); } catch { }
            Controls.Add(_calibCurvePanel);
            _calibCurvePanel.BringToFront();
            Resize += (s, ev) =>
            {
                try
                {
                    if (_calibFactorHud != null && !_calibFactorHud.IsDisposed)
                        _calibFactorHud.Location = new Point(ClientSize.Width - 360, 20);
                    if (_calibCurvePanel != null && !_calibCurvePanel.IsDisposed)
                        _calibCurvePanel.Location = new Point(ClientSize.Width - 340, 130);
                }
                catch { }
            };

            // ── Click-on-marker hit-testing ───────────────────────────────
            // When the user clicks on a training marker in the 3D arena,
            // we want to select that cell and start calibration from it.
            // We hook the form's MouseDown event — this fires AFTER
            // base.OnMouseDown (which raises the event) but BEFORE the
            // existing OnMouseDown state-machine logic runs. If we change
            // the state to Cooldown via PlaceCardinalTrainingTarget, the
            // existing state machine will see the new state and skip its
            // own click handling.
            _arena3D.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if (!_isTrainingMode) return;
                if (_arena3D == null || _arena3D.IsDisposed) return;
                // Only intercept clicks in states where the user would
                // normally be picking a target (or starting over). Don't
                // intercept during Flicking / ASDWAdjusting / Cooldown —
                // those have their own click meanings.
                if (_state != TrialState.SetStartPoint &&
                    _state != TrialState.SetTargetPoint &&
                    _state != TrialState.TrialComplete)
                    return;

                // Selection is driven by the arena crosshair, not the Windows
                // cursor position. The render loop continuously updates these
                // values from the marker currently under the center crosshair.
                // A left click confirms that crosshair selection and starts
                // calibration for that exact marker.
                int hoveredAxis = _arena3D.HoveredMarkerAxisIndex;
                double hoveredDistanceCm = _arena3D.HoveredMarkerDistanceCm;
                if (hoveredAxis < 0 || hoveredDistanceCm <= 0.0) return;

                _selectedAxisIndex = hoveredAxis;
                double maxDistanceCm = _maxDistanceCm;
                // Save the exact canonical marker distance under the crosshair
                // so target placement reuses the same point instead of snapping
                // to a different zone center.
                _selectedDistanceCm = hoveredDistanceCm;
                // Map this marker's distance to its zone index. In the new
                // training-derived model, marker index == zone index, so
                // TrainingGeometry.Current.DistanceToMarkerIndex returns the
                // canonical index that matches the marker the user picked.
                // The hardcoded 46 ceiling is gone — the count is whatever
                // Training says it is.
                _selectedDistanceZone = TrainingGeometry.Current.DistanceToMarkerIndex(
                    hoveredDistanceCm, maxDistanceCm);
                if (_selectedDistanceZone < 0)
                    _selectedDistanceZone = 0;
                int directionCountForZoneCount = DesktopSensTrainingState.ResolveDirectionCount(_profile);
                int distanceZoneCount = TrainingGeometry.Current.MultiDirectionZoneCount(
                    _selectedAxisIndex, directionCountForZoneCount);
                if (_selectedDistanceZone >= distanceZoneCount)
                    _selectedDistanceZone = distanceZoneCount - 1;

                // FIX (8/16-way): resolve the angle zone from the active
                // direction-count mode so clicking on an 8-way or 16-way
                // marker selects the correct cell.
                int[] angleZones0 = DesktopSensTrainingState.AngleZoneIndicesForCount(directionCountForZoneCount);
                if (_selectedAxisIndex >= angleZones0.Length) return;
                int angleZone = angleZones0[_selectedAxisIndex];
                // FIX #14: use soft rule (yellow counts as "available") when the
                // user has toggled sequential-override on (O key).
                // FIX (8/16-way): use the direction-count-aware overload so
                // diagonal axes' reach is bounded by min(horizontal, vertical).
                bool selectedCellAvailable = (_profile != null && _profile.AllowSequentialOverride)
                    ? DesktopSensTrainingState.IsCellAvailableForTrainingSoft(
                        _profile.CellTrainingStates, angleZone, _selectedDistanceZone, true)
                    : DesktopSensTrainingState.IsCellAvailableForTrainingForCount(
                        _profile.CellTrainingStates, angleZone, _selectedDistanceZone, directionCountForZoneCount);

                // At exactly 180°, Right and Left are physically the same point.
                // If the overlapped direction is locked but the other direction's
                // endpoint is available, route this single shared point to that
                // available cell instead of making the user click an invisible
                // offset marker.
                if (!selectedCellAvailable &&
                    Math.Abs(hoveredDistanceCm - maxDistanceCm) < 0.001 &&
                    (_selectedAxisIndex == 0 || _selectedAxisIndex == 2))
                {
                    int otherAxis = _selectedAxisIndex == 0 ? 2 : 0;
                    int otherAngleZone = angleZones0[otherAxis];
                    if ((_profile != null && _profile.AllowSequentialOverride
                            ? DesktopSensTrainingState.IsCellAvailableForTrainingSoft(
                                _profile.CellTrainingStates, otherAngleZone, _selectedDistanceZone, true)
                            : DesktopSensTrainingState.IsCellAvailableForTrainingForCount(
                                _profile.CellTrainingStates, otherAngleZone, _selectedDistanceZone, directionCountForZoneCount)))
                    {
                        _selectedAxisIndex = otherAxis;
                        angleZone = otherAngleZone;
                        selectedCellAvailable = true;
                    }
                }

                // Publish the selected axis/cell even when sequential
                // training rules currently lock it. Previously this returned
                // silently before refreshing the arena/HUD, so a successful
                // hit-test looked exactly like a click that selected nothing.
                UpdateTrainingArenaProperties();
                UpdateTrainingHud();
                if (!selectedCellAvailable)
                    return;

                // Place the target on the selected cell. This sets state
                // to Cooldown, so the existing OnMouseDown state machine
                // will skip its own click handling.
                PlaceCardinalTrainingTarget();

                UpdateTrainingArenaProperties();
                UpdateTrainingHud();
            };

            FormClosed += (s, e) =>
            {
                _trainingPulseTimer?.Stop();
                _trainingPulseTimer?.Dispose();
                _trainingRenderTimer?.Stop();
                _trainingRenderTimer?.Dispose();
            };
        }

        /// <summary>
        /// Handles training-mode-specific keys. Called from OnKeyDown BEFORE
        /// the existing ASDWAdjusting branch so the user can pick an axis
        /// at any time. Returns true if the key was consumed (so the caller
        /// should skip the existing key handling).
        ///
        /// Keys:
        ///   1 → pick Right axis, select next eligible cell
        ///   2 → pick Down  axis, select next eligible cell
        ///   3 → pick Left  axis, select next eligible cell
        ///   4 → pick Up    axis, select next eligible cell
        ///   T → toggle training mode (visual overlay)
        ///   M → manual merge-and-apply (no-op if not 100% green)
        ///   R → RESET ALL TRAINING (clears every cell's yellow/green state,
        ///       clears the per-cell factor arrays, and re-runs the
        ///       calibrator so the profile is back to "no cells verified".
        ///       Asks for confirmation before doing anything destructive.)
        ///
        /// In Sens Finder mode this method returns false immediately —
        /// the cardinal-trainer keys (1/2/3/4/T/M/R) are NOT processed
        /// because Sens Finder is a separate environment (the user's
        /// directive: 'mohite traine kharab nashe' — the trainer
        /// environment must not be disturbed). Sens Finder uses the
        /// same manual-target-placement flow as the cardinal trainer
        /// and doesn't need these keys.
        /// </summary>
        private bool HandleTrainingModeKey(KeyEventArgs e)
        {
            // Sens Finder mode = its own environment. Cardinal-trainer
            // keys must NOT do anything here.
            if (_sensitivityFinderMode) return false;

            if (e.KeyCode == Keys.T && !e.Shift && !e.Control && !e.Alt)
            {
                // FIX 4 (from AI review): T toggles ONLY the on-screen
                // marker overlay. _isTrainingMode is NOT touched — it
                // stays true so calibration attempts keep being recorded.
                // The previous behaviour flipped _isTrainingMode, which
                // (a) made RecordTrainingAttempt early-return without
                // recording the trial, (b) made the marker-click
                // handler early-return so no cell could be selected,
                // and (c) cleared ShowTrainingMarkers — all silently,
                // with no toast or HUD change. The user would then
                // flick for several minutes before realising nothing
                // was being calibrated.
                _showTrainingMarkers = !_showTrainingMarkers;
                if (_arena3D != null && !_arena3D.IsDisposed)
                {
                    _arena3D.ShowTrainingMarkers = _showTrainingMarkers;
                    _arena3D.Invalidate();
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
                return true;
            }
            if (e.KeyCode == Keys.M && !e.Shift && !e.Control && !e.Alt)
            {
                TryMergeAndApply();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return true;
            }
            if (e.KeyCode == Keys.R && !e.Shift && !e.Control && !e.Alt)
            {
                ResetAllTraining();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return true;
            }

            // FIXES keys: B/N/V/O/Shift+M. These are NEW; the existing
            // M (manual merge) is preserved when Shift is not held.
            if (e.KeyCode == Keys.B && !e.Control && !e.Alt && !e.Shift)
            {
                RollbackProfileFromBackup();
                e.Handled = true; e.SuppressKeyPress = true; return true;
            }
            if (e.KeyCode == Keys.N && !e.Control && !e.Alt && !e.Shift)
            {
                ToggleNoiseFloorEstimation();
                e.Handled = true; e.SuppressKeyPress = true; return true;
            }
            if (e.KeyCode == Keys.V && !e.Control && !e.Alt && !e.Shift)
            {
                ToggleABTestMode();
                e.Handled = true; e.SuppressKeyPress = true; return true;
            }
            if (e.KeyCode == Keys.O && !e.Control && !e.Alt && !e.Shift)
            {
                if (_profile != null)
                {
                    _profile.AllowSequentialOverride = !_profile.AllowSequentialOverride;
                    UpdateFixesHud();
                }
                e.Handled = true; e.SuppressKeyPress = true; return true;
            }
            if (e.KeyCode == Keys.M && e.Shift && !e.Control && !e.Alt)
            {
                if (_profile != null)
                {
                    _profile.MicroBoostEnabled = !_profile.MicroBoostEnabled;
                    UpdateFixesHud();
                }
                e.Handled = true; e.SuppressKeyPress = true; return true;
            }

            // PATCHED: +/- keys to advance through markers on the currently
            // selected axis WITHOUT needing to click on them in 3D space.
            //
            // Root cause of the persistent "90° cap" complaint: even after
            // all clamps were removed, markers past 90° from the calibration
            // forward direction are BEHIND the 3D camera. ProjectToScreen()
            // returns null for them (clip W <= 0), so the click-on-marker
            // hit-test can never reach them. The user sees only markers
            // within ±90° of the camera and assumes there's a 90° cap on
            // calibration — but actually the cap is on VISIBILITY, not on
            // calibration.
            //
            // The fix: let the user advance through ALL markers on the
            // selected axis using +/- keys, completely bypassing the 3D
            // visibility check. The user presses 1 (Right), then presses +
            // repeatedly to advance from zone 0 (3.3°) through zone 18
            // (170°) — every marker on the Right axis, including those
            // that are physically behind the 3D camera.
            if ((e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add ||
                 e.KeyCode == Keys.D8) && !e.Control && !e.Alt && !e.Shift)
            {
                if (_selectedAxisIndex >= 0)
                {
                    int dirCount = DesktopSensTrainingState.ResolveDirectionCount(_profile);
                    int maxZones = TrainingGeometry.Current.MultiDirectionZoneCount(_selectedAxisIndex, dirCount);
                    if (maxZones > 0)
                    {
                        _selectedDistanceZone = (_selectedDistanceZone + 1) % maxZones;
                        _selectedDistanceCm = DesktopSensTrainingState.DistanceZoneCenterCm(_selectedDistanceZone);
                        PlaceCardinalTrainingTarget();
                        UpdateTrainingArenaProperties();
                        UpdateTrainingHud();
                    }
                }
                e.Handled = true; e.SuppressKeyPress = true; return true;
            }
            if ((e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract ||
                 e.KeyCode == Keys.D9) && !e.Control && !e.Alt && !e.Shift)
            {
                if (_selectedAxisIndex >= 0)
                {
                    int dirCount = DesktopSensTrainingState.ResolveDirectionCount(_profile);
                    int maxZones = TrainingGeometry.Current.MultiDirectionZoneCount(_selectedAxisIndex, dirCount);
                    if (maxZones > 0)
                    {
                        _selectedDistanceZone = (_selectedDistanceZone - 1 + maxZones) % maxZones;
                        _selectedDistanceCm = DesktopSensTrainingState.DistanceZoneCenterCm(_selectedDistanceZone);
                        PlaceCardinalTrainingTarget();
                        UpdateTrainingArenaProperties();
                        UpdateTrainingHud();
                    }
                }
                e.Handled = true; e.SuppressKeyPress = true; return true;
            }

            // ====================================================================
            // TEST: F1 auto-sweep through markers on the Left axis.
            //
            // Press F1: starts a 600 ms timer that advances through every
            // zone on the Left axis (zone 0 → zone max-1). At each step
            // it calls PlaceCardinalTrainingTarget so the user can SEE
            // exactly where calibration stops working (e.g. where the
            // marker disappears behind the 3D camera, or where the trial
            // is rejected, or where the cell never turns yellow).
            //
            // Press F1 again or Esc to stop the sweep.
            // ====================================================================
            if (e.KeyCode == Keys.F1 && !e.Control && !e.Alt && !e.Shift)
            {
                if (_sweepTimer != null && _sweepTimer.Enabled)
                {
                    StopSweep();
                }
                else
                {
                    StartSweep(2); // 2 = Left axis
                }
                e.Handled = true; e.SuppressKeyPress = true; return true;
            }
            // Shift+F1: sweep through Right axis instead.
            if (e.KeyCode == Keys.F1 && e.Shift && !e.Control && !e.Alt)
            {
                if (_sweepTimer != null && _sweepTimer.Enabled)
                {
                    StopSweep();
                }
                else
                {
                    StartSweep(0); // 0 = Right axis
                }
                e.Handled = true; e.SuppressKeyPress = true; return true;
            }
            if (e.KeyCode == Keys.Escape && _sweepTimer != null && _sweepTimer.Enabled)
            {
                StopSweep();
                e.Handled = true; e.SuppressKeyPress = true; return true;
            }

            // ====================================================================
            // CALIBRATOR TEST: F2.
            // Synthesizes a perfect flick to each zone of the Left axis
            // (or Right axis with Shift+F2) and dumps the calibrator's
            // computed factor for every zone. This bypasses the mouse
            // entirely so we can test the calibrator in isolation.
            // ====================================================================
            if (e.KeyCode == Keys.F2 && !e.Control && !e.Alt && !e.Shift)
            {
                StartCalibratorTest(2); // Left axis
                e.Handled = true; e.SuppressKeyPress = true; return true;
            }
            if (e.KeyCode == Keys.F2 && e.Shift && !e.Control && !e.Alt)
            {
                StartCalibratorTest(0); // Right axis
                e.Handled = true; e.SuppressKeyPress = true; return true;
            }
            // FIX (AI review root-cause #5): Force-merge with U key.
            // This bypasses the precision gate entirely and applies
            // whatever factors the calibrator has computed so far —
            // even if no cell is green or yellow yet. This is for
            // advanced users who know what they're doing and want to
            // apply a partial calibration without waiting for the
            // precision gate to pass. The factors are always
            // confidence-weighted, so low-confidence cells contribute
            // less — the driver never gets a wild factor.
            if (e.KeyCode == Keys.U && !e.Control && !e.Alt && !e.Shift)
            {
                DesktopSensLog.Warn("FORCE-MERGE (U key): bypassing precision gate, applying current factors.");
                // Force-merge: bypass IsReady / IsPartiallyReady and
                // apply whatever factors the calibrator has computed.
                // This is for advanced users who want to apply a partial
                // calibration without waiting for the precision gate.
                if (_cardinalApplier == null)
                {
                    DesktopSensToast.Show(this,
                        "No RawAccel apply adapter is connected. Force-merge is unavailable.");
                }
                else if (_profile.Trials == null || _profile.Trials.Count == 0)
                {
                    DesktopSensToast.Show(this,
                        "No trials collected yet. Do at least one flick before force-merging.");
                }
                else
                {
                    // Re-run the calibrator to make sure the profile has
                    // the latest factors from the current trial set.
                    try
                    {
                        DesktopSensCalibrator.Calibrate(_profile);
                        }
                    catch (Exception ex)
                    {
                        DesktopSensLog.Error("Force-merge: calibrator failed", ex);
                    }
                    // Build and apply directly, bypassing the gates.
                    MergedCardinalProfile merged = DesktopSensProfileMerger.BuildMergedProfile(_profile);
                    if (merged != null && merged.PassedSanityCheck)
                    {
                        bool applied = _cardinalApplier.Apply(merged, _profile);
                        if (applied)
                        {
                            OnCellVerifiedPersistProfile();
                            System.Media.SystemSounds.Asterisk.Play();
                            int forceMergeDirectionCount = DesktopSensTrainingState.ResolveDirectionCount(_profile);
                            int verified = DesktopSensTrainingState.CountVerifiedCellsForCount(_profile.CellTrainingStates, forceMergeDirectionCount);
                            int attempted = DesktopSensTrainingState.CountAttemptedCellsForCount(_profile.CellTrainingStates, forceMergeDirectionCount);
                            int total = DesktopSensTrainingState.TotalCardinalCellsForCount(forceMergeDirectionCount);
                            MessageBox.Show(this,
                                "FORCE-MERGE applied to RawAccel.\n" +
                                "Verified: " + verified + "/" + total + " | Attempted: " + attempted + "/" + total + "\n" +
                                "R=" + _profile.RightSensitivityFactor.ToString("0.000") +
                                " D=" + _profile.DownSensitivityFactor.ToString("0.000") +
                                " L=" + _profile.LeftSensitivityFactor.ToString("0.000") +
                                " U=" + _profile.UpSensitivityFactor.ToString("0.000"),
                                "Force-Merge Applied",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            MessageBox.Show(this,
                                "Force-merge: the RawAccel adapter rejected the settings.",
                                "Force-Merge Failed",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }
                    else
                    {
                        string errors = merged != null && merged.SanityErrors != null && merged.SanityErrors.Count > 0
                            ? string.Join("\n", merged.SanityErrors)
                            : "Unknown sanity-check failure.";
                        MessageBox.Show(this,
                            "Force-merge failed sanity check:\n" + errors,
                            "Force-Merge Failed",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    UpdateTrainingHud();
                }
                e.Handled = true; e.SuppressKeyPress = true; return true;
            }

            int? axisIndex = null;
            // FIX (8/16-way): hotkeys per mode. The mapping is intentionally
            // restricted to keys that do NOT collide with the in-flick
            // ASDW-adjusting hotkeys (A/S/D/W used for fine-tuning after
            // the flick lands), so the user can train any axis without
            // accidentally entering adjustment mode.
            //
            //   4-way:  1=Right  2=Down  3=Left  4=Up
            //
            //   8-way:  1=Right           2=DownRight  3=Down  4=DownLeft
            //           5=Left            6=UpLeft     7=Up    8=UpRight
            //
            //   16-way: 1=Right   2=Right/DownRight      3=DownRight
            //           4=DownRight/Down  5=Down  6=Down/DownLeft
            //           7=DownLeft  8=DownLeft/Left      9=Left  0=Left/UpLeft
            //           Q=UpLeft    E=UpLeft/Up          F=Up    G=Up/UpRight
            //           H=UpRight   J=UpRight/Right
            //
            // Note: 16-way skips W (reserved for ASDW), R (reset), A/S/D
            // (ASDW-adjusting), and Z/X/C/V/K/L/I/P/U/Y/T/M/N/O (reserved
            // for other trainer functions). It uses 1-9, 0, then Q, E, F,
            // G, H, J — exactly 16 keys for 16 axes, in the same angular
            // order as SixteenWayAngleZoneIndices.
            int directionCount = DesktopSensTrainingState.ResolveDirectionCount(_profile);
            if (directionCount == 8)
            {
                if (e.KeyCode == Keys.D1 || e.KeyCode == Keys.NumPad1) axisIndex = 0;
                else if (e.KeyCode == Keys.D2 || e.KeyCode == Keys.NumPad2) axisIndex = 1;
                else if (e.KeyCode == Keys.D3 || e.KeyCode == Keys.NumPad3) axisIndex = 2;
                else if (e.KeyCode == Keys.D4 || e.KeyCode == Keys.NumPad4) axisIndex = 3;
                else if (e.KeyCode == Keys.D5 || e.KeyCode == Keys.NumPad5) axisIndex = 4;
                else if (e.KeyCode == Keys.D6 || e.KeyCode == Keys.NumPad6) axisIndex = 5;
                else if (e.KeyCode == Keys.D7 || e.KeyCode == Keys.NumPad7) axisIndex = 6;
                else if (e.KeyCode == Keys.D8 || e.KeyCode == Keys.NumPad8) axisIndex = 7;
            }
            else if (directionCount == 16)
            {
                if (e.KeyCode == Keys.D1 || e.KeyCode == Keys.NumPad1) axisIndex = 0;
                else if (e.KeyCode == Keys.D2 || e.KeyCode == Keys.NumPad2) axisIndex = 1;
                else if (e.KeyCode == Keys.D3 || e.KeyCode == Keys.NumPad3) axisIndex = 2;
                else if (e.KeyCode == Keys.D4 || e.KeyCode == Keys.NumPad4) axisIndex = 3;
                else if (e.KeyCode == Keys.D5 || e.KeyCode == Keys.NumPad5) axisIndex = 4;
                else if (e.KeyCode == Keys.D6 || e.KeyCode == Keys.NumPad6) axisIndex = 5;
                else if (e.KeyCode == Keys.D7 || e.KeyCode == Keys.NumPad7) axisIndex = 6;
                else if (e.KeyCode == Keys.D8 || e.KeyCode == Keys.NumPad8) axisIndex = 7;
                else if (e.KeyCode == Keys.D9 || e.KeyCode == Keys.NumPad9) axisIndex = 8;
                else if (e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0) axisIndex = 9;
                else if (e.KeyCode == Keys.Q) axisIndex = 10;
                else if (e.KeyCode == Keys.E) axisIndex = 11;
                else if (e.KeyCode == Keys.F) axisIndex = 12;
                else if (e.KeyCode == Keys.G) axisIndex = 13;
                else if (e.KeyCode == Keys.H) axisIndex = 14;
                else if (e.KeyCode == Keys.J) axisIndex = 15;
            }
            else
            {
                if (e.KeyCode == Keys.D1 || e.KeyCode == Keys.NumPad1) axisIndex = 0;
                else if (e.KeyCode == Keys.D2 || e.KeyCode == Keys.NumPad2) axisIndex = 1;
                else if (e.KeyCode == Keys.D3 || e.KeyCode == Keys.NumPad3) axisIndex = 2;
                else if (e.KeyCode == Keys.D4 || e.KeyCode == Keys.NumPad4) axisIndex = 3;
            }

            if (!axisIndex.HasValue) return false;
            // Safety: clamp axisIndex to the active mode's range.
            int maxAxes = DesktopSensTrainingState.AngleZoneIndicesForCount(directionCount).Length;
            if (axisIndex.Value < 0 || axisIndex.Value >= maxAxes) return false;

            // Axis hotkeys select a direction only. They must not auto-place
            // a distance target: that immediately enters Cooldown, hides/captures
            // the mouse and prevents manual selection of the intended marker.
            if (_state != TrialState.SetStartPoint &&
                _state != TrialState.SetTargetPoint &&
                _state != TrialState.TrialComplete)
                return false;

            _selectedAxisIndex = axisIndex.Value;
            _selectedDistanceZone = -1;
            _selectedDistanceCm = 0.0;

            _state = TrialState.SetTargetPoint;
            RestoreTrainerCursor();
            _targetPos = PointF.Empty;
            _targetDistanceCm = 0.0;
            if (_arena3D != null && !_arena3D.IsDisposed)
            {
                _arena3D.ShowStart = false;
                _arena3D.ShowTarget = false;
            }

            UpdateTrainingArenaProperties();
            UpdateTrainingHud();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }

        /// <summary>
        /// Auto-place the calibration target on the currently selected
        /// cardinal cell. Mirrors what the existing OnMouseDown does in the
        /// SetTargetPoint branch, but computes yaw/pitch/distance from the
        /// selected (axis, distanceZone) instead of from the user's free
        /// aim. This is what makes trials actually count toward the cell's
        /// attempt counter (RecordTrainingAttempt checks that
        /// trial.AngleZoneIndex == the selected axis's cardinal angle zone).
        ///
        /// After placing the target, the trainer enters the Cooldown state
        /// (3 s) and then the Flicking state automatically.
        /// </summary>
        // ====================================================================
        // SWEEP TEST MODE
        // ====================================================================
        // StartSweep: begins a 600 ms auto-advance through every zone on
        // the selected axis. PlaceCardinalTrainingTarget is called at each
        // step so the user can SEE the marker move (or fail to move) and
        // can identify exactly where the calibration environment breaks.
        //
        // The sweep does NOT flick the mouse — it only places the target
        // marker. The user just watches the marker travel through zones
        // 0, 1, 2, ..., max-1, then stops at the end.
        // ====================================================================
        private void StartSweep(int axis)
        {
            if (_profile == null) return;
            _sweepAxis = axis;
            _selectedAxisIndex = axis;
            _selectedDistanceZone = -1;
            _sweepCurrentZone = 0;

            // Cancel any pending cooldown / flick state so the trainer
            // doesn't capture the mouse during the sweep.
            try { _cooldownTimer?.Stop(); } catch { }
            _state = TrialState.SetTargetPoint;
            RestoreTrainerCursor();

            // Set up the sweep timer. 600 ms per zone gives the user time
            // to see the marker position and the HUD update.
            _sweepTimer = new Timer { Interval = 600 };
            _sweepTimer.Tick += (s, ev) =>
            {
                try
                {
                    int dirCount = DesktopSensTrainingState.ResolveDirectionCount(_profile);
                    int maxZones = TrainingGeometry.Current.MultiDirectionZoneCount(_sweepAxis, dirCount);
                    if (maxZones <= 0 || _sweepCurrentZone >= maxZones)
                    {
                        StopSweep();
                        return;
                    }
                    _selectedAxisIndex = _sweepAxis;
                    _selectedDistanceZone = _sweepCurrentZone;
                    _selectedDistanceCm = DesktopSensTrainingState.DistanceZoneCenterCm(_sweepCurrentZone);
                    PlaceCardinalTrainingTarget();
                    UpdateTrainingArenaProperties();
                    UpdateTrainingHud();
                    _sweepCurrentZone++;
                }
                catch
                {
                    StopSweep();
                }
            };
            _sweepTimer.Start();
            // Fire the first tick immediately so the user doesn't wait 600 ms
            // before the first marker appears.
            try
            {
                int dirCount = DesktopSensTrainingState.ResolveDirectionCount(_profile);
                int maxZones = TrainingGeometry.Current.MultiDirectionZoneCount(_sweepAxis, dirCount);
                if (maxZones > 0)
                {
                    _selectedDistanceZone = 0;
                    _selectedDistanceCm = DesktopSensTrainingState.DistanceZoneCenterCm(0);
                    PlaceCardinalTrainingTarget();
                    UpdateTrainingArenaProperties();
                    UpdateTrainingHud();
                    _sweepCurrentZone = 1;
                }
            }
            catch { }
        }

        private void StopSweep()
        {
            if (_sweepTimer != null)
            {
                try { _sweepTimer.Stop(); _sweepTimer.Dispose(); } catch { }
                _sweepTimer = null;
            }
        }
        // ====================================================================
        // END SWEEP TEST MODE
        // ====================================================================

        // ====================================================================
        // CALIBRATOR TEST MODE (F2)
        // ====================================================================
        // StartCalibratorTest: runs the calibrator against synthetic trials
        // that have ZERO error (the mouse landed exactly on the target).
        // The calibrator should compute factor = 1.0 for every zone.
        // Any deviation reveals a bug in the calibrator's chain logic,
        // confidence weighting, or factor clamping.
        //
        // Output: a per-zone log is appended to _calibTestLog and dumped
        // to DesktopSensLog so the user can read it from the diagnostic
        // log button on the config form.
        // ====================================================================
        private void StartCalibratorTest(int axis)
        {
            if (_profile == null) return;
            _calibTestAxis = axis;
            _calibTestCurrentZone = 0;
            _calibTestLog.Clear();
            _calibTestLog.Add($"=== CALIBRATOR TEST START (axis={AxisNameLocal(axis)}) ===");
            _calibTestLog.Add($"MaxAxisDegrees={TrainingGeometry.Current.MaxAxisDegrees}");
            _calibTestLog.Add($"MaxPitchDegrees={TrainingGeometry.Current.MaxPitchDegrees}");
            _calibTestLog.Add($"MaxDistanceCm={TrainingGeometry.Current.EffectiveMaxDistanceCm}");
            _calibTestLog.Add($"MarkerStepDegrees={TrainingGeometry.Current.MarkerStepDegrees}");
            _calibTestLog.Add($"DistanceZoneCount={TrainingGeometry.Current.DistanceZoneCount}");

            // Cancel any sweep.
            StopSweep();
            // Cancel any pending cooldown / flick state.
            try { _cooldownTimer?.Stop(); } catch { }
            _state = TrialState.SetTargetPoint;
            RestoreTrainerCursor();

            _calibTestTimer = new Timer { Interval = 50 };
            _calibTestTimer.Tick += (s, ev) =>
            {
                try
                {
                    int dirCount = DesktopSensTrainingState.ResolveDirectionCount(_profile);
                    int maxZones = TrainingGeometry.Current.MultiDirectionZoneCount(_calibTestAxis, dirCount);
                    if (maxZones <= 0 || _calibTestCurrentZone >= maxZones)
                    {
                        FinishCalibratorTest();
                        return;
                    }
                    RunOneCalibTestZone(_calibTestAxis, _calibTestCurrentZone);
                    _calibTestCurrentZone++;
                }
                catch (Exception ex)
                {
                    // PATCHED: include full stack trace so we can see exactly
                    // where the "Input string was not in a correct format"
                    // exception is thrown. The previous single-line message
                    // was useless for debugging.
                    _calibTestLog.Add($"EXCEPTION at zone {_calibTestCurrentZone}:");
                    _calibTestLog.Add($"  Message: {ex.Message}");
                    _calibTestLog.Add($"  Type:    {ex.GetType().FullName}");
                    _calibTestLog.Add($"  Source:  {ex.Source}");
                    _calibTestLog.Add($"  Stack:");
                    _calibTestLog.Add(ex.StackTrace ?? "  (no stack trace)");
                    if (ex.InnerException != null)
                    {
                        _calibTestLog.Add($"  Inner exception:");
                        _calibTestLog.Add($"    Message: {ex.InnerException.Message}");
                        _calibTestLog.Add($"    Type:    {ex.InnerException.GetType().FullName}");
                        _calibTestLog.Add($"    Stack:   {ex.InnerException.StackTrace}");
                    }
                    FinishCalibratorTest();
                }
            };
            _calibTestTimer.Start();
        }

        // Builds one synthetic "perfect" trial for the given (axis, zone),
        // adds it to _profile.Trials, then runs DirectionalCalibrator.Calibrate
        // and records the resulting factor for this cell.
        private void RunOneCalibTestZone(int axis, int zone)
        {
            // Resolve the axis's cardinal angle zone and direction in the
            // calibration frame: 0=Right, 8=Down, 16=Left, 24=Up.
            int dirCount = DesktopSensTrainingState.ResolveDirectionCount(_profile);
            int[] angleZones = DesktopSensTrainingState.AngleZoneIndicesForCount(dirCount);
            if (axis >= angleZones.Length) return;
            int angleZone = angleZones[axis];
            double axisAngleDeg = angleZone * (360.0 / 32.0);

            // The target distance for this zone.
            double targetCm = DesktopSensTrainingState.DistanceZoneCenterCm(zone);
            double targetDistanceCm = targetCm;
            double maxDistanceCm = TrainingGeometry.Current.EffectiveMaxDistanceCm;
            double degrees = (targetCm / Math.Max(0.001, maxDistanceCm)) *
                             TrainingGeometry.Current.MaxAxisDegrees;

            // Compute the target's (yaw, pitch) in the calibration frame.
            double targetYawDeg, targetPitchDeg;
            bool isPureRight = Math.Abs(axisAngleDeg - 0.0) < 0.01;
            bool isPureLeft = Math.Abs(axisAngleDeg - 180.0) < 0.01;
            bool isPureDown = Math.Abs(axisAngleDeg - 90.0) < 0.01;
            bool isPureUp = Math.Abs(axisAngleDeg - 270.0) < 0.01;
            if (isPureRight)      { targetYawDeg = degrees;  targetPitchDeg = 0.0; }
            else if (isPureLeft)  { targetYawDeg = -degrees; targetPitchDeg = 0.0; }
            else if (isPureDown)  { targetYawDeg = 0.0;      targetPitchDeg = -degrees; }
            else if (isPureUp)    { targetYawDeg = 0.0;      targetPitchDeg = degrees;  }
            else
            {
                double yawFraction = Math.Cos(axisAngleDeg * Math.PI / 180.0);
                double pitchFraction = Math.Sin(axisAngleDeg * Math.PI / 180.0);
                targetYawDeg = degrees * yawFraction;
                targetPitchDeg = -degrees * pitchFraction;
            }

            // The target angle in the mouse-DX/DY plane (atan2 convention).
            double targetYawSigned = targetYawDeg;
            while (targetYawSigned > 180.0) targetYawSigned -= 360.0;
            while (targetYawSigned < -180.0) targetYawSigned += 360.0;
            double targetRayAngleRad = Math.Atan2(-targetPitchDeg, targetYawSigned);
            double targetAngle = targetRayAngleRad * 180.0 / Math.PI;
            while (targetAngle < 0.0) targetAngle += 360.0;

            // Synthesize a "perfect" raw sample set: ONE big sample whose
            // (dx, dy) exactly reaches the target distance along the target
            // angle. Convert target cm to mouse counts using the profile's
            // CPI and the trainer's degrees-per-count.
            double cmPerCount = _profile.CmPerCount;
            double countsPerCm = 1.0 / Math.Max(1e-9, cmPerCount);
            double targetCounts = targetCm * countsPerCm;
            int dxCounts = (int)Math.Round(targetCounts * Math.Cos(targetRayAngleRad));
            int dyCounts = (int)Math.Round(targetCounts * Math.Sin(targetRayAngleRad));

            // Build the synthetic trial.
            var sample = new MouseSample
            {
                Timestamp = 0,
                DX = dxCounts,
                DY = dyCounts
            };
            var rawSamples = new List<MouseSample> { sample };

            // Manually compute signedErrorCm: this is the trial's projected
            // distance along the target axis minus the target distance.
            // Since we made a PERFECT flick, signedErrorCm should be 0.
            double projectedCounts = dxCounts * Math.Cos(targetRayAngleRad) +
                                     dyCounts * Math.Sin(targetRayAngleRad);
            double flickDistanceCm = projectedCounts * cmPerCount;
            double signedErrorCm = flickDistanceCm - targetDistanceCm;

            // Build the trial object directly (bypassing the trainer's
            // FinishTrial state machine, since we are not actually flicking).
            var trial = new DesktopSensTrial
            {
                SessionId = _profile.CurrentSessionId,
                StartX = 0, StartY = 0,
                TargetX = 0, TargetY = 0,
                TargetAngleDegrees = targetAngle,
                TargetDistanceCm = targetDistanceCm,
                RawSamples = rawSamples,
                LandedX = 0, LandedY = 0,
                FinalErrorCm = Math.Abs(signedErrorCm),
                SignedErrorCm = signedErrorCm,
                FlickDistanceCm = flickDistanceCm,
                DurationMs = 100,
                PeakVelocityPxPerSec = 1000,
                MeanVelocityPxPerSec = 1000,
                VelocityStdDevPxPerSec = 0,
                AngularErrorDegrees = 0,
                HasAngularMeasurement = true,
                HasMovementMeasurement = true,
                HasRotationMeasurement = true,
                RawSampleCount = 1,
                AngleZoneIndex = angleZone,
                DistanceZoneIndex = zone,
                MouseDPI = _profile.MouseDPI,
                EffectiveCpi = _profile.EffectiveCpi,
                GameSensitivity = _profile.UserInputGameSensitivity,
                CanvasPixelsPerCm = _pixelsPerCm,
                QuantizationErrorCm = _profile.QuantizationErrorCm,
                CompletionMode = FlickCompletionMode.Click,
                CompletionTimestamp = 0
            };
            try
            {
                trial.Validate(
                    noiseFloorCm: _profile.NoiseFloorCm,
                    minRawSamples: 1,
                    minDurationMs: 1,
                    maxDurationMs: 60_000,
                    maxAngularErrorDegrees: 180.0);
            }
            catch (Exception exVal)
            {
                _calibTestLog.Add($"zone {zone}: trial.Validate threw: {exVal.Message}");
            }

            _profile.Trials.Add(trial);

            // PATCHED: call BOTH calibrators. DirectionalCalibrator updates
            // the 4 cardinal-direction factors (Right/Down/Left/Up).
            // DesktopSensCalibrator updates the per-cell factor arrays
            // (CellSensitivityFactor / CellAccelerationFactor) — these are
            // what the trainer's per-cell training state relies on.
            // Without calling DesktopSensCalibrator, the per-cell factors
            // stay at their default 1.0 with confidence 0, which is exactly
            // what the first test showed.
            try
            {
                DirectionalCalibrator.Calibrate(_profile, _profile.Trials, 0.0);
            }
            catch (Exception ex)
            {
                _calibTestLog.Add($"zone {zone}: DirectionalCalibrator threw: {ex.Message}");
                _calibTestLog.Add($"  Stack: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    _calibTestLog.Add($"  Inner: {ex.InnerException.Message}");
                    _calibTestLog.Add($"  InnerStack: {ex.InnerException.StackTrace}");
                }
            }
            try
            {
                DesktopSensCalibrator.Calibrate(_profile);
            }
            catch (Exception ex)
            {
                _calibTestLog.Add($"zone {zone}: DesktopSensCalibrator threw: {ex.Message}");
                _calibTestLog.Add($"  Stack: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    _calibTestLog.Add($"  Inner: {ex.InnerException.Message}");
                    _calibTestLog.Add($"  InnerStack: {ex.InnerException.StackTrace}");
                }
            }

            // Read back the factor the calibrator computed for this cell.
            int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, zone);
            double cellFactor = 1.0;
            double cellConfidence = 0.0;
            if (_profile.CellAccelerationFactor != null &&
                cellIndex >= 0 && cellIndex < _profile.CellAccelerationFactor.Length)
            {
                cellFactor = _profile.CellAccelerationFactor[cellIndex];
            }
            if (_profile.CellAccelerationConfidence != null &&
                cellIndex >= 0 && cellIndex < _profile.CellAccelerationConfidence.Length)
            {
                cellConfidence = _profile.CellAccelerationConfidence[cellIndex];
            }

            // Also read the per-cell sensitivity factor and the per-direction
            // factor for this axis. The DirectionalCalibrator sets the
            // direction-level factors; DesktopSensCalibrator sets the
            // per-cell factors.
            double cellSensFactor = 1.0;
            double cellSensConf = 0.0;
            if (_profile.CellSensitivityFactor != null &&
                cellIndex >= 0 && cellIndex < _profile.CellSensitivityFactor.Length)
            {
                cellSensFactor = _profile.CellSensitivityFactor[cellIndex];
            }
            if (_profile.CellSensitivityConfidence != null &&
                cellIndex >= 0 && cellIndex < _profile.CellSensitivityConfidence.Length)
            {
                cellSensConf = _profile.CellSensitivityConfidence[cellIndex];
            }
            double dirFactor = 1.0;
            double dirConf = 0.0;
            switch (axis)
            {
                case 0: dirFactor = _profile.RightSensitivityFactor; dirConf = _profile.RightSensitivityConfidence; break;
                case 1: dirFactor = _profile.DownSensitivityFactor;  dirConf = _profile.DownSensitivityConfidence; break;
                case 2: dirFactor = _profile.LeftSensitivityFactor;  dirConf = _profile.LeftSensitivityConfidence; break;
                case 3: dirFactor = _profile.UpSensitivityFactor;    dirConf = _profile.UpSensitivityConfidence; break;
            }

            double targetAngleDisplay = axisAngleDeg;
            // PATCHED: removed the complex custom format string "+0.000;-0.000;0.000"
            // and the degree symbol "°" — these caused FormatException on
            // non-invariant cultures (e.g. fa-IR). Use simple F3 format and
            // InvariantCulture for the whole format operation.
            string signedErrStr = signedErrorCm.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
            string factorStr = cellFactor.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
            string confStr = cellConfidence.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            string angleStr = targetAngleDisplay.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            string distStr = targetCm.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            string cellSensStr = cellSensFactor.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
            string cellSensConfStr = cellSensConf.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            string dirFactorStr = dirFactor.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
            string dirConfStr = dirConf.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            _calibTestLog.Add(
                "zone " + zone.ToString().PadLeft(2) +
                " | angle=" + angleStr + " deg" +
                " | dist=" + distStr + " cm" +
                " | signedErr=" + signedErrStr + " cm" +
                " | cellAccelFactor=" + factorStr + " conf=" + confStr +
                " | cellSensFactor=" + cellSensStr + " conf=" + cellSensConfStr +
                " | dirFactor=" + dirFactorStr + " conf=" + dirConfStr +
                " | rejected=" + trial.WasRejected +
                " | reason=" + (trial.RejectionReason ?? ""));
        }

        private void FinishCalibratorTest()
        {
            if (_calibTestTimer != null)
            {
                try { _calibTestTimer.Stop(); _calibTestTimer.Dispose(); } catch { }
                _calibTestTimer = null;
            }
            _calibTestLog.Add("=== CALIBRATOR TEST END ===");
            string summary = string.Join(Environment.NewLine, _calibTestLog);
            try
            {
                DesktopSensLog.Warn(summary);
            }
            catch { }
            // Also dump to a file the user can read directly.
            try
            {
                string path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "desktopsens_calib_test.log");
                System.IO.File.WriteAllText(path, summary);
                _calibTestLog.Add("Log written to: " + path);
            }
            catch { }
            UpdateTrainingHud();
        }

        private static string AxisNameLocal(int axis)
        {
            switch (axis)
            {
                case 0: return "Right";
                case 1: return "Down";
                case 2: return "Left";
                case 3: return "Up";
                default: return "Axis" + axis;
            }
        }
        // ====================================================================
        // END CALIBRATOR TEST MODE
        // ====================================================================

        private void PlaceCardinalTrainingTarget()
        {
            if (_selectedAxisIndex < 0 || _selectedDistanceZone < 0) return;
            if (_profile == null) return;

            // FIX (8/16-way): resolve the active direction-count mode and
            // use the matching axis array. The four cardinal axes keep
            // their legacy behaviour; the diagonal axes (8-way indices 1,
            // 3, 5, 7 and 16-way indices 1, 3, 5, ...) split the angular
            // travel between yaw and pitch using the same projection as
            // EnsureCanonicalTrainingPoints.
            int directionCount = DesktopSensTrainingState.ResolveDirectionCount(_profile);
            int[] angleZones = DesktopSensTrainingState.AngleZoneIndicesForCount(directionCount);
            if (_selectedAxisIndex >= angleZones.Length) return;
            int angleZone = angleZones[_selectedAxisIndex];
            // Use the EXACT clicked cm if available (>0). Fall back to
            // zone-center cm only when the user picked via key 1-8 / 1-16
            // (which doesn't set _selectedDistanceCm — there's no click
            // position to inherit from).
            double targetCm = _selectedDistanceCm > 0.0
                ? _selectedDistanceCm
                : DesktopSensTrainingState.DistanceZoneCenterCm(_selectedDistanceZone);
            double maxDistanceCm = _maxDistanceCm;
            double degrees = (targetCm / Math.Max(0.001, maxDistanceCm)) * TrainingGeometry.Current.MaxAxisDegrees;
            degrees = Math.Min(TrainingGeometry.Current.MaxAxisDegrees, Math.Max(0.0, degrees));

            // Resolve yaw/pitch from the axis's calibration-frame angle.
            // The axis at index i sits at angle zone * 11.25 degrees in
            // the calibration frame; 0° = Right, 90° = Down, 180° = Left,
            // 270° = Up.
            double axisAngleDeg = angleZone * (360.0 / 32.0);
            axisAngleDeg = ((axisAngleDeg % 360.0) + 360.0) % 360.0;
            bool isPureRight = Math.Abs(axisAngleDeg - 0.0) < 0.01;
            bool isPureLeft = Math.Abs(axisAngleDeg - 180.0) < 0.01;
            bool isPureDown = Math.Abs(axisAngleDeg - 90.0) < 0.01;
            bool isPureUp = Math.Abs(axisAngleDeg - 270.0) < 0.01;

            double yawDeg, pitchDeg;
            if (isPureRight)      { yawDeg = degrees;  pitchDeg = 0.0; }
            else if (isPureLeft)  { yawDeg = -degrees; pitchDeg = 0.0; }
            else if (isPureDown)  { yawDeg = 0.0;      pitchDeg = -degrees; }
            else if (isPureUp)    { yawDeg = 0.0;      pitchDeg = degrees;  }
            else
            {
                double yawFraction = Math.Cos(axisAngleDeg * Math.PI / 180.0);
                double pitchFraction = Math.Sin(axisAngleDeg * Math.PI / 180.0);
                yawDeg = degrees * yawFraction;
                pitchDeg = -degrees * pitchFraction;
            }
            double targetAngleDeg = axisAngleDeg;

            // PATCHED: removed the MaxPitchDegrees clamp on pitchDeg.
            // The user explicitly said no limit on calibration.

            _targetYawDegrees = NormalizeAngle(yawDeg);
            _targetPitchDegrees = pitchDeg;
            _targetDistanceCm = Math.Max(0.5, targetCm);
            _targetAngleDegrees = targetAngleDeg;

            // Recompute the 2D HUD label anchor so the existing HUD shows
            // the target's angular position. This mirrors OnMouseDown.
            double aimMagnitude = Math.Sqrt(yawDeg * yawDeg + pitchDeg * pitchDeg);
            double yawRad = _targetYawDegrees * Math.PI / 180.0;
            double pitchRad = _targetPitchDegrees * Math.PI / 180.0;
            double maxArenaRadiusPx = Math.Max(1.0, Math.Min(ClientSize.Width, ClientSize.Height) * 0.42);
            double arenaRadiusPx = maxArenaRadiusPx * (aimMagnitude / Math.Max(0.001, TrainingGeometry.Current.MaxAxisDegrees));
            _targetPos = new PointF(
                _startPos.X + (float)(Math.Sin(yawRad) * Math.Cos(pitchRad) * arenaRadiusPx),
                _startPos.Y - (float)(Math.Sin(pitchRad) * arenaRadiusPx));

            // Place the 3D marker on the calibration sphere.
            EnsureCanonicalTrainingPoints();
            Vector3 canonicalPoint = WorldPointFromAngles(_targetYawDegrees, _targetPitchDegrees, _targetDistanceCm);
            if (_selectedAxisIndex >= 0 && _selectedAxisIndex < _canonicalTrainingMarkerPositions.Length &&
                _selectedDistanceCm > 0.0)
            {
                double[] canonicalDistances = _canonicalTrainingMarkerDistanceCm[_selectedAxisIndex];
                Vector3[] canonicalPositions = _canonicalTrainingMarkerPositions[_selectedAxisIndex];
                for (int i = 0; i < canonicalDistances.Length; i++)
                {
                    if (Math.Abs(canonicalDistances[i] - _selectedDistanceCm) < 0.001)
                    {
                        canonicalPoint = canonicalPositions[i];
                        break;
                    }
                }
            }
            _arena3D.TargetPoint = canonicalPoint;
            _arena3D.ShowTarget = true;

            // Reset state and start the cooldown. The cursor is owned by
            // the trainer only during the active flick/cooldown phase.
            HideTrainerCursor();
            _rawSamples.Clear();
            _currentCursorPos = _startPos;
            _cameraYawDegrees = _calibrationYawDegrees;
            _cameraPitchDegrees = CalibrationPitchDegrees;
            _state = TrialState.Cooldown;
            _cooldownTimer.Stop();
            _cooldownTimer.Start();

            UpdateArena3D();
        }

        // =========================================================================
        // Chained-correction methods.
        //
        // These implement the user's "chain" requirement: when calibrating
        // cell N+1, the corrections from cells 0..N (already green) are
        // APPLIED during the flick, so the measured signed error is the
        // RESIDUAL error after those corrections. The calibrator then
        // computes cell N+1's factor as the residual correction needed.
        //
        // Without this chain, each cell would be calibrated from raw errors
        // — cell N+1's factor would compensate for cell N's residual too,
        // and merging the two would double-correct. The merged curve would
        // not be a smooth up/down piecewise curve as the user described.
        // =========================================================================

        /// <summary>
        /// Resets the cumulative flick distance to zero. Called at the start
        /// of each flick (in OnCooldownFinished, when state transitions to
        /// Flicking).
        /// </summary>
        private void ResetFlickCumulativeDistance()
        {
        }

        /// <summary>
        /// Applies the per-cell chained corrections to one mouse sample.
        /// Called per sample in the Flicking state, AFTER the rotation
        /// correction block and BEFORE the camera update
        /// (`_cameraYawDegrees += dx * degreesPerCount`).
        ///
        /// Behavior:
        ///   1. Compute the magnitude of this sample in cm (using DPI).
        ///   2. Look up the cell at (selected cardinal axis, current
        ///      cumulative distance). The cell index is determined by the
        ///      GAME-EQUIVALENT distance traveled so far — i.e., the
        ///      distance AFTER applying previous samples' per-cell factors.
        ///   3. Build the multiplier for this cell:
        ///      - If the cell is Verified (green): multiply by its
        ///        CellAccelerationFactor. With BaseZoneEndCm = 0.0 there is
        ///        no separate "first 3 cm" CellSensitivityFactor band — the
        ///        same CellAccelerationFactor array covers the full range,
        ///        including micro-flick zones.
        ///      - The directional base sensitivity (Right/Down/Left/Up) is
        ///        still applied as a separate cardinal multiplier.
        ///      - If the cell is NOT yet verified: multiplier stays at 1.0
        ///        (no correction yet — this is the cell being trained).
        ///   4. Apply the multiplier to dx and dy (by reference).
        ///   5. Accumulate the factored movement distance for the current flick.
        ///      magnitude, so the next sample's cell lookup reflects the
        ///      game-equivalent distance.
        ///
        /// Modifies dx and dy in place. The trainer's existing camera
        /// update then consumes these factored values, so the game rotation
        /// reflects the chained corrections. The signed error measured at
        /// the end of the flick (in FinishTrial) is therefore the RESIDUAL
        /// error after the previous cells' corrections are applied.
        /// </summary>
        private void ApplyChainedCellCorrections(ref double dx, ref double dy)
        {
            // DISABLED — this hook was breaking the mouse during calibration.
            // The trainer should measure raw mouse movement without applying
            // per-cell corrections during the flick. Corrections are computed
            // by the calibrator from the trial set and applied to the driver
            // (RawAccel), NOT applied during the trainer's flick simulation.
            //
            // The original implementation here multiplied dx/dy by the
            // per-cell factor for the currently-selected cardinal axis,
            // which made camera rotation non-linear and made the user feel
            // the mouse was broken. That is wrong.
            //
            // To re-enable: implement a proper version that does NOT modify
            // dx/dy. The "chain" requirement (later cells' errors are
            // residuals after earlier cells' corrections) should be handled
            // in the calibrator, not in the trainer's per-sample path.
            return;
        }




        /// <summary>
        /// Called from FinishTrial after the trial has been added to
        /// _profile.Trials, so the calibrator sees the current sample. This lets us look up the
        /// selected cell, feed the trial's SignedErrorCm into the training
        /// state, and trigger yellow/green transitions.
        ///
        /// If no training cell is currently selected, this is a no-op (the
        /// trial is still recorded via the existing _profile.Trials.Add path).
        /// </summary>
        private void RecordTrainingAttempt(DesktopSensTrial trial)
        {
            var calibrateMergeApplyTimer = System.Diagnostics.Stopwatch.StartNew();
            // FIX 6 (from AI review): explicit logging for discarded
            // trials so the user (or developer) can see — via the
            // "Show Diag Log" button on DesktopSensConfigForm — exactly
            // why a trial was not recorded. Previously, these guards
            // returned silently, which made the "curve fit / cell
            // verified" failures impossible to debug from outside.
            if (!_isTrainingMode)
            {
                DesktopSensLog.Warn(
                    "Training trial DISCARDED: training mode is off " +
                    "(was T pressed? was the trainer opened in Sens Finder mode?). " +
                    "axis=" + _selectedAxisIndex + " zone=" + _selectedDistanceZone);
                return;
            }
            if (trial == null) return;
            if (trial.IsSessionAnchor || trial.IsEvaluationOnly) return;
            if (_selectedAxisIndex < 0 || _selectedDistanceZone < 0)
            {
                DesktopSensLog.Warn(
                    "Training trial DISCARDED: no cell selected. " +
                    "axis=" + _selectedAxisIndex + " zone=" + _selectedDistanceZone +
                    " trialAngle=" + trial.AngleZoneIndex +
                    " trialZone=" + trial.DistanceZoneIndex +
                    " (the user must click a coloured marker or press 1/2/3/4 " +
                    "before flicking, otherwise the trial has no cell to be " +
                    "credited to)");
                return;
            }

            // With BaseZoneEndCm = 0.0 every positive-distance trial is
            // Train calibration data — the previous 3 cm exemption is gone.
            // Require the captured trial to remain bound to the marker the
            // user chose.
            if (!DesktopSensTrainingState.IsCalibrationDistanceZone(_selectedDistanceZone) ||
                !DesktopSensTrainingState.IsCalibrationDistanceZone(trial.DistanceZoneIndex) ||
                trial.TargetDistanceCm <= DesktopSensConstants.BaseZoneEndCm ||
                trial.DistanceZoneIndex != _selectedDistanceZone)
            {
                trial.IsCalibrationExcluded = true;
                DesktopSensLog.Warn(
                    "Train trial ignored: outside the eligible far-distance zone. " +
                    "selectedZone=" + _selectedDistanceZone +
                    " trialZone=" + trial.DistanceZoneIndex +
                    " targetCm=" + trial.TargetDistanceCm.ToString("0.000"));
                return;
            }

            // FIX (8/16-way): resolve the angle zone from the active
            // direction-count mode so 8-way and 16-way axes' trials are
            // attributed to the correct cell.
            int directionCount = DesktopSensTrainingState.ResolveDirectionCount(_profile);
            int[] angleZones = DesktopSensTrainingState.AngleZoneIndicesForCount(directionCount);
            if (_selectedAxisIndex >= angleZones.Length) return;
            int angleZone = angleZones[_selectedAxisIndex];
            // Sanity: the trial's angle zone should match the selected axis.
            // (The trainer sets AngleZoneIndex from the actual aim direction;
            // if the user free-aimed away from the cardinal direction, the
            // trial doesn't count toward this cell's training.)
            if (!trial.IsValidForCalibration || trial.AngleZoneIndex != angleZone)
            {
                // A rejected/measurement-less trial (or a mismatch between the
                // selected canonical marker and its derived angle zone) is NOT
                // calibration data.
                // Still persist the attempt count so the marker and menu show
                // that this point was attempted. Do not contaminate error history.
                DesktopSensTrainingState.RecordRejectedAttempt(
                    _profile.CellTrainingStates,
                    angleZone,
                    _selectedDistanceZone,
                    trial.SignedErrorCm,
                    DateTime.UtcNow,
                    _profile.CurrentSessionId);

                if (!trial.IsValidForCalibration)
                {
                    DesktopSensLog.Warn(
                        "Training trial counted as rejected: validForCalibration=false; " +
                        "wasRejected=" + trial.WasRejected +
                        " movement=" + trial.HasMovementMeasurement +
                        " targetCm=" + trial.TargetDistanceCm.ToString("0.000") +
                        " signedErrorCm=" + trial.SignedErrorCm.ToString("+0.000;-0.000;0.000") +
                        " reason=" + (trial.RejectionReason ?? TrialRejectionReason.None));
                }

                if (trial.AngleZoneIndex != angleZone)
                {
                    // Prevent a mismapped cardinal trial from being consumed by
                    // a later full-profile recalibration under the wrong axis.
                    trial.IsCalibrationExcluded = true;
                    trial.WasRejected = true;
                    trial.RejectionReason = TrialRejectionReason.CardinalAxisMismatch;
                    DesktopSensLog.Warn(
                        "Training trial did not map back to its selected cardinal axis; counted as rejected attempt. " +
                        "Selected angle zone=" + angleZone +
                        ", trial angle zone=" + trial.AngleZoneIndex + ".");
                }
                OnCellVerifiedPersistProfile();
                // FIX (keep axis after trial): keep _selectedAxisIndex set so
                // the user can immediately pick the next zone on the same
                // axis without re-pressing 1/2/3/4 or re-clicking an axis
                // marker. Only the distance zone is cleared — the user
                // still picks the next cell manually (no auto-advance,
                // per the user's original directive).
                //
                // FIX 2 (from AI review): also keep _selectedDistanceZone
                // (and _selectedDistanceCm) so the user can do several
                // trials back-to-back on the SAME cell without re-clicking
                // the marker or re-pressing 1/2/3/4 each time. The previous
                // behaviour forced the user to re-select the cell after
                // every trial, and if they forgot (or clicked an empty
                // space by mistake), the next trial was silently
                // discarded by RecordTrainingAttempt's early-return.
                // Now the cell stays selected until the user explicitly
                // picks another (via 1/2/3/4 or marker click). The arena
                // keeps showing the target's marker colour, but the
                // actual target point is hidden until the next click/
                // key press so the user is not tempted to flick to the
                // just-finished target by mistake.
                if (_arena3D != null && !_arena3D.IsDisposed)
                    _arena3D.ShowTarget = false;
                UpdateTrainingArenaProperties();
                UpdateTrainingHud();
                return;
            }

            // NEW (Model v6): if the trial carries a NeededDistanceCm (ASDW
            // correction landed inside tolerance), the calibration reference is
            // that effective target. The residual we feed the training state is
            // FlickDistanceCm - effectiveTarget, so the factor the estimator
            // produces matches (flick + nudge) / flick.
            double effectiveTargetCm = trial.TargetDistanceCm;
            double signedError = trial.HasChainAdjustedSignedError
                ? trial.ChainAdjustedSignedErrorCm
                : trial.SignedErrorCm;
            if (IsFiniteValue(trial.NeededDistanceCm) &&
                trial.NeededDistanceCm > DesktopSensConstants.MinimumDenominatorCm)
            {
                effectiveTargetCm = trial.NeededDistanceCm;
                // Recompute residual against the effective target so the gain
                // estimator sees the "needed" distance.
                double observedFlickCm = trial.FlickDistanceCm;
                if (IsFiniteValue(observedFlickCm))
                    signedError = observedFlickCm - effectiveTargetCm;
            }
            if (double.IsNaN(signedError) || double.IsInfinity(signedError))
            {
                DesktopSensTrainingState.RecordRejectedAttempt(
                    _profile.CellTrainingStates, angleZone, _selectedDistanceZone,
                    signedError, DateTime.UtcNow, _profile.CurrentSessionId);
                OnCellVerifiedPersistProfile();
                // FIX (keep axis + zone after trial): see the comment above
                // for why _selectedAxisIndex AND _selectedDistanceZone are
                // NOT cleared here — keep the cell selected so the user
                // can immediately do another trial on the same cell.
                if (_arena3D != null && !_arena3D.IsDisposed)
                    _arena3D.ShowTarget = false;
                UpdateTrainingArenaProperties();
                UpdateTrainingHud();
                return;
            }
            double sessionBias;
            bool sessionBiasApplied = DesktopSensCalibrator.TryGetSessionBiasLogGain(
                _profile.Trials, _profile.CurrentSessionId, out sessionBias);
            if (sessionBiasApplied)
            {
                double observed = effectiveTargetCm + signedError;
                if (IsFiniteValue(observed) && observed > 0.05)
                    signedError = observed * Math.Exp(-sessionBias) - effectiveTargetCm;
            }

            int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, _selectedDistanceZone);
            if (_profile.CellTrainingStates.TryGetValue(cellIndex, out var existingCellState) &&
                existingCellState != null && existingCellState.IsDiverging)
            {
                trial.IsCalibrationExcluded = true;
                trial.WasRejected = true;
                trial.RejectionReason = TrialRejectionReason.CellDiverging;
                DesktopSensTrainingState.RecordRejectedAttempt(
                    _profile.CellTrainingStates, angleZone, _selectedDistanceZone,
                    trial.SignedErrorCm, DateTime.UtcNow, _profile.CurrentSessionId);
                DesktopSensLog.Warn("Training trial blocked: selected cell is diverging; cell=" + cellIndex);
                DesktopSensToast.Show(this, "این نقطه واگرا شده؛ برای ادامه Undo یا Reset همین نقطه را انجام بده.");
                OnCellVerifiedPersistProfile();
                UpdateTrainingArenaProperties();
                UpdateTrainingHud();
                return;
            }

            // Capture the previous verified-flag so we can detect the
            // yellow→green transition and trigger an explicit save. The
            // user explained: "when a point becomes green, it must be
            // saved" — so the chain is persisted immediately, not just on
            // trainer close. Without an immediate save, a crash or trainer
            // close mid-axis would lose the verified cells.
            bool wasVerifiedBefore = false;
            bool wasPrecisionVerifiedBefore = false;
            bool wasFirstRoundBefore = false;
            string verifiedSessionBefore = null;
            if (_profile.CellTrainingStates.TryGetValue(cellIndex, out var prevState) && prevState != null)
            {
                wasVerifiedBefore = prevState.Verified;
                wasPrecisionVerifiedBefore = prevState.PrecisionVerified;
                wasFirstRoundBefore = prevState.FirstRoundComplete;
                verifiedSessionBefore = prevState.VerifiedSessionId;
            }

            // FIX #8 + #3: pass the session id (for drift detection across
            // sessions) and the angular error (for the cell's history).
            double angularErr = trial.HasAngularMeasurement ? trial.AngularErrorDegrees : double.NaN;
            DesktopSensTrainingState.RecordAttempt(
                _profile.CellTrainingStates,
                angleZone,
                _selectedDistanceZone,
                signedError,
                DateTime.UtcNow,
                sessionId: _profile.CurrentSessionId,
                angularErrorDegrees: angularErr,
                targetDistanceCm: effectiveTargetCm,
                sessionBiasApplied: sessionBiasApplied,
                sessionBiasLogGain: sessionBiasApplied ? sessionBias : 0.0,
                rawFlickErrorCm: trial.SignedErrorCm);

            if (_profile.CellTrainingStates.TryGetValue(cellIndex, out var newlyDivergingState) &&
                newlyDivergingState != null && newlyDivergingState.IsDiverging)
            {
                trial.IsCalibrationExcluded = true;
                trial.WasRejected = true;
                trial.RejectionReason = TrialRejectionReason.CellDiverging;
                DesktopSensLog.Warn("Training cell divergence detected; freezing further updates: cell=" +
                    cellIndex + " robustErrorCm=" + newlyDivergingState.RobustSignedErrorCm.ToString("0.000") +
                    " streak=" + newlyDivergingState.DivergenceWorseningStreak);
                DesktopSensToast.Show(this, "خطای این نقطه در چند trial پیاپی بدتر شد؛ کالیبراسیون متوقف شد. Undo/Reset نقطه را پیشنهاد می‌کنم.");
                OnCellVerifiedPersistProfile();
                UpdateTrainingArenaProperties();
                UpdateTrainingHud();
                return;
            }

            // NEW (Model v6): capture the cell's previous acceleration factor so
            // we can detect a >1% change after Calibrate() runs. Previously a
            // cell could improve its factor over many trials without ever
            // re-triggering the driver, so the driver kept an old curve while
            // the profile had new data.
            double previousCellFactor = 1.0;
            if (_profile.CellAccelerationFactor != null &&
                cellIndex >= 0 && cellIndex < _profile.CellAccelerationFactor.Length)
                previousCellFactor = _profile.CellAccelerationFactor[cellIndex];

            // Re-run the per-cell calibrator so factors stay in sync with the
            // trial set. Live mouse input remains raw; Calibrate() rebuilds all
            // chain residuals from raw samples in deterministic zone order.
            try
            {
                DesktopSensCalibrator.Calibrate(_profile);
                double sensitivityFactorForLog = cellIndex >= 0 &&
                    _profile.CellSensitivityFactor != null && cellIndex < _profile.CellSensitivityFactor.Length
                        ? _profile.CellSensitivityFactor[cellIndex] : 1.0;
                double sensitivityConfidenceForLog = cellIndex >= 0 &&
                    _profile.CellSensitivityConfidence != null && cellIndex < _profile.CellSensitivityConfidence.Length
                        ? _profile.CellSensitivityConfidence[cellIndex] : 0.0;
                double accelerationFactorForLog = cellIndex >= 0 &&
                    _profile.CellAccelerationFactor != null && cellIndex < _profile.CellAccelerationFactor.Length
                        ? _profile.CellAccelerationFactor[cellIndex] : 1.0;
                double accelerationConfidenceForLog = cellIndex >= 0 &&
                    _profile.CellAccelerationConfidence != null && cellIndex < _profile.CellAccelerationConfidence.Length
                        ? _profile.CellAccelerationConfidence[cellIndex] : 0.0;
                bool isBaseDimensionForLog = TrainingGeometry.Current.ZoneCenterCm(_selectedDistanceZone)
                    <= DesktopSensConstants.BaseZoneEndCm;
                double trainFactor = isBaseDimensionForLog ? sensitivityFactorForLog : accelerationFactorForLog;
                double trainConfidence = isBaseDimensionForLog ? sensitivityConfidenceForLog : accelerationConfidenceForLog;
                TrainingCellState diagnosticState = null;
                if (_profile.CellTrainingStates != null)
                    _profile.CellTrainingStates.TryGetValue(cellIndex, out diagnosticState);
                DesktopSensLog.Warn(
                    "Train calibration recomputed: cell=" + cellIndex +
                    " trials=" + _profile.Trials.Count +
                    " factorKind=" + (isBaseDimensionForLog ? "sensitivity" : "acceleration") +
                    " factor=" + trainFactor.ToString("0.000") +
                    " confidence=" + trainConfidence.ToString("0.000") +
                    " sensFactor=" + sensitivityFactorForLog.ToString("0.000") +
                    " sensConfidence=" + sensitivityConfidenceForLog.ToString("0.000") +
                    " accelFactor=" + accelerationFactorForLog.ToString("0.000") +
                    " accelConfidence=" + accelerationConfidenceForLog.ToString("0.000") +
                    " rawError=" + signedError.ToString("+0.000;-0.000;0.000") +
                    " robustRawError=" + (diagnosticState != null
                        ? diagnosticState.RobustSignedErrorCm.ToString("+0.000;-0.000;0.000") : "n/a") +
                    " firstRound=" + (diagnosticState != null && diagnosticState.FirstRoundComplete) +
                    " verified=" + (diagnosticState != null && diagnosticState.Verified));
            }
            catch (Exception ex)
            {
                // Keep the trainer responsive, but never hide a failed update.
                DesktopSensLog.Error("Calibration recompute failed after a training attempt", ex);
            }

            // Detect yellow→green transition for the currently selected
            // cell. If the cell just turned green, persist the profile
            // immediately so the chain survives a trainer crash or close.
            bool isVerifiedNow = false;
            bool isPrecisionVerifiedNow = false;
            bool isFirstRoundNow = false;
            string verifiedSessionNow = null;
            if (_profile.CellTrainingStates.TryGetValue(cellIndex, out var postState) && postState != null)
            {
                isVerifiedNow = postState.Verified;
                isPrecisionVerifiedNow = postState.PrecisionVerified;
                isFirstRoundNow = postState.FirstRoundComplete;
                verifiedSessionNow = postState.VerifiedSessionId;
            }
            bool trainingStateChanged =
                wasVerifiedBefore != isVerifiedNow ||
                wasPrecisionVerifiedBefore != isPrecisionVerifiedNow ||
                wasFirstRoundBefore != isFirstRoundNow ||
                !string.Equals(verifiedSessionBefore, verifiedSessionNow, StringComparison.Ordinal);

            // A previously green cell that loses verification no longer owns
            // its frozen LUT segment. Release this cell's nodes and every
            // higher-speed dependent node before the next sample-fit build.
            bool wasGreenBefore = wasVerifiedBefore && wasPrecisionVerifiedBefore;
            bool isGreenNow = isVerifiedNow && isPrecisionVerifiedNow;
            if (wasGreenBefore && !isGreenNow)
            {
                DesktopSensProfileMerger.UnfreezeSampleLutFromCell(
                    _profile, angleZone, _selectedDistanceZone);
                CalibrationResidualCache.Clear(_profile);
                DesktopSensLog.Info("SampleLut unfreeze on green-to-non-green transition: angleZone=" +
                    angleZone + " distanceZone=" + _selectedDistanceZone);
            }
            // Persist every accepted training sample, not only color changes:
            // a crash must not discard the first-round sample history or the
            // recalculated per-cell factors needed by the next chain step.
            OnCellVerifiedPersistProfile();

            // ───────────────────────────────────────────────────────────────
            // NO AUTO-ADVANCE TO THE NEXT ZONE — user's directive.
            //
            // The user explained: "vaghti ye ye noghteh calibrate mishe,
            //  ye noghteh dige ro automatic mizaneh baraye calibrate,
            //  in bayad hazf beshe" — when one point is calibrated, the
            //  code should NOT auto-place the next point for calibration.
            //
            // The previous behaviour auto-advanced to the next eligible
            // cell on the same cardinal axis and called
            // PlaceCardinalTrainingTarget() so the next trial started
            // immediately after the 3-second cooldown. The user wants
            // to manually pick the next point (by clicking on a marker
            // or pressing 1/2/3/4) so they have full control over which
            // cell is calibrated next.
            //
            // ───────────────────────────────────────────────────────────────
            // REVISED (keep axis + cell after trial): the user asked
            // that after a trial the selected AXIS remain selected so
            // they don't have to re-press 1/2/3/4 each time. The AI
            // review additionally asked that the selected DISTANCE ZONE
            // also stay selected so the user can do several trials
            // back-to-back on the SAME cell without re-clicking the
            // marker. Both _selectedAxisIndex and _selectedDistanceZone
            // now stay set after a trial. The user explicitly picks a
            // new cell via 1/2/3/4 or by clicking a marker on the arena;
            // without such a selection, the trainer stays on the
            // current cell so the user can immediately flick again
            // and have the trial credited to the same cell.
            //
            // The "no auto-advance" directive is still respected: the
            // trainer never auto-selects a DIFFERENT cell. It just
            // keeps the cell the user already picked.
            // ───────────────────────────────────────────────────────────────
            if (_arena3D != null && !_arena3D.IsDisposed)
            {
                _arena3D.ShowTarget = false;
            }

            UpdateTrainingArenaProperties();
            UpdateTrainingHud();

            // GRADUAL MERGE (user's directive): apply the calibration
            // to the driver IMMEDIATELY after every successful trial
            // — not only when the whole grid is green. The user
            // explicitly asked that each verified cell be applied to
            // the driver the instant it goes green, so they can feel
            // the cumulative effect while they're still calibrating
            // the rest. This means when the user is calibrating cell N,
            // cells 0..N-1 are already live in the driver — the user
            // does not have to wait until the entire grid is done to
            // see any effect in real life.
            //
            // The previous behaviour only fired TryMergeAndApply when
            // IsReady was true (i.e. ALL cells verified). With
            // IsPartiallyReady now accepting the case where at least
            // one cell is verified, TryMergeAndApply will succeed for
            // partial merges too. We always try the merge here; if
            // nothing has changed since the last merge (e.g. the trial
            // didn't flip any cell to green), MergeAndApply will still
            // re-apply the same factors, which is fine — the driver is
            // idempotent for the same settings.
            //
            // The user can still press M to merge manually, and the
            // "all cells verified" case is still the FINAL merge —
            // both flows end up calling TryMergeAndApply, which now
            // accepts both partial and full state.
            // NEW (Model v6): also merge when a cell's factor changed by more than
            // MergeFactorChangeThreshold, even if its colour did not change.
            // Before this fix a cell could improve its factor over many trials
            // without ever re-triggering the driver, so the driver kept an old
            // curve while the profile had new data.
            double newCellFactor = 1.0;
            if (_profile.CellAccelerationFactor != null &&
                cellIndex >= 0 && cellIndex < _profile.CellAccelerationFactor.Length)
                newCellFactor = _profile.CellAccelerationFactor[cellIndex];

            bool factorChanged = IsFiniteValue(newCellFactor) &&
                Math.Abs(newCellFactor - previousCellFactor) >
                    DesktopSensConstants.MergeFactorChangeThreshold *
                    Math.Max(1.0, Math.Abs(previousCellFactor));

            try
            {
                // GRADUAL MERGE (user's directive): only merge when the
                // training state CHANGED — i.e. a cell went from white to
                // yellow, or yellow to green, or a new cell became
                // verified. This avoids a re-merge (and the associated
                // backup write + driver activation) on every trial when
                // nothing has actually changed in the cell's verified
                // status. When a cell DOES go green, we merge
                // immediately so the user feels the cumulative effect in
                // real life.
                if ((trainingStateChanged || factorChanged) &&
                    (DesktopSensProfileMerger.IsPartiallyReady(_profile) ||
                     DesktopSensProfileMerger.IsReady(_profile)))
                {
                    // Auto-merge: do NOT pop a MessageBox — that would
                    // interrupt the user after every successful trial.
                    // The merge result is logged to DesktopSensLog so
                    // the user can verify via "Show Diag Log".
                    TryMergeAndApply(false);
                    LogSampleFitApplyDiagnostics();
                }
            }
            catch (Exception ex)
            {
                DesktopSensLog.Warn("Auto-merge after trial failed (non-fatal)", ex);
            }
            calibrateMergeApplyTimer.Stop();
            DesktopSensLog.Info("Train Calibrate+Merge+Apply elapsedMs=" +
                calibrateMergeApplyTimer.Elapsed.TotalMilliseconds.ToString("0.0") +
                " trialCount=" + _profile.Trials.Count);
        }

        private void LogSampleFitApplyDiagnostics()
        {
            if (_profile == null) return;
            if (_profile.LastBuiltLutPointsByAngleZone != null)
            {
                foreach (var entry in _profile.LastBuiltLutPointsByAngleZone)
                {
                    if (entry.Value == null) continue;
                    var x = new List<string>();
                    var y = new List<string>();
                    foreach (LutPoint point in entry.Value)
                    {
                        x.Add(point.InputSpeedCmPerSec.ToString("0.00"));
                        y.Add(point.Factor.ToString("0.000"));
                    }
                    DesktopSensLog.Info("SampleLut Apply axisZone=" + entry.Key +
                        " nodeXcmPerSec=[" + string.Join(",", x) + "] nodeY=[" +
                        string.Join(",", y) + "]");
                }
            }
            object activeProfile = null;
            try { activeProfile = _activeProfileProvider != null ? _activeProfileProvider() : null; }
            catch (Exception ex) { DesktopSensLog.Warn("Could not read active RawAccel profile for smoothing diagnostics", ex); }
            DesktopSensLog.Info("RawAccel smoothing on Apply: input_speed_smooth_halflife=" +
                ReadProfileDiagnosticValue(activeProfile, "input_speed_smooth_halflife") +
                " scale_smooth_halflife=" + ReadProfileDiagnosticValue(activeProfile, "scale_smooth_halflife") +
                " output_speed_smooth_halflife=" + ReadProfileDiagnosticValue(activeProfile, "output_speed_smooth_halflife"));
        }

        private static string ReadProfileDiagnosticValue(object profile, string name)
        {
            if (profile == null) return "n/a";
            try
            {
                var type = profile.GetType();
                var field = type.GetField(name, System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (field != null)
                {
                    object value = field.GetValue(profile);
                    return value != null ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : "null";
                }
                var property = type.GetProperty(name, System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (property != null)
                {
                    object value = property.GetValue(profile, null);
                    return value != null ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : "null";
                }
            }
            catch { }
            return "n/a";
        }

        /// <summary>
        /// RESET ALL TRAINING — user's directive.
        ///
        /// The user explained: "ye gozineh bayad ezafeh beshe ke
        ///  hameye trainha reset beshe too menu asli" — an option
        ///  should be added that resets all trainings in the main
        ///  menu.
        ///
        /// This clears:
        ///   • The per-cell training-state map (every cell goes back to
        ///     "white / not started" — no yellow, no green).
        ///   • The per-cell factor arrays on the profile
        ///     (CellSensitivityFactor, CellAccelerationFactor,
        ///     CellRotationOffsetDegrees, all curve arrays).
        ///   • The legacy one-dimensional SensitivityFactor and
        ///     AccelerationFactor summaries.
        ///   • The trial list (profile.Trials) — the trials themselves
        ///     are removed, because the calibrator derives everything
        ///     from the trial set and would otherwise immediately
        ///     re-populate the per-cell factors from the same trials.
        ///   • AllCardinalCellsVerified flag.
        ///
        /// Asks for confirmation before doing anything, because this is
        /// destructive and cannot be undone (the in-memory profile is
        /// mutated; if the user has not saved since the last verified
        /// cell, those verifications will be lost).
        ///
        /// After the reset, the trainer returns to the SetStartPoint
        /// state and the user must press 1/2/3/4 or click on a marker
        /// to start a fresh calibration.
        /// </summary>
        private void ResetAllTraining()
        {
            // Confirm before destroying potentially hours of work.
            DialogResult result = MessageBox.Show(
                this,
                "Reset ALL training state?\n\n" +
                "This will:\n" +
                "  • Clear every cell's yellow/green status\n" +
                "  • Wipe the per-cell factor arrays\n" +
                "  • Delete all recorded trials\n" +
                "  • Clear the previous session and trainer-only overrides\n" +
                "  • Reset sequential override and MicroBoost toggle\n" +
                "  • Set AllCardinalCellsVerified back to false\n\n" +
                "Mouse/device identity settings such as DPI are preserved.\n" +
                "This CANNOT be undone. Make a backup if you need this data.\n\n" +
                "Proceed?",
                "Reset All Training",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes) return;

            if (_profile == null) return;

            _cooldownTimer?.Stop();
            _trialStopwatch.Reset();
            _isTrainingMode = true;
            // FIX 4: also reset the overlay-visibility flag so the user
            // sees the markers again after a reset (in case they had
            // hidden them with T before the reset).
            _showTrainingMarkers = true;
            if (_arena3D != null && !_arena3D.IsDisposed)
                _arena3D.ShowTrainingMarkers = true;

            _isInNoiseFloorEstimation = false;
            _noiseFloorErrors.Clear();
            _isInABTestMode = false;
            _abPlan = null;
            _abPairIndex = -1;
            _abTestErrors.Clear();
            _abTestCurrentFlick = 0;
            // Start a genuinely separate training session and clear the
            // trainer-only overrides so the next person does not inherit
            // the previous operator's workflow preferences. Hardware
            // identity/settings (for example mouse DPI) are deliberately
            // preserved because they describe the device, not the user.
            _profile.CurrentSessionId = Guid.NewGuid().ToString("N");
            _profile.SessionStartedAtUtc = DateTime.UtcNow;
            _profile.AllowSequentialOverride = false;
            _profile.MicroBoostEnabled = false;

            // 1. Clear the per-cell training-state map.
            if (_profile.CellTrainingStates != null)
                _profile.CellTrainingStates.Clear();
            else
                _profile.CellTrainingStates = new Dictionary<int, TrainingCellState>();

            // Frozen sample-fit LUT node ownership is training state too.
            // A full reset must clear it alongside cell verification state.
            if (_profile.FrozenSampleLutNodeFactorsByAngleZone != null)
                _profile.FrozenSampleLutNodeFactorsByAngleZone.Clear();
            if (_profile.LastBuiltLutPointsByAngleZone != null)
                _profile.LastBuiltLutPointsByAngleZone.Clear();
            if (_profile.LastBuiltLutBaseFactorByAngleZone != null)
                _profile.LastBuiltLutBaseFactorByAngleZone.Clear();

            // 2. Clear the trial list. The calibrator derives everything
            // from this list, so if we left the trials in place the
            // per-cell factors would reappear on the next Calibrate()
            // call. Clearing the trials makes the reset real.
            if (_profile.Trials != null)
                _profile.Trials.Clear();
            else
                _profile.Trials = new List<DesktopSensTrial>();

            // 3. Reset the per-cell factor arrays to their defaults
            //    (1.0 for factors, 0.0 for confidences, NoAccel for
            //    curve modes). We do this by re-running the calibrator
            //    on the now-empty trial set — Calibrate() resets every
            //    array to the default values when there are no trials.
            try
            {
                DesktopSensCalibrator.Calibrate(_profile);
            }
            catch (Exception ex)
            {
                DesktopSensLog.Error("Calibration recompute failed after resetting training", ex);
            }

            // Sensitivity Finder directional factors are independent and are
            // intentionally preserved by a Train-only reset.

            // 5. Reset the merge-applied flag and the cells-stable
            //    counters so the HUD reflects the reset state.
            _profile.AllCardinalCellsVerified = false;
            _profile.LastMergeAppliedTimestampUtc = null;
            _profile.LastABTestResult = null;
            _profile.NoiseFloorCm = 0.0;
            _profile.NoiseFloorSampleCount = 0;
            _profile.NoiseFloorEstimatedAtUtc = null;
            _profile.CellsStable = 0;
            _profile.CellsLowConfidence = 0;
            int distanceZoneCount = TrainingGeometry.Current.DistanceZoneCount;
            _profile.CellsInsufficientData =
                DesktopSensConstants.AngularZoneCount * distanceZoneCount;
            _profile.OverallCoverage = 0.0;
            _profile.OverallConfidence = 0.0;
            _profile.ModifiedTimestamp = DateTime.UtcNow;

            // 6. Clear the trainer's own selection state and hide the
            //    target marker so the user must pick a fresh point to
            //    start the next calibration.
            _selectedAxisIndex = -1;
            _selectedDistanceZone = -1;
            _selectedDistanceCm = 0.0;
            _rawSamples.Clear();
            _state = TrialState.SetStartPoint;
            _startPos = new PointF(ClientSize.Width / 2f, ClientSize.Height / 2f);
            _currentCursorPos = _startPos;
            _cameraYawDegrees = _calibrationYawDegrees;
            _cameraPitchDegrees = CalibrationPitchDegrees;
            if (_arena3D != null && !_arena3D.IsDisposed)
            {
                _arena3D.ShowTarget = false;
                _arena3D.FieldOfViewDegrees = 68f;
            }

            // 7. Reset the trainer's per-zone trial counters (used by
            //    the existing HUD's coverage display).
            if (_distanceZoneCounts != null)
            {
                for (int i = 0; i < _distanceZoneCounts.Length; i++)
                    _distanceZoneCounts[i] = 0;
            }
            if (_angleZoneCounts != null)
            {
                for (int i = 0; i < _angleZoneCounts.Length; i++)
                    _angleZoneCounts[i] = 0;
            }

            // 8. Refresh the arena and HUD so the user sees the reset
            //    take effect immediately.
            UpdateTrainingArenaProperties();
            UpdateTrainingHud();

            OnCellVerifiedPersistProfile();

            // Play a confirmation sound so the user knows it happened
            // even if they weren't looking at the HUD.
            System.Media.SystemSounds.Asterisk.Play();
        }

        /// <summary>
        /// Persists the shared profile when a cell changes verification state,
        /// a session re-verifies it, or the user resets/rolls back training.
        /// </summary>
        private void OnCellVerifiedPersistProfile()
        {
            try
            {
                if (_persistProfile != null)
                    _persistProfile();
            }
            catch (Exception ex)
            {
                DesktopSensLog.Error("Profile persistence failed", ex);
            }
        }

        /// <summary>
        /// Triggers the merge-and-apply step. No-op if no cells are
        /// verified or if no applier was injected. The applier decides
        /// whether to write a LUT, a parameter set, or both — it is
        /// the bridge to the existing ApplyDirectionalProfile /
        /// ComputeWeightedRotation logic.
        ///
        /// GRADUAL MERGE: this method now accepts BOTH partial state
        /// (at least one cell verified) and full state (all cells
        /// verified). The user explicitly asked that each verified
        /// cell be applied to the driver the instant it goes green.
        /// Auto-merge (called from RecordTrainingAttempt) does NOT
        /// show a modal MessageBox — that would interrupt the user
        /// every trial. Manual merge (called from M key) DOES show a
        /// MessageBox so the user gets confirmation that they pressed
        /// it.
        /// </summary>
        private void TryMergeAndApply() { TryMergeAndApply(true); }

        private void TryMergeAndApply(bool showSuccessMessage)
        {
            if (_cardinalApplier == null)
            {
                if (showSuccessMessage)
                    DesktopSensToast.Show(this,
                        "No RawAccel apply adapter is connected. Calibration can continue, but merge/apply is unavailable.");
                return;
            }

            if (!WriteBackupBeforeMerge())
            {
                if (showSuccessMessage)
                    MessageBox.Show(this,
                        "The profile backup could not be written. Merge/apply was cancelled to avoid an unsafe overwrite.",
                        "Backup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string failureReason;
            bool ok = DesktopSensProfileMerger.MergeAndApply(_profile, _cardinalApplier, out failureReason);
            if (ok)
            {
                OnCellVerifiedPersistProfile();
                System.Media.SystemSounds.Asterisk.Play();
                int mergeDirectionCount = DesktopSensTrainingState.ResolveDirectionCount(_profile);
                int mergeVerified = DesktopSensTrainingState.CountVerifiedCellsForCount(
                    _profile.CellTrainingStates, mergeDirectionCount);
                int mergeTotal = DesktopSensTrainingState.TotalCardinalCellsForCount(mergeDirectionCount);
                bool isFullMerge = mergeVerified >= mergeTotal;
                // DIAGNOSTIC: always log the merge result (so Show Diag
                // Log reveals what happened, even when no MessageBox is
                // shown).
                DesktopSensLog.Warn(
                    "Merge " + (isFullMerge ? "FINAL" : "PARTIAL") + ": " +
                    mergeVerified + "/" + mergeTotal + " cells verified, " +
                    "applied to driver. " +
                    "R=" + (_profile.LastAppliedMergedProfile != null ? _profile.LastAppliedMergedProfile.Right.BaseSensitivityFactor : 1.0).ToString("0.000") +
                    " D=" + (_profile.LastAppliedMergedProfile != null ? _profile.LastAppliedMergedProfile.Down.BaseSensitivityFactor : 1.0).ToString("0.000") +
                    " L=" + (_profile.LastAppliedMergedProfile != null ? _profile.LastAppliedMergedProfile.Left.BaseSensitivityFactor : 1.0).ToString("0.000") +
                    " U=" + (_profile.LastAppliedMergedProfile != null ? _profile.LastAppliedMergedProfile.Up.BaseSensitivityFactor : 1.0).ToString("0.000"));
                if (showSuccessMessage)
                {
                    MessageBox.Show(
                        this,
                        (isFullMerge ? "FINAL merge complete — all cells verified.\n" :
                                       "Partial merge — " + mergeVerified + "/" + mergeTotal + " cells verified so far.\n") +
                        "Directional base profile and compatible speed-LUT calibration activated in RawAccel. RawAccel has one shared LUT per axis, so positive/negative curves must match; conflicting curves are rejected rather than blended.\n" +
                        "Verified cells: " + mergeVerified + "/" + mergeTotal,
                        isFullMerge ? "Cardinal Training Complete" : "Partial Calibration Applied",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            else
            {
                string reason = failureReason ?? "unknown reason";
                if (showSuccessMessage)
                {
                    MessageBox.Show(
                        this,
                        reason,
                        "Merge / Apply Failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                else
                {
                    // NEW (Model v6): auto-merge failures must be visible, not only logged.
                    // A silent failure is what made "only the first zone works"
                    // impossible to diagnose from the trainer UI.
                    DesktopSensLog.Warn("Auto-merge failed: " + reason);
                    try
                    {
                        DesktopSensToast.Show(this, "Auto-merge failed: " + reason);
                    }
                    catch (Exception ex)
                    {
                        DesktopSensLog.Warn("Could not show auto-merge failure toast", ex);
                    }
                }
            }
            UpdateTrainingHud();
        }



















    }
}

// ============================================================================
// PATCHED: CurvePanel — a small custom WinForms control that draws the
// fitted acceleration curve for the currently selected cell. Called from
// the trainer's UpdateCalibFactorHud so the curve updates in real time.
// ============================================================================
namespace grapher.DesktopSens.Views
{
    using System;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Windows.Forms;
    using grapher.DesktopSens.Models;

    internal sealed class CurvePanel : Panel
    {
        private AccelCurveMode _mode = AccelCurveMode.NoAccel;
        private double[] _parameters = new double[0];
        private int _sampleCount = 0;
        private double _rmse = 0.0;
        private double _maxSpeed = 100.0;       // cm/s (x-axis upper bound)
        private double _maxFactor = 2.0;        // y-axis upper bound
        private string _cellLabel = "—";

        public CurvePanel()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(28, 30, 38);
            ForeColor = Color.Gainsboro;
            BorderStyle = BorderStyle.FixedSingle;
            Font = new Font("Consolas", 8f, FontStyle.Regular);
            Width = 320;
            Height = 200;
        }

        public void SetCell(string label, AccelCurveMode mode, double[] parameters,
                            int sampleCount, double rmse)
        {
            _cellLabel = label;
            _mode = mode;
            _parameters = parameters ?? new double[0];
            _sampleCount = sampleCount;
            _rmse = rmse;
            _maxSpeed = 100.0;
            _maxFactor = 2.0;
            try
            {
                double maxF = 1.0;
                for (double v = 0.0; v <= _maxSpeed; v += 1.0)
                {
                    double f = AccelCurveFitter.Evaluate(_mode, _parameters, v);
                    if (f > maxF) maxF = f;
                }
                _maxFactor = Math.Max(1.1, Math.Ceiling(maxF * 1.2));
            }
            catch { }
            Invalidate();
        }

        public void Clear()
        {
            _mode = AccelCurveMode.NoAccel;
            _parameters = new double[0];
            _sampleCount = 0;
            _rmse = 0.0;
            _cellLabel = "—";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = ClientRectangle;

            using (var bg = new SolidBrush(Color.FromArgb(28, 30, 38)))
                g.FillRectangle(bg, r);

            int pad = 32;
            Rectangle plot = new Rectangle(
                pad, pad + 6, r.Width - pad - 8, r.Height - pad - 6 - pad);

            string title = "CURVE: " + _cellLabel + "  [" + _mode + "  n=" + _sampleCount + "]";
            using (var titleBrush = new SolidBrush(Color.LightSkyBlue))
            using (var titleFont = new Font("Consolas", 9f, FontStyle.Bold))
                g.DrawString(title, titleFont, titleBrush, 4, 4);

            using (var framePen = new Pen(Color.FromArgb(80, 80, 90)))
                g.DrawRectangle(framePen, plot);

            using (var gridPen = new Pen(Color.FromArgb(45, 45, 55)))
            using (var labelBrush = new SolidBrush(Color.DimGray))
            using (var labelFont = new Font("Consolas", 7f))
            {
                for (double f = 0.0; f <= _maxFactor; f += 0.5)
                {
                    int y = plot.Bottom - (int)(f / _maxFactor * plot.Height);
                    if (y < plot.Top || y > plot.Bottom) continue;
                    g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                    g.DrawString(f.ToString("F1"), labelFont, labelBrush, 2, y - 6);
                }
                for (int v = 0; v <= 100; v += 20)
                {
                    int x = plot.Left + (int)((double)v / _maxSpeed * plot.Width);
                    if (x < plot.Left || x > plot.Right) continue;
                    g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
                    g.DrawString(v.ToString(), labelFont, labelBrush, x - 8, plot.Bottom + 2);
                }
            }

            using (var basePen = new Pen(Color.FromArgb(90, 90, 100), 1f) { DashStyle = DashStyle.Dot })
            {
                int y1 = plot.Bottom - (int)(1.0 / _maxFactor * plot.Height);
                g.DrawLine(basePen, plot.Left, y1, plot.Right, y1);
            }

            if (_mode == AccelCurveMode.NoAccel || _sampleCount == 0)
            {
                using (var ndBrush = new SolidBrush(Color.DimGray))
                using (var ndFont = new Font("Consolas", 10f, FontStyle.Bold))
                {
                    string msg = _sampleCount == 0 ? "NO DATA (flick this cell)" : "NoAccel";
                    var sz = g.MeasureString(msg, ndFont);
                    g.DrawString(msg, ndFont, ndBrush,
                        plot.Left + (plot.Width - sz.Width) / 2,
                        plot.Top + (plot.Height - sz.Height) / 2);
                }
                return;
            }

            using (var curvePen = new Pen(Color.LightGreen, 2f))
            {
                Point? prev = null;
                for (int x = plot.Left; x <= plot.Right; x++)
                {
                    double v = (double)(x - plot.Left) / plot.Width * _maxSpeed;
                    double f = AccelCurveFitter.Evaluate(_mode, _parameters, v);
                    if (f < 0.0) f = 0.0;
                    if (f > _maxFactor) f = _maxFactor;
                    int y = plot.Bottom - (int)(f / _maxFactor * plot.Height);
                    var p = new Point(x, y);
                    if (prev.HasValue)
                        g.DrawLine(curvePen, prev.Value, p);
                    prev = p;
                }
            }

            using (var rmseBrush = new SolidBrush(Color.Khaki))
            using (var rmseFont = new Font("Consolas", 7f))
                g.DrawString("RMSE " + _rmse.ToString("F4"), rmseFont, rmseBrush,
                    plot.Right - 80, 2);
        }
    }
}
