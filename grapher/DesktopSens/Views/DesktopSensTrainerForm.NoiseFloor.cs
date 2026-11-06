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
        // FIX #10: noise floor estimation.
        private void ToggleNoiseFloorEstimation()
        {
            if (_profile == null) return;
            if (!_isInNoiseFloorEstimation && _isInABTestMode)
            {
                DesktopSensToast.Show(this, "Stop the A/B comparison before starting noise-floor estimation.");
                return;
            }
            if (_isInNoiseFloorEstimation)
            {
                _isInNoiseFloorEstimation = false;
                _noiseFloorFlickCount = 0;
                _noiseFloorErrors.Clear();
                UpdateFixesHud();
                return;
            }
            _isInNoiseFloorEstimation = true;
            _noiseFloorFlickCount = 0;
            _noiseFloorErrors.Clear();
            int zoneCount = TrainingGeometry.Current.DistanceZoneCount;
            int halfZone = Math.Max(0, Math.Min(zoneCount - 1, zoneCount / 2));
            _selectedAxisIndex = 0;
            _selectedDistanceZone = halfZone;
            _selectedDistanceCm = DesktopSensTrainingState.DistanceZoneCenterCm(halfZone);
            try { PlaceCardinalTrainingTarget(); } catch (Exception ex) { DesktopSensLog.Warn("Could not place the next training target", ex); }
            UpdateFixesHud();
        }

        private void RecordNoiseFloorSample(DesktopSensTrial trial)
        {
            if (!_isInNoiseFloorEstimation) return;
            if (trial == null) return;
            if (trial.WasRejected || !IsFiniteValue(trial.SignedErrorCm))
            {
                try { PlaceCardinalTrainingTarget(); } catch (Exception ex) { DesktopSensLog.Warn("Could not place the next training target", ex); }
                return;
            }
            _noiseFloorErrors.Add(trial.SignedErrorCm);
            _noiseFloorFlickCount++;
            if (_noiseFloorFlickCount >= NoiseFloorTargetFlicks)
                FinalizeNoiseFloorEstimation();
            else
            {
                // Repeat the same direction and distance so this measures
                // within-axis repeatability rather than directional bias.
                try { PlaceCardinalTrainingTarget(); } catch (Exception ex) { DesktopSensLog.Warn("Could not place the next training target", ex); }
            }
            UpdateFixesHud();
        }

        private void FinalizeNoiseFloorEstimation()
        {
            if (_profile == null) return;
            if (_noiseFloorErrors.Count < 3)
            {
                _isInNoiseFloorEstimation = false;
                _noiseFloorErrors.Clear();
                UpdateFixesHud();
                return;
            }
            double sigma = RobustStatistics.RobustSigma(_noiseFloorErrors);
            if (!IsFiniteValue(sigma) || sigma <= 0.0) sigma = 0.0;
            if (sigma > 3.0) sigma = 3.0;
            _profile.NoiseFloorCm = sigma;
            _profile.NoiseFloorEstimatedAtUtc = DateTime.UtcNow;
            _profile.NoiseFloorSampleCount = _noiseFloorErrors.Count;

            // Push the noise floor into the per-cell adaptive tolerances.
            // Distance-scaled: a flick to 45cm has more noise than a flick
            // to 15cm, so scale sigma by sqrt(distance/maxDistance).
            // FIX (8/16-way): iterate over the axes in the active mode so
            // diagonal axes' cells also get the adaptive tolerance.
            int directionCount = DesktopSensTrainingState.ResolveDirectionCount(_profile);
            int[] angleZones = DesktopSensTrainingState.AngleZoneIndicesForCount(directionCount);
            for (int axisIdx = 0; axisIdx < angleZones.Length; axisIdx++)
            {
                int angleZone = angleZones[axisIdx];
                int count = TrainingGeometry.Current.MultiDirectionZoneCount(axisIdx, directionCount);
                for (int d = 0; d < count; d++)
                {
                    double zoneCm = TrainingGeometry.Current.ZoneCenterCm(d);
                    double scale = Math.Sqrt(Math.Max(1.0, zoneCm) / Math.Max(1.0, _maxDistanceCm));
                    double perCellNoise = sigma * scale;
                    DesktopSensTrainingState.SetAdaptiveTolerance(
                        _profile.CellTrainingStates, angleZone, d, perCellNoise);
                }
            }
            _isInNoiseFloorEstimation = false;
            _noiseFloorErrors.Clear();
            OnCellVerifiedPersistProfile();
            UpdateFixesHud();
        }
    }
}
