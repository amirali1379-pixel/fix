using System;
using System.Collections.Generic;
using grapher.Models.Mouse;

namespace grapher.DesktopSens.Models
{
    public enum FlickCompletionMode
    {
        VelocityStop,
        Click
    }

    /// <summary>
    /// Reason a trial was rejected by the new trial-validation layer.
    /// Empty string means the trial was accepted. Used for HUD display
    /// and for the calibrator's per-cell rejection accounting.
    /// </summary>
    public static class TrialRejectionReason
    {
        public const string None = "";
        public const string TooFewSamples = "too_few_samples";
        public const string DurationOutOfRange = "duration_out_of_range";
        public const string VelocityProfileAnomalous = "velocity_anomaly";
        public const string AngularErrorTooLarge = "angular_error_too_large";
        public const string NoiseFloorExceeded = "noise_floor_exceeded";
        public const string SensorGlitch = "sensor_glitch";
        public const string CardinalAxisMismatch = "cardinal_axis_mismatch";
    }

    public class DesktopSensTrial
    {
        public string TrialId = Guid.NewGuid().ToString();
        public string SessionId;
        // Short fixed-distance observation used only to estimate session-level human bias.
        public bool IsSessionAnchor;
        public double StartX;
        public double StartY;
        public double TargetX;
        public double TargetY;
        public double TargetAngleDegrees;
        public double TargetDistanceCm;
        public List<MouseSample> RawSamples = new List<MouseSample>();
        public double LandedX;
        public double LandedY;
        public double FinalErrorCm;

        // Signed error along the target direction: negative = short, positive = past target.
        public double SignedErrorCm;
        public double ProjectedDistanceCm;
        public bool IsUnderFlick;
        public bool IsOverFlick;
        public double FlickAngleDegrees;

        // Signed shortest angular correction: target angle minus measured flick angle.
        public double SignedRotationErrorDegrees;
        // Rotation correction applied by the trainer before camera movement.
        public double AppliedRotationCorrectionDegrees;

        // NEW (fix #3): Off-axis error magnitude in degrees.
        // |AngularErrorDegrees| is the perpendicular deviation of the
        // actual flick direction from the target direction. A flick
        // aimed "north" that landed 10° east has AngularError = +10.
        // The calibrator uses this to reject trials whose along-axis
        // signed error is contaminated by an off-axis component.
        public double AngularErrorDegrees;

        // Defaults preserve legacy JSON trials: old records remain movement observations,
        // but rotation is only trusted when explicitly measured by the current trainer.
        public bool HasMovementMeasurement = true;
        public bool HasRotationMeasurement;

        // NEW (fix #3): HasAngularMeasurement mirrors HasRotationMeasurement.
        // Old serialized trials do not have AngularErrorDegrees, so the
        // calibrator must skip the angular check for them.
        public bool HasAngularMeasurement;

        public double FlickDistanceCm;

        // Manual ASDW adjustment stored separately from the raw mouse flick.
        // It is computed from the camera delta between the saved raw landing
        // and the final post-ASDW camera position, projected onto the target
        // axis. It may confirm a curve sample (when the final landing falls
        // inside the cell's adaptive tolerance) but it NEVER rewrites
        // SignedErrorCm or FlickDistanceCm, which remain based on raw mouse
        // samples only.
        //
        // Sign convention:
        //   - Negative = user nudged backward (raw flick overshot).
        //   - Positive = user nudged forward  (raw flick undershot).
        //   - Zero     = no ASDW used (or nudges cancelled out).
        //
        // ManualAsdwFinalErrorCm is the residual error magnitude of the
        // post-ASDW landing, computed from (signedErrorCm + asdwNudgeCm)
        // combined with the post-ASDW angular error. It is the
        // "did the user actually reach the target" confirmation, used by
        // the calibrator to accept or reject a curve sample for that
        // cell. It is NOT used by the sensitivity estimator (which uses
        // SignedErrorCm).
        public double ManualAsdwCorrectionCm;
        public double ManualAsdwFinalErrorCm;
        public bool HasManualAsdwCorrection;

        // NEW (Model v6): effective target distance used for calibration.
        //
        // When the user's ASDW nudge landed inside the cell's adaptive tolerance,
        // NeededDistanceCm = FlickDistanceCm + ManualAsdwCorrectionCm, i.e. the
        // total physical movement the user needed to hit the target under the
        // active LUT. This makes the calibration factor
        //
        //     factor = NeededDistanceCm / FlickDistanceCm
        //            = (flick + nudge) / flick
        //
        // so a larger confirmed ASDW correction produces a stronger factor.
        //
        // When ASDW was not used, or when the post-ASDW landing is outside
        // tolerance, this stays NaN and every consumer falls back to
        // TargetDistanceCm — preserving legacy behaviour for old profiles.
        //
        // SignedErrorCm is NOT modified by this field. All existing diagnostic
        // code paths (FinalErrorCm, IsUnderFlick, IsOverFlick, ASDW gating) keep
        // their current meaning.
        public double NeededDistanceCm = double.NaN;

        // Applied output factors captured from the active RawAccel profile
        // for this trial's raw-input direction and average pre-driver speed.
        // The base directional multiplier remains configured outside argsX/Y;
        // the LUT multiplier is composed into the replacement LUT separately.
        // Legacy trials default both multipliers to 1.0.
        public double AppliedBaseFactor = 1.0;
        public double AppliedLutFactor = 1.0;
        public bool HasAppliedLutFactor;
        // Per-raw-sample LUT factor snapshot, index-aligned with RawSamples.
        // Empty on legacy trials; those continue to use AppliedLutFactor.
        public List<double> AppliedLutFactorsByRawSample = new List<double>();

        public double DurationMs;
        public double PeakVelocityPxPerSec;

        // NEW (fix #9): velocity-profile diagnostics for trial rejection.
        // MeanVelocity is the average sample magnitude over the flick;
        // VelocityStdDev is its standard deviation. A "good" flick has a
        // smooth ramp-up, plateau, ramp-down — high stddev relative to mean
        // indicates stuttering or aborted motion.
        public double MeanVelocityPxPerSec;
        public double VelocityStdDevPxPerSec;

        // NEW (fix #9): Raw sample count captured for this trial.
        // Used by the validation layer to reject trials whose RawSamples
        // list is too short to compute a meaningful velocity profile.
        public int RawSampleCount;

        // Legacy capture-time chain metadata, retained for backward compatibility
        // and immediate training-state hit checks. The v4 calibrator rebuilds
        // residuals from raw samples in deterministic zone order.
        public bool HasChainAdjustedSignedError;
        public double ChainAdjustedSignedErrorCm;

        public int AngleZoneIndex;
        public int DistanceZoneIndex;
        public FlickCompletionMode CompletionMode;
        public long CompletionTimestamp;
        public int MouseDPI;
        // Physical CPI captured when this trial was recorded. Zero on legacy
        // trials means use MouseDPI as the backward-compatible fallback.
        public double EffectiveCpi;

        // Sensitivity used to convert raw mouse counts to CS2 view rotation for this trial.
        public double GameSensitivity;
        public double EffectivePollingRateHz;
        public double CanvasPixelsPerCm;

        // NEW (fix #9 + #4): trial-rejection flag.
        // WasRejected = true means the trial failed the validation layer
        // (sample count, duration, velocity profile, angular error, or
        // noise-floor check). Rejected trials are kept in profile.Trials
        // for diagnostic purposes but the calibrator skips them.
        public bool WasRejected;
        // Permanent routing exclusion (for example, a trial aimed at a different
        // Train axis). Unlike validation results, this must survive recalibration.
        public bool IsCalibrationExcluded;
        // Evaluation-only trials (noise-floor / post-merge checks) are retained for diagnostics but never calibrate cells.
        public bool IsEvaluationOnly;
        public string RejectionReason = TrialRejectionReason.None;

        // NEW (fix #4): quantization-aware correction.
        // QuantizationErrorCm is the smallest distance the mouse can
        // resolve at the trial's DPI (2.54 cm / DPI per count). The
        // calibrator uses this to avoid over-fitting: a 0.4 cm signed
        // error at 400 DPI is mostly quantization noise, not real error.
        public double QuantizationErrorCm;

        // NEW (fix #2): chained-correction metadata.
        // CumulativeCorrectionFactor is the product of all verified
        // per-cell factors that were ALREADY applied (in simulation) to
        // the raw signed error before the calibrator computed this
        // trial's residual error. 1.0 means no prior corrections were
        // applied (cell 0 of an axis, or a non-cardinal cell).
        public double CumulativeCorrectionFactor = 1.0;

        /// <summary>
        /// Convenience: returns true if this trial should be considered
        /// a valid input for calibration. Rejected trials are kept for
        /// diagnostics but ignored by the calibrator.
        /// </summary>
        public bool IsValidForCalibration => !WasRejected
            && !IsCalibrationExcluded
            && !IsEvaluationOnly
            && HasMovementMeasurement
            && IsFinite(SignedErrorCm)
            && TargetDistanceCm > 0.0;

        /// <summary>
        /// Validate this trial against the new rejection criteria.
        /// Sets WasRejected and RejectionReason and returns true if
        /// the trial was rejected. Called by the trainer's FinishTrial
        /// path BEFORE the trial is added to profile.Trials, so the
        /// rejection is recorded in-place.
        ///
        /// The thresholds are intentionally conservative — they reject
        /// only clearly-broken trials, not borderline ones. The
        /// calibrator's robust statistics (Huber/MAD) handle the
        /// borderline cases.
        /// </summary>
        public bool Validate(
            double noiseFloorCm = 0.0,
            int minRawSamples = 3,
            double minDurationMs = 40.0,
            double maxDurationMs = 2500.0,
            double maxAngularErrorDegrees = 15.0)
        {
            if (IsCalibrationExcluded)
            {
                WasRejected = true;
                if (string.IsNullOrEmpty(RejectionReason) || RejectionReason == TrialRejectionReason.None)
                    RejectionReason = TrialRejectionReason.SensorGlitch;
                return true;
            }

            WasRejected = false;
            RejectionReason = TrialRejectionReason.None;

            // Older serialized trials may not have RawSampleCount. Prefer the
            // actual sample list, and do not reject legacy records solely
            // because they predate raw-sample capture.
            int availableSamples = RawSamples != null ? RawSamples.Count : 0;
            int recordedSamples = RawSampleCount > 0
                ? Math.Max(RawSampleCount, availableSamples)
                : availableSamples;
            bool hasSampleMetadata = recordedSamples > 0;

            // Calibration is intentionally unrestricted by sample count,
            // flick duration, velocity dispersion, or angular deviation.
            // Those measurements remain available to the estimator/logs;
            // only non-finite sensor data is rejected as unusable.
            if (HasAngularMeasurement && !IsFinite(AngularErrorDegrees))
            {
                WasRejected = true;
                RejectionReason = TrialRejectionReason.SensorGlitch;
                return true;
            }

            // NeededDistanceCm is optional. If present it must be finite and positive;
            // otherwise the calibrator falls back to TargetDistanceCm and we should
            // not silently carry a corrupted value into the cache key.
            if (!double.IsNaN(NeededDistanceCm))
            {
                if (!IsFinite(NeededDistanceCm) || NeededDistanceCm <= 0.0)
                    NeededDistanceCm = double.NaN;
            }

            // Do not reject a trial because its signed aiming error is large
            // relative to the measured noise floor. That error is calibration
            // signal, not sensor noise; rejecting it would bias the estimator
            // toward lucky/near-target flicks. NoiseFloorCm is used by the
            // training state's adaptive tolerance and confidence calculations.
            return false;
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
