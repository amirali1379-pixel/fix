using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using grapher.Models.Mouse;
using grapher.DesktopSens.Models;
using OpenTK;

namespace grapher.DesktopSens.Views
{
    public partial class DesktopSensTrainerForm : Form
    {
        private readonly DesktopSensProfile _profile;
        private readonly Action _persistProfile;
        private readonly List<MouseSample> _rawSamples = new List<MouseSample>();
        private readonly Stopwatch _trialStopwatch = new Stopwatch();
        private double _flickStartMs = -1;
        private double _flickLastMoveMs = -1;
        private readonly Timer _cooldownTimer;
        private readonly Timer _renderTimer;
        private readonly DesktopSensArena3DControl _arena3D;
        private readonly Label _hud;
        private readonly Label[] _degreeLabels = new Label[36];
        private readonly Label _selectedAngleLabel;

        private enum TrialState { SetStartPoint, SetTargetPoint, Cooldown, Flicking, ASDWAdjusting, TrialComplete }
        private TrialState _state = TrialState.SetStartPoint;

        private PointF _startPos;
        private PointF _targetPos;
        private PointF _currentCursorPos;
        private PointF _rawLandedPos;

        private double _targetYawDegrees;
        private double _targetPitchDegrees;
        private double _targetAngleDegrees;
        private double _landedAngleDegrees;
        private double _landedYawDegrees;
        private double _landedPitchDegrees;
        private double _targetDistanceCm;
        private readonly double _maxDistanceCm;
        private readonly float _pixelsPerCm;
        private readonly int[] _angleZoneCounts = new int[DesktopSensConstants.AngularZoneCount];
        // Initial size uses the legacy default; the constructor resizes
        // this array after TrainingGeometry is configured so it always
        // matches TrainingGeometry.Current.DistanceZoneCount.
        private int[] _distanceZoneCounts = new int[DesktopSensConstants.DefaultDistanceZoneCount];

        private float _cameraYawDegrees;
        private float _calibrationYawDegrees;
        private const float CalibrationPitchDegrees = 0f;
        // FIX (ASDW fine-tuning): save the camera position at the moment
        // the user clicks to land (state Flicking → ASDWAdjusting). This
        // is the "raw landing" before any ASDW nudge. In FinishTrial we
        // compute the ASDW nudge as the delta between the current camera
        // position (which includes ASDW) and this saved position, and
        // add it to the flick distance so the calibration sees the
        // fine-tuned result.
        private float _rawLandedCameraYaw;
        private float _rawLandedCameraPitch;
        private Vector3 _calibrationOrigin;
        private Vector3 _calibrationForward = new Vector3(0f, 0f, -1f);
        private Vector3 _calibrationRight = Vector3.UnitX;
        private Vector3 _calibrationUp = Vector3.UnitY;
        private const float CalibrationPlaneDistance = 1.5f;
        private float _cameraPitchDegrees = 0f;
        // PATCHED: was ±89f / 89.0 — capped Up/Down cardinal axes at ±89°,
        // which caused "ASDW bishtar az 90 darajeh tekoon nemikhoreh".
        // Now ±180 so the full hemisphere is reachable on every axis.
        private const float MinPitch = -180f;
        private const float MaxPitch = 180f;
        private const double MaxPitchDegrees = 180.0;
        private double _activeRotationCorrectionDegrees = 0.0;

        // ====================================================================
        // ASDW HOLD-TO-ACCELERATE (user directive:
        //   "ba har ye d 3 pixel mire jolo daghigh nist ... bayad bazeh
        //    multiply 2 sanieh bashe va roo oon sorate nahai bemooneh
        //    bade 2 sanieh")
        //
        // The previous exponential formula (2^tickCount) reached max speed
        // in ~250 ms, which felt too jerky. We now use LINEAR growth:
        //
        //   • Initial press:    step = 1 px (precise placement)
        //   • 0–200 ms held:    no auto-repeat (precise placement window)
        //   • 200–2000 ms held: step grows LINEARLY from 1 px to 10 px
        //   • After 2000 ms:    step stays at 10 px (final speed)
        //
        // Also: Windows sends repeated OnKeyDown events while the key is
        // held. We detect this in OnKeyDown (key already in _asdwHeldKey)
        // and skip those repeats — otherwise the user would see ~3 px per
        // "press" because Windows auto-repeat fires 3+ times per second.
        //
        // Internally the camera stores degrees, not pixels, so we convert:
        //   degreesPerPixel = MaxAxisDegrees / arenaRadiusPx
        // (arenaRadiusPx is the on-screen radius that spans the full
        // MaxAxisDegrees arc).
        // ====================================================================
        private Timer _asdwHoldTimer;
        private Keys _asdwHeldKey = Keys.None;
        private long _asdwHoldStartTicks;  // Stopwatch.GetTimestamp() at press
        private const double AsdwInitialStepPixels = 1.0;
        private const double AsdwMaxStepPixels = 10.0;
        private const int AsdwAccelerationDelayMs = 200;     // hold this long before accelerating
        private const int AsdwAccelerationRampMs = 2000;     // ramp from 1 px to 10 px over 2 s
        private const int AsdwAccelerationIntervalMs = 16;    // timer tick rate

        private bool _cursorHidden;

        // ─────────────────────────────────────────────────────────────────────────
        // Sensitivity Finder mode.
        //
        // When enabled, the trainer auto-places targets at random R/L/U/D
        // directions with random distances in the 0-3 cm micro zone. The user
        // just flicks and clicks — the trainer handles target placement, trial
        // advancement, and auto-exit when each direction has enough samples.
        //
        // This mode is designed as a pre-training step: collect a clean batch
        // of micro-zone trials per direction so DirectionalCalibrator can
        // produce reliable 4-way base-sensitivity factors before the user
        // starts the full mixed-distance training.
        // ─────────────────────────────────────────────────────────────────────────
        private const double SensitivityFinderMinCm = 0.0;
        private const double SensitivityFinderMaxCm = 2.0;
        private bool _sensitivityFinderMode;
        private int _sensitivityFinderTrialsPerDirection = 5;
        private readonly int[] _sensitivityFinderDirectionCounts = new int[4];
        private readonly Random _rng = new Random();

        private void HideTrainerCursor()
        {
            if (_cursorHidden) return;
            Cursor.Hide();
            _cursorHidden = true;
        }

        private void RestoreTrainerCursor()
        {
            if (!_cursorHidden) return;
            Cursor.Show();
            _cursorHidden = false;
        }

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        public DesktopSensTrainerForm(DesktopSensProfile profile) : this(profile, false, 40, null, null)
        {
        }

        /// <summary>
        /// Construct the trainer. Pass <paramref name="sensitivityFinderMode"/> = true
        /// to run the pre-training sensitivity-finder flow in the 0-2 cm range.
        /// The trainer collects trials across R/L/U/D until each direction has
        /// <paramref name="trialsPerDirection"/> samples, then auto-closes.
        /// </summary>
        public DesktopSensTrainerForm(DesktopSensProfile profile, bool sensitivityFinderMode, int trialsPerDirection)
            : this(profile, sensitivityFinderMode, trialsPerDirection, null, null)
        {
        }

        public DesktopSensTrainerForm(
            DesktopSensProfile profile,
            bool sensitivityFinderMode,
            int trialsPerDirection,
            ICardinalProfileApplier cardinalProfileApplier,
            Action persistProfile,
            Func<object> activeProfileProvider = null)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _persistProfile = persistProfile;
            if (_profile.MouseDPI < DesktopSensConstants.MinMouseDPI ||
                _profile.MouseDPI > DesktopSensConstants.MaxMouseDPI)
                throw new ArgumentOutOfRangeException(nameof(profile), "Mouse DPI is outside the supported range.");

            _sensitivityFinderMode = sensitivityFinderMode;
            if (trialsPerDirection < 1) trialsPerDirection = 1;
            if (trialsPerDirection > 50) trialsPerDirection = 50;
            _sensitivityFinderTrialsPerDirection = trialsPerDirection;

            _maxDistanceCm = ZoneCalculations.MaxDistanceCmForTurnDegrees(
                _profile.EffectiveCpi, _profile.UserInputGameSensitivity);
            _targetDistanceCm = 0.0;
            _pixelsPerCm = (float)(Math.Min(Screen.PrimaryScreen.Bounds.Width, Screen.PrimaryScreen.Bounds.Height) /
                (2.0 * _maxDistanceCm));

            // ───────────────────────────────────────────────────────────────
            // Push the trainer's geometry into the global TrainingGeometry
            // singleton so every downstream component (calibrator, merger,
            // profile arrays, arena, training-state map) reads the SAME
            // step / max-axis / max-pitch / max-distance values. The trainer
            // is the central authority — no parallel definitions allowed.
            //
            // Geometry defaults live in the trainer, while the marker step is
            // persisted on DesktopSensProfile and owned by TrainingGeometry.
            // ───────────────────────────────────────────────────────────────
            double markerStepDegrees = _profile.TrainingMarkerStepDegrees;
            if (double.IsNaN(markerStepDegrees) || double.IsInfinity(markerStepDegrees) ||
                markerStepDegrees < 1.0 || markerStepDegrees > 45.0)
                markerStepDegrees = 10.0;
            _profile.TrainingMarkerStepDegrees = markerStepDegrees;
            TrainingGeometry.Configure(
                markerStepDegrees,
                TrainingMaxAxisDegrees,
                MaxPitchDegrees,
                _maxDistanceCm);

            // The _distanceZoneCounts field was sized at field-initializer
            // time (before the constructor body ran) using the legacy
            // default count. Resize it now to match TrainingGeometry so
            // the trial-recording path doesn't index out of bounds when
            // the user has changed the step.
            int currentDistanceZoneCount = TrainingGeometry.Current.DistanceZoneCount;
            if (_distanceZoneCounts.Length != currentDistanceZoneCount)
            {
                var resized = new int[currentDistanceZoneCount];
                if (_distanceZoneCounts != null)
                {
                    int copy = Math.Min(_distanceZoneCounts.Length, currentDistanceZoneCount);
                    for (int i = 0; i < copy; i++) resized[i] = _distanceZoneCounts[i];
                }
                _distanceZoneCounts = resized;
            }

            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            BackColor = Color.FromArgb(24, 26, 29);
            DoubleBuffered = true;
            KeyPreview = true;
            _arena3D = new DesktopSensArena3DControl { Dock = DockStyle.Fill, Radius = (float)_maxDistanceCm };
            Controls.Add(_arena3D);
            _arena3D.SendToBack();

            _hud = new Label
            {
                AutoSize = true,
                Location = new Point(20, 20),
                Padding = new Padding(10),
                BackColor = Color.FromArgb(220, 12, 16, 22),
                ForeColor = Color.Gainsboro,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold)
            };
            ForwardMouseInput(_hud);
            Controls.Add(_hud);

            for (int i = 0; i < _degreeLabels.Length; i++)
            {
                int degree = i * 10;
                var label = new Label
                {
                    AutoSize = false,
                    Size = new Size(42, 20),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Text = degree + "°",
                    Font = new Font("Segoe UI", degree % 30 == 0 ? 9f : 8f,
                        degree % 30 == 0 ? FontStyle.Bold : FontStyle.Regular),
                    ForeColor = degree == 0 ? Color.LightGreen :
                        degree % 30 == 0 ? Color.Khaki : Color.Gainsboro,
                    BackColor = Color.FromArgb(190, 12, 16, 22),
                    TabStop = false
                };
                ForwardMouseInput(label);
                _degreeLabels[i] = label;
                Controls.Add(label);
            }

            _selectedAngleLabel = new Label
            {
                AutoSize = true,
                Padding = new Padding(6, 3, 6, 3),
                BackColor = Color.FromArgb(225, 90, 42, 12),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Text = "TARGET: —"
            };
            ForwardMouseInput(_selectedAngleLabel);
            Controls.Add(_selectedAngleLabel);

            _hud.BringToFront();
            // MouseDown is wired after InitializeTrainingMode below so
            // the cardinal marker hit-test handler gets first refusal.
            // If the generic state-machine handler runs first, it changes
            // SetTargetPoint to Cooldown before marker selection can inspect
            // the click, making the marker handler appear to do nothing.
            _arena3D.MouseWheel += (s, e) => HandleMouseWheel(e);

            // Keep the OS cursor available while the user is choosing the
            // next target. It is hidden/recentered only after a target has
            // been placed and the timed flick phase begins.

            _startPos = new PointF(ClientSize.Width / 2f, ClientSize.Height / 2f);
            _currentCursorPos = _startPos;
            UpdateArena3D();

            // In Sensitivity Finder mode the targets are short (0.5-3 cm) and the
            // user flicks quickly, so a 3-second cooldown between trials is
            // overkill — it makes 40-trial-per-direction sessions take 8+ minutes.
            // Use 1.5 s instead. In normal training the player needs the full 3 s
            // to reset their grip for longer flicks, so we keep the long cooldown
            // there.
            int cooldownMs = _sensitivityFinderMode ? 1500 : 3000;
            _cooldownTimer = new Timer { Interval = cooldownMs };
            _cooldownTimer.Tick += OnCooldownFinished;

            _renderTimer = new Timer { Interval = 16 };
            _renderTimer.Tick += (s, e) => { RecenterCursorIfNeeded(); UpdateArena3D(); };
            _renderTimer.Start();

            FormClosed += (s, e) =>
            {
                _cooldownTimer.Stop();
                _cooldownTimer.Dispose();
                _renderTimer.Stop();
                _renderTimer.Dispose();
                RestoreTrainerCursor();
            };

            // Sensitivity Finder mode: auto-start by simulating the
            // SetStartPoint click so the user immediately enters the
            // SetTargetPoint state — ready to aim and click to place
            // their first target.
            //
            // WHY SHOWN, NOT BeginInvoke:
            //   The previous implementation called
            //       BeginInvoke((Action)(() => { ... }))
            //   directly inside the constructor. BeginInvoke requires
            //   the form's window handle to already exist. At constructor
            //   time the form has NOT been shown yet (ShowDialog is
            //   called by the parent AFTER `new DesktopSensTrainerForm`
            //   returns), so the handle is still null. The result was
            //   the unhandled-exception dialog the user saw:
            //
            //     "Invoke or BeginInvoke cannot be called on a control
            //      until the window handle has been created."
            //
            //   Hooking the Shown event instead fixes this: Shown fires
            //   after the form has been fully displayed (handle created,
            //   layout done), so the call is safe.
            //
            // USER'S DIRECTIVE (Persian, paraphrased):
            //   "marhale aval ke bayad noghteh ro moshakhas konam ro
            //    nadareh mostaghim bade start varede calibrate mishe"
            //   — the first stage where the user should specify the
            //   target point is missing; it goes directly to calibration
            //   after start.
            //
            //   So in Sens Finder mode we NO LONGER auto-place a random
            //   target. The user manually aims and clicks to specify
            //   each target, exactly like in the regular trainer. Sens
            //   Finder now just keeps the auto-start (so the user
            //   doesn't have to click for the start point) and the
            //   auto-close-when-complete behaviour.
            if (_sensitivityFinderMode)
            {
                Shown += (sender, args) =>
                {
                    SimulateStartPointClick();
                    // The user will click to specify their own target, via
                    // the SetTargetPoint branch in OnMouseDown. (The old
                    // auto-place method PlaceSensitivityFinderTarget was
                    // removed — the user's directive was "I want to
                    // specify the target myself, don't auto-place it".)
                };
            }

            // Initialize the cardinal-direction training mode with the
            // caller's real RawAccel bridge and profile persistence callback.
            //
            // The user's directive: Sens Finder must have its OWN
            // environment — separate from the cardinal trainer. The
            // cardinal-trainer UI (training markers overlay, training
            // HUD with verified-cell count, R/T/M/1-4 keys) must NOT
            // appear in Sens Finder mode. Sens Finder is a quick
            // micro-zone collector that produces four base-sensitivity
            // values; it has nothing to do with the per-cell acceleration
            // training.
            //
            // We therefore disable _isTrainingMode when entering Sens
            // Finder mode. InitializeTrainingMode checks this flag
            // and skips the cardinal-trainer-specific setup (HUD,
            // pulse timer, marker click handler) when it's false.
            // The shared setup (geometry push, render timer) still
            // runs because the calibrator needs TrainingGeometry to be
            // configured even in Sens Finder mode.
            _isTrainingMode = !_sensitivityFinderMode;
            InitializeTrainingMode(cardinalProfileApplier, activeProfileProvider);
            // Keep this after InitializeTrainingMode: in cardinal mode the
            // marker-selection MouseDown handler is registered there first,
            // and must run before the generic trainer state machine.
            _arena3D.MouseDown += (s, e) => OnMouseDown(e);
            // Sensitivity Finder is deliberately isolated from cardinal-trainer UI.
            if (!_sensitivityFinderMode)
                InitializeFixes();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                RestoreTrainerCursor();
            }
            base.Dispose(disposing);
        }

        private void ForwardMouseInput(Control target)
        {
            target.MouseDown += (s, e) => OnMouseDown(e);
            target.MouseWheel += (s, e) => HandleMouseWheel(e);
        }

        private void HandleMouseWheel(MouseEventArgs e)
        {
            if (_arena3D == null || _arena3D.IsDisposed || e.Delta == 0) return;

            // Mouse wheel controls camera zoom in the training arena.
            // Wheel up zooms in (smaller FOV); wheel down zooms out (larger FOV).
            const float fovStepDegrees = 2f;
            const float minFovDegrees = 30f;
            const float maxFovDegrees = 100f;
            float currentFov = _arena3D.FieldOfViewDegrees;
            float nextFov = currentFov - Math.Sign(e.Delta) * fovStepDegrees;
            _arena3D.FieldOfViewDegrees = Math.Max(minFovDegrees, Math.Min(maxFovDegrees, nextFov));
            _arena3D.Invalidate();
        }

        private void RecenterCursorIfNeeded()
        {
            // The trainer only owns the OS cursor while a target is active.
            // In SetStartPoint / SetTargetPoint the user must be able to move
            // and click normally; unconditional recentering made the mouse
            // appear frozen after Space even though the trial state had reset.
            if (!_cursorHidden ||
                (_state != TrialState.Cooldown &&
                 _state != TrialState.Flicking &&
                 _state != TrialState.ASDWAdjusting))
                return;
            if (!GetCursorPos(out POINT p)) return;
            Point center = PointToScreen(new Point(ClientSize.Width / 2, ClientSize.Height / 2));
            int dx = p.X - center.X;
            int dy = p.Y - center.Y;
            if (dx * dx + dy * dy > 100 * 100)
                SetCursorPos(center.X, center.Y);
        }

        /// <summary>
        /// Simulate the user clicking to set the start point. Used by Sensitivity
        /// Finder mode to skip the manual SetStartPoint step.
        /// </summary>
        private void SimulateStartPointClick()
        {
            _startPos = new PointF(ClientSize.Width / 2f, ClientSize.Height / 2f);
            CaptureCalibrationPlane();
            _cameraPitchDegrees = CalibrationPitchDegrees;
            _currentCursorPos = _startPos;
            _state = TrialState.SetTargetPoint;
        }

        /// <summary>
        /// Check whether all 4 directions have enough samples in Sensitivity
        /// Finder mode. If so, auto-close the trainer.
        /// </summary>
        private bool SensitivityFinderComplete()
        {
            if (!_sensitivityFinderMode) return false;
            for (int d = 0; d < 4; d++)
                if (_sensitivityFinderDirectionCounts[d] < _sensitivityFinderTrialsPerDirection)
                    return false;
            return true;
        }

        public void OnMouseSample(MouseSample sample)
        {
            if (_state == TrialState.Cooldown ||
                _state == TrialState.ASDWAdjusting ||
                _state == TrialState.TrialComplete)
                return;

            double cmPerCount = _profile.CmPerCount;
            float degreesPerCount = (float)(TrainingGeometry.Current.MaxAxisDegrees * cmPerCount / Math.Max(0.001, _maxDistanceCm));

            if (_state == TrialState.Flicking)
            {
                double corrRad = _activeRotationCorrectionDegrees * Math.PI / 180.0;
                double dx = sample.DX;
                double dy = sample.DY;
                if (Math.Abs(corrRad) > 1e-6)
                {
                    double c = Math.Cos(corrRad);
                    double s = Math.Sin(corrRad);
                    double dxR = dx * c - dy * s;
                    double dyR = dx * s + dy * c;
                    dx = dxR;
                    dy = dyR;
                }
                // Keep the live input path unmodified during calibration.
                // The hook is intentionally a no-op; chain effects are
                // reconstructed offline from the raw samples and the verified
                // factors frozen on the completed trial.
                if (_isTrainingMode) ApplyChainedCellCorrections(ref dx, ref dy);
                _cameraYawDegrees += (float)(dx * degreesPerCount);
                // PATCHED: removed the Math.Max(MinPitch, Math.Min(MaxPitch, ...))
                // clamp entirely. The user explicitly said "mahdoodiate calibrate
                // ASDW ro bardar kolan nakhastim" — remove the ASDW calibration
                // limit completely. There must be NO clamp on pitch (or yaw).
                // The previous ±89° (and later ±180°) caps were artificial limits
                // the user never asked for.
                _cameraPitchDegrees -= (float)(dy * degreesPerCount);
                AccumulateRawCursor((float)dx, (float)dy);
                if (sample.DX != 0 || sample.DY != 0)
                {
                    double nowMs = _trialStopwatch.Elapsed.TotalMilliseconds;
                    if (_flickStartMs < 0) _flickStartMs = nowMs;
                    _flickLastMoveMs = nowMs;
                }
                _rawSamples.Add(sample);
            }
            else
            {
                _cameraYawDegrees += (float)(sample.DX * degreesPerCount);
                // PATCHED: removed the MinPitch/MaxPitch clamp — see the
                // Flicking branch above for the rationale.
                _cameraPitchDegrees -= (float)(sample.DY * degreesPerCount);
            }
        }

        private void AccumulateRawCursor(float dx, float dy)
        {
            double scale = _pixelsPerCm * _profile.CmPerCount;
            _currentCursorPos.X += (float)(dx * scale);
            _currentCursorPos.Y += (float)(dy * scale);
            ClampCursorToCanvas();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            base.OnMouseDown(e);

            // In cardinal training, arena clicks are handled exclusively by
            // InitializeTrainingMode's marker hit-test handler (registered first).
            // Never let the generic free-aim state machine turn a missed marker
            // click into a start-point click or the misleading "click a marker"
            // loop. Sensitivity Finder keeps its own manual target-placement flow.
            if (_isTrainingMode &&
                (_state == TrialState.SetStartPoint ||
                 _state == TrialState.SetTargetPoint ||
                 _state == TrialState.TrialComplete))
                return;

            if (_state == TrialState.SetStartPoint)
            {
                _startPos = new PointF(ClientSize.Width / 2f, ClientSize.Height / 2f);
                CaptureCalibrationPlane();
                _cameraPitchDegrees = CalibrationPitchDegrees;
                _currentCursorPos = _startPos;
                _state = TrialState.SetTargetPoint;
            }
            else if (_state == TrialState.SetTargetPoint)
            {
                // FIX 3 (from AI review): in training mode, if no cell is
                // currently selected (which is the case after a reset or
                // before the user picks an axis), do NOT place a target
                // from free aim. The previous behaviour silently created
                // a trial that RecordTrainingAttempt then discarded —
                // because of the `if (_selectedAxisIndex < 0 || _selected-
                // DistanceZone < 0) return;` guard at the top of
                // RecordTrainingAttempt. The user saw a successful click,
                // a saved trial, and then no calibration feedback. Now
                // we surface a toast so the user knows they need to pick
                // a coloured marker first.
                if (_isTrainingMode &&
                    (_selectedAxisIndex < 0 || _selectedDistanceZone < 0))
                {
                    DesktopSensToast.Show(this,
                        "TRAINING: First select a coloured marker or press its axis key.");
                    return;
                }

                // Train targets are canonical marker cells. Free-aiming here
                // used to create a target at an arbitrary angle while retaining
                // the previously selected cell; RecordTrainingAttempt then
                // rejected the sample as a cardinal-axis mismatch. Marker clicks
                // and axis keys already call PlaceCardinalTrainingTarget().
                if (_isTrainingMode)
                {
                    DesktopSensToast.Show(this,
                        "TRAINING: Select the exact coloured marker (or press its axis key); free-aim targets are disabled.");
                    return;
                }

                // "What you aim is what you get." Marker is placed exactly at
                // the user's aim direction. In Sensitivity Finder mode this
                // branch IS reached because the user manually places their
                // own target via this same code path.
                double rawYaw = NormalizeSignedAngle(_cameraYawDegrees - _calibrationYawDegrees);
                double rawPitch = _cameraPitchDegrees - CalibrationPitchDegrees;
                double aimMagnitude = Math.Sqrt(rawYaw * rawYaw + rawPitch * rawPitch);
                double requestedDistanceCm = aimMagnitude /
                    Math.Max(0.001, TrainingGeometry.Current.MaxAxisDegrees) * _maxDistanceCm;

                // Use the exact requested physical distance in both Train and
                // Sensitivity Finder. Do not cap long-distance targets or impose
                // an artificial minimum distance on calibration.
                _targetDistanceCm = requestedDistanceCm;

                _targetYawDegrees = NormalizeAngle(rawYaw);
                _targetPitchDegrees = rawPitch;
                // PATCHED: removed the MaxPitchDegrees clamp on _targetPitchDegrees.
                // The user explicitly said no limit on calibration.
                _targetAngleDegrees = NearestCardinalIndex(rawYaw, rawPitch) * 90.0;
                double yawRad = _targetYawDegrees * Math.PI / 180.0;
                double pitchRad = _targetPitchDegrees * Math.PI / 180.0;
                double maxArenaRadiusPx = Math.Max(1.0, Math.Min(ClientSize.Width, ClientSize.Height) * 0.42);
                double arenaRadiusPx = maxArenaRadiusPx * (aimMagnitude / Math.Max(0.001, TrainingGeometry.Current.MaxAxisDegrees));
                _targetPos = new PointF(
                    _startPos.X + (float)(Math.Sin(yawRad) * Math.Cos(pitchRad) * arenaRadiusPx),
                    _startPos.Y - (float)(Math.Sin(pitchRad) * arenaRadiusPx));
                _arena3D.TargetPoint = WorldPointFromAngles(_targetYawDegrees, _targetPitchDegrees, _targetDistanceCm);
                _arena3D.ShowTarget = true;
                _rawSamples.Clear();
                _currentCursorPos = _startPos;
                _cameraYawDegrees = _calibrationYawDegrees;
                _cameraPitchDegrees = CalibrationPitchDegrees;
                HideTrainerCursor();
                _state = TrialState.Cooldown;
                _cooldownTimer.Stop();
                _cooldownTimer.Start();
            }
            else if (_state == TrialState.Flicking)
            {
                _rawLandedPos = _currentCursorPos;
                _landedYawDegrees = NormalizeAngle(_rawLandedCameraYaw - _calibrationYawDegrees);
                _landedPitchDegrees = _rawLandedCameraPitch - CalibrationPitchDegrees;
                // FIX (ASDW fine-tuning): save the raw camera position at
                // the moment of landing, BEFORE any ASDW nudge. This is
                // used in FinishTrial to compute how much ASDW changed the
                // effective landing position.
                _rawLandedCameraYaw = _cameraYawDegrees;
                _rawLandedCameraPitch = _cameraPitchDegrees;
                double sumDx = 0.0, sumDy = 0.0;
                foreach (var s in _rawSamples) { sumDx += s.DX; sumDy += s.DY; }
                _landedAngleDegrees = (Math.Abs(sumDx) < 1e-6 && Math.Abs(sumDy) < 1e-6)
                    ? _targetAngleDegrees
                    : NormalizeAngle(Math.Atan2(sumDy, sumDx) * 180.0 / Math.PI);
                _state = TrialState.ASDWAdjusting;
            }
            else if (_state == TrialState.TrialComplete)
            {
                if (_sensitivityFinderMode && SensitivityFinderComplete())
                {
                    Close();
                    return;
                }
                if (_sensitivityFinderMode)
                {
                    // USER'S DIRECTIVE: in Sens Finder mode the user
                    // wants to manually aim and click to specify each
                    // target point. So after a trial completes and the
                    // user clicks, transition straight to SetTargetPoint
                    // (skipping SetStartPoint because the start is
                    // always at the center of the screen in Sens Finder
                    // mode). The user then aims and clicks again to
                    // place the next target via the SetTargetPoint
                    // branch above.
                    //
                    // This is the missing "marhale aval" (first stage)
                    // the user was complaining about — before this fix
                    // the trainer auto-placed a random target right
                    // after the start, skipping the user's manual
                    // target-specification stage.
                    _state = TrialState.SetTargetPoint;
                    _rawSamples.Clear();
                    _startPos = new PointF(ClientSize.Width / 2f, ClientSize.Height / 2f);
                    _targetPos = PointF.Empty;
                    _currentCursorPos = _startPos;
                    _cameraYawDegrees = _calibrationYawDegrees;
                    _cameraPitchDegrees = CalibrationPitchDegrees;
                    _targetAngleDegrees = 0;
                    if (_arena3D != null && !_arena3D.IsDisposed)
                    {
                        _arena3D.ShowStart = false;
                        _arena3D.ShowTarget = false;
                    }
                }
                else
                {
                    // FIX (AI review root-cause #4): in training mode,
                    // do NOT let a free-aim click on empty space
                    // transition to SetStartPoint. The previous
                    // behaviour silently took the user into free-aim
                    // mode where the next trial would be attributed
                    // to a cardinal axis based on aim direction, not
                    // the selected cell — and if the aim didn't match
                    // the selected cell, the trial was rejected. The
                    // user saw "trial saved" but no calibration
                    // feedback, then concluded calibration was broken.
                    //
                    // Instead, block the click and show a toast that
                    // tells the user to pick a marker or press an
                    // axis key. The marker-click handler (in
                    // InitializeTrainingMode) runs first and will
                    // handle clicks that land on a marker.
                    if (_isTrainingMode)
                    {
                        DesktopSensToast.Show(this,
                            "TRAINING: برای trial بعدی روی marker رنگی کلیک کن یا 1/2/3/4 بزن. " +
                            "کلیک روی فضای خالی وارد free-aim می‌شه و trial رد می‌شه.");
                        return;
                    }
                    _state = TrialState.SetStartPoint;
                    _rawSamples.Clear();
                    _startPos = new PointF(ClientSize.Width / 2f, ClientSize.Height / 2f);
                    _targetPos = PointF.Empty;
                    _currentCursorPos = _startPos;
                    _targetAngleDegrees = 0;
                    _arena3D.ShowStart = false;
                    _arena3D.ShowTarget = false;
                }
            }

            UpdateArena3D();
        }

        private void OnCooldownFinished(object sender, EventArgs e)
        {
            _cooldownTimer.Stop();
            _cameraYawDegrees = _calibrationYawDegrees;
            _cameraPitchDegrees = CalibrationPitchDegrees;
            _currentCursorPos = _startPos;
            _state = TrialState.Flicking;

            // Keep the input-path hook aligned with the offline-only chain model.
            // The reset hook is intentionally a no-op while live correction is disabled.
            ResetFlickCumulativeDistance();

            _rawSamples.Clear();
            _trialStopwatch.Restart();
            _flickStartMs = -1;
            _flickLastMoveMs = -1;

            // FIX (8/16-way): the previous implementation rounded the
            // target angle to the nearest 90° and picked the rotation
            // offset for that cardinal. That works for 4-way mode but
            // mis-handles diagonals in 8/16-way mode:
            //   • A 45° DownRight flick rounded to 0° (Right) and applied
            //     Right's rotation, even though Down's rotation may differ
            //     by several degrees.
            //   • A 22.5° half-step in 16-way mode rounded to 0° (Right)
            //     and ignored the user's intent for that axis.
            //
            // The fix: pick the rotation offset for the cardinal axis
            // whose angular distance to _targetAngleDegrees is smallest.
            // In 4-way mode the four cardinal buckets are at 0/90/180/270
            // degrees and the rounding matches the old behaviour exactly
            // (because Math.Round(x/90)*90 picks the nearest multiple of
            // 90). In 8-way and 16-way mode the same rule continues to
            // pick the cardinal axis that is angularly closest, which is
            // the best the four-rotation-offset RawAccel bridge can do
            // until per-diagonal rotation offsets are added.
            //
            // The trade-off: a DownRight flick will use whichever of
            // Right/Down's rotation offset is closer (within 45°). For
            // a 45° DownRight axis both Right and Down are equally close,
            // so the deterministic tiebreaker picks Right (case 0 below)
            // — same as the legacy rounding behaviour for that angle.
            _activeRotationCorrectionDegrees = PickNearestCardinalRotationOffset(
                _targetAngleDegrees);

            UpdateArena3D();
        }

        /// <summary>
        /// FIX (8/16-way): return the per-cardinal rotation offset whose
        /// axis is angularly closest to `targetAngleDegrees`. Used by
        /// OnCooldownFinished to apply a sensible rotation correction in
        /// 8/16-way mode when only four cardinal offsets are available on
        /// the profile.
        ///
        /// Ties (e.g. a target angle exactly between Right and Down)
        /// resolve deterministically to the lower-indexed cardinal
        /// (Right for 45°, Down for 135°, Left for 225°, Up for 315°).
        /// This matches the previous Math.Round-based behaviour for the
        /// cardinal axes themselves (where Round(x/90)*90 picks the same
        /// cardinal).
        /// </summary>
        private double PickNearestCardinalRotationOffset(double targetAngleDegrees)
        {
            if (_profile == null) return 0.0;
            // Normalize to [0, 360).
            double t = ((targetAngleDegrees % 360.0) + 360.0) % 360.0;
            // The four cardinal axes sit at 0°, 90°, 180°, 270° in the
            // calibration frame. For each, compute the absolute angular
            // distance to `t` (taking the shorter way around the circle).
            double[] cardinalAngles = { 0.0, 90.0, 180.0, 270.0 };
            double[] offsets =
            {
                _profile.RightRotationOffsetDegrees,
                _profile.DownRotationOffsetDegrees,
                _profile.LeftRotationOffsetDegrees,
                _profile.UpRotationOffsetDegrees,
            };
            int bestIdx = 0;
            double bestDist = double.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                double delta = Math.Abs(t - cardinalAngles[i]);
                if (delta > 180.0) delta = 360.0 - delta;
                if (delta < bestDist)
                {
                    bestDist = delta;
                    bestIdx = i;
                }
            }
            return offsets[bestIdx];
        }

        // GLControl can consume keyboard messages before the Form's
        // KeyPreview/OnKeyDown path. Route trainer hotkeys through the
        // command-key stage as well, so 1-4 work while the arena has focus.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var trainingKey = new KeyEventArgs(keyData);
            if (HandleTrainingModeKey(trainingKey))
                return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                _cooldownTimer.Stop();
                Close();
                return;
            }

            // ── Training-mode hook (added) ───────────────────────────────
            // Handle 1/2/3/4 (axis select), T (toggle overlay), M (manual
            // merge). Must run BEFORE the ASDWAdjusting block so the user
            // can pick an axis at any time, not just during ASDW.
            if (HandleTrainingModeKey(e)) return;

            if (_state == TrialState.ASDWAdjusting)
            {
                // ASDW with hold-to-accelerate. Each press moves the camera by
                // exactly 1 PIXEL on screen (not 1 degree). If the user holds
                // the key for >= 200 ms, a timer fires every 16 ms and the
                // per-tick step grows LINEARLY over 2 seconds, from 1 px to
                // AsdwMaxStepPixels (10 px), and stays at max speed after that.
                //
                // IMPORTANT: Windows sends repeated OnKeyDown events while the
                // key is held. We must ignore those repeats — otherwise each
                // Windows auto-repeat would re-apply the initial 1 px step
                // AND restart the hold timer, so the user would see ~3 px per
                // "press" and never reach the accelerated mode.
                bool adjusted = IsAsdwKey(e.KeyCode);
                if (adjusted)
                {
                    // If this key is already being held (Windows auto-repeat),
                    // skip — the hold timer is already running and will
                    // accelerate on its own.
                    if (_asdwHeldKey == e.KeyCode && _asdwHoldTimer != null && _asdwHoldTimer.Enabled)
                    {
                        adjusted = false;  // don't re-apply, don't restart timer
                    }
                    else
                    {
                        // Compute the degree-equivalent of 1 pixel at the current
                        // arena radius. arenaRadiusPx spans the full MaxAxisDegrees,
                        // so degreesPerPixel = MaxAxisDegrees / arenaRadiusPx.
                        double arenaRadiusPx = Math.Max(1.0,
                            Math.Min(ClientSize.Width, ClientSize.Height) * 0.42);
                        double maxAxisDeg = Math.Max(0.001, TrainingGeometry.Current.MaxAxisDegrees);
                        double degreesPerPixel = maxAxisDeg / arenaRadiusPx;
                        // Apply the initial 1-pixel step now.
                        ApplyAsdwStep(e.KeyCode, degreesPerPixel);
                        // Start the hold timer (it will use the same degreesPerPixel
                        // for its accelerated steps).
                        StartAsdwHold(e.KeyCode, degreesPerPixel);
                        SyncCursorPositionToCamera();
                        ClampCursorToCanvas();
                        UpdateArena3D();
                    }
                }

                if (e.KeyCode == Keys.Space)
                {
                    FinishTrial();
                    UpdateArena3D();
                }

                if (adjusted || e.KeyCode == Keys.Space)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            // Stop the ASDW hold timer when the user releases the key.
            if (IsAsdwKey(e.KeyCode)) StopAsdwHold();
            base.OnKeyUp(e);
        }

        private static bool IsAsdwKey(Keys k)
            => k == Keys.A || k == Keys.D || k == Keys.W || k == Keys.S;

        /// <summary>
        /// Apply one ASDW step of the given magnitude (in degrees) for the
        /// given key. W = +pitch, S = -pitch, D = +yaw, A = -yaw.
        /// </summary>
        private void ApplyAsdwStep(Keys key, double stepDegrees)
        {
            switch (key)
            {
                case Keys.D: _cameraYawDegrees   += (float)stepDegrees; break;
                case Keys.A: _cameraYawDegrees   -= (float)stepDegrees; break;
                case Keys.W: _cameraPitchDegrees += (float)stepDegrees; break;
                case Keys.S: _cameraPitchDegrees -= (float)stepDegrees; break;
            }
        }

        /// <summary>
        /// Start the hold-to-accelerate timer. If the user keeps holding the
        /// key past AsdwAccelerationDelayMs (200 ms), a 16 ms timer fires and
        /// the per-tick step grows LINEARLY from AsdwInitialStepPixels (1 px)
        /// to AsdwMaxStepPixels (10 px) over AsdwAccelerationRampMs (2000 ms).
        /// After 2000 ms the step stays at the max speed forever.
        /// The `degreesPerPixel` argument converts pixel-based steps to
        /// degree-based camera rotations.
        /// </summary>
        private void StartAsdwHold(Keys key, double degreesPerPixel)
        {
            StopAsdwHold();
            _asdwHeldKey = key;
            _asdwHoldStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            _asdwHoldTimer = new Timer { Interval = AsdwAccelerationIntervalMs };
            _asdwHoldTimer.Tick += (s, ev) =>
            {
                if (_asdwHeldKey == Keys.None) { StopAsdwHold(); return; }
                double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _asdwHoldStartTicks)
                                   * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                // Only fire once we are past the initial delay. The first
                // 200 ms is the "precise placement" window — the user gets
                // just the 1 px they already received on key-down.
                if (elapsedMs < AsdwAccelerationDelayMs) return;
                // LINEAR growth: from 1 px at 200 ms to 10 px at 2000 ms.
                // After 2000 ms the step stays at 10 px.
                double rampMs = (double)AsdwAccelerationRampMs;
                double rampStart = (double)AsdwAccelerationDelayMs;
                double t = (elapsedMs - rampStart) / rampMs;  // 0 → 1 over 2 s
                if (t > 1.0) t = 1.0;
                if (t < 0.0) t = 0.0;
                double pixels = AsdwInitialStepPixels
                              + (AsdwMaxStepPixels - AsdwInitialStepPixels) * t;
                double stepDegrees = pixels * degreesPerPixel;
                ApplyAsdwStep(_asdwHeldKey, stepDegrees);
                SyncCursorPositionToCamera();
                ClampCursorToCanvas();
                UpdateArena3D();
            };
            _asdwHoldTimer.Start();
        }

        private void StopAsdwHold()
        {
            if (_asdwHoldTimer != null)
            {
                try { _asdwHoldTimer.Stop(); _asdwHoldTimer.Dispose(); } catch { }
                _asdwHoldTimer = null;
            }
            _asdwHeldKey = Keys.None;
        }

        private void SyncCursorPositionToCamera()
        {
            double yawRadians = NormalizeSignedAngle(_cameraYawDegrees - _calibrationYawDegrees) * Math.PI / 180.0;
            double pitchRadians = (_cameraPitchDegrees - CalibrationPitchDegrees) * Math.PI / 180.0;
            double arenaRadiusPx = Math.Max(1.0, Math.Min(ClientSize.Width, ClientSize.Height) * 0.42);
            _currentCursorPos = new PointF(
                _startPos.X + (float)(Math.Sin(yawRadians) * arenaRadiusPx),
                _startPos.Y - (float)(Math.Sin(pitchRadians) * arenaRadiusPx));
        }

        private void FinishTrial()
        {
            // A trial may be finalized only once, while the state machine is
            // still in ASDWAdjusting. Lock the state before calibration and
            // persistence callbacks run so a repeated Space/key event cannot
            // record the same physical flick twice.
            if (_state != TrialState.ASDWAdjusting)
                return;

            // Lock the state immediately before any callbacks/calibration work.
            _state = TrialState.TrialComplete;
            _trialStopwatch.Stop();

            // COMBINED FIX (Z.ai Stage 2 + GPT 0002):
            //
            // The raw flick error MUST stay computed from raw mouse
            // samples only — ASDW nudging is the user's MANUAL correction,
            // not part of the observation. The previous comment claimed
            // the ASDW nudge was added to the raw flick distance to make
            // the signed error reflect the "fine-tuned effective landing",
            // but the actual code at line 980 did
            // `flickDistanceCm = rawFlickDistanceCm` (no ASDW added). So
            // SignedErrorCm has always been the raw flick error. This is
            // correct and we keep it.
            //
            // What WAS wrong:
            //   1. asdwNudgeCm was computed but only sent to the log. It
            //      was NOT stored on the trial. Now stored as
            //      trial.ManualAsdwCorrectionCm (signed along target axis)
            //      and trial.ManualAsdwFinalErrorCm (the post-ASDW landing
            //      residual used by the calibrator to validate the curve
            //      sample for this cell). HasManualAsdwCorrection is set
            //      when |asdwNudgeCm| > 1e-9.
            //   2. _landedYawDegrees / _landedPitchDegrees were recomputed
            //      from the CURRENT (post-ASDW) camera, so the angular
            //      error was contaminated by ASDW. Now recomputed from the
            //      saved RAW camera position (_rawLandedCameraYaw /
            //      _rawLandedCameraPitch), so the angular error used by
            //      FinalErrorCm is a property of the raw flick only.
            //
            // The calibrator's sensitivity estimator continues to use
            // SignedErrorCm (the raw flick error) — it does NOT add
            // ManualAsdwCorrectionCm to it. The manual correction is used
            // only as an independent confirmation that the user reached
            // the target with ASDW, for accepting/rejecting that trial's
            // curve sample (DesktopSensCalibrator.TryBuildCurveSample).
            _landedYawDegrees = NormalizeAngle(_rawLandedCameraYaw - _calibrationYawDegrees);
            _landedPitchDegrees = _rawLandedCameraPitch - CalibrationPitchDegrees;

            // Compute the ASDW nudge: the camera delta from the raw
            // landing (saved in OnMouseDown) to the current position
            // (after ASDW fine-tuning). This is the user's manual
            // correction, projected onto the target axis. It is stored
            // on the trial separately and NEVER added to the raw flick.
            double asdwYawDeltaDeg = NormalizeSignedAngle(_cameraYawDegrees - _rawLandedCameraYaw);
            double asdwPitchDeltaDeg = _cameraPitchDegrees - _rawLandedCameraPitch;

            // Convert the angular nudge to equivalent mouse counts
            // (same formula as OnMouseSample) and project onto the
            // target axis direction (same formula as
            // CalculateFlickTravelCm).
            double targetYaw = _targetYawDegrees;
            double targetPitch = _targetPitchDegrees;
            double targetYawSigned = NormalizeSignedAngle(targetYaw);
            // Mouse-plane Y is the inverse of camera pitch: positive mouse DY
            // moves the camera pitch down. Derive the real target ray from the
            // full yaw/pitch vector so diagonal 8/16-way targets stay correct.
            double targetRayAngleRad = Math.Atan2(-targetPitch, targetYawSigned);
            double targetAngle = NormalizeAngle(targetRayAngleRad * 180.0 / Math.PI);

            double cmPerCount = _profile.CmPerCount;
            double degreesPerCount = TrainingGeometry.Current.MaxAxisDegrees * cmPerCount /
                Math.Max(0.001, _maxDistanceCm);
            double asdwDxCounts = asdwYawDeltaDeg / Math.Max(1e-9, degreesPerCount);
            double asdwDyCounts = -asdwPitchDeltaDeg / Math.Max(1e-9, degreesPerCount);
            double axisX = Math.Cos(targetRayAngleRad);
            double axisY = Math.Sin(targetRayAngleRad);
            double asdwProjectedCounts = asdwDxCounts * axisX + asdwDyCounts * axisY;
            double asdwNudgeCm = asdwProjectedCounts * cmPerCount;

            double landedYaw = _landedYawDegrees;
            double landedPitch = _landedPitchDegrees;
            // Recompute the landed direction from the final camera endpoint,
            // not the raw mouse samples. This includes any ASDW fine adjustment.
            // Comparing ray angles (rather than endpoint distance) ensures a pure
            // under/over-flick along the target ray has zero angular error.
            double landedYawSigned = NormalizeSignedAngle(landedYaw);
            double landedMagnitude = Math.Sqrt(
                landedYawSigned * landedYawSigned + landedPitch * landedPitch);
            if (landedMagnitude > 1e-6)
                _landedAngleDegrees = NormalizeAngle(
                    Math.Atan2(-landedPitch, landedYawSigned) * 180.0 / Math.PI);
            else
                _landedAngleDegrees = targetAngle;

            double flickAngle = _landedAngleDegrees;
            double signedRotationError = NormalizeSignedAngle(targetAngle - flickAngle);
            double angularErrorDeg = Math.Abs(signedRotationError);
            double angularErrorCm = angularErrorDeg * _maxDistanceCm /
                Math.Max(0.001, TrainingGeometry.Current.MaxAxisDegrees);
            double targetDistanceCm = _targetDistanceCm;
            // The raw flick distance is computed from raw mouse samples
            // only (CalculateFlickTravelCm walks _rawSamples). ASDW is
            // NOT added — ASDW is the user's manual correction, not part
            // of the observed mouse motion. See the COMBINED FIX note
            // above.
            //
            // CalculateFlickTravelCm is derived from the raw mouse-input
            // samples captured by MouseWatcher. Keep this pre-driver distance
            // unchanged; the active LUT is accounted for separately below via
            // AppliedLutFactor and by the calibrator's residual reconstruction.
            double rawFlickDistanceCm = CalculateFlickTravelCm(targetAngle);
            // مشاهده = فقط حرکت فیزیکی موس. نودج ASDW خودش «خطا»ست و جزو مشاهده نیست.
            double flickDistanceCm = rawFlickDistanceCm;
            double signedErrorCm = flickDistanceCm - targetDistanceCm;
            // FinalErrorCm uses the raw (pre-ASDW) angular error and the
            // raw signed error — it is a property of the raw flick only.
            // ASDW does NOT reduce FinalErrorCm: the trial does not look
            // "perfect" just because the user manually corrected.
            double finalErrorCm = Math.Sqrt(angularErrorCm * angularErrorCm + signedErrorCm * signedErrorCm);

            // Compute the post-ASDW landing residual. The post-ASDW camera
            // direction is `_cameraYawDegrees` / `_cameraPitchDegrees`
            // (which include the ASDW nudge). This is the "did the user
            // actually reach the target" confirmation. The calibrator's
            // curve-sample validation accepts this trial's (speed, ratio)
            // sample only when this residual is within the cell's
            // adaptive tolerance. The sensitivity estimator does NOT use
            // it — it uses signedErrorCm (raw).
            double finalLandedYawSigned = NormalizeSignedAngle(
                NormalizeAngle(_cameraYawDegrees - _calibrationYawDegrees));
            double finalLandedPitch = _cameraPitchDegrees - CalibrationPitchDegrees;
            double finalLandedMagnitude = Math.Sqrt(
                finalLandedYawSigned * finalLandedYawSigned +
                finalLandedPitch * finalLandedPitch);
            double finalLandedAngleDeg = finalLandedMagnitude > 1e-6
                ? NormalizeAngle(Math.Atan2(-finalLandedPitch, finalLandedYawSigned) * 180.0 / Math.PI)
                : targetAngle;
            double finalSignedRotationErrorDeg = NormalizeSignedAngle(targetAngle - finalLandedAngleDeg);
            double finalAngularErrorDeg = Math.Abs(finalSignedRotationErrorDeg);
            double finalAngularErrorCm = finalAngularErrorDeg * _maxDistanceCm /
                Math.Max(0.001, TrainingGeometry.Current.MaxAxisDegrees);
            double manualAsdwFinalSignedErrorCm = signedErrorCm + asdwNudgeCm;
            double manualAsdwFinalErrorCm = Math.Sqrt(
                finalAngularErrorCm * finalAngularErrorCm +
                manualAsdwFinalSignedErrorCm * manualAsdwFinalSignedErrorCm);
            bool hasManualAsdwCorrection = Math.Abs(asdwNudgeCm) > 1e-9;

            // NEW (Model v6): effective target for calibration. When the user actually
            // used ASDW and the post-ASDW landing sits inside this cell's adaptive
            // tolerance, the "true target" for the flick was flick + nudge, so the
            // calibration factor becomes (flick + nudge) / flick. This is what makes
            // ASDW-verified corrections influence the per-cell factor rather than just
            // being logged. SignedErrorCm stays raw; only the calibration residual
            // (which the calibrator reconstructs from NeededDistanceCm) reflects the
            // correction.
            double neededDistanceCm = double.NaN;
            if (hasManualAsdwCorrection)
            {
                double cellTolerance = DesktopSensConstants.DefaultToleranceCm;
                try
                {
                    int angleZoneForTol = ZoneCalculations.AngleToZoneIndex(targetAngle);
                    int distanceZoneForTol = _selectedDistanceZone >= 0
                        ? _selectedDistanceZone
                        : ZoneCalculations.DistanceToZoneIndex(_targetDistanceCm, _maxDistanceCm);
                    int cellIndexForTol = DesktopSensProfile.GetCellIndex(
                        angleZoneForTol, distanceZoneForTol);
                    if (_profile.CellTrainingStates != null &&
                        _profile.CellTrainingStates.TryGetValue(cellIndexForTol, out var tolState) &&
                        tolState != null &&
                        IsFiniteValue(tolState.EffectiveToleranceCm) &&
                        tolState.EffectiveToleranceCm > 0.0)
                        cellTolerance = tolState.EffectiveToleranceCm;
                }
                catch (Exception ex)
                {
                    DesktopSensLog.Warn("Could not resolve cell tolerance for NeededDistanceCm; using default", ex);
                }

                if (IsFiniteValue(manualAsdwFinalErrorCm) &&
                    manualAsdwFinalErrorCm <= cellTolerance &&
                    IsFiniteValue(flickDistanceCm) &&
                    flickDistanceCm > DesktopSensConstants.MinimumDenominatorCm)
                {
                    double candidate = flickDistanceCm + asdwNudgeCm;
                    if (IsFiniteValue(candidate) &&
                        candidate > DesktopSensConstants.MinimumDenominatorCm)
                        neededDistanceCm = candidate;
                }
            }

            // CHAIN FIX (Claude directive #1): compute the live LUT
            // factor at this trial's average speed. The trial's average
            // speed is the same formula as TryBuildCurveSample:
            // FlickDistanceCm / DurationMs * 1000 (cm/s).
            //
            // We pick the axis ("X" or "Y") based on the target angle:
            //   • cardinal direction index 0 (Right) or 2 (Left) → "X"
            //   • cardinal direction index 1 (Down) or 3 (Up) → "Y"
            //   • non-cardinal angles → use the dominant axis (the axis
            //     whose absolute direction has the larger |cos|).
            //
            // LookupActiveLutFactor returns 1.0 when no speed LUT is active.
            // The directional base multiplier is captured independently
            // below, so this flag describes only argsX/argsY LUT state.
            double appliedLutFactor = 1.0;
            bool hasAppliedLutFactor = false;
            // Trial duration: same formula the trial initializer uses below
            // — first-mouse-move to last-mouse-move, falling back to the
            // trial stopwatch elapsed time if no moves were captured.
            double trialDurationMs = (_flickStartMs >= 0 && _flickLastMoveMs > _flickStartMs)
                ? _flickLastMoveMs - _flickStartMs
                : _trialStopwatch.Elapsed.TotalMilliseconds;
            // CLAUDE FIX (round 2, directive #3) — look up the live LUT
            // factor from the ACTIVELY-APPLIED RawAccel Profile
            // (_accelGUI.Settings.ActiveProfile), NOT the merged profile
            // cache. This is the same Profile the driver is currently
            // applying, so the lookup matches exactly what the driver
            // did to this trial's mouse motion.
            //
            // The active profile is provided by the
            // _activeProfileProvider callback (set by the config form
            // in InitializeTrainingMode). If the callback is null
            // (e.g. test builds), the lookup returns 1.0 — the
            // calibrator treats the trial as a pre-LUT observation.
            object activeProfile = null;
            try { activeProfile = _activeProfileProvider != null ? _activeProfileProvider() : null; }
            catch (Exception ex) { DesktopSensLog.Error("ActiveProfileProvider threw", ex); }

            // The active directional base multiplier is configured outside
            // argsX/argsY, so capture it separately from the speed-LUT factor.
            double appliedBaseFactor = 1.0;
            string directionForBase = "Right";
            int cardinalIdxForBase = DesktopSensTrainingState.GetCardinalDirectionIndex(
                ZoneCalculations.AngleToZoneIndex(targetAngle));
            if (cardinalIdxForBase == 0) directionForBase = "Right";
            else if (cardinalIdxForBase == 1) directionForBase = "Down";
            else if (cardinalIdxForBase == 2) directionForBase = "Left";
            else if (cardinalIdxForBase == 3) directionForBase = "Up";
            else
            {
                double directionRad = targetAngle * Math.PI / 180.0;
                if (Math.Abs(Math.Cos(directionRad)) >= Math.Abs(Math.Sin(directionRad)))
                    directionForBase = Math.Cos(directionRad) >= 0.0 ? "Right" : "Left";
                else
                    directionForBase = Math.Sin(directionRad) >= 0.0 ? "Down" : "Up";
            }
            appliedBaseFactor = RawAccelSettingsBridge.LookupActiveDirectionalBaseFactorFromProfile(
                activeProfile, _profile.RawAccelBaseOutputDPI, directionForBase);

            if (activeProfile != null &&
                !double.IsNaN(flickDistanceCm) && !double.IsInfinity(flickDistanceCm) &&
                flickDistanceCm > 0.0 &&
                !double.IsNaN(trialDurationMs) && !double.IsInfinity(trialDurationMs) &&
                trialDurationMs > 0.0)
            {
                double trialSpeedCmPerSec = flickDistanceCm * 1000.0 / trialDurationMs;
                if (!double.IsNaN(trialSpeedCmPerSec) && !double.IsInfinity(trialSpeedCmPerSec) &&
                    trialSpeedCmPerSec > 0.0)
                {
                    int cardinalIdxForLut = DesktopSensTrainingState.GetCardinalDirectionIndex(
                        ZoneCalculations.AngleToZoneIndex(targetAngle));
                    string lutAxis = "X";
                    if (cardinalIdxForLut == 1 || cardinalIdxForLut == 3)
                        lutAxis = "Y";
                    else if (cardinalIdxForLut < 0)
                    {
                        // Non-cardinal angle: use the dominant axis by
                        // |cos(targetAngle)| vs |sin(targetAngle)|.
                        double rad = targetAngle * Math.PI / 180.0;
                        if (Math.Abs(Math.Sin(rad)) > Math.Abs(Math.Cos(rad)))
                            lutAxis = "Y";
                    }
                    appliedLutFactor = RawAccelSettingsBridge.LookupActiveLutFactorFromProfile(
                        activeProfile, trialSpeedCmPerSec, lutAxis);
                    if (!double.IsNaN(appliedLutFactor) && !double.IsInfinity(appliedLutFactor) &&
                        appliedLutFactor > 0.0 && Math.Abs(appliedLutFactor - 1.0) > 1e-9)
                        hasAppliedLutFactor = true;
                }
            }

            double projectedDistancePx = (targetDistanceCm + signedErrorCm) * _pixelsPerCm;
            double actualDistancePx = flickDistanceCm * _pixelsPerCm;
            double peakVelocity = CalculatePeakVelocity();

            // DIAGNOSTIC: log the raw flick, the ASDW nudge (signed), and
            // the post-ASDW landing residual so the user can see (via
            // Show Diag Log) that the raw flick error is preserved AND
            // the manual ASDW correction is recorded separately AND the
            // final landing residual is what gates the curve sample.
            DesktopSensLog.Warn(
                "FinishTrial: rawFlick=" + rawFlickDistanceCm.ToString("0.000") + "cm" +
                " needed=" + (double.IsNaN(neededDistanceCm)
                    ? "n/a"
                    : neededDistanceCm.ToString("0.000") + "cm") +
                " asdwNudge=" + asdwNudgeCm.ToString("+0.000;-0.000;0.000") + "cm" +
                " (stored separately, NOT added to flick)" +
                " target=" + targetDistanceCm.ToString("0.000") + "cm" +
                " signedError=" + signedErrorCm.ToString("+0.000;-0.000;0.000") + "cm" +
                " | rawAngularError=" + angularErrorDeg.ToString("0.00") + "°" +
                " | postAsdwFinalError=" + manualAsdwFinalErrorCm.ToString("0.000") + "cm" +
                (hasManualAsdwCorrection ? " (gates curve sample)" : " (no ASDW used)"));

            var trial = new DesktopSensTrial
            {
                StartX = _startPos.X,
                StartY = _startPos.Y,
                TargetX = _targetPos.X,
                TargetY = _targetPos.Y,
                TargetAngleDegrees = targetAngle,
                TargetDistanceCm = targetDistanceCm,
                RawSamples = new List<MouseSample>(_rawSamples),
                LandedX = _rawLandedPos.X,
                LandedY = _rawLandedPos.Y,
                FinalErrorCm = finalErrorCm,
                SignedErrorCm = signedErrorCm,
                ProjectedDistanceCm = projectedDistancePx / _pixelsPerCm,
                IsUnderFlick = signedErrorCm < 0.0,
                IsOverFlick = signedErrorCm > 0.0,
                FlickAngleDegrees = flickAngle,
                SignedRotationErrorDegrees = signedRotationError,
                AppliedRotationCorrectionDegrees = _activeRotationCorrectionDegrees,
                HasMovementMeasurement = actualDistancePx >= 0.25 * _pixelsPerCm,
                HasRotationMeasurement = actualDistancePx >= 0.25 * _pixelsPerCm,
                FlickDistanceCm = flickDistanceCm,
                DurationMs = (_flickStartMs >= 0 && _flickLastMoveMs > _flickStartMs)
                    ? _flickLastMoveMs - _flickStartMs
                    : _trialStopwatch.Elapsed.TotalMilliseconds,
                PeakVelocityPxPerSec = peakVelocity,
                // FIXES #3 + #9: populate the new trial-validation fields.
                AngularErrorDegrees = angularErrorDeg,
                HasAngularMeasurement = actualDistancePx >= 0.25 * _pixelsPerCm,
                RawSampleCount = _rawSamples.Count,
                MeanVelocityPxPerSec = CalculateMeanVelocity(),
                VelocityStdDevPxPerSec = CalculateVelocityStdDev(),
                QuantizationErrorCm = _profile.QuantizationErrorCm,
                AngleZoneIndex = ZoneCalculations.AngleToZoneIndex(targetAngle),
                DistanceZoneIndex = !_sensitivityFinderMode && _selectedDistanceZone >= 0
                    ? _selectedDistanceZone
                    : ZoneCalculations.DistanceToZoneIndex(targetDistanceCm, _maxDistanceCm),
                CompletionMode = FlickCompletionMode.Click,
                CompletionTimestamp = _rawSamples.Count > 0 ? _rawSamples[_rawSamples.Count - 1].Timestamp : 0,
                MouseDPI = _profile.MouseDPI,
                EffectiveCpi = _profile.EffectiveCpi,
                SessionId = _profile.CurrentSessionId,
                IsSessionAnchor = false,
                GameSensitivity = _profile.UserInputGameSensitivity,
                EffectivePollingRateHz = _profile.PollingRateHz,
                CanvasPixelsPerCm = _pixelsPerCm,
                IsEvaluationOnly = _isInNoiseFloorEstimation || _isInABTestMode || _isABTestTrialInProgress,
                // COMBINED FIX: record the ASDW nudge as the user's manual
                // correction. ManualAsdwCorrectionCm is signed along the
                // target axis (positive = forward, negative = backward).
                // ManualAsdwFinalErrorCm is the post-ASDW landing residual
                // that gates the curve sample for this cell — the
                // calibrator's TryBuildCurveSample accepts this trial's
                // (speed, ratio) sample only when this residual is within
                // the cell's adaptive tolerance. The sensitivity
                // estimator continues to use SignedErrorCm (raw flick);
                // the manual correction is stored SEPARATELY so the
                // trial is NOT made to look like a perfect flick.
                ManualAsdwCorrectionCm = asdwNudgeCm,
                ManualAsdwFinalErrorCm = manualAsdwFinalErrorCm,
                HasManualAsdwCorrection = hasManualAsdwCorrection,
                NeededDistanceCm = neededDistanceCm,
                // Record the live LUT factor at this trial's pre-driver
                // average speed. The calibrator applies it once to the raw
                // input observation, estimates the remaining residual factor,
                // and the merger composes that residual with the active factor
                // at the representative median speed to emit a total LUT.
                //
                // The lookup is on _profile.LastAppliedMergedProfile (the
                // most recent successful merge-and-apply). When no LUT
                // has been applied yet (or the merged profile is null),
                // AppliedLutFactor stays at 1.0 and HasAppliedLutFactor
                // stays false — the calibrator treats this trial as a
                // pre-LUT observation, which is the correct
                // backward-compat behaviour.
                AppliedBaseFactor = appliedBaseFactor,
                AppliedLutFactor = appliedLutFactor,
                HasAppliedLutFactor = hasAppliedLutFactor
            };

            // Snapshot the active speed LUT at each raw input sample so
            // residual reconstruction can replay the actual per-sample
            // driver multiplier (rather than a single median-speed factor).
            trial.AppliedLutFactorsByRawSample = new List<double>(trial.RawSamples.Count);
            trial.AppliedLutFactorsByRawSample.Add(1.0);
            double sampleCmPerCount = trial.EffectiveCpi > 0.0
                ? DesktopSensConstants.InchesToCm / trial.EffectiveCpi : 0.0;
            double targetRadForSampleLut = targetAngle * Math.PI / 180.0;
            string sampleLutAxis = Math.Abs(Math.Cos(targetRadForSampleLut)) >=
                Math.Abs(Math.Sin(targetRadForSampleLut)) ? "X" : "Y";
            for (int sampleIndex = 1; sampleIndex < trial.RawSamples.Count; sampleIndex++)
            {
                MouseSample prevSample = trial.RawSamples[sampleIndex - 1];
                MouseSample currSample = trial.RawSamples[sampleIndex];
                long deltaTicks = currSample.Timestamp - prevSample.Timestamp;
                double factorAtSample = 1.0;
                if (activeProfile != null && deltaTicks > 0 && sampleCmPerCount > 0.0)
                {
                    if (SampleLutSampleProcessor.TryProcess(
                        currSample.DX, currSample.DY, deltaTicks, trial.EffectiveCpi,
                        targetAngle, out double speedCmPerSec, out _))
                    {
                        double queried = RawAccelSettingsBridge.LookupActiveLutFactorFromProfile(
                            activeProfile, speedCmPerSec, sampleLutAxis);
                        if (IsFiniteValue(queried) && queried > 0.0)
                            factorAtSample = queried;
                    }
                }
                trial.AppliedLutFactorsByRawSample.Add(factorAtSample);
            }

            // Preserve the capture-time residual for the training-state hit check.
            // DesktopSensCalibrator independently replays raw samples in
            // deterministic zone order on every calibration pass; it does
            // not consume this frozen value for factor estimation.
            if (!trial.IsEvaluationOnly && !_sensitivityFinderMode)
            {
                double chainResidual = DesktopSensCalibrator.ComputeChainAdjustedResidual(
                    _profile, trial, trial.SignedErrorCm);
                if (!double.IsNaN(chainResidual) && !double.IsInfinity(chainResidual))
                {
                    trial.HasChainAdjustedSignedError = true;
                    trial.ChainAdjustedSignedErrorCm = chainResidual;
                }
            }

            // FIXES #9: validate the trial against the new rejection criteria
            // (sample count, duration, velocity profile, angular error,
            // noise floor). Sets trial.WasRejected and RejectionReason.
            // Rejected trials stay in profile.Trials for diagnostic display
            // but the calibrator skips them.
            trial.Validate(
                noiseFloorCm: _profile.NoiseFloorCm,
                minRawSamples: DesktopSensCalibrator.MinRawSamplesForValidTrial,
                minDurationMs: DesktopSensCalibrator.MinFlickDurationMs,
                maxDurationMs: DesktopSensCalibrator.MaxFlickDurationMs,
                // AngularErrorDegrees now stores the measured off-axis angle only.
                // Radial under/over-flick remains in SignedErrorCm and is not
                // rejected by the angular cutoff. Finder also uses its own
                // directional calibration dataset and does not use Train's
                // angular rejection threshold.
                maxAngularErrorDegrees: _sensitivityFinderMode
                    ? double.MaxValue
                    : DesktopSensCalibrator.MaxAngularErrorDegrees);

            // Apply the strict gate only to cardinal Train flicks. Sensitivity
            // Finder keeps its own capture policy and diagnostic/evaluation
            // trials never alter training state.
            DesktopSensTrialGateResult trainGateResult = null;
            if (!_sensitivityFinderMode && _isTrainingMode && !trial.IsEvaluationOnly &&
                !trial.WasRejected && _selectedDistanceZone >= 0)
            {
                double zoneStartCm = _selectedDistanceZone > 0
                    ? TrainingGeometry.Current.MarkerDistanceCm(
                        _selectedDistanceZone - 1, _maxDistanceCm)
                    : 0.0;
                trainGateResult = DesktopSensTrialGate.Evaluate(
                    trial, zoneStartCm, _selectedDistanceZone == 0);
                if (!trainGateResult.Accepted)
                {
                    trial.WasRejected = true;
                    trial.RejectionReason = trainGateResult.Reason;
                    DesktopSensLog.Warn(
                        "Train trial gate rejected: reason=" + trainGateResult.Reason +
                        " rawFlickCm=" + trial.FlickDistanceCm.ToString("0.000") +
                        " targetCm=" + trial.TargetDistanceCm.ToString("0.000") +
                        " zoneStartCm=" + zoneStartCm.ToString("0.000") +
                        " durationMs=" + trial.DurationMs.ToString("0.0"));
                    string gateMessage;
                    switch (trainGateResult.Reason)
                    {
                        case TrialRejectionReason.DidNotEnterZone:
                            gateMessage = "فلیک هنوز داخل زون قبلی مانده؛ نقطه‌ی انتهای زون قبلی را دوباره کالیبره کن.";
                            break;
                        case TrialRejectionReason.OvershootOutOfRange:
                            gateMessage = "فلیک بیش از حد از هدف عبور کرد؛ این trial رد شد.";
                            break;
                        case TrialRejectionReason.DurationOutOfRange:
                            gateMessage = "فلیک بیش از حد طولانی یا کوتاه بود؛ یک flick سریع و پیوسته انجام بده.";
                            break;
                        case TrialRejectionReason.LongPause:
                            gateMessage = "وسط فلیک مکث طولانی ثبت شد؛ حرکت را پیوسته انجام بده.";
                            break;
                        case TrialRejectionReason.PeakVelocityTooLow:
                            gateMessage = "سرعت فلیک خیلی کم بود؛ با حرکت سریع‌تر تکرار کن.";
                            break;
                        default:
                            gateMessage = "فلیک برای کالیبراسیون معتبر نبود؛ دوباره تلاش کن.";
                            break;
                    }
                    DesktopSensToast.Show(this, gateMessage);
                }
            }


            DesktopSensLog.Warn(
                (_sensitivityFinderMode ? "Sensitivity Finder" : "Train") +
                " trial validation: " + (trial.IsValidForCalibration
                    ? "ACCEPTED"
                    : (trial.WasRejected ? "REJECTED" : "NOT_CALIBRATION_ELIGIBLE")) +
                " reason=" + (trial.RejectionReason ?? TrialRejectionReason.None) +
                " samples=" + trial.RawSampleCount +
                " durationMs=" + trial.DurationMs.ToString("0.0") +
                " targetCm=" + trial.TargetDistanceCm.ToString("0.000") +
                " signedErrorCm=" + trial.SignedErrorCm.ToString("+0.000;-0.000;0.000") +
                " angularErrorDeg=" + trial.AngularErrorDegrees.ToString("0.00") +
                " meanVel=" + trial.MeanVelocityPxPerSec.ToString("0") +
                " stdVel=" + trial.VelocityStdDevPxPerSec.ToString("0"));

            // Do not reject a Finder trial merely because the actual raw-input
            // direction differs from the camera/target-angle quadrant. In Finder
            // the user manually aims the target in 3D; target yaw alone cannot
            // reliably encode vertical movement, and a strict expected-vs-measured
            // gate can discard otherwise valid Up/Down (or diagonal) observations.
            // DirectionalCalibrator assigns each accepted sample using its raw DX/DY.
            // Noise-floor and post-merge evaluation flicks are diagnostic only.
            // Their IsEvaluationOnly flag was set in the trial initializer.
            // Route the completed trial into exactly one mode-specific history.
            // Finder recalculates from SensitivityFinderTrials; Train records
            // into Trials and lets RecordTrainingAttempt rebuild per-cell factors.
            if (_sensitivityFinderMode)
            {
                if (_profile.SensitivityFinderTrials == null)
                    _profile.SensitivityFinderTrials = new List<DesktopSensTrial>();
                _profile.SensitivityFinderTrials.Add(trial);
                try
                {
                    DirectionalCalibrator.Calibrate(_profile, _profile.SensitivityFinderTrials, 0.0, preserveUnobservedDirections: true);
                    DesktopSensLog.Warn(
                        "Finder calibration updated from " + _profile.SensitivityFinderTrials.Count +
                        " trials: R=" + _profile.RightSensitivityFactor.ToString("0.000") +
                        " D=" + _profile.DownSensitivityFactor.ToString("0.000") +
                        " L=" + _profile.LeftSensitivityFactor.ToString("0.000") +
                        " U=" + _profile.UpSensitivityFactor.ToString("0.000"));
                }
                catch (Exception ex)
                {
                    DesktopSensLog.Error("Sensitivity Finder calibration recompute failed", ex);
                }
            }
            else
            {
                _profile.Trials.Add(trial);
            }
            // Clear the completed trial's marker before the A/B callback can
            // place the next target and mark its upcoming trial in progress.
            _isABTestTrialInProgress = false;
            if (!_sensitivityFinderMode && !trial.IsEvaluationOnly && !trial.WasRejected &&
                !trial.IsSessionAnchor &&
                DesktopSensTrainingState.IsCalibrationDistanceZone(trial.DistanceZoneIndex) &&
                trial.TargetDistanceCm > DesktopSensConstants.BaseZoneEndCm)
            {
                _angleZoneCounts[trial.AngleZoneIndex]++;
                _distanceZoneCounts[trial.DistanceZoneIndex]++;
            }
            _profile.ModifiedTimestamp = DateTime.UtcNow;

            // Sensitivity Finder has no Apply/merge step. Persist every captured
            // attempt immediately so its calibration history survives closing or
            // interrupting the trainer; final Base Sens factors are recalculated
            // and saved by StartSensitivityFinder when the form closes.
            if (_sensitivityFinderMode)
                OnCellVerifiedPersistProfile();

            if (!_sensitivityFinderMode && !trial.IsEvaluationOnly)
                RecordTrainingAttempt(trial);

            // The evaluation-mode handlers may choose the next target; run
            // them only after the current trial has been stored.
            OnFixesTrialFinished(trial);

            // Space completes calibration. Immediately re-arm target selection
            // so mouse samples are accepted again; never require an extra click
            // merely to unfreeze the cursor. The user still chooses the next
            // marker/target explicitly, so this does not auto-advance a cell.
            // Do not override evaluation handlers that already entered Cooldown.
            if (_state == TrialState.TrialComplete)
            {
                _state = TrialState.SetTargetPoint;
                RestoreTrainerCursor();
                _startPos = new PointF(ClientSize.Width / 2f, ClientSize.Height / 2f);
                _targetPos = PointF.Empty;
                _currentCursorPos = _startPos;
                _cameraYawDegrees = _calibrationYawDegrees;
                _cameraPitchDegrees = CalibrationPitchDegrees;
                _targetAngleDegrees = 0.0;
                _targetDistanceCm = 0.0;
                _rawSamples.Clear();
                if (_arena3D != null && !_arena3D.IsDisposed)
                {
                    _arena3D.ShowStart = false;
                    _arena3D.ShowTarget = false;
                }
            }

            // The trainer is already back in SetTargetPoint above. The
            // selected Train cell remains unchanged; the user explicitly
            // chooses the next marker, while the mouse can move immediately.

            // Sensitivity Finder: increment the per-direction counter so we know
            // when each cardinal has enough samples.
            // Note: this micro-zone upper bound uses MicroZoneMaxCm (the
            // Finder mode's own 3 cm target scope) and is NOT tied to
            // BaseZoneEndCm, which is now 0.0 after the user's directive
            // to remove the 3 cm Train-calibration exemption. The two
            // concepts are independent: BaseZoneEndCm controls whether a
            // trial feeds Train per-cell calibration; MicroZoneMaxCm here
            // controls the Sensitivity Finder's micro-zone target scope.
            if (_sensitivityFinderMode && trial.IsValidForCalibration &&
                trial.TargetDistanceCm <= DesktopSensConstants.MicroZoneMaxCm)
            {
                // Use the same raw-input classifier as DirectionalCalibrator;
                // count only samples that actually contributed to the estimate.
                int dir = DirectionalCalibrator.DirectionFromSamples(trial);
                if (dir >= 0 && dir < 4)
                    _sensitivityFinderDirectionCounts[dir]++;
            }

            UpdateArena3D();

            // In Sensitivity Finder mode, check whether all 4 directions
            // have enough samples. If so, auto-close (same as before).
            // If not, do NOT auto-place the next target. The state is
            // already SetTargetPoint, so the user can move the mouse and then
            // choose the next target explicitly without a release click.
            if (_sensitivityFinderMode && SensitivityFinderComplete())
            {
                // Use a tiny delay so the user sees the "Trial saved"
                // feedback before the form closes.
                var completeCheck = new Timer { Interval = 300 };
                completeCheck.Tick += (s, e2) =>
                {
                    completeCheck.Stop();
                    completeCheck.Dispose();
                    Close();
                };
                completeCheck.Start();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Never leave the machine running the baseline (A) mapping if the
            // user closes the trainer halfway through a counterbalanced test.
            if (_isInABTestMode)
                StopABTestAndRestoreCurrent();

            if (!_sensitivityFinderMode && _profile != null && HasUnmergedTrainingProgress())
            {
                DialogResult choice = MessageBox.Show(
                    this,
                    "You have calibration attempts that are not yet verified or merged.\\n\\n" +
                    "Save the current profile and create a recovery backup before closing?",
                    "Unmerged calibration progress",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Warning);

                if (choice == DialogResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }

                if (choice == DialogResult.Yes)
                {
                    bool backupWritten = WriteBackupBeforeMerge();
                    OnCellVerifiedPersistProfile();
                    if (!backupWritten)
                    {
                        MessageBox.Show(this,
                            "The recovery backup could not be created. The profile save was still attempted.",
                            "Backup warning",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }
                }
            }

            base.OnFormClosing(e);
        }

        private bool HasUnmergedTrainingProgress()
        {
            if (_profile == null || _profile.CellTrainingStates == null)
                return false;

            if (_profile.AllCardinalCellsVerified)
                return false;

            foreach (TrainingCellState state in _profile.CellTrainingStates.Values)
            {
                if (state != null && state.TotalAttemptCount > 0 &&
                    DesktopSensTrainingState.IsCalibrationDistanceZone(state.DistanceZoneIndex))
                    return true;
            }
            return false;
        }

        private double CalculateFlickTravelCm(double targetAngleDegrees)
        {
            if (_rawSamples == null || _rawSamples.Count == 0 ||
                _profile == null || _profile.EffectiveCpi <= 0.0)
                return 0.0;

            // Measure net displacement projected onto the requested target
            // axis. Summing sample magnitudes measured path length, so jitter
            // and backtracking were incorrectly counted as forward travel.
            double angleRad = targetAngleDegrees * Math.PI / 180.0;
            double axisX = Math.Cos(angleRad);
            double axisY = Math.Sin(angleRad);
            double projectedCounts = 0.0;

            for (int i = 0; i < _rawSamples.Count; i++)
            {
                MouseSample sample = _rawSamples[i];
                if ((object)sample == null) continue;
                double dx = sample.DX;
                double dy = sample.DY;
                double correctionRad = _activeRotationCorrectionDegrees * Math.PI / 180.0;
                if (Math.Abs(correctionRad) > 1e-9)
                {
                    double c = Math.Cos(correctionRad);
                    double si = Math.Sin(correctionRad);
                    double rotatedX = dx * c - dy * si;
                    double rotatedY = dx * si + dy * c;
                    dx = rotatedX;
                    dy = rotatedY;
                }
                projectedCounts += dx * axisX + dy * axisY;
            }

            return projectedCounts * _profile.CmPerCount;
        }

        private double CalculatePeakVelocity()
        {
            if (_rawSamples.Count < 2) return 0.0;
            double peak = 0.0;
            double scale = _pixelsPerCm * _profile.CmPerCount;
            for (int i = 1; i < _rawSamples.Count; i++)
            {
                long ticks = _rawSamples[i].Timestamp - _rawSamples[i - 1].Timestamp;
                if (ticks <= 0) continue;
                double seconds = ticks / (double)Stopwatch.Frequency;
                double dx = (_rawSamples[i].DX) * scale;
                double dy = (_rawSamples[i].DY) * scale;
                double velocity = Math.Sqrt(dx * dx + dy * dy) / seconds;
                if (velocity > peak) peak = velocity;
            }
            return peak;
        }

        private PointF ClampToTurnCircle(PointF point)
        {
            double dx = point.X - _startPos.X;
            double dy = point.Y - _startPos.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            double maxRadius = _maxDistanceCm * _pixelsPerCm;
            if (distance > maxRadius && distance > 0.0)
            {
                double scale = maxRadius / distance;
                point = new PointF(
                    _startPos.X + (float)(dx * scale),
                    _startPos.Y + (float)(dy * scale));
            }
            return point;
        }

        private void ClampCursorToCanvas()
        {
            _currentCursorPos.X = Math.Max(0, Math.Min(ClientSize.Width - 1, _currentCursorPos.X));
            _currentCursorPos.Y = Math.Max(0, Math.Min(ClientSize.Height - 1, _currentCursorPos.Y));
            if (_state != TrialState.SetStartPoint)
                _currentCursorPos = ClampToTurnCircle(_currentCursorPos);
        }

        private static double NormalizeAngle(double angle)
        {
            if (double.IsNaN(angle) || double.IsInfinity(angle)) return 0.0;
            angle %= 360.0;
            return angle < 0.0 ? angle + 360.0 : angle;
        }

        private static double NormalizeSignedAngle(double angle)
        {
            angle = NormalizeAngle(angle);
            return angle > 180.0 ? angle - 360.0 : angle;
        }

        private static string CardinalName(double angleDeg)
        {
            int idx = (int)Math.Round(angleDeg / 90.0) % 4;
            switch (idx)
            {
                case 0: return "RIGHT";
                case 1: return "DOWN";
                case 2: return "LEFT";
                case 3: return "UP";
                default: return "?";
            }
        }

        private static int NearestCardinalIndex(double yawDeg, double pitchDeg)
        {
            double absYaw = Math.Abs(NormalizeSignedAngle(yawDeg));
            double absPitch = Math.Abs(pitchDeg);
            if (absYaw >= absPitch)
                return yawDeg > 0 ? 0 : 2;
            else
                return pitchDeg > 0 ? 3 : 1;
        }

        private void UpdateArena3D()
        {
            if (_arena3D == null || _arena3D.IsDisposed || IsDisposed) return;
            _arena3D.YawDegrees = _cameraYawDegrees;
            _arena3D.PitchDegrees = _cameraPitchDegrees;
            _arena3D.ViewDirection = CameraDirectionFromCalibration();
            _arena3D.Radius = (float)_maxDistanceCm;
            _arena3D.StartPoint = _calibrationOrigin;
            _arena3D.TargetPoint = WorldPointFromAngles(_targetYawDegrees, _targetPitchDegrees, _targetDistanceCm);
            _arena3D.CalibrationRight = _calibrationRight;
            _arena3D.CalibrationForward = _calibrationForward;
            _arena3D.CalibrationUp = _calibrationUp;
            _arena3D.ShowStart = _state != TrialState.SetStartPoint;
            // In training mode, do NOT show the target marker after a trial
            // completes. The user's directive: "bade etmame ye train ye
            // noghteh dige mizareh nemikham in etefagh biofteh khodam taeen
            // konam noghteh badio" — after one training finishes, the trainer
            // must NOT place another point; the user themselves must choose
            // the next point.
            //
            // The OLD behaviour kept the just-finished target visible during
            // TrialComplete (via this ShowTarget condition), which the user
            // read as "the trainer auto-placed a new point". In training
            // mode we therefore hide the target the moment the trial ends;
            // the user must click on a training marker or press 1/2/3/4 to
            // place the next target. Non-training mode keeps the legacy
            // behaviour (show the just-finished target so the user can see
            // where their flick landed).
            if (_isTrainingMode && _state == TrialState.TrialComplete)
            {
                _arena3D.ShowTarget = false;
            }
            else
            {
                _arena3D.ShowTarget = _state == TrialState.Cooldown || _state == TrialState.Flicking ||
                    _state == TrialState.ASDWAdjusting || _state == TrialState.TrialComplete;
            }
            _arena3D.StatusText = _state.ToString();
            _arena3D.TrialCount = _sensitivityFinderMode
                ? (_profile.SensitivityFinderTrials != null ? _profile.SensitivityFinderTrials.Count : 0)
                : (_profile.Trials != null ? _profile.Trials.Count : 0);

            _arena3D.ActiveZoneIndex =
                (_state == TrialState.Flicking
              || _state == TrialState.ASDWAdjusting
              || _state == TrialState.TrialComplete)
                    ? ((int)Math.Floor(_targetYawDegrees / (360.0 / 32.0))) % 32
                    : -1;

            double relativeYaw = NormalizeAngle(_cameraYawDegrees - _calibrationYawDegrees);
            double relativePitch = _cameraPitchDegrees - CalibrationPitchDegrees;

            string instruction;
            switch (_state)
            {
                case TrialState.SetStartPoint:
                    instruction = "Move mouse to aim the reference direction  |  Left-click to set start  |  ESC: exit"; break;
                case TrialState.SetTargetPoint:
                {
                    double aimMag = Math.Sqrt(relativeYaw * relativeYaw + relativePitch * relativePitch);
                    double autoDist = aimMag / Math.Max(0.001, TrainingGeometry.Current.MaxAxisDegrees) * _maxDistanceCm;
                    // FIX 3 (from AI review): in training mode, tell the
                    // user explicitly what to do next. The generic "Aim
                    // anywhere" hint is wrong in training mode — the user
                    // must first pick a coloured marker (or press an
                    // axis key) to select a cell, then click on the
                    // marker (or the same cell) to place the target.
                    // Without this distinction, the user clicked empty
                    // space, the click was silently turned into a
                    // target, the trial was silently discarded by
                    // RecordTrainingAttempt, and the user saw no
                    // feedback at all.
                    if (_isTrainingMode &&
                        (_selectedAxisIndex < 0 || _selectedDistanceZone < 0))
                    {
                        instruction = "TRAINING: click a coloured marker OR press 1/2/3/4 (or 1-8 / 1-9,0,Q,E,F,G,H,J in 8/16-way) to pick a cell first";
                    }
                    else if (_isTrainingMode)
                    {
                        instruction = "TRAINING: click the selected marker again to place the target  |  Distance " +
                            autoDist.ToString("0.0") + " cm  |  or press 1/2/3/4 to switch cells";
                    }
                    else
                    {
                        instruction = "Aim anywhere (mouse + wheel for pitch)  |  Distance: " +
                            autoDist.ToString("0.0") + " cm (" +
                            aimMag.ToString("0.0") + "°)  |  Left-click to place";
                    }
                    break;
                }
                case TrialState.Cooldown:
                    instruction = "Crosshair locked on start · 3 seconds…"; break;
                case TrialState.Flicking:
                    instruction = "Flick to target  |  rotation correction " +
                        _activeRotationCorrectionDegrees.ToString("0.00") + "°  |  Left-click to land"; break;
                case TrialState.ASDWAdjusting:
                    instruction = "W/A/S/D to fine-tune  |  SPACE to save trial"; break;
                default:
                    instruction = _sensitivityFinderMode
                        ? "Trial saved  |  Auto-advancing to next target…"
                        : "Trial saved  |  Click to start the next trial"; break;
            }

            string trialLine = _state == TrialState.SetTargetPoint
                ? "AIM: yaw " + NormalizeSignedAngle(relativeYaw).ToString("0.0") + "°  pitch " +
                  relativePitch.ToString("0.0") + "°  →  " +
                  CardinalName(NearestCardinalIndex(relativeYaw, relativePitch) * 90.0) +
                  " (" + Math.Sqrt(relativeYaw * relativeYaw + relativePitch * relativePitch).ToString("0.0") + "°)"
                : ( _targetAngleDegrees >= 0
                    ? "TARGET: yaw " + NormalizeSignedAngle(_targetYawDegrees).ToString("0.0") + "°  pitch " +
                      _targetPitchDegrees.ToString("0.0") + "°  |  LANDING: yaw " +
                      NormalizeSignedAngle(_landedYawDegrees).ToString("0.0") + "°  pitch " +
                      _landedPitchDegrees.ToString("0.0") + "°"
                    : "");

            string modeBanner = _sensitivityFinderMode
                ? "  |  SENS FINDER  R " + _sensitivityFinderDirectionCounts[0] + "/" + _sensitivityFinderTrialsPerDirection +
                  "  D " + _sensitivityFinderDirectionCounts[1] + "/" + _sensitivityFinderTrialsPerDirection +
                  "  L " + _sensitivityFinderDirectionCounts[2] + "/" + _sensitivityFinderTrialsPerDirection +
                  "  U " + _sensitivityFinderDirectionCounts[3] + "/" + _sensitivityFinderTrialsPerDirection
                : "";

            _hud.Text =
                "DESKTOPSENS 3D" + modeBanner + "  |  Relative yaw " + relativeYaw.ToString("0.0") + "°" +
                "  Pitch " + _cameraPitchDegrees.ToString("0.0") + "°" +
                "  |  Trials " + (_sensitivityFinderMode
                    ? (_profile.SensitivityFinderTrials != null ? _profile.SensitivityFinderTrials.Count : 0)
                    : (_profile.Trials != null ? _profile.Trials.Count : 0)) + Environment.NewLine +
                trialLine + Environment.NewLine +
                instruction + Environment.NewLine +
                "Cardinal stats:  " + CardinalStatsLine();

            float cx = ClientSize.Width * 0.5f;
            float cy = ClientSize.Height * 0.5f;
            float compassRadius = Math.Max(100f, Math.Min(ClientSize.Width, ClientSize.Height) * 0.34f);
            for (int i = 0; i < _degreeLabels.Length; i++)
            {
                double radians = (i * TrainingGeometry.Current.MarkerStepDegrees - relativeYaw) * Math.PI / 180.0;
                Label label = _degreeLabels[i];
                label.Location = new Point(
                    (int)(cx + Math.Sin(radians) * compassRadius - label.Width / 2f),
                    (int)(cy - Math.Cos(radians) * compassRadius - label.Height / 2f));
            }

            string targetText = _state == TrialState.SetTargetPoint
                ? "AIM: " + NormalizeSignedAngle(relativeYaw).ToString("0.0") + "° / " +
                  relativePitch.ToString("0.0") + "°"
                : (_targetAngleDegrees >= 0
                    ? "TARGET: " + NormalizeSignedAngle(_targetYawDegrees).ToString("0.0") + "° / " +
                      _targetPitchDegrees.ToString("0.0") + "°"
                    : "TARGET: —");
            _selectedAngleLabel.Text = targetText;

            PointF angleAnchor;
            if (_state == TrialState.SetTargetPoint)
            {
                double aimPitch = _cameraPitchDegrees - CalibrationPitchDegrees;
                double yawRad = relativeYaw * Math.PI / 180.0;
                double pitchRad = aimPitch * Math.PI / 180.0;
                angleAnchor = new PointF(
                    cx + (float)(Math.Sin(yawRad) * Math.Cos(pitchRad) * compassRadius),
                    cy - (float)(Math.Sin(pitchRad) * compassRadius));
            }
            else
            {
                angleAnchor = _targetPos;
            }
            _selectedAngleLabel.Location = new Point(
                (int)(angleAnchor.X - _selectedAngleLabel.Width / 2f),
                (int)(angleAnchor.Y - _selectedAngleLabel.Height - 10f));
            _selectedAngleLabel.BringToFront();
            _hud.BringToFront();
            _arena3D.Invalidate();

            // ── Training-mode hook (added) ───────────────────────────────
            // Push the per-cell training state into the arena control so
            // the colored axes / yellow / green markers stay in sync with
            // the latest trial. Also refresh the training HUD overlay
            // (verified count, current selection, attempt counter).
            UpdateTrainingArenaProperties();
            UpdateTrainingHud();
        }

        private string CardinalStatsLine()
        {
            int r = 0, d = 0, l = 0, u = 0;
            foreach (var t in _profile.Trials)
            {
                if (t == null) continue;
                int idx = (int)Math.Round(t.TargetAngleDegrees / 90.0) % 4;
                switch (idx)
                {
                    case 0: r++; break;
                    case 1: d++; break;
                    case 2: l++; break;
                    case 3: u++; break;
                }
            }
            return string.Format(
                "R {0} (factor {1:0.00})  D {2} (factor {3:0.00})  L {4} (factor {5:0.00})  U {6} (factor {7:0.00})",
                r, _profile.RightSensitivityFactor,
                d, _profile.DownSensitivityFactor,
                l, _profile.LeftSensitivityFactor,
                u, _profile.UpSensitivityFactor);
        }

        private Vector3 CameraDirectionFromCalibration()
        {
            double yaw = NormalizeSignedAngle(_cameraYawDegrees - _calibrationYawDegrees) * Math.PI / 180.0;
            double pitch = (_cameraPitchDegrees - CalibrationPitchDegrees) * Math.PI / 180.0;

            Vector3 horizontal = Vector3.Normalize(
                _calibrationForward * (float)Math.Cos(yaw) +
                _calibrationRight * (float)Math.Sin(yaw));
            return Vector3.Normalize(
                horizontal * (float)Math.Cos(pitch) +
                _calibrationUp * (float)Math.Sin(pitch));
        }

        private void CaptureCalibrationPlane()
        {
            float yawDeg = (float)NormalizeAngle(_cameraYawDegrees);
            _calibrationYawDegrees = yawDeg;

            float yawRad = yawDeg * (float)Math.PI / 180f;
            float sinYaw = (float)Math.Sin(yawRad);
            float cosYaw = (float)Math.Cos(yawRad);

            _calibrationForward = new Vector3(sinYaw, 0f, -cosYaw);
            _calibrationRight   = new Vector3(cosYaw, 0f,  sinYaw);
            _calibrationUp      = Vector3.UnitY;
            // Rebuild Trainer-owned marker coordinates whenever the calibration frame changes.
            _canonicalTrainingMarkerPositions = null;
            _canonicalTrainingMarkerDistanceCm = null;
            _canonicalDirectionCountCached = -1;

            Vector3 eye = new Vector3(0f, 1.7f, 0f);
            _calibrationOrigin = eye + _calibrationForward * CalibrationPlaneDistance;

            _arena3D.StartPoint         = _calibrationOrigin;
            _arena3D.CalibrationRight   = _calibrationRight;
            _arena3D.CalibrationForward = _calibrationForward;
            _arena3D.CalibrationUp      = _calibrationUp;
            _arena3D.ShowStart          = true;
        }

        private Vector3 WorldPointFromAngles(double yawDegrees, double pitchDegrees, double distanceCm)
        {
            double yaw = yawDegrees * Math.PI / 180.0;
            double pitch = pitchDegrees * Math.PI / 180.0;

            Vector3 horizontal = Vector3.Normalize(
                _calibrationForward * (float)Math.Cos(yaw) +
                _calibrationRight * (float)Math.Sin(yaw));

            Vector3 direction = Vector3.Normalize(
                horizontal * (float)Math.Cos(pitch) +
                _calibrationUp * (float)Math.Sin(pitch));

            Vector3 eye = new Vector3(0f, 1.7f, 0f);

            const float markerRadius = 1.5f;
            Vector3 marker = eye + direction * markerRadius;
            if (marker.Y < 0.15f) marker.Y = 0.15f;
            return marker;
        }

        // =========================================================================
        // FIXES (added): velocity-profile helpers for trial validation.
        // =========================================================================

        /// <summary>
        /// Mean sample velocity (px/s) for the just-completed flick.
        /// Used by trial.Validate to reject flicks whose velocity profile
        /// is anomalous (e.g. stutter or aborted motion).
        /// </summary>
        private double CalculateMeanVelocity()
        {
            if (_rawSamples.Count < 2) return 0.0;
            double scale = _pixelsPerCm * _profile.CmPerCount;
            double totalDistPx = 0.0;
            double totalTimeSec = 0.0;
            for (int i = 1; i < _rawSamples.Count; i++)
            {
                long ticks = _rawSamples[i].Timestamp - _rawSamples[i - 1].Timestamp;
                if (ticks <= 0) continue;
                double seconds = ticks / (double)Stopwatch.Frequency;
                double dx = _rawSamples[i].DX * scale;
                double dy = _rawSamples[i].DY * scale;
                totalDistPx += Math.Sqrt(dx * dx + dy * dy);
                totalTimeSec += seconds;
            }
            return totalTimeSec > 0 ? totalDistPx / totalTimeSec : 0.0;
        }

        /// <summary>
        /// Standard deviation of per-sample velocities (px/s). A normal flick
        /// includes acceleration and deceleration, so stddev may exceed mean.
        /// Trial validation rejects only extreme dispersion above 3x the mean.
        /// </summary>
        private double CalculateVelocityStdDev()
        {
            if (_rawSamples.Count < 2) return 0.0;
            double scale = _pixelsPerCm * _profile.CmPerCount;
            long windowTicks = Stopwatch.Frequency / 50;   // پنجره‌ی ۲۰ میلی‌ثانیه
            var velocities = new List<double>();
            long winStart = _rawSamples[0].Timestamp;
            double distPx = 0.0;
            for (int i = 1; i < _rawSamples.Count; i++)
            {
                double dx = _rawSamples[i].DX * scale;
                double dy = _rawSamples[i].DY * scale;
                distPx += Math.Sqrt(dx * dx + dy * dy);
                long elapsed = _rawSamples[i].Timestamp - winStart;
                if (elapsed >= windowTicks)
                {
                    velocities.Add(distPx / (elapsed / (double)Stopwatch.Frequency));
                    winStart = _rawSamples[i].Timestamp;
                    distPx = 0.0;
                }
            }
            if (velocities.Count < 2) return 0.0;
            double mean = 0.0;
            foreach (var v in velocities) mean += v;
            mean /= velocities.Count;
            double sumSq = 0.0;
            foreach (var v in velocities) sumSq += (v - mean) * (v - mean);
            return Math.Sqrt(sumSq / velocities.Count);
        }
    }
}
