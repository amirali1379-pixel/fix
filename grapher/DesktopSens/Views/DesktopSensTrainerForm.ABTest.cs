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
        // T8: true A/B using the two most recently activated RawAccel mappings.
        private void ToggleABTestMode()
        {
            if (_profile == null) return;
            if (_isInABTestMode)
            {
                StopABTestAndRestoreCurrent();
                UpdateFixesHud();
                return;
            }
            if (_isInNoiseFloorEstimation)
            {
                MessageBox.Show(this, "Stop noise-floor estimation before starting the A/B comparison.",
                    "Evaluation already active", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_cardinalApplier == null ||
                _profile.PreviousAppliedMergedProfile == null ||
                _profile.LastAppliedMergedProfile == null)
            {
                MessageBox.Show(this,
                    "A true A/B test requires two saved, successfully activated mappings. Apply the baseline and the new profile first; this profile does not yet contain both snapshots.",
                    "A/B baseline unavailable", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _abPlan = new CounterbalancedABPlan(12, _rng);
            _abPairIndex = -1;
            _isInABTestMode = true;
            _abTestErrors.Clear();
            _abTestCurrentFlick = 0;
            PlaceABTestTarget();
            UpdateFixesHud();
        }

        private bool ApplyABCondition(bool useNewProfile)
        {
            if (_cardinalApplier == null || _profile == null) return false;
            MergedCardinalProfile mapping = useNewProfile
                ? _profile.LastAppliedMergedProfile
                : _profile.PreviousAppliedMergedProfile;
            if (mapping == null) return false;
            try { return _cardinalApplier.Apply(mapping, _profile); }
            catch (Exception ex)
            {
                DesktopSensLog.Error("Could not switch RawAccel mapping during A/B test", ex);
                return false;
            }
        }

        private void StopABTestAndRestoreCurrent()
        {
            _isInABTestMode = false;
            _abPlan = null;
            _abPairIndex = -1;
            _abTestErrors.Clear();
            _abTestCurrentFlick = 0;
            bool trialInFlight = _state == TrialState.Flicking || _state == TrialState.ASDWAdjusting;
            if (!trialInFlight) _isABTestTrialInProgress = false;
            _selectedAxisIndex = -1;
            _selectedDistanceZone = -1;
            _selectedDistanceCm = 0.0;
            if (!trialInFlight && _arena3D != null && !_arena3D.IsDisposed)
                _arena3D.ShowTarget = false;
            if (!ApplyABCondition(true))
                MessageBox.Show(this,
                    "The A/B test stopped, but restoring the current B mapping was not confirmed. Re-apply the current profile before continuing.",
                    "A/B restore warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void PlaceABTestTarget()
        {
            if (!_isInABTestMode || _abPlan == null || _abPlan.IsComplete) return;
            int pairIndex = _abPlan.CurrentPairIndex;
            if (pairIndex != _abPairIndex)
            {
                _abPairIndex = pairIndex;
                // FIX (8/16-way): pick the A/B axis from the active mode's
                // axis count so diagonal axes can also be A/B tested.
                int abDirectionCount = DesktopSensTrainingState.ResolveDirectionCount(_profile);
                int abAxisCount = DesktopSensTrainingState.AngleZoneIndicesForCount(abDirectionCount).Length;
                _abPairAxis = _rng.Next(0, abAxisCount);
                int maxZone = TrainingGeometry.Current.MultiDirectionZoneCount(_abPairAxis, abDirectionCount);
                _abPairDistanceZone = maxZone > 2
                    ? 1 + _rng.Next(maxZone - 2)
                    : Math.Max(0, maxZone - 1);
                _abPairTargetCm = DesktopSensTrainingState.DistanceZoneCenterCm(_abPairDistanceZone);
            }

            if (!ApplyABCondition(_abPlan.CurrentConditionIsB))
            {
                _isInABTestMode = false;
                _abPlan = null;
                ApplyABCondition(true);
                MessageBox.Show(this,
                    "RawAccel did not confirm the requested A/B mapping switch. The test stopped and a restore of the current B mapping was attempted.",
                    "A/B switch failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                UpdateFixesHud();
                return;
            }

            _selectedAxisIndex = _abPairAxis;
            _selectedDistanceZone = _abPairDistanceZone;
            _selectedDistanceCm = _abPairTargetCm;
            try
            {
                _isABTestTrialInProgress = true;
                PlaceCardinalTrainingTarget();
            }
            catch (Exception ex)
            {
                _isABTestTrialInProgress = false;
                DesktopSensLog.Error("Could not place the A/B target", ex);
            }
        }

        private void RecordABTestSample(DesktopSensTrial trial)
        {
            if (!_isInABTestMode || _abPlan == null || trial == null) return;
            double observed = trial.TargetDistanceCm + trial.SignedErrorCm;
            if (trial.WasRejected || !IsFiniteValue(observed) || observed <= 0.05 ||
                !IsFiniteValue(trial.TargetDistanceCm) || trial.TargetDistanceCm <= 0.05)
            {
                PlaceABTestTarget();
                return;
            }

            double absoluteLogError = Math.Abs(Math.Log(observed / trial.TargetDistanceCm));
            if (!_abPlan.RecordCurrent(absoluteLogError, Math.Abs(trial.SignedErrorCm)))
            {
                PlaceABTestTarget();
                return;
            }
            _abTestCurrentFlick = _abPlan.CompletedPairs;
            if (_abPlan.IsComplete) FinalizeABTest();
            else PlaceABTestTarget();
            UpdateFixesHud();
        }

        private void FinalizeABTest()
        {
            if (_profile == null || _abPlan == null) return;
            ABTestResult result = _abPlan.BuildResult();
            if (result == null)
            {
                StopABTestAndRestoreCurrent();
                MessageBox.Show(this,
                    "The A/B run did not produce enough complete paired observations.",
                    "A/B inconclusive", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            bool restored = ApplyABCondition(true);
            _selectedAxisIndex = -1;
            _selectedDistanceZone = -1;
            _selectedDistanceCm = 0.0;
            if (_arena3D != null && !_arena3D.IsDisposed) _arena3D.ShowTarget = false;
            _profile.LastABTestResult = result;
            OnCellVerifiedPersistProfile();
            _isInABTestMode = false;
            _abPlan = null;
            _abPairIndex = -1;
            _abTestErrors.Clear();
            _abTestCurrentFlick = 0;
            UpdateFixesHud();

            string summary =
                "Counterbalanced A/B Result\n\n" +
                "Complete pairs: " + result.TrialsRun + "\n" +
                "Mean |log error| A: " + result.MeanAbsoluteLogErrorBefore.ToString("P3") + "\n" +
                "Mean |log error| B: " + result.MeanAbsoluteLogErrorAfter.ToString("P3") + "\n" +
                "Paired improvement: " + result.MeanPairedLogImprovement.ToString("P3") + "\n" +
                "95% bootstrap CI: [" + result.PairedImprovementCiLower.ToString("F5") + ", " +
                    result.PairedImprovementCiUpper.ToString("F5") + "]\n" +
                "Significant (CI excludes 0): " + (result.ImprovementStatisticallySignificant ? "YES" : "NO") + "\n" +
                "Median |log error| B: " + result.MedianAbsoluteLogErrorAfter.ToString("P3") + "\n" +
                "Absolute accuracy gate (<=1.5%): " +
                    (result.MedianAbsoluteLogErrorAfter <= 0.015 ? "PASS" : "FAIL") + "\n\n" +
                result.Notes +
                (restored ? "" : "\n\nWARNING: restoring the current B mapping was not confirmed.");
            MessageBox.Show(this, summary, "Counterbalanced A/B", MessageBoxButtons.OK,
                restored ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private static bool IsFiniteValue(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
