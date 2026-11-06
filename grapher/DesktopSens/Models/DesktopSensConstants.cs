namespace grapher.DesktopSens.Models
{
    public static class DesktopSensConstants
    {
        // Angular: 32 bins cover the full 360-degree mouse-movement direction.
        public const int AngularZoneCount = 32;
        public const double AngularZoneSizeDegrees = 11.25;

        // ─────────────────────────────────────────────────────────────────────
        // DISTANCE ZONES — REFACTORED.
        //
        // The user's directive (Persian, paraphrased): "Training is the
        // central authority for the points, the distance between them,
        // and the real zone count. Every downstream component must read
        // its zone count from Training — nobody is allowed to keep a
        // hardcoded 46 or a parallel definition."
        //
        // The runtime value of DistanceZoneCount is now
        //     TrainingGeometry.Current.DistanceZoneCount
        // (default 18 for a 10° step over 180°, since one zone == one
        // training marker in the new model).
        //
        // The constants below are kept ONLY for two reasons:
        //   1. To size the per-cell arrays in DesktopSensProfile's field
        //      initializers, which run BEFORE the trainer has had a
        //      chance to call TrainingGeometry.Configure(...). The
        //      calibrator's EnsureArray helper then resizes every array
        //      to TrainingGeometry.Current.DistanceZoneCount on the
        //      first call, preserving the leading prefix of values.
        //   2. As a historical record of the legacy 4-band model so
        //      anyone re-reading an old serialized profile can reason
        //      about what the indices meant at the time.
        //
        // NO new code should read these constants for runtime zone
        // math. Use TrainingGeometry.Current.* instead.
        // ─────────────────────────────────────────────────────────────
        public const int LegacyDistanceZoneCount = 46;
        public const int LegacyMicroZoneCount = 6;
        public const int LegacyShortZoneCount = 7;
        public const int LegacyMediumZoneCount = 5;
        public const int LegacyLongZoneCount = 28;
        public const double LegacyMicroZoneSizeCm = 0.5;
        public const double LegacyShortZoneSizeCm = 1.0;
        public const double LegacyMediumZoneSizeCm = 2.0;
        public const double LegacyLongZoneSizeCm = 80.0 / LegacyLongZoneCount;
        public const double LegacyMicroZoneMaxCm = 3.0;
        public const double LegacyShortZoneMaxCm = 10.0;
        public const double LegacyMediumZoneMaxCm = 20.0;
        public const double LegacyLongZoneMaxCm = 100.0;
        public const int LegacyShortZoneStartIndex = 6;
        public const int LegacyMediumZoneStartIndex = 13;
        public const int LegacyLongZoneStartIndex = 18;

        // Default initial size for per-cell arrays sized at field
        // initializer time. Equals the legacy count so the array is
        // non-empty until the calibrator resizes it.
        public const int DefaultDistanceZoneCount = LegacyDistanceZoneCount;

        // Backward-compat aliases. Existing field initializers in
        // DesktopSensProfile reference these names; we keep them as
        // aliases to DefaultDistanceZoneCount so the codebase keeps
        // compiling while the runtime value migrates to
        // TrainingGeometry.Current.DistanceZoneCount. New code must not
        // use these aliases.
        public const int DistanceZoneCount = LegacyDistanceZoneCount;
        public const int MicroZoneCount = LegacyMicroZoneCount;
        public const int ShortZoneCount = LegacyShortZoneCount;
        public const int MediumZoneCount = LegacyMediumZoneCount;
        public const int LongZoneCount = LegacyLongZoneCount;
        public const double MicroZoneSizeCm = LegacyMicroZoneSizeCm;
        public const double ShortZoneSizeCm = LegacyShortZoneSizeCm;
        public const double MediumZoneSizeCm = LegacyMediumZoneSizeCm;
        public const double LongZoneSizeCm = LegacyLongZoneSizeCm;
        public const double MicroZoneMaxCm = LegacyMicroZoneMaxCm;
        public const double ShortZoneMaxCm = LegacyShortZoneMaxCm;
        public const double MediumZoneMaxCm = LegacyMediumZoneMaxCm;
        public const double LongZoneMaxCm = LegacyLongZoneMaxCm;
        public const int ShortZoneStartIndex = LegacyShortZoneStartIndex;
        public const int MediumZoneStartIndex = LegacyMediumZoneStartIndex;
        public const int LongZoneStartIndex = LegacyLongZoneStartIndex;

        // Rotation
        public const int RotationFourierOrder = 3;
        public const double RotationMaxResidualDegrees = 3.0;
        public const double RotationMinFitQuality = 0.7;

        // Flick completion
        public const double MovementStartVelocityThresholdPxPerSec = 50.0;
        public const double MovementEndVelocityThresholdPxPerSec = 5.0;
        public const double StabilizationPeriodMs = 150.0;

        // CS2 default m_yaw: one raw mouse count rotates the view by sensitivity * 0.022 degrees.
        public const double Cs2YawDegreesPerCount = 0.022;
        public const double MaxGameTurnDegrees = 180.0;

        // LUT
        public const int LutMaxPoints = 257;
        public const int LutMinPoints = 2;

        // Hardware
        public const int MinMouseDPI = 100;
        public const int MaxMouseDPI = 30000;
        public const double MinPollingRateHz = 125;
        public const double TargetPollingRateHz = 1000;

        // Schema
        // Bumped 4 -> 5: NeededDistanceCm added to DesktopSensTrial, ASDW-confirmed
        // target is now part of the calibration residual. Older profiles fall back
        // to TargetDistanceCm when NeededDistanceCm is NaN.
        public const int CurrentSchemaVersion = 5;
        // Bumped 7 -> 8: sample-fit anchors now persist their directional
        // Base Sens context so they can be renormalized after a Base Sens change.
        // Bumped 8 -> 9: zone target aggregation now uses the ASDW-confirmed
        // effective target consistently with ComputeResidualSignedError so the
        // (target, residual) pairs stay coherent.
        // Bumped 9 -> 10: stale CalibrationCellResultCache and
        // CalibrationResidualCache entries written under v8/v9 (which used the
        // raw TargetDistanceCm in zone aggregation) must not be reused — the
        // aggregated targets differ, so any cached factor is invalid.
        public const int CurrentModelVersion = 10;

        // Shared calibration defaults and physical thresholds.
        public const double NeutralFactor = 1.0;
        public const double MinimumObservedDistanceCm = 0.05;
        public const double MinimumDenominatorCm = 0.05;
        // FIX (user request): the 3 cm micro-flick exclusion is REMOVED.
        // Previously this was 3.0, which forced the calibrator to skip every
        // trial whose target lay inside the first 3 cm and to compute the
        // per-cell factor only on the EXCESS distance beyond 3 cm. That
        // left micro-flicks (the most common flick length in tactical
        // shooters) with a neutral, un-calibrated multiplier and produced
        // the "weak calibration" the user reported.
        //
        // Setting this to 0.0 makes IsCalibrationDistanceZone(d) return
        // true for every zone with a positive center (i.e. every zone),
        // routes every cell through the CellAccelerationFactor path in
        // DesktopSensCalibrator, and makes the per-sample "excess" math
        // degenerate to the full target distance — exactly the uniform
        // behaviour the user wants across the full range.
        //
        // The legacy directional base-sensitivity fields
        // (Right/Down/Left/UpSensitivityFactor) are no longer read by the
        // merger or the translator; they are kept only for serialisation
        // backward compatibility with older profile files.
        public const double BaseZoneEndCm = 0.0;
        public const double MinimumCalibrationFactor = 0.1;
        public const double MaximumCalibrationFactor = 10.0;
        public const double DefaultToleranceCm = 1.0;

        // ─────────────────────────────────────────────────────────────────────────
        // Zone-0 sub-marker density (user directive: "3 ta noghteh calib be
        // zone avval ezafeh kon baraye har 16 mehvar").
        //
        // This is the compile-time default for TrainingGeometry.Zone0SubMarkerCount.
        // The runtime value can be overridden via TrainingGeometry.Configure(..., int)
        // but in practice every trainer session uses this default. The first zone
        // along each axis is subdivided into this many calibration points at
        // angular positions step/N, 2*step/N, ..., step.
        // ─────────────────────────────────────────────────────────────────────────
        public const int DefaultZone0SubMarkerCount = 3;

        // Conversion
        public const double InchesToCm = 2.54;
        public const double AspectRatioWidthFactor = 0.8716;
        public const double AspectRatioHeightFactor = 0.4903;

        // ─────────────────────────────────────────────────────────────────────────
        // Acceleration curve fitting (Model v5)
        // ─────────────────────────────────────────────────────────────────────────
        //
        // These constants bound the parameter search during curve fitting and are
        // also the legal ranges RawAccel accepts when applying a fitted curve.
        // Bounds are intentionally loose enough to fit any physically-plausible
        // mouse behaviour, but tight enough that a degenerate fit (e.g. a 1000x
        // sensitivity at one speed) cannot survive.

        /// <summary>Minimum samples required before any curve model is fitted. Below this, the cell uses the legacy ratio.</summary>
        public const int CurveFittingMinSamples = 5;

        /// <summary>Minimum samples required before the 4-parameter synchronous curve is attempted.</summary>
        public const int SynchronousMinSamples = 8;

        /// <summary>Levenberg-Marquardt iteration cap. The fitter rarely needs more than 20-30; 50 is a safety margin.</summary>
        public const int CurveFittingMaxIterations = 50;

        /// <summary>Relative improvement below this threshold counts as convergence.</summary>
        public const double CurveFittingTolerance = 1e-6;

        /// <summary>Maximum speed (cm/s) at which acceleration begins to engage. Above this, accel is active.</summary>
        public const double InputOffsetMax = 50.0;

        /// <summary>Linear mode acceleration slope: sensitivity = 1 + accel * (v - offset). Bounded to ±2.0 / cm/s.</summary>
        public const double LinearAccelMin = -2.0;
        public const double LinearAccelMax = 2.0;

        /// <summary>Natural mode "limit" — the asymptotic sensitivity gain above the offset. 0 = no accel, 3 = up to 4x.</summary>
        public const double NaturalLimitMin = 0.0;
        public const double NaturalLimitMax = 3.0;

        /// <summary>Natural mode "decay rate" — how quickly the curve approaches the limit. Higher = sharper transition.</summary>
        public const double NaturalDecayMin = 0.001;
        public const double NaturalDecayMax = 1.0;

        /// <summary>Power mode "scale" — multiplier on (v - offset) before the power is applied.</summary>
        public const double PowerScaleMin = 0.001;
        public const double PowerScaleMax = 10.0;

        /// <summary>Power mode "exponent" — curve shape. &lt;1 = concave (rapid early growth), &gt;1 = convex (slow early growth).</summary>
        public const double PowerExponentMin = 0.1;
        public const double PowerExponentMax = 5.0;

        /// <summary>Synchronous mode "sync speed" — the input speed (cm/s) at which sensitivity = (motivity + 1) / 2.</summary>
        public const double SyncSpeedMax = 200.0;

        /// <summary>Synchronous mode "motivity" — the asymptotic sensitivity ratio. 1.0 = no accel, 3.0 = up to 3x.</summary>
        public const double MotivityMin = 1.0;
        public const double MotivityMax = 3.0;

        /// <summary>Synchronous mode "gamma" — curve bend. 1.0 = symmetric S-curve, &lt;1 = sharper transition, &gt;1 = softer.</summary>
        public const double GammaMin = 0.1;
        public const double GammaMax = 5.0;

        // Per-cell curve parameter storage. Each cell stores up to
        // MaxCurveParameters doubles. The meaning depends on the mode (see
        // AccelCurveFitter.Evaluate). Flattening into a fixed-size array keeps
        // the profile JSON schema stable across model versions.
        public const int MaxCurveParameters = 4;

        // 4-way directional curve storage (R/L/U/D), same MaxCurveParameters.
        public const int DirectionalCount = 4;

        // ─────────────────────────────────────────────────────────────────────────
        // Sample-fit LUT (Model v6)
        //
        // Instead of summarizing each cell by a single median speed, the calibrator
        // now fits the continuous multiplier f(v) directly to the raw per-sample
        // (speed, displacement) observations. The driver applies f at the instant
        // speed of every sample, so a proper per-sample fit is what the driver
        // actually needs.
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>Default number of interpolation nodes in the sample-fit LUT.</summary>
        public const int SampleLutDefaultNodes = 12;

        /// <summary>Minimum nodes (below this the fit is under-determined).</summary>
        public const int SampleLutMinNodes = 8;

        /// <summary>Maximum nodes (RawAccel caps at 257 points; we keep room for bookends).</summary>
        public const int SampleLutMaxNodes = 16;

        /// <summary>Speed range for the sample-fit nodes (cm/s).</summary>
        public const double SampleLutMinSpeedCmPerSec = 1.0;
        public const double SampleLutMaxSpeedCmPerSec = 200.0;

        /// <summary>Ridge prior on (f - 1) to keep the fit well-behaved when data is sparse.</summary>
        public const double SampleLutPriorWeight = 1e-3;

        /// <summary>Smoothness penalty between adjacent nodes.</summary>
        public const double SampleLutSmoothWeight = 1e-3;

        /// <summary>
        /// Anchor weight for previously-verified LUT points. Anchors pin the
        /// nearest node to its previous factor so a re-fit cannot rewrite the
        /// curve at speeds the user already verified. The weight is large
        /// relative to the per-trial data weight (=1.0) and the smoothness
        /// penalty (=1e-3) so the anchored node barely moves, while still
        /// allowing the smoothness term to blend it into adjacent free nodes.
        /// </summary>
        public const double SampleLutAnchorWeight = 1e3;

        /// <summary>Minimum confidence weight a yellow cell contributes to the LUT.</summary>
        public const double YellowCellMinLutWeight = 0.05;
        // A yellow cell must never carry the same fitting influence as green.
        public const double YellowCellMaxLutWeight = 0.5;

        /// <summary>Factor-change threshold above which the trainer triggers a merge.</summary>
        public const double MergeFactorChangeThreshold = 0.01;

        // ─────────────────────────────────────────────────────────────────────────
        // NEW (Model v10, magnitude-aware slope boost): the per-cell acceleration
        // factor is multiplied by a sign-aware, magnitude-aware boost derived
        // from the ABSOLUTE under/over-flick magnitude of the cell's trials.
        //
        // The user's directive: a 1-cm under-flick should produce a SMALL
        // correction, but a 5-cm under-flick should produce a MUCH LARGER
        // correction (steeper slope). The relationship is NON-LINEAR: the
        // boost grows faster than linearly with the under-flick magnitude.
        //
        //     boost = sign(signedError) * K * |signedError|^P
        //     factor = baseFactor * (1 + boost)
        //
        //   * K (MagnitudeSlopeCoefficient): overall boost scale
        //   * P (MagnitudeSlopePower): non-linearity. P=1 → linear,
        //     P>1 → larger under-flicks produce disproportionately larger
        //     corrections (the user's intent: "5-cm under-flick needs more
        //     than 5× the correction of a 1-cm under-flick")
        //
        // The boost is SIGN-AWARE: under-flick (signedError < 0) increases
        // the factor, over-flick (signedError > 0) decreases it. Both signs
        // use the same magnitude formula, so over-flick 5-cm produces the
        // same magnitude of correction as under-flick 5-cm, just opposite
        // direction.
        //
        // Zone index is NOT used: the user explicitly said the slope must
        // depend on the under-flick magnitude, not on which zone the trial
        // landed in. A 4-cm under-flick in zone 2 produces the same boost
        // as a 4-cm under-flick in zone 8.
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Overall scale of the magnitude-aware boost. At MagnitudeSlopePower=1.5
        /// and a 1-cm under-flick, this produces a boost of approximately
        /// MagnitudeSlopeCoefficient * 1 = MagnitudeSlopeCoefficient.
        /// </summary>
        public const double MagnitudeSlopeCoefficient = 0.02;

        /// <summary>
        /// Non-linearity exponent. P=1 → linear (each extra cm of under-flick
        /// adds the same boost). P&gt;1 → larger under-flicks produce
        /// disproportionately larger boosts. P=1.5 means a 5-cm under-flick
        /// produces ~11× the boost of a 1-cm under-flick (instead of 5×).
        /// </summary>
        public const double MagnitudeSlopePower = 1.5;

        /// <summary>
        /// Dead-zone (in cm) below which the boost is suppressed. Small
        /// under-flicks (e.g. 0.5 cm) are typically sensor noise rather than
        /// a real scaling error, so the boost should not fire for them. A
        /// 1-cm dead-zone matches the cell tolerance and prevents the boost
        /// from contaminating high-precision calibration tests.
        /// </summary>
        public const double MagnitudeSlopeDeadZoneCm = 1.0;

        /// <summary>
        /// Maximum boost magnitude (positive or negative) that can be applied
        /// to a single cell's factor. Prevents a single bad trial from
        /// exploding the factor when the absolute under-flick is very large.
        /// </summary>
        public const double MagnitudeSlopeMaxBoost = 0.5;

        // ─────────────────────────────────────────────────────────────────────────
        // NEW (Model v10, adaptive convergence): the per-cell acceleration
        // factor is updated INCREMENTALLY across trains, rather than recomputed
        // from scratch on every Calibrate() call. The user's directive:
        //
        //   "seri aval 1.5 zarib dad bazam 2 sant to taraine badi kam ovord
        //    bayad shibo bishtaro bishtar koneh ta moghei ke dige noghteh
        //    sabz beshe"
        //
        // The implementation interpolates between the previously stored factor
        // (the prior) and the freshly-computed estimate from the current
        // trial history:
        //
        //     adaptiveFactor = prior + alpha * (newEstimate - prior)
        //
        //   alpha grows with trial count:
        //     1 trial  → alpha = AdaptiveLearningRateMin (cautious, e.g. 0.3)
        //     5 trials → alpha = midpoint
        //     10+      → alpha = AdaptiveLearningRateMax (confident, e.g. 0.7)
        //
        // The bidirectional property (over-flick reduces, under-flick
        // increases) is preserved automatically: the new estimate can be
        // either > or < the prior, so the adaptive factor moves in the
        // appropriate direction.
        //
        // This is PER-CELL: each cellIndex keeps its own prior, so zone 2
        // can have factor=1.23 (under-flick) while zone 3 has factor=0.24
        // (over-flick) at the same time. There is NO single repeated
        // number scaled by distance — every cell converges to its own
        // observed correction.
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Initial learning rate (applied when only 1 trial has been recorded).
        /// A small value means the first train only nudges the factor toward
        /// the new estimate by this fraction (e.g. 0.3 = 30% of the way).
        /// </summary>
        public const double AdaptiveLearningRateMin = 0.3;

        /// <summary>
        /// Asymptotic learning rate (applied when AdaptiveLearningRateFullTrialCount
        /// or more trials have been recorded). At this point the calibrator
        /// trusts the new estimate heavily, but never 100% — the prior
        /// still carries some weight so a single noisy trial cannot
        /// derail a converged cell.
        /// </summary>
        public const double AdaptiveLearningRateMax = 0.7;

        /// <summary>
        /// Trial count at which the learning rate reaches its maximum.
        /// Below this count, alpha is interpolated linearly between
        /// AdaptiveLearningRateMin and AdaptiveLearningRateMax.
        /// </summary>
        public const int AdaptiveLearningRateFullTrialCount = 10;

        /// <summary>
        /// Initial value of CellAccelerationFactor when a cell is first
        /// touched (no prior history). A value of 1.0 means "no correction
        /// yet — start neutral". The first train will move the factor
        /// toward the new estimate by AdaptiveLearningRateMin.
        /// </summary>
        public const double AdaptiveInitialFactor = 1.0;

        /// <summary>
        /// Hard cap on the per-cell factor AFTER adaptive convergence. This
        /// prevents a single trial with a very large under-flick (e.g. flick=1,
        /// target=20 → raw ratio=20) from blowing up the cell's factor on the
        /// first train and crashing the LUT builder / driver apply path.
        ///
        /// The user's directive: "noghteh aval calib nemishehey shibo tondtar
        /// mikoneh ta jai ke barnameh crash mikoneh" — the first point must
        /// NOT crash the program by jumping to an extreme factor value. The
        /// cap keeps the first train safe; subsequent trains refine the
        /// factor inside the cap.
        /// </summary>
        public const double AdaptiveFactorMax = 3.0;

        /// <summary>
        /// Hard floor on the per-cell factor AFTER adaptive convergence.
        /// Symmetric with AdaptiveFactorMax for the over-flick direction.
        /// </summary>
        public const double AdaptiveFactorMin = 0.3;
    }
}
