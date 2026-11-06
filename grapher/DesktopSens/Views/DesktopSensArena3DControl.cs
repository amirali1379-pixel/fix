using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL;
using grapher.DesktopSens.Models;

namespace grapher.DesktopSens.Views
{
    internal sealed class DesktopSensArena3DControl : GLControl
    {
        internal float YawDegrees { get; set; }
        internal float PitchDegrees { get; set; } = 18f;
        // Field of view used by the trainer camera. Smaller FOV = zoom in.
        internal float FieldOfViewDegrees { get; set; } = 68f;
        internal Vector3 ViewDirection { get; set; } = new Vector3(0f, 0f, -1f);
        internal float Radius { get; set; } = 10f;
        internal Vector3 StartPoint { get; set; }
        internal Vector3 TargetPoint { get; set; }
        internal Vector3 CalibrationRight { get; set; } = Vector3.UnitX;
        internal Vector3 CalibrationForward { get; set; } = new Vector3(0f, 0f, -1f);
        internal Vector3 CalibrationUp { get; set; } = Vector3.UnitY;
        internal bool ShowStart { get; set; }
        internal bool ShowTarget { get; set; }
        internal string StatusText { get; set; } = "";
        internal int TrialCount { get; set; }
        // 0..31 selects which floor sector is highlighted (target or current aim).
        // -1 disables the highlight. Sector boundaries are spaced every 11.25 degrees
        // in the calibration frame so the visual grid matches the 32 angular zones
        // the runtime consumes via atan2(raw dx, raw dy).
        internal int ActiveZoneIndex { get; set; } = -1;

        // ===========================================================================
        // Cardinal training-mode rendering (added).
        //
        // When ShowTrainingMarkers is true, the arena renders the Trainer's
        // canonical angular samples (10° apart) on the four cardinal axes.
        // Each point is colored according to the per-cell training state:
        //
        //   axis color — no calibration attempt recorded
        //   yellow     — at least one attempt recorded, not yet verified
        //   green      — Verified (locked)
        //
        // The next eligible cell on each axis (per the sequential rule) is
        // drawn with a pulsing white outline so the user knows where to click
        // to continue training.
        //
        // The Right-180° and Left-180° samples share the same world position.
        // The endpoint is drawn once; Trainer resolves which directional cell
        // can accept the selection. No artificial pitch offsets are applied.
        // ===========================================================================
        internal bool ShowTrainingMarkers { get; set; }
        internal Dictionary<int, TrainingCellState> TrainingStates { get; set; }
        // Canonical geometry is built by DesktopSensTrainerForm and supplied here.
        // The arena is a renderer/hit-tester only; it must never invent marker positions.
        internal Vector3[][] CanonicalTrainingMarkerPositions { get; set; }
        internal double[][] CanonicalTrainingMarkerDistanceCm { get; set; }
        internal int HoveredMarkerAxisIndex { get; private set; } = -1;
        internal double HoveredMarkerDistanceCm { get; private set; } = -1.0;
        // The next-eligible cell on each axis (4 entries: 0=right, 1=down,
        // 2=left, 3=up). -1 means that axis is fully verified. Drives the
        // pulsing outline that tells the user "click here next".
        // With 8-way / 16-way this array is sized to the active direction
        // count by the trainer.
        internal int[] NextEligibleDistanceZone { get; set; } = { -1, -1, -1, -1 };
        // Pulsing phase (0..1) for the highlighted next-eligible markers.
        // Driven by the render timer in the parent trainer form via PulsePhase.
        internal float PulsePhase { get; set; }

        // ───────────────────────────────────────────────────────────────────
        // FIX (8/16-way + selected-axis highlight): the arena now needs to
        // know (a) which direction-count mode is active so it can size its
        // arrays and (b) which axis is currently selected for calibration
        // so it can brighten that axis's line. The user's directive:
        //   "vaghti ye mehvar baraye calib entekhab shod oon mehvar ye
        //    linesh kami roshantar bashe" — when an axis is selected for
        //    calibration, its line should be slightly brighter.
        // -1 means no axis is currently selected (trainer idle).
        // ───────────────────────────────────────────────────────────────────
        internal int ActiveDirectionCount { get; set; } = 4;
        internal int SelectedAxisIndex { get; set; } = -1;
        // When true, the selected axis's arc and markers get an extra
        // brightness boost and an outline ring so the user can see at a
        // glance which axis the next flick will calibrate.
        internal bool HighlightSelectedAxis { get; set; } = true;

        // Snapshot of marker positions from the last DrawTrainingAxes call,
        // used for click hit-testing. _trainingMarkerPositions[axisIdx][cm-1]
        // = Vector3 in world space. _trainingMarkerDistanceCm[axisIdx][cm-1]
        // = the distance in cm.
        // Set back to null when training markers are disabled so hit-testing
        // returns "no hit" cleanly.
        internal Vector3[][] _trainingMarkerPositions;
        internal double[][] _trainingMarkerDistanceCm;
        // Screen-space positions captured during the same render pass as the visible markers.
        // Hit-testing must use this snapshot, not whichever GL matrices happen to be active on click.
        private Vector2?[][] _trainingMarkerScreenPositions;

        /// <summary>
        /// Hit-test the last drawn training markers against a screen-space
        /// point (mouse click). Returns the (axisIdx, distanceCm) of the
        /// closest marker within `pixelTolerance`, or (-1, -1) if no hit.
        ///
        /// Implementation: project each marker's 3D world position to screen
        /// space using the current OpenGL projection/modelview matrices
        /// (captured during the last RenderScene call), then find the closest
        /// one to (screenX, screenY).
        /// </summary>
        // FIX 5 (from AI review): default pixel tolerance raised from
        // 30 to 45 pixels. With 10° marker spacing on a typical 1080p
        // monitor at default arena size, markers are about 30-40 px
        // apart; a 30 px tolerance was too strict — a click 2-3 pixels
        // off-centre missed the marker entirely and was silently
        // swallowed (the click handler in InitializeTrainingMode
        // returns from `if (hit.axisIdx < 0)`). The 45 px tolerance
        // makes the hit-test match what the user sees as the marker's
        // visual extent (the pulsing point size is 26-42 px).
        public (int axisIdx, double distanceCm) HitTestMarker(int screenX, int screenY, float pixelTolerance = 45f)
        {
            var screenPositions = _trainingMarkerScreenPositions;
            var distancesByAxis = _trainingMarkerDistanceCm;
            if (screenPositions == null || distancesByAxis == null) return (-1, -1.0);

            int bestAxis = -1;
            double bestCm = -1.0;
            float bestDistSq = pixelTolerance * pixelTolerance;
            for (int axisIdx = 0; axisIdx < screenPositions.Length; axisIdx++)
            {
                var positions = screenPositions[axisIdx];
                var distances = axisIdx < distancesByAxis.Length ? distancesByAxis[axisIdx] : null;
                if (positions == null || distances == null) continue;
                int count = Math.Min(positions.Length, distances.Length);
                for (int i = 0; i < count; i++)
                {
                    if (!positions[i].HasValue) continue;
                    float dx = positions[i].Value.X - screenX;
                    float dy = positions[i].Value.Y - screenY;
                    float distSq = dx * dx + dy * dy;
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestAxis = axisIdx;
                        bestCm = distances[i];
                    }
                }
            }
            return (bestAxis, bestCm);
        }

        /// <summary>
        /// Project a 3D world position to 2D screen coordinates.
        /// Returns null if the point is behind the camera (clip W <= 0).
        /// </summary>
        private static Vector2? ProjectToScreen(Vector3 world, int[] viewport, double[] modelview, double[] projection)
        {
            // Transform world → eye → clip space using the captured matrices.
            // We do the math manually to avoid depending on OpenTK's gluProject
            // binding (which is sometimes missing in OpenTK 3.x builds).
            //
            // v_eye = modelview * v_world
            // v_clip = projection * v_eye
            // v_ndc = v_clip.xyz / v_clip.w
            // v_screen = viewport transform of v_ndc
            double vx = world.X, vy = world.Y, vz = world.Z;

            // eye = modelview * (vx, vy, vz, 1)
            double ex = modelview[0] * vx + modelview[4] * vy + modelview[8]  * vz + modelview[12];
            double ey = modelview[1] * vx + modelview[5] * vy + modelview[9]  * vz + modelview[13];
            double ez = modelview[2] * vx + modelview[6] * vy + modelview[10] * vz + modelview[14];
            double ew = modelview[3] * vx + modelview[7] * vy + modelview[11] * vz + modelview[15];

            // clip = projection * (ex, ey, ez, ew)
            double cx = projection[0] * ex + projection[4] * ey + projection[8]  * ez + projection[12] * ew;
            double cy = projection[1] * ex + projection[5] * ey + projection[9]  * ez + projection[13] * ew;
            double cz = projection[2] * ex + projection[6] * ey + projection[10] * ez + projection[14] * ew;
            double cw = projection[3] * ex + projection[7] * ey + projection[11] * ez + projection[15] * ew;

            // Behind the camera or degenerate.
            if (cw <= 0.0001) return null;

            double ndcX = cx / cw;
            double ndcY = cy / cw;

            // Viewport transform. OpenTK/OpenGL viewport: x,y = lower-left,
            // w,h = size. Screen Y is inverted (top-left origin).
            double screenX = viewport[0] + (ndcX + 1.0) * 0.5 * viewport[2];
            double screenY = viewport[1] + (1.0 - (ndcY + 1.0) * 0.5) * viewport[3];

            return new Vector2((float)screenX, (float)screenY);
        }


        public DesktopSensArena3DControl() : base(new GraphicsMode(32, 24, 0, 4))
        {
            BackColor = Color.FromArgb(12, 16, 22);
            VSync = true;
            Paint += RenderScene;
            Resize += (s, e) => { MakeCurrent(); GL.Viewport(0, 0, Width, Height); Invalidate(); };
        }

        private void RenderScene(object sender, PaintEventArgs e)
        {
            if (Width < 1 || Height < 1 || IsDisposed) return;
            MakeCurrent();
            GL.Viewport(0, 0, Width, Height);
            GL.ClearColor(0.035f, 0.05f, 0.065f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();
            float fov = Math.Max(30f, Math.Min(100f, FieldOfViewDegrees));
            Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(fov), (float)Width / Height, 0.05f, 500f);
            GL.LoadMatrix(ref projection);
            Vector3 look = ViewDirection.LengthSquared > 0.0001f
                ? Vector3.Normalize(ViewDirection)
                : new Vector3(0f, 0f, -1f);
            Vector3 eye = new Vector3(0f, 1.7f, 0f);
            Vector3 worldUp = Vector3.UnitY;
            Vector3 up = worldUp - look * Vector3.Dot(worldUp, look);
            if (up.LengthSquared < 0.0001f)
            {
                Vector3 fallback = Vector3.UnitZ;
                up = fallback - look * Vector3.Dot(fallback, look);
            }
            up = Vector3.Normalize(up);
            Matrix4 view = Matrix4.LookAt(eye, eye + look, up);
            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadMatrix(ref view);
            DrawIndoorRange();
            DrawAzimuthTicks();
            DrawZoneGrid();

            // Each marker gets TWO visual layers:
            //   - a solid point (always screen-facing, so visible from any pitch)
            //   - a small billboard-ish circle in the calibration plane
            // The previous version only had the circle. For a downward pitch the
            // circle is nearly edge-on to the viewer and shrinks to a line, so a
            // 1.5 m down target looked like "nothing on the ground". The point
            // guarantees a visible dot regardless of camera angle.
            if (ShowStart)
            {
                // The calibration origin is the stable green reference point.
                // Keep it visually distinct from the orange active target.
                DrawMarker(StartPoint, 0.25f, 0.95f, 0.30f, 0.95f);
                DrawCalibrationCircle(StartPoint, CalibrationRight, CalibrationUp, 0.08f, 0.25f, 0.95f, 0.30f);
            }
            if (ShowTarget)
            {
                DrawMarker(TargetPoint, 0.55f, 1.0f, 0.55f, 0.18f);
                DrawCalibrationCircle(TargetPoint, CalibrationRight, CalibrationUp, 0.12f, 1.0f, 0.55f, 0.18f);
            }

            // Training markers are drawn AFTER the start/target markers so
            // they appear ON TOP of them. Previously DrawTrainingAxes was
            // called BEFORE start/target, which meant the 1-7 cm training
            // markers (which sit inside the 8 cm start-marker circle) were
            // hidden behind the start marker. The user couldn't see them.
            //
            // FIX 4 (from AI review): we always call DrawTrainingAxes so
            // that the hit-test cache (_trainingMarkerPositions) stays
            // up-to-date even when the user has pressed T to hide the
            // on-screen overlay. Without this, hiding the overlay would
            // also disable the marker-click handler — which would mean
            // the user, after pressing T, could no longer pick cells by
            // clicking where the marker used to be visible. The arena
            // would only respond to the 1/2/3/4 hotkeys. That breaks the
            // "hide visual clutter while still recording" use case that
            // Fix 4 enables.
            //
            // DrawTrainingAxes itself checks ShowTrainingMarkers and
            // only draws the visible coloured dots / arcs when it is
            // true; the hit-test cache is always populated regardless.
            {
                GL.Enable(EnableCap.PointSmooth);
                GL.Hint(HintTarget.PointSmoothHint, HintMode.Nicest);
                DrawTrainingAxes();
                GL.Disable(EnableCap.PointSmooth);
            }
            // No else branch — the hit-test cache must remain populated
            // even with the overlay hidden, per Fix 4 above.
            DrawScreenCrosshair();
            SwapBuffers();
        }

        // 32 angular sectors + a few distance reference rings on the floor.
        // This is the visual counterpart of the 32x46 calibration profile that
        // the runtime consumes. The active sector is highlighted so the player
        // can see which angular zone the current trial is training.
        private void DrawZoneGrid()
        {
            const float radius = 24f;
            const float y = 0.02f;
            const int zones = 32;
            double degPerZone = 360.0 / zones;
            Vector3 fwd = new Vector3(CalibrationForward.X, 0f, CalibrationForward.Z);
            if (fwd.LengthSquared < 1e-6f) fwd = new Vector3(0f, 0f, -1f);
            fwd = Vector3.Normalize(fwd);
            Vector3 rgt = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
            // Sector boundary lines (every 11.25 deg). Cardinal boundaries are brighter.
            GL.LineWidth(1f);
            GL.Begin(PrimitiveType.Lines);
            for (int i = 0; i < zones; i++)
            {
                double ang = i * degPerZone * Math.PI / 180.0;
                Vector3 dir = fwd * (float)Math.Cos(ang) + rgt * (float)Math.Sin(ang);
                bool cardinal = (i % 8 == 0);
                GL.Color4(
                    cardinal ? 0.48f : 0.20f,
                    cardinal ? 0.40f : 0.16f,
                    cardinal ? 0.30f : 0.22f,
                    0.75f);
                GL.Vertex3(0f, y, 0f);
                GL.Vertex3(dir.X * radius, y, dir.Z * radius);
            }
            GL.End();
            // Distance reference rings (25 / 50 / 75 % of the grid radius).
            GL.Begin(PrimitiveType.Lines);
            GL.Color4(0.22f, 0.28f, 0.34f, 0.55f);
            for (int ring = 1; ring <= 3; ring++)
            {
                float r = radius * ring / 3.0f;
                int seg = 64;
                for (int i = 0; i < seg; i++)
                {
                    double a1 = i * Math.PI * 2.0 / seg;
                    double a2 = (i + 1) * Math.PI * 2.0 / seg;
                    GL.Vertex3((float)Math.Cos(a1) * r, y, (float)Math.Sin(a1) * r);
                    GL.Vertex3((float)Math.Cos(a2) * r, y, (float)Math.Sin(a2) * r);
                }
            }
            GL.End();
            // Active sector fill (amber). Coordinates are expressed in the calibration
            // frame so the highlighted wedge follows the same 32-zone convention as
            // the runtime's atan2(dy, dx) lookup.
            if (ActiveZoneIndex >= 0 && ActiveZoneIndex < zones)
            {
                double a1 = ActiveZoneIndex * degPerZone * Math.PI / 180.0;
                double a2 = (ActiveZoneIndex + 1) * degPerZone * Math.PI / 180.0;
                GL.Begin(PrimitiveType.TriangleFan);
                GL.Color4(1.0f, 0.60f, 0.20f, 0.22f);
                GL.Vertex3(0f, y, 0f);
                const int arcSegs = 10;
                for (int i = 0; i <= arcSegs; i++)
                {
                    double ang = a1 + (a2 - a1) * i / arcSegs;
                    Vector3 dir = fwd * (float)Math.Cos(ang) + rgt * (float)Math.Sin(ang);
                    GL.Vertex3(dir.X * radius, y, dir.Z * radius);
                }
                GL.End();
            }
        }

        // ===========================================================================
        // Cardinal training axes renderer (added).
        //
        // Draws four colored ARCS on the calibration sphere (the 1.5 m radius
        // sphere centered at the camera eye), one per cardinal direction.
        // Each arc goes from "forward" (yaw=0, pitch=0) — the calibration
        // origin — through the cardinal direction, all the way to "backward"
        // (yaw=±180° or pitch=±180°). At 180° the right and left arcs would
        // coincide (both end up behind the camera); to resolve that visual
        // collision we lift the right arc slightly up and the left arc
        // slightly down — the user's "180 right up, 180 left down" rule.
        //
        // One marker is placed for every reachable training zone along
        // each arc. Marker color encodes the training state
        // (white / yellow / green). The next-eligible marker on each axis
        // pulses so the user can see where to click.
        //
        // The arc math mirrors DesktopSensTrainerForm.WorldPointFromAngles:
        //   horizontal = Normalize(CalibrationForward * cos(yaw)
        //                          + CalibrationRight * sin(yaw))
        //   direction   = Normalize(horizontal * cos(pitch)
        //                          + CalibrationUp * sin(pitch))
        //   marker      = eye + direction * MarkerRadius
        // where MarkerRadius = 1.5 m (matches CalibrationPlaneDistance).
        // ===========================================================================

        private void DrawTrainingAxes()
        {
            // Trainer is the single source of truth for every marker position and distance.
            Vector3[][] positionsByAxis = CanonicalTrainingMarkerPositions;
            double[][] distancesByAxis = CanonicalTrainingMarkerDistanceCm;
            if (positionsByAxis == null || distancesByAxis == null)
            {
                _trainingMarkerPositions = null;
                _trainingMarkerDistanceCm = null;
                _trainingMarkerScreenPositions = null;
                HoveredMarkerAxisIndex = -1;
                HoveredMarkerDistanceCm = -1.0;
                return;
            }

            _trainingMarkerPositions = positionsByAxis;
            _trainingMarkerDistanceCm = distancesByAxis;

            // Capture the exact screen coordinates used by this render pass.
            // Re-reading GL matrices in the MouseDown event is unreliable:
            // overlay drawing can leave a different matrix state active, so
            // clicks are compared against coordinates unrelated to the visible dots.
            int[] viewport = new int[4];
            double[] modelview = new double[16];
            double[] projection = new double[16];
            try
            {
                GL.GetInteger(GetPName.Viewport, viewport);
                GL.GetDouble(GetPName.ModelviewMatrix, modelview);
                GL.GetDouble(GetPName.ProjectionMatrix, projection);
            }
            catch (Exception ex)
            {
                DesktopSensLog.Warn("Could not update arena marker hover state", ex);
                _trainingMarkerScreenPositions = null;
                HoveredMarkerAxisIndex = -1;
                HoveredMarkerDistanceCm = -1.0;
                return;
            }

            var screenPositionsByAxis = new Vector2?[positionsByAxis.Length][];
            for (int axisIdx = 0; axisIdx < positionsByAxis.Length; axisIdx++)
            {
                Vector3[] positions = positionsByAxis[axisIdx];
                int count = positions == null ? 0 : positions.Length;
                screenPositionsByAxis[axisIdx] = new Vector2?[count];
                for (int i = 0; i < count; i++)
                {
                    Vector2? projected = ProjectToScreen(positions[i], viewport, modelview, projection);
                    if (projected.HasValue && viewport[2] > 0 && viewport[3] > 0)
                    {
                        // OpenGL viewport dimensions can be physical pixels on
                        // high-DPI displays, while WinForms MouseEventArgs uses
                        // logical client coordinates. Convert into this control's
                        // client space before comparing with click coordinates.
                        float clientX = (projected.Value.X - viewport[0]) * Width / viewport[2];
                        float clientY = (projected.Value.Y - viewport[1]) * Height / viewport[3];
                        screenPositionsByAxis[axisIdx][i] = new Vector2(clientX, clientY);
                    }
                }
            }
            _trainingMarkerScreenPositions = screenPositionsByAxis;

            // Keep hit-testing functional when the visual overlay is toggled off.
            if (!ShowTrainingMarkers)
            {
                HoveredMarkerAxisIndex = -1;
                HoveredMarkerDistanceCm = -1.0;
                return;
            }

            // The crosshair is fixed at the screen center. A marker becomes brighter
            // only when its projected center is actually underneath that crosshair.
            float crosshairX = Width * 0.5f;
            float crosshairY = Height * 0.5f;
            const float hoverRadiusPixels = 18f;
            float bestHoverDistanceSquared = hoverRadiusPixels * hoverRadiusPixels;
            int hoveredAxis = -1;
            double hoveredCm = -1.0;

            // Find the marker under the crosshair before drawing, so rendering and
            // hit-testing use the same canonical positions and the same camera matrices.
            for (int axisIdx = 0; axisIdx < positionsByAxis.Length; axisIdx++)
            {
                Vector3[] positions = positionsByAxis[axisIdx];
                double[] distances = axisIdx < distancesByAxis.Length ? distancesByAxis[axisIdx] : null;
                if (positions == null || distances == null) continue;
                int count = Math.Min(positions.Length, distances.Length);
                for (int i = 0; i < count; i++)
                {
                    Vector2? screen = screenPositionsByAxis[axisIdx][i];
                    if (!screen.HasValue) continue;
                    float dx = screen.Value.X - crosshairX;
                    float dy = screen.Value.Y - crosshairY;
                    float distanceSquared = dx * dx + dy * dy;
                    if (distanceSquared < bestHoverDistanceSquared)
                    {
                        bestHoverDistanceSquared = distanceSquared;
                        hoveredAxis = axisIdx;
                        hoveredCm = distances[i];
                    }
                }
            }

            HoveredMarkerAxisIndex = hoveredAxis;
            HoveredMarkerDistanceCm = hoveredCm;

            for (int axisIdx = 0; axisIdx < positionsByAxis.Length; axisIdx++)
            {
                Vector3[] positions = positionsByAxis[axisIdx];
                double[] distances = axisIdx < distancesByAxis.Length ? distancesByAxis[axisIdx] : null;
                if (positions == null || distances == null) continue;

                int count = Math.Min(positions.Length, distances.Length);
                // FIX (8/16-way): resolve the per-axis RGB from the active
                // direction-count mode so 8-way and 16-way axes get their
                // own distinct colors. Falls back to 4-way colors if the
                // trainer has not set ActiveDirectionCount yet.
                int directionCount = ActiveDirectionCount > 0 ? ActiveDirectionCount : 4;
                int[] rgb = DesktopSensTrainingState.GetAxisRgbForMode(axisIdx, directionCount);
                float cr = rgb[0] / 255f, cg = rgb[1] / 255f, cb = rgb[2] / 255f;

                // FIX (selected-axis highlight): the user wants the
                // currently-calibrating axis to be visibly brighter than
                // the others. We boost the brightness of every marker on
                // the selected axis and draw a thicker, more saturated
                // outline ring around the next-eligible marker.
                bool isSelected = HighlightSelectedAxis &&
                    SelectedAxisIndex >= 0 && axisIdx == SelectedAxisIndex;
                float selectionBoost = isSelected ? 0.35f : 0.0f;

                int nextEligible = NextEligibleDistanceZone != null && axisIdx < NextEligibleDistanceZone.Length
                    ? NextEligibleDistanceZone[axisIdx] : -1;
                int angleZone = DesktopSensTrainingState.AngleZoneIndicesForCount(directionCount)[axisIdx % DesktopSensTrainingState.AngleZoneIndicesForCount(directionCount).Length];

                int previousZone = -1;
                for (int i = 0; i < count; i++)
                {
                    Vector3 pos = positions[i];
                    double distanceCm = distances[i];

                    // In the new training-derived model, marker index == zone
                    // index. The trainer places one marker per zone and the
                    // calibrator / merger / training-state map all use the
                    // same index space, so we no longer need to convert the
                    // marker's distance back into a legacy zone index. The
                    // canonical marker distance already IS the zone's center.
                    int zoneIdx = i;
                    // FIX (8/16-way): use the active direction-count mode's
                    // zone count so diagonal axes don't get an out-of-range
                    // zone index from the legacy CardinalDirectionZoneCount.
                    int modeDirectionCount = ActiveDirectionCount > 0 ? ActiveDirectionCount : 4;
                    int axisZoneLimit = TrainingGeometry.Current.MultiDirectionZoneCount(axisIdx, modeDirectionCount);
                    zoneIdx = Math.Max(0, Math.Min(axisZoneLimit - 1, zoneIdx));

                    bool verified = false, firstRound = false, attempted = false;
                    int successCount = 0;
                    if (TrainingStates != null &&
                        DesktopSensTrainingState.IsCalibrationDistanceZone(zoneIdx))
                    {
                        int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, zoneIdx);
                        if (TrainingStates.TryGetValue(cellIndex, out var state) && state != null)
                        {
                            // Only accepted calibration observations make a
                            // marker yellow. Rejected/measurement-less attempts
                            // remain visible in the HUD counters but must not
                            // masquerade as a point that has calibration data.
                            attempted = state.AcceptedAttemptCount > 0 ||
                                (state.SignedErrorHistoryCm != null &&
                                 state.SignedErrorHistoryCm.Count > 0) ||
                                state.FirstRoundComplete || state.Verified;
                            verified = state.Verified;
                            firstRound = state.FirstRoundComplete;
                            successCount = state.SuccessfulAttemptCount;
                        }
                    }

                    // State color is categorical, not a progress gradient:
                    //   no recorded attempt -> axis color
                    //   attempted but not verified -> clear yellow
                    //   fully verified -> green
                    // This makes the first recorded calibration immediately visible.
                    float mr, mg, mb;
                    GetMarkerGradientColor(attempted, successCount, firstRound, verified,
                        rgb[0], rgb[1], rgb[2], out mr, out mg, out mb);

                    // FIX (selected-axis highlight): brighten every marker
                    // on the selected axis so the user can see at a glance
                    // which axis is being calibrated.
                    if (selectionBoost > 0f)
                    {
                        mr = Math.Min(1f, mr + selectionBoost);
                        mg = Math.Min(1f, mg + selectionBoost);
                        mb = Math.Min(1f, mb + selectionBoost);
                    }

                    bool hovered = axisIdx == HoveredMarkerAxisIndex &&
                                   Math.Abs(distanceCm - HoveredMarkerDistanceCm) < 0.001;
                    bool firstMarkerInNextZone = zoneIdx == nextEligible && zoneIdx != previousZone;
                    // Smaller circles keep the 10-degree samples distinct.
                    float pointSize = 26f + (verified ? 12f : (firstRound ? 9f + Math.Min(successCount, 5) * 1.5f : 0f));
                    if (firstMarkerInNextZone) pointSize += 4f;
                    // FIX (selected-axis highlight): the selected axis's
                    // markers are also slightly larger so the user can tell
                    // them apart from neighbouring axes' markers when many
                    // axes are drawn (16-way mode).
                    if (isSelected) pointSize += 4f;
                    if (hovered)
                    {
                        // Bright overlay is tied to the exact canonical point under the crosshair.
                        pointSize += 10f;
                        mr = Math.Min(1f, mr + 0.35f);
                        mg = Math.Min(1f, mg + 0.35f);
                        mb = Math.Min(1f, mb + 0.35f);
                    }

                    GL.PointSize(pointSize);
                    GL.Begin(PrimitiveType.Points);
                    GL.Color3(mr, mg, mb);
                    GL.Vertex3(pos.X, pos.Y, pos.Z);
                    GL.End();

                    previousZone = zoneIdx;
                }

                // FIX (selected-axis highlight): draw a brighter outline
                // arc along the selected axis from origin to its last
                // marker. This is the "line" the user explicitly asked to
                // be made brighter. Non-selected axes get a fainter arc
                // so the selected one stands out.
                if (count > 0 && isSelected)
                {
                    DrawAxisArc(positions, cr, cg, cb, /*alpha*/0.9f, /*lineWidth*/3.5f);
                }
                else if (count > 0)
                {
                    DrawAxisArc(positions, cr, cg, cb, /*alpha*/0.45f, /*lineWidth*/1.5f);
                }
            }

            // FIX #13: render per-marker numeric labels, uncertainty bars,
            // and drift rings. Called once per frame, AFTER all markers
            // are drawn, so the labels appear on top.
            DrawTrainingMarkerOverlay();
        }

        // Billboard ring helper for the pulsing next-eligible marker.
        private static void DrawBillboardRing(Vector3 center, Vector3 right, Vector3 up,
            float radius, float red, float green, float blue, float alpha)
        {
            Vector3 normal = Vector3.Normalize(Vector3.Cross(right, up));
            center += normal * 0.04f;
            GL.LineWidth(2f);
            GL.Begin(PrimitiveType.LineLoop);
            GL.Color4(red, green, blue, alpha);
            for (int i = 0; i < 32; i++)
            {
                float ang = (float)(i * Math.PI * 2.0 / 32.0);
                Vector3 p = center
                    + right * ((float)Math.Cos(ang) * radius)
                    + up * ((float)Math.Sin(ang) * radius);
                GL.Vertex3(p);
            }
            GL.End();
        }

        /// <summary>
        /// FIX (selected-axis highlight): draw a polyline through the
        /// canonical marker positions of one axis so the user sees a
        /// visible "line" running from the calibration origin through
        /// every marker on that axis. The selected axis is drawn with a
        /// brighter color and thicker line; non-selected axes get a
        /// fainter trace so they remain visible without competing for
        /// attention.
        /// </summary>
        private static void DrawAxisArc(Vector3[] positions, float r, float g, float b,
            float alpha, float lineWidth)
        {
            if (positions == null || positions.Length == 0) return;
            GL.LineWidth(lineWidth);
            GL.Begin(PrimitiveType.LineStrip);
            GL.Color4(r, g, b, alpha);
            // Start from the calibration origin (the first sample's
            // direction is implied by positions[0] - eye, but we can
            // approximate the origin as the eye position projected onto
            // the marker sphere). To keep this renderer-only helper
            // independent of the trainer's camera, we just connect the
            // markers themselves — the visual is still a clean arc.
            for (int i = 0; i < positions.Length; i++)
            {
                GL.Vertex3(positions[i].X, positions[i].Y, positions[i].Z);
            }
            GL.End();
        }

        private void DrawScreenCrosshair()
        {
            GL.Disable(EnableCap.DepthTest);
            GL.MatrixMode(MatrixMode.Projection);
            GL.PushMatrix();
            GL.LoadIdentity();
            GL.Ortho(0, Width, Height, 0, -1, 1);
            GL.MatrixMode(MatrixMode.Modelview);
            GL.PushMatrix();
            GL.LoadIdentity();
            float cx = Width * 0.5f;
            float cy = Height * 0.5f;
            const float arm = 9f;
            const float gap = 4f;
            GL.LineWidth(2f);
            GL.Begin(PrimitiveType.Lines);
            GL.Color4(0f, 0f, 0f, 0.9f);
            GL.Vertex2(cx - arm - 1f, cy); GL.Vertex2(cx - gap + 1f, cy);
            GL.Vertex2(cx + gap - 1f, cy); GL.Vertex2(cx + arm + 1f, cy);
            GL.Vertex2(cx, cy - arm - 1f); GL.Vertex2(cx, cy - gap + 1f);
            GL.Vertex2(cx, cy + gap - 1f); GL.Vertex2(cx, cy + arm + 1f);
            GL.Color4(0.9f, 0.98f, 1f, 1f);
            GL.Vertex2(cx - arm, cy); GL.Vertex2(cx - gap, cy);
            GL.Vertex2(cx + gap, cy); GL.Vertex2(cx + arm, cy);
            GL.Vertex2(cx, cy - arm); GL.Vertex2(cx, cy - gap);
            GL.Vertex2(cx, cy + gap); GL.Vertex2(cx, cy + arm);
            GL.End();
            GL.PopMatrix();
            GL.MatrixMode(MatrixMode.Projection);
            GL.PopMatrix();
            GL.MatrixMode(MatrixMode.Modelview);
            GL.Enable(EnableCap.DepthTest);
        }
        private void DrawIndoorRange()
        {
            const float floorRadius = 180f;
            const int segments = 180;
            GL.Begin(PrimitiveType.TriangleFan);
            GL.Color3(0.075f, 0.09f, 0.10f);
            GL.Vertex3(0f, 0f, 0f);
            for (int i = 0; i <= segments; i++)
            {
                double a = i * Math.PI * 2.0 / segments;
                GL.Vertex3((float)Math.Cos(a) * floorRadius, 0f,
                    (float)Math.Sin(a) * floorRadius);
            }
            GL.End();
            GL.LineWidth(1f);
            GL.Begin(PrimitiveType.Lines);
            for (int ring = 4; ring <= 176; ring += 4)
            {
                for (int i = 0; i < segments; i++)
                {
                    double a1 = i * Math.PI * 2.0 / segments;
                    double a2 = (i + 1) * Math.PI * 2.0 / segments;
                    GL.Color3(ring % 20 == 0 ? 0.22f : 0.13f,
                        ring % 20 == 0 ? 0.27f : 0.17f, 0.18f);
                    GL.Vertex3((float)Math.Cos(a1) * ring, 0.012f,
                        (float)Math.Sin(a1) * ring);
                    GL.Vertex3((float)Math.Cos(a2) * ring, 0.012f,
                        (float)Math.Sin(a2) * ring);
                }
            }
            for (int degree = 0; degree < 360; degree += 15)
            {
                double a = degree * Math.PI / 180.0;
                GL.Color3(degree % 45 == 0 ? 0.23f : 0.14f,
                    degree % 45 == 0 ? 0.28f : 0.18f, 0.20f);
                GL.Vertex3(0f, 0.018f, 0f);
                GL.Vertex3((float)Math.Cos(a) * floorRadius, 0.018f,
                    (float)Math.Sin(a) * floorRadius);
            }
            GL.End();
            const float shellRadius = 150f;
            const int azimuthSegments = 72;
            const int elevationSegments = 18;
            GL.LineWidth(1f);
            GL.Begin(PrimitiveType.Lines);
            for (int row = 1; row < elevationSegments; row++)
            {
                double elev = -Math.PI * 0.5 + row * Math.PI / elevationSegments;
                double yv = Math.Sin(elev) * shellRadius;
                double r = Math.Cos(elev) * shellRadius;
                for (int i = 0; i < azimuthSegments; i++)
                {
                    double a1 = i * Math.PI * 2.0 / azimuthSegments;
                    double a2 = (i + 1) * Math.PI * 2.0 / azimuthSegments;
                    GL.Color3(row == elevationSegments / 2 ? 0.22f : 0.12f,
                        row == elevationSegments / 2 ? 0.29f : 0.17f,
                        row == elevationSegments / 2 ? 0.32f : 0.20f);
                    GL.Vertex3((float)(Math.Cos(a1) * r), (float)yv, (float)(Math.Sin(a1) * r));
                    GL.Vertex3((float)(Math.Cos(a2) * r), (float)yv, (float)(Math.Sin(a2) * r));
                }
            }
            for (int degree = 0; degree < 360; degree += 15)
            {
                double a = degree * Math.PI / 180.0;
                for (int row = 0; row < elevationSegments; row++)
                {
                    double e1 = -Math.PI * 0.5 + row * Math.PI / elevationSegments;
                    double e2 = -Math.PI * 0.5 + (row + 1) * Math.PI / elevationSegments;
                    GL.Color3(0.12f, 0.17f, 0.20f);
                    GL.Vertex3((float)(Math.Cos(a) * Math.Cos(e1) * shellRadius),
                        (float)(Math.Sin(e1) * shellRadius),
                        (float)(Math.Sin(a) * Math.Cos(e1) * shellRadius));
                    GL.Vertex3((float)(Math.Cos(a) * Math.Cos(e2) * shellRadius),
                        (float)(Math.Sin(e2) * shellRadius),
                        (float)(Math.Sin(a) * Math.Cos(e2) * shellRadius));
                }
            }
            GL.End();
            for (int degree = 0; degree < 360; degree += 30)
            {
                double a = degree * Math.PI / 180.0;
                float x = (float)Math.Sin(a) * 32f;
                float z = (float)-Math.Cos(a) * 32f;
                float height = degree % 90 == 0 ? 7.5f : 4.0f;
                float halfWidth = degree % 90 == 0 ? 0.8f : 0.38f;
                GL.Begin(PrimitiveType.Quads);
                if (degree == 0) GL.Color3(0.15f, 0.85f, 0.55f);
                else if (degree % 90 == 0) GL.Color3(0.9f, 0.55f, 0.18f);
                else GL.Color3(0.24f, 0.42f, 0.52f);
                GL.Vertex3(x - halfWidth, 0f, z);
                GL.Vertex3(x + halfWidth, 0f, z);
                GL.Vertex3(x + halfWidth, height, z);
                GL.Vertex3(x - halfWidth, height, z);
                GL.End();
                GL.Begin(PrimitiveType.Lines);
                GL.Color3(0.72f, 0.82f, 0.88f);
                GL.Vertex3(x - halfWidth, height, z);
                GL.Vertex3(x + halfWidth, height, z);
                GL.Vertex3(x, height, z);
                GL.Vertex3(x, height + 0.8f, z);
                GL.End();
            }
        }
        private void DrawAzimuthTicks()
        {
            Vector3 forward = new Vector3(CalibrationForward.X, 0f, CalibrationForward.Z);
            if (forward.LengthSquared < 0.0001f)
                forward = new Vector3(0f, 0f, -1f);
            forward = Vector3.Normalize(forward);
            Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
            const float innerRadius = 20.5f;
            const float outerMinor = 21.0f;
            const float outerMajor = 21.8f;
            GL.LineWidth(2f);
            GL.Begin(PrimitiveType.Lines);
            for (int degree = 0; degree < 360; degree += 10)
            {
                double radians = degree * Math.PI / 180.0;
                Vector3 direction = Vector3.Normalize(
                    forward * (float)Math.Cos(radians) +
                    right * (float)Math.Sin(radians));
                float outer = degree % 30 == 0 ? outerMajor : outerMinor;
                if (degree == 0) GL.Color3(0.25f, 0.95f, 0.62f);
                else if (degree % 30 == 0) GL.Color3(0.95f, 0.72f, 0.25f);
                else GL.Color3(0.36f, 0.48f, 0.53f);
                GL.Vertex3(direction.X * innerRadius, 0.03f, direction.Z * innerRadius);
                GL.Vertex3(direction.X * outer, 0.03f, direction.Z * outer);
            }
            GL.End();
        }
        // Always-screen-facing point marker. Uses GL.POINTS with an oversized
        // point size so the target is visible from any camera angle, including
        // near-vertical downward pitch where the calibration plane circle would
        // be edge-on (and effectively invisible).
        // The crosshair lines (X + Z in world axes) give a sense of scale and
        // orientation even when the point sprite is small on a distant monitor.
        private static void DrawMarker(Vector3 p, float size, float r, float g, float b)
        {
            // Size in pixels for the point sprite (fixed screen size regardless
            // of distance). The old version multiplied by 30 to get a big dot;
            // that is kept here so callers can pass small world units and still
            // get a clearly visible pixel marker.
            GL.PointSize(Math.Max(6f, size * 30f));
            GL.Begin(PrimitiveType.Points);
            GL.Color3(r, g, b);
            GL.Vertex3(p.X, p.Y, p.Z);
            GL.End();
            // Crosshair lines in the world X / Z axes. These are drawn with the
            // calibration-plane depth test still enabled, so they naturally clip
            // against the floor and other geometry. Size is in metres.
            GL.LineWidth(2f);
            GL.Begin(PrimitiveType.Lines);
            GL.Color3(r, g, b);
            GL.Vertex3(p.X - size, p.Y, p.Z);
            GL.Vertex3(p.X + size, p.Y, p.Z);
            GL.Vertex3(p.X, p.Y, p.Z - size);
            GL.Vertex3(p.X, p.Y, p.Z + size);
            // Small vertical stem so the dot appears anchored, not floating.
            GL.Vertex3(p.X, p.Y - size * 0.5f, p.Z);
            GL.Vertex3(p.X, p.Y + size * 0.5f, p.Z);
            GL.End();
        }
        private static void DrawCalibrationCircle(Vector3 center, Vector3 right, Vector3 up,
            float radius, float red, float green, float blue)
        {
            Vector3 normal = Vector3.Normalize(Vector3.Cross(right, up));
            center += normal * 0.035f;
            GL.LineWidth(3f);
            GL.Begin(PrimitiveType.LineLoop);
            GL.Color3(red, green, blue);
            for (int i = 0; i < 64; i++)
            {
                float angle = (float)(i * Math.PI * 2.0 / 64.0);
                Vector3 p = center + right * ((float)Math.Cos(angle) * radius)
                    + up * ((float)Math.Sin(angle) * radius);
                GL.Vertex3(p);
            }
            GL.End();
            GL.PointSize(5f);
            GL.Begin(PrimitiveType.Points);
            GL.Color3(red, green, blue);
            GL.Vertex3(center);
            GL.End();
        }

        // ===========================================================================
        // FIX #13: visual-feedback helper methods.
        //
        // These render per-marker numeric labels (SuccessfulAttemptCount +
        // LastSignedErrorCm), an "uncertainty bar" proportional to the
        // cell's RobustSigmaCm, smooth color gradients from white to yellow
        // to green based on attempt count, and red pulsing rings around
        // drift-detected cells.
        // ===========================================================================

        /// <summary>
        /// Categorical marker color based on recorded calibration state.
        ///   no recorded attempt -> axis color
        ///   attempted but not verified -> bright yellow
        ///   verified -> bright green
        /// </summary>
        internal static void GetMarkerGradientColor(
            bool attempted,
            int successfulAttempts,
            bool firstRoundComplete,
            bool verified,
            int axisRgbRed, int axisRgbGreen, int axisRgbBlue,
            out float r, out float g, out float b)
        {
            if (verified) { r = 0.20f; g = 0.95f; b = 0.30f; return; }
            if (attempted) { r = 1.0f; g = 0.82f; b = 0.08f; return; }

            r = axisRgbRed / 255f;
            g = axisRgbGreen / 255f;
            b = axisRgbBlue / 255f;
        }

        /// <summary>
        /// Draw a small red "uncertainty bar" under each marker, length
        /// proportional to 2 * RobustSigmaCm. Visual cue for "this cell's
        /// measurement is noisy - re-verify if you see drift".
        /// </summary>
        internal static void DrawMarkerUncertaintyBar(
            Vector3 markerPos,
            Vector3 axisDirection,
            double robustSigmaCm,
            double cmToArenaUnits)
        {
            if (robustSigmaCm <= 0.0 || cmToArenaUnits <= 0.0) return;
            double widthCm = Math.Min(5.0, 2.0 * robustSigmaCm);
            float halfWidth = (float)(widthCm * 0.5 * cmToArenaUnits);
            Vector3 dir = Vector3.Normalize(axisDirection);
            Vector3 a = markerPos - dir * halfWidth;
            Vector3 b = markerPos + dir * halfWidth;

            GL.LineWidth(2f);
            GL.Begin(PrimitiveType.Lines);
            GL.Color4(0.95f, 0.30f, 0.30f, 0.65f);
            GL.Vertex3(a.X, a.Y, a.Z);
            GL.Vertex3(b.X, b.Y, b.Z);
            GL.End();
        }

        /// <summary>
        /// Top-level overlay hook. Called by DrawTrainingAxes after every
        /// marker has been drawn. Renders numeric labels, uncertainty bars,
        /// and drift rings on all visible markers.
        /// </summary>
        internal void DrawTrainingMarkerOverlay()
        {
            if (_trainingMarkerPositions == null || _trainingMarkerDistanceCm == null) return;
            if (TrainingStates == null) return;

            // Recover cmToArenaUnits from the outermost marker.
            double cmToArenaUnits = 1.0;
            try
            {
                var positions0 = _trainingMarkerPositions[0];
                var distances0 = _trainingMarkerDistanceCm[0];
                if (positions0 != null && positions0.Length > 0 && distances0 != null && distances0.Length > 0)
                {
                    Vector3 p0 = positions0[0];
                    double distFromEye = Math.Sqrt(p0.X * p0.X + p0.Y * p0.Y + p0.Z * p0.Z);
                    double cm = distances0[0];
                    if (cm > 0.0)
                        cmToArenaUnits = distFromEye / cm;
                }
            }
            catch (Exception ex) { DesktopSensLog.Warn("Could not derive arena marker scale", ex); }

            for (int axisIdx = 0; axisIdx < _trainingMarkerPositions.Length; axisIdx++)
            {
                var positions = _trainingMarkerPositions[axisIdx];
                var distances = axisIdx < _trainingMarkerDistanceCm.Length
                    ? _trainingMarkerDistanceCm[axisIdx] : null;
                if (positions == null || distances == null) continue;
                int count = Math.Min(positions.Length, distances.Length);
                // FIX (8/16-way): resolve the angle zone from the active
                // direction-count mode so the overlay labels and drift
                // rings land on the correct cells.
                int overlayDirectionCount = ActiveDirectionCount > 0 ? ActiveDirectionCount : 4;
                int[] overlayAngleZones = DesktopSensTrainingState.AngleZoneIndicesForCount(overlayDirectionCount);
                int angleZone = axisIdx < overlayAngleZones.Length
                    ? overlayAngleZones[axisIdx]
                    : 0;

                // PATCH 5 from review: per-marker radial direction from
                // the camera eye. The eye is always at (0, 1.7, 0) in
                // this renderer's world space (see RenderScene). For
                // cardinal markers this is the radial axis direction;
                // for diagonal markers it's the correct bent-axis
                // direction. This works for every direction count
                // without a hardcoded index map (the previous code
                // returned (0,0,0) for axisIdx >= 4 and produced NaN
                // vertices after Normalize).
                const float eyeY = 1.7f;
                Vector3 eye = new Vector3(0f, eyeY, 0f);

                for (int i = 0; i < count; i++)
                {
                    if (!DesktopSensTrainingState.IsCalibrationDistanceZone(i)) continue;
                    int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, i);
                    if (!TrainingStates.TryGetValue(cellIndex, out var state) || state == null)
                        continue;
                    if (state.SuccessfulAttemptCount == 0 && !state.Verified && !state.FirstRoundComplete)
                        continue;

                    Vector3 markerDir = positions[i] - eye;
                    Vector3 axisDir = markerDir.LengthSquared > 1e-6f
                        ? Vector3.Normalize(markerDir)
                        : Vector3.UnitY;

                    DrawMarkerUncertaintyBar(positions[i], axisDir, state.RobustSigmaCm, cmToArenaUnits);
                    DrawMarkerLabel(positions[i], state.SuccessfulAttemptCount,
                        state.LastSignedErrorCm, state.RobustSigmaCm, state.Verified);
                }
            }

            // FIX #8: draw a red pulsing ring around drifted cells.
            GL.LineWidth(2.5f);
            // FIX (8/16-way): resolve the angle zone array once so the
            // drift-ring loop also works in 8-way and 16-way modes.
            int driftDirectionCount = ActiveDirectionCount > 0 ? ActiveDirectionCount : 4;
            int[] driftAngleZones = DesktopSensTrainingState.AngleZoneIndicesForCount(driftDirectionCount);
            for (int axisIdx = 0; axisIdx < _trainingMarkerPositions.Length; axisIdx++)
            {
                var positions = _trainingMarkerPositions[axisIdx];
                if (positions == null) continue;
                int count = positions.Length;
                int angleZone = axisIdx < driftAngleZones.Length
                    ? driftAngleZones[axisIdx]
                    : 0;
                for (int i = 0; i < count; i++)
                {
                    if (!DesktopSensTrainingState.IsCalibrationDistanceZone(i)) continue;
                    int cellIndex = DesktopSensProfile.GetCellIndex(angleZone, i);
                    if (!TrainingStates.TryGetValue(cellIndex, out var state) || state == null)
                        continue;
                    if (!state.DriftDetected) continue;

                    Vector3 pos = positions[i];
                    float pulse = 0.6f + 0.4f * (float)Math.Sin(PulsePhase * Math.PI * 2);
                    GL.Color4(1.0f, 0.15f, 0.15f, pulse);
                    GL.PointSize(38f);
                    GL.Begin(PrimitiveType.Points);
                    GL.Vertex3(pos.X, pos.Y, pos.Z);
                    GL.End();
                }
            }
        }

        /// <summary>
        /// Render a small numeric label on top of a marker showing
        /// SuccessfulAttemptCount and LastSignedErrorCm.
        /// </summary>
        internal void DrawMarkerLabel(
            Vector3 markerPosWorld,
            int successfulAttempts,
            double lastSignedErrorCm,
            double robustSigmaCm,
            bool verified)
        {
            int[] viewport = new int[4];
            double[] modelview = new double[16];
            double[] projection = new double[16];
            try
            {
                MakeCurrent();
                GL.GetInteger(GetPName.Viewport, viewport);
                GL.GetDouble(GetPName.ModelviewMatrix, modelview);
                GL.GetDouble(GetPName.ProjectionMatrix, projection);
            }
            catch { return; }

            Vector2? screen = ProjectMarkerToScreen(markerPosWorld, viewport, modelview, projection);
            if (!screen.HasValue) return;

            float sx = screen.Value.X;
            float sy = screen.Value.Y;

            string text;
            if (verified) text = string.Format("V s={0:F2}", robustSigmaCm);
            else if (successfulAttempts > 0)
            {
                string sign = lastSignedErrorCm >= 0 ? "+" : "";
                text = string.Format("{0} | {1}{2:F2}cm", successfulAttempts, sign, lastSignedErrorCm);
            }
            else return;

            DrawTextOverlay(text, sx, sy + 14f, verified ? Color.LimeGreen : Color.Gainsboro);
        }

        /// <summary>
        /// Project a 3D world position to screen coordinates.
        /// </summary>
        private Vector2? ProjectMarkerToScreen(Vector3 world, int[] viewport, double[] modelview, double[] projection)
        {
            double vx = world.X, vy = world.Y, vz = world.Z;
            double ex = modelview[0] * vx + modelview[4] * vy + modelview[8] * vz + modelview[12];
            double ey = modelview[1] * vx + modelview[5] * vy + modelview[9] * vz + modelview[13];
            double ez = modelview[2] * vx + modelview[6] * vy + modelview[10] * vz + modelview[14];
            double ew = modelview[3] * vx + modelview[7] * vy + modelview[11] * vz + modelview[15];
            double cx = projection[0] * ex + projection[4] * ey + projection[8] * ez + projection[12] * ew;
            double cy = projection[1] * ex + projection[5] * ey + projection[9] * ez + projection[13] * ew;
            double cz = projection[2] * ex + projection[6] * ey + projection[10] * ez + projection[14] * ew;
            double cw = projection[3] * ex + projection[7] * ey + projection[11] * ez + projection[15] * ew;
            if (cw <= 0.0001) return null;
            double ndcX = cx / cw;
            double ndcY = cy / cw;
            double screenX = viewport[0] + (ndcX + 1.0) * 0.5 * viewport[2];
            double screenY = viewport[1] + (1.0 - (ndcY + 1.0) * 0.5) * viewport[3];
            return new Vector2((float)screenX, (float)screenY);
        }

        // Bounded FIFO texture cache for label rendering.
        private const int MaxLabelTextures = 200;
        private readonly Dictionary<string, int> _labelTextureCache = new Dictionary<string, int>();
        private readonly Queue<string> _labelTextureOrder = new Queue<string>();

        /// <summary>
        /// Render a text string as a 2D screen-space overlay.
        /// </summary>
        internal void DrawTextOverlay(string text, float screenX, float screenY, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (Width < 1 || Height < 1 || IsDisposed) return;

            string cacheKey = text;
            int texId;
            if (!_labelTextureCache.TryGetValue(cacheKey, out texId))
            {
                texId = CreateTextTexture(text);
                if (texId == 0) return;
                _labelTextureCache[cacheKey] = texId;
                _labelTextureOrder.Enqueue(cacheKey);
                while (_labelTextureCache.Count > MaxLabelTextures && _labelTextureOrder.Count > 0)
                {
                    string oldestKey = _labelTextureOrder.Dequeue();
                    if (_labelTextureCache.TryGetValue(oldestKey, out int oldestTexture))
                    {
                        try
                        {
                            if (oldestTexture != 0) GL.DeleteTexture(oldestTexture);
                        }
                        catch (Exception ex)
                        {
                            DesktopSensLog.Warn("Could not evict an old label texture", ex);
                        }
                        _labelTextureCache.Remove(oldestKey);
                    }
                }
            }
            if (texId == 0) return;

            int texW = 128, texH = 24; // fixed label texture size

            GL.Disable(EnableCap.DepthTest);
            GL.MatrixMode(MatrixMode.Projection);
            GL.PushMatrix();
            GL.LoadIdentity();
            GL.Ortho(0, Width, Height, 0, -1, 1);
            GL.MatrixMode(MatrixMode.Modelview);
            GL.PushMatrix();
            GL.LoadIdentity();

            GL.Enable(EnableCap.Texture2D);
            GL.BindTexture(TextureTarget.Texture2D, texId);
            GL.Color4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
            GL.Begin(PrimitiveType.Quads);
            GL.TexCoord2(0, 0); GL.Vertex2(screenX, screenY);
            GL.TexCoord2(1, 0); GL.Vertex2(screenX + texW, screenY);
            GL.TexCoord2(1, 1); GL.Vertex2(screenX + texW, screenY + texH);
            GL.TexCoord2(0, 1); GL.Vertex2(screenX, screenY + texH);
            GL.End();
            GL.Disable(EnableCap.Texture2D);

            GL.PopMatrix();
            GL.MatrixMode(MatrixMode.Projection);
            GL.PopMatrix();
            GL.MatrixMode(MatrixMode.Modelview);
            GL.Enable(EnableCap.DepthTest);
        }

        private int CreateTextTexture(string text)
        {
            try
            {
                using (var bmp = new Bitmap(128, 24, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(0, 0, 0, 0));
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    using (var brush = new SolidBrush(Color.White))
                    using (var font = new Font("Consolas", 9f, FontStyle.Regular))
                    {
                        g.DrawString(text, font, brush, new PointF(0, 0));
                    }
                    int texId;
                    GL.GenTextures(1, out texId);
                    GL.BindTexture(TextureTarget.Texture2D, texId);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                    var data = bmp.LockBits(
                        new Rectangle(0, 0, bmp.Width, bmp.Height),
                        System.Drawing.Imaging.ImageLockMode.ReadOnly,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                        data.Width, data.Height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);
                    bmp.UnlockBits(data);
                    return texId;
                }
            }
            catch (Exception ex)
            {
                DesktopSensLog.Warn("Could not create a label texture", ex);
                return 0;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _labelTextureCache.Count > 0)
            {
                bool contextReady = false;
                try
                {
                    if (IsHandleCreated)
                    {
                        MakeCurrent();
                        contextReady = true;
                    }
                }
                catch (Exception ex)
                {
                    DesktopSensLog.Warn("Could not make the arena GL context current during disposal", ex);
                }

                if (contextReady)
                {
                    foreach (int textureId in _labelTextureCache.Values)
                    {
                        if (textureId == 0) continue;
                        try { GL.DeleteTexture(textureId); }
                        catch (Exception ex) { DesktopSensLog.Warn("Could not release an arena label texture", ex); }
                    }
                }

                _labelTextureCache.Clear();
                _labelTextureOrder.Clear();
            }

            base.Dispose(disposing);
        }
    }
}