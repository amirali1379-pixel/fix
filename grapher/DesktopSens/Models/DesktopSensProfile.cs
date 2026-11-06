using System;
using System.Collections.Generic;

namespace grapher.DesktopSens.Models
{
    /// <summary>
    /// Controls what RawAccel's LUT returns above the highest calibrated point.
    /// Model v6 defaults to NeutralOne (return 1.0) so a fitted correction is
    /// never silently extrapolated to untested high speeds. Legacy behaviour
    /// (ExtendLastFactor) is preserved for callers that explicitly request it.
    /// </summary>
    public enum LutUpperBookendMode
    {
        NeutralOne = 0,
        ExtendLastFactor = 1
    }

    /// <summary>
    /// FIX #12: A/B comparison test result.
    /// Captured by the trainer's RunABComparison method after a
    /// merge-and-apply, comparing the user's flick accuracy with the
    /// new profile vs the previous profile.
    /// </summary>
    [Serializable]
    public class ABTestResult
    {
        public DateTime RunAtUtc = DateTime.UtcNow;
        public int TrialsRun;
        public double MeanAbsoluteErrorBeforeCm;
        public double MeanAbsoluteErrorAfterCm;
        public double MedianAbsoluteErrorBeforeCm;
        public double MedianAbsoluteErrorAfterCm;
        public double StdDevBeforeCm;
        public double StdDevAfterCm;
        public double ImprovementPercent;
        public bool ImprovementStatisticallySignificant;
        // False for the current single-arm post-merge check; true only when both baselines were measured.
        public bool HasBaselineComparison = false;
        public double MeanAbsoluteLogErrorBefore;
        public double MeanAbsoluteLogErrorAfter;
        public double MedianAbsoluteLogErrorBefore;
        public double MedianAbsoluteLogErrorAfter;
        public double MeanPairedLogImprovement;
        public double MedianPairedLogImprovement;
        public double PairedImprovementCiLower;
        public double PairedImprovementCiUpper;
        public string Notes = "";
    }

    public class DesktopSensProfile
    {
        public int SchemaVersion = DesktopSensConstants.CurrentSchemaVersion;
        public int ModelVersion = DesktopSensConstants.CurrentModelVersion;
        public string ProfileId = Guid.NewGuid().ToString();
        public DateTime CreatedTimestamp = DateTime.UtcNow;
        public DateTime ModifiedTimestamp = DateTime.UtcNow;
        public double UserInputGameSensitivity = DesktopSensConstants.NeutralFactor;
        public double BaseSensitivity = DesktopSensConstants.NeutralFactor;
        public double BaseSensitivityConfidence = 0.0;

        // Original RawAccel output DPI captured on first profile application,
        // preventing repeated-apply compounding.
        public double RawAccelBaseOutputDPI = 0.0;

        // ─────────────────────────────────────────────────────────────────────────
        // Four cardinal-direction sensitivity multipliers (legacy, single-factor).
        // ─────────────────────────────────────────────────────────────────────────
        public double RightSensitivityFactor = DesktopSensConstants.NeutralFactor;
        public double LeftSensitivityFactor = DesktopSensConstants.NeutralFactor;
        public double UpSensitivityFactor = DesktopSensConstants.NeutralFactor;
        public double DownSensitivityFactor = DesktopSensConstants.NeutralFactor;
        public double RightSensitivityConfidence = 0.0;
        public double LeftSensitivityConfidence = 0.0;
        public double UpSensitivityConfidence = 0.0;
        public double DownSensitivityConfidence = 0.0;

        // ─────────────────────────────────────────────────────────────────────────
        // Four-quadrant rotation corrections (legacy, single circular mean each).
        // ─────────────────────────────────────────────────────────────────────────
        public double RightRotationOffsetDegrees = 0.0;
        public double DownRotationOffsetDegrees  = 0.0;
        public double LeftRotationOffsetDegrees  = 0.0;
        public double UpRotationOffsetDegrees    = 0.0;

        // ─────────────────────────────────────────────────────────────────────────
        // NEW (Model v5): per-direction acceleration curve fits.
        // ─────────────────────────────────────────────────────────────────────────
        public int RightCurveMode = (int)AccelCurveMode.NoAccel;
        public int LeftCurveMode = (int)AccelCurveMode.NoAccel;
        public int UpCurveMode = (int)AccelCurveMode.NoAccel;
        public int DownCurveMode = (int)AccelCurveMode.NoAccel;

        public double[] DirectionalCurveParameters = new double[DesktopSensConstants.DirectionalCount * DesktopSensConstants.MaxCurveParameters];
        public double[] DirectionalCurveRmse = new double[DesktopSensConstants.DirectionalCount];
        public double[] DirectionalCurveAic = new double[DesktopSensConstants.DirectionalCount];
        public int[] DirectionalCurveSampleCount = new int[DesktopSensConstants.DirectionalCount];

        public int MouseDPI = 800;
        // Zero means no physical measurement has been recorded; preserve
        // compatibility by falling back to the configured nominal DPI.
        public double MeasuredCpi = 0.0;
        public double EffectiveCpi
            => IsFinitePositive(MeasuredCpi) ? MeasuredCpi : MouseDPI;
        public double CmPerCount
            => EffectiveCpi > 0.0 ? DesktopSensConstants.InchesToCm / EffectiveCpi : 0.0;
        public double PollingRateHz = 1000.0;

        // ─────────────────────────────────────────────────────────────────────────
        // Legacy one-dimensional arrays retained for backward compatibility.
        // (Same comment as before — DO NOT read DistanceZoneCount from
        // these arrays' .Length; query TrainingGeometry.Current instead.)
        // ─────────────────────────────────────────────────────────────────────────
        public double[] SensitivityFactor = new double[DesktopSensConstants.AngularZoneCount];
        public double[] SensitivityConfidence = new double[DesktopSensConstants.AngularZoneCount];
        public double[] AccelerationFactor = new double[DesktopSensConstants.DefaultDistanceZoneCount];
        public double[] AccelerationConfidence = new double[DesktopSensConstants.DefaultDistanceZoneCount];

        // Per-cell legacy single-factor arrays.
        public double[] CellSensitivityFactor = new double[DesktopSensConstants.AngularZoneCount * DesktopSensConstants.DefaultDistanceZoneCount];
        public double[] CellSensitivityConfidence = new double[DesktopSensConstants.AngularZoneCount * DesktopSensConstants.DefaultDistanceZoneCount];
        public double[] CellAccelerationFactor = new double[DesktopSensConstants.AngularZoneCount * DesktopSensConstants.DefaultDistanceZoneCount];
        public double[] CellAccelerationConfidence = new double[DesktopSensConstants.AngularZoneCount * DesktopSensConstants.DefaultDistanceZoneCount];
        public double[] CellRotationOffsetDegrees = new double[DesktopSensConstants.AngularZoneCount * DesktopSensConstants.DefaultDistanceZoneCount];
        public double[] CellRotationConfidence = new double[DesktopSensConstants.AngularZoneCount * DesktopSensConstants.DefaultDistanceZoneCount];

        // ─────────────────────────────────────────────────────────────────────────
        // NEW (Model v5): per-cell acceleration curve fits.
        // ─────────────────────────────────────────────────────────────────────────
        public int[] CellCurveMode = new int[DesktopSensConstants.AngularZoneCount * DesktopSensConstants.DefaultDistanceZoneCount];
        public double[] CellCurveParameters = new double[DesktopSensConstants.AngularZoneCount * DesktopSensConstants.DefaultDistanceZoneCount * DesktopSensConstants.MaxCurveParameters];
        public double[] CellCurveRmse = new double[DesktopSensConstants.AngularZoneCount * DesktopSensConstants.DefaultDistanceZoneCount];
        public double[] CellCurveAic = new double[DesktopSensConstants.AngularZoneCount * DesktopSensConstants.DefaultDistanceZoneCount];
        public int[] CellCurveSampleCount = new int[DesktopSensConstants.AngularZoneCount * DesktopSensConstants.DefaultDistanceZoneCount];

        // ─────────────────────────────────────────────────────────────────────────
        // NEW (Model v5): Micro-flick boost.
        // ─────────────────────────────────────────────────────────────────────────
        public bool MicroBoostEnabled = false;
        public double MicroBoostLowStart = MicroBoostCurve.DefaultLowStart;
        public double MicroBoostRangeCmPerSec = MicroBoostCurve.DefaultBoostRangeCmPerSec;

        // ─────────────────────────────────────────────────────────────────────────
        // NEW (Model v6): LUT construction mode.
        //
        // true  = sample-fit LUT (default, Model v6): the calibrator fits the
        //         continuous multiplier f(v) directly to the raw per-sample
        //         observations. Yellow cells participate with a confidence weight.
        // false = legacy median-speed LUT (Model v5): only green cells contribute,
        //         each cell becomes one LUT point at the median speed.
        //
        // The flag is persisted so a saved profile reproduces the same apply path
        // on reload. Old profiles loaded with a missing field default to the new
        // sample-fit path (true) so the v6 improvements take effect automatically.
        // ─────────────────────────────────────────────────────────────────────────
        public bool UseSampleFitLut = true;

        // Upper-bookend behaviour of the emitted LUT. Model v6 defaults to NeutralOne
        // because extending the last calibrated factor to +infinity silently applies
        // a calibrated correction to speeds the user never tested.
        public LutUpperBookendMode LutUpperBookend = LutUpperBookendMode.NeutralOne;

        // ─────────────────────────────────────────────────────────────────────────
        // NEW (Model v6, per-point isolation): the LUT points most recently built
        // for each angle zone. BuildSampleFitLutPoints reads this dictionary on
        // entry and feeds the previous points as anchors to SampleLutFitter.Fit,
        // so a re-fit cannot rewrite the curve at speeds the user already
        // verified. After the fit, the new LUT points replace this entry so the
        // next build sees the freshly-verified points as anchors.
        //
        // Keyed by angle-zone index (0..AngularZoneCount-1). Missing key = no
        // anchors yet (first build for that axis). Null entries are treated as
        // "no anchors" too.
        // ─────────────────────────────────────────────────────────────────────────
        public Dictionary<int, List<LutPoint>> LastBuiltLutPointsByAngleZone = new Dictionary<int, List<LutPoint>>();
        // Base factor active when each persisted anchor curve was built.
        // Needed to renormalize anchors if Base Sens changes before the next fit.
        public Dictionary<int, double> LastBuiltLutBaseFactorByAngleZone = new Dictionary<int, double>();

        // Exact frozen node factors for sample-fit LUTs, persisted per angle
        // zone. NaN entries are free nodes; finite positive entries are
        // immutable until an explicit recalibration/unfreeze operation.
        // Arrays use the fixed SampleLutFitter node grid.
        public Dictionary<int, double[]> FrozenSampleLutNodeFactorsByAngleZone =
            new Dictionary<int, double[]>();

        public RotationModel Rotation = new RotationModel();

        public int CellsStable = 0;
        public int CellsLowConfidence = 0;

        // Initial default uses the legacy count; the calibrator recomputes
        // this from TrainingGeometry.Current.DistanceZoneCount on every
        // Calibrate() call, so the value here is only correct until then.
        public int CellsInsufficientData = DesktopSensConstants.AngularZoneCount * DesktopSensConstants.DefaultDistanceZoneCount;
        public double OverallCoverage = 0.0;
        public double OverallConfidence = 0.0;

        // ─────────────────────────────────────────────────────────────────────────
        // NEW (cardinal-direction training): per-cell training state for the
        // 4-axis calibration trainer. Keyed by GetCellIndex(angleZone, distanceZone).
        // ─────────────────────────────────────────────────────────────────────────
        public Dictionary<int, TrainingCellState> CellTrainingStates = new Dictionary<int, TrainingCellState>();

        // True once DesktopSensProfileMerger.MergeAndApply has successfully
        // written the merged four-direction profile to RawAccel.
        public bool AllCardinalCellsVerified = false;

        // Timestamp of the last successful merge-and-apply. Diagnostic only.
        public DateTime? LastMergeAppliedTimestampUtc;

        // ─────────────────────────────────────────────────────────────────────────
        // NEW (fix #8): session bookkeeping.
        // CurrentSessionId is set by the trainer on startup (Guid) and
        // bumped whenever the user opens a new training session. Each
        // training attempt and each merge-and-apply stamps its work with
        // this id so we can later identify "this cell was last verified
        // in session X, but the user's last attempt in session Y drifted
        // by 2 cm — re-verify".
        // ─────────────────────────────────────────────────────────────────────────
        public string CurrentSessionId = Guid.NewGuid().ToString();

        // Training geometry is profile data: downstream views, state maps,
        // calibrators and mergers all consume the same configured step.
        public double TrainingMarkerStepDegrees = 10.0;
        public double AppliedTrainingMarkerStepDegrees = 10.0;
        // Physical scale used when the training cells were last verified.
        // Zero means an older profile has no persisted scale and must migrate.
        public double AppliedTrainingMaxDistanceCm = 0.0;

        public DateTime? SessionStartedAtUtc;

        // ─────────────────────────────────────────────────────────────────────────
        // NEW (fix #10): noise floor.
        // The user's measured flick noise floor in cm. Zero means
        // "not yet estimated — use the default 1.0 cm tolerance".
        // The trainer's EstimateNoiseFloor() method runs ~10 flicks to
        // a fixed target and sets this from the standard deviation of
        // the signed errors (after robust outlier rejection).
        // ─────────────────────────────────────────────────────────────────────────
        public double NoiseFloorCm = 0.0;
        public DateTime? NoiseFloorEstimatedAtUtc;
        public int NoiseFloorSampleCount = 0;

        // ─────────────────────────────────────────────────────────────────────────
        // NEW (fix #14): sequential-rule override.
        // When true, the trainer's sequential rule (cell N+1 cannot be
        // calibrated until cell N is verified) is relaxed to a warning.
        // The user can train cells in any order; the calibrator's chain
        // logic still computes the residual error correctly because it
        // applies the verified cells' factors before computing the
        // current cell's factor.
        // ─────────────────────────────────────────────────────────────────────────
        public bool AllowSequentialOverride = false;

        // ─────────────────────────────────────────────────────────────────────────
        // NEW (fix #11): backup before merge.
        // Path to the JSON file written by DesktopSensProfileMerger
        // before the last MergeAndApply call. The trainer exposes a
        // B key to roll back to this file. Null means "no backup has
        // been made yet" or "the user already rolled back".
        // ─────────────────────────────────────────────────────────────────────────
        public string LastBackupPath;
        public DateTime? LastBackupAtUtc;

        // ─────────────────────────────────────────────────────────────────────────
        // NEW (fix #12): A/B comparison result.
        // Filled by RunABComparison after a merge. The user can compare
        // the mean absolute error before vs after to decide whether
        // the new profile is actually better.
        // ─────────────────────────────────────────────────────────────────────────
        public ABTestResult LastABTestResult;
        // The two most recent mappings that were successfully activated.
        // Older profiles without both snapshots cannot claim a real A/B comparison.
        public MergedCardinalProfile PreviousAppliedMergedProfile;
        public MergedCardinalProfile LastAppliedMergedProfile;

        // ─────────────────────────────────────────────────────────────────────────
        // NEW (LUT overwrite): snapshot of the user's previous argsX/argsY
        // mode + parameters before the Train LUT applier overwrote them.
        // Stored so a Reset button can restore the previous mode (e.g.
        // "classic" or "natural") without losing the user's hand-set
        // parameters.
        //
        // Each entry is a serialized dictionary of the AccelArgs fields
        // captured from the active RawAccel settings BEFORE the LUT
        // write. The dictionary has the same shape as the dictionaries
        // passed to RawAccelSettingsBridge.TrySetNestedFields:
        //   "mode"   : string ("classic" | "natural" | "power" | "synchronous" | "jump" | "lut" | "noaccel")
        //   "gain"   : bool
        //   ... mode-specific parameter fields ...
        //   For "lut" mode only: "length" : int, "data" : float[]
        //
        // Null / empty means "no LUT overwrite has happened yet, or the
        // user already reset". The Train applier only stores these
        // snapshots when it ACTUALLY overwrote argsX/argsY with a LUT.
        // ─────────────────────────────────────────────────────────────────────────
        public Dictionary<string, object> PreviousArgsXBeforeLut;
        public Dictionary<string, object> PreviousArgsYBeforeLut;
        // True once the Train LUT applier has overwritten argsX/argsY
        // with a LUT. Reset to false on user-initiated Reset.
        public bool LutOverwriteActive;

        // ─────────────────────────────────────────────────────────────────────────
        // NEW (fix #6): direction-count mode for the trainer.
        // 4 = cardinal (default, backward-compat)
        // 8 = 8-way (adds the four diagonal directions)
        // 16 = 16-way (every 22.5°)
        // The trainer uses this to decide which cells to populate
        // and the arena renderer uses it to decide which axes to draw.
        // ─────────────────────────────────────────────────────────────────────────
        public int CalibrationDirectionCount = 4;

        // Sensitivity Finder observations are intentionally isolated from cardinal Train trials.
        // DirectionalCalibrator consumes only this collection; Train owns Trials.
        public List<DesktopSensTrial> SensitivityFinderTrials = new List<DesktopSensTrial>();

        public List<DesktopSensTrial> Trials = new List<DesktopSensTrial>();

        public DesktopSensProfile()
        {
            InitializeFactors(SensitivityFactor, DesktopSensConstants.NeutralFactor);
            InitializeFactors(AccelerationFactor, DesktopSensConstants.NeutralFactor);
            InitializeFactors(CellSensitivityFactor, DesktopSensConstants.NeutralFactor);
            InitializeFactors(CellAccelerationFactor, DesktopSensConstants.NeutralFactor);
            InitializeFactors(CellRotationOffsetDegrees, 0.0);
            InitializeFactors(SensitivityConfidence, 0.0);
            InitializeFactors(AccelerationConfidence, 0.0);
            InitializeFactors(CellSensitivityConfidence, 0.0);
            InitializeFactors(CellAccelerationConfidence, 0.0);
            InitializeFactors(CellRotationConfidence, 0.0);
            InitializeIntFactors(CellCurveMode, (int)AccelCurveMode.NoAccel);
            InitializeFactors(CellCurveParameters, 0.0);
            InitializeFactors(CellCurveRmse, 0.0);
            InitializeFactors(CellCurveAic, 0.0);
            InitializeIntFactors(CellCurveSampleCount, 0);
            InitializeIntFactors(DirectionalCurveSampleCount, 0);
            InitializeFactors(DirectionalCurveParameters, 0.0);
            InitializeFactors(DirectionalCurveRmse, 0.0);
            InitializeFactors(DirectionalCurveAic, 0.0);
            // MicroBoost defaults are set by field initializers; no extra init needed.
            // CellTrainingStates starts as an empty dictionary — populated lazily
            // by DesktopSensTrainingState.RecordAttempt as trials are recorded.
            if (CellTrainingStates == null)
                CellTrainingStates = new Dictionary<int, TrainingCellState>();
            if (string.IsNullOrEmpty(CurrentSessionId))
                CurrentSessionId = Guid.NewGuid().ToString();
        }

        public static int GetCellIndex(int angleZoneIndex, int distanceZoneIndex)
        {
            if (angleZoneIndex < 0 || angleZoneIndex >= DesktopSensConstants.AngularZoneCount)
                throw new ArgumentOutOfRangeException(nameof(angleZoneIndex));

            // The distance-zone count comes from TrainingGeometry (the
            // trainer is the central authority). If the trainer hasn't
            // been opened yet (e.g. reading a serialized profile in a
            // standalone tool), TrainingGeometry.Current still has its
            // default values, so this call is safe.
            int distanceZoneCount = TrainingGeometry.Current.DistanceZoneCount;
            if (distanceZoneIndex < 0 || distanceZoneIndex >= distanceZoneCount)
                throw new ArgumentOutOfRangeException(nameof(distanceZoneIndex));
            return angleZoneIndex * distanceZoneCount + distanceZoneIndex;
        }

        /// <summary>
        /// Read the curve parameters for a single cell as a fresh double[].
        /// Returns an empty array for NoAccel cells.
        /// </summary>
        public double[] GetCellCurveParameters(int cellIndex)
        {
            int max = DesktopSensConstants.MaxCurveParameters;
            if (CellCurveParameters == null || cellIndex < 0 || cellIndex * max >= CellCurveParameters.Length)
                return new double[0];
            double[] result = new double[max];
            Array.Copy(CellCurveParameters, cellIndex * max, result, 0, max);
            return result;
        }

        /// <summary>
        /// Write the curve parameters for a single cell. Pads with zeros if the
        /// supplied array is shorter than MaxCurveParameters.
        /// </summary>
        public void SetCellCurveParameters(int cellIndex, double[] parameters)
        {
            int max = DesktopSensConstants.MaxCurveParameters;
            if (CellCurveParameters == null || cellIndex < 0 || cellIndex * max >= CellCurveParameters.Length)
                return;
            for (int i = 0; i < max; i++)
                CellCurveParameters[cellIndex * max + i] = (parameters != null && i < parameters.Length) ? parameters[i] : 0.0;
        }

        /// Same accessor pair for the four cardinal directions (0=R, 1=D, 2=L, 3=U).
        public double[] GetDirectionalCurveParameters(int direction)
        {
            int max = DesktopSensConstants.MaxCurveParameters;
            if (DirectionalCurveParameters == null || direction < 0 || direction * max >= DirectionalCurveParameters.Length)
                return new double[0];
            double[] result = new double[max];
            Array.Copy(DirectionalCurveParameters, direction * max, result, 0, max);
            return result;
        }

        public void SetDirectionalCurveParameters(int direction, double[] parameters)
        {
            int max = DesktopSensConstants.MaxCurveParameters;
            if (DirectionalCurveParameters == null || direction < 0 || direction * max >= DirectionalCurveParameters.Length)
                return;
            for (int i = 0; i < max; i++)
                DirectionalCurveParameters[direction * max + i] = (parameters != null && i < parameters.Length) ? parameters[i] : 0.0;
        }

        /// Convenience: read direction's curve mode as the enum.
        public AccelCurveMode GetDirectionalCurveMode(int direction)
        {
            switch (direction)
            {
                case 0: return (AccelCurveMode)RightCurveMode;
                case 1: return (AccelCurveMode)DownCurveMode;
                case 2: return (AccelCurveMode)LeftCurveMode;
                case 3: return (AccelCurveMode)UpCurveMode;
                default: return AccelCurveMode.NoAccel;
            }
        }

        public void SetDirectionalCurveMode(int direction, AccelCurveMode mode)
        {
            switch (direction)
            {
                case 0: RightCurveMode = (int)mode; break;
                case 1: DownCurveMode = (int)mode; break;
                case 2: LeftCurveMode = (int)mode; break;
                case 3: UpCurveMode = (int)mode; break;
            }
        }

        /// <summary>
        /// FIX #4: compute the quantization error (smallest resolvable
        /// distance) at the profile's DPI. The calibrator uses this to
        /// avoid over-fitting factors to sub-quantization noise.
        /// </summary>
        public double QuantizationErrorCm => CmPerCount;

        /// <summary>
        /// FIX #4: minimum sample count for a cell to be considered
        /// "stable", accounting for the polling rate. A 125 Hz mouse
        /// captures ~8x fewer samples per second than a 1000 Hz mouse
        /// for the same flick duration, so each sample carries more
        /// statistical weight. We require more samples at low polling
        /// rates to compensate.
        /// </summary>
        public int MinSamplesForStability
        {
            get
            {
                double rate = PollingRateHz > 0 ? PollingRateHz : DesktopSensConstants.TargetPollingRateHz;
                // At 1000 Hz, return 10 (legacy baseline).
                // At 500 Hz, return 14.
                // At 125 Hz, return about 28 (10 * sqrt(1000 / 125)).
                double scale = DesktopSensConstants.TargetPollingRateHz / Math.Max(1.0, rate);
                return Math.Max(5, (int)Math.Round(10.0 * Math.Sqrt(scale)));
            }
        }

        private static bool IsFinitePositive(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0.0;
        }

        private static void InitializeFactors(double[] values, double initialValue)
        {
            if (values == null) return;
            for (int i = 0; i < values.Length; i++) values[i] = initialValue;
        }

        private static void InitializeIntFactors(int[] values, int initialValue)
        {
            if (values == null) return;
            for (int i = 0; i < values.Length; i++) values[i] = initialValue;
        }
    }
}
