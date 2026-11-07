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
        private void UpdateFixesHud()
        {
            if (_fixesHud == null) return;
            if (_profile == null) { _fixesHud.Text = "FIXES: no profile"; return; }

            var sb = new System.Text.StringBuilder();
            sb.Append("FIXES:");
            sb.Append(string.Format(" noise={0}",
                _profile.NoiseFloorCm > 0 ? string.Format("{0:F2}cm", _profile.NoiseFloorCm) : "n/a"));
            sb.Append(string.Format(" | seq-override={0}", _profile.AllowSequentialOverride ? "ON" : "off"));
            sb.Append(string.Format(" | micro-boost={0}", _profile.MicroBoostEnabled ? "ON" : "off"));
            sb.Append(" | V=counterbalanced A/B (baseline vs current)");

            if (_isInNoiseFloorEstimation)
                sb.Append(string.Format(" | NOISE-CAL {0}/{1}", _noiseFloorFlickCount, NoiseFloorTargetFlicks));
            if (_isInABTestMode && _abPlan != null)
                sb.Append(string.Format(" | A/B {0}/{1} pairs · {2}", _abPlan.CompletedPairs, _abPlan.PairCount,
                    _abPlan.CurrentConditionIsB ? "B" : "A"));
            if (!string.IsNullOrEmpty(_profile.LastBackupPath))
                sb.Append(string.Format(" | backup={0}", System.IO.Path.GetFileName(_profile.LastBackupPath)));

            _fixesHud.Text = sb.ToString();
        }

        private void OnFixesTrialFinished(DesktopSensTrial trial)
        {
            if (trial == null) return;
            if (_isInNoiseFloorEstimation)
                RecordNoiseFloorSample(trial);
            else if (_isInABTestMode)
                RecordABTestSample(trial);
        }

        /// <summary>
        /// Called once from the existing constructor at the end of setup.
        /// Initializes the fixes-HUD label.
        /// </summary>
        private void InitializeFixes()
        {
            if (_sensitivityFinderMode) return;
            _fixesHud = new Label
            {
                AutoSize = true,
                Location = new Point(20, 140),
                Padding = new Padding(8),
                BackColor = Color.FromArgb(220, 12, 16, 22),
                ForeColor = Color.Gainsboro,
                Font = new Font("Segoe UI", 8f, FontStyle.Regular),
                Text = "FIXES: idle"
            };
            try { ForwardMouseInput(_fixesHud); } catch (Exception ex) { DesktopSensLog.Warn("Could not forward mouse input to the fixes HUD", ex); }
            Controls.Add(_fixesHud);
            _fixesHud.BringToFront();
            FormClosed += (s, e) => { try { _fixesHud?.Dispose(); } catch (Exception ex) { DesktopSensLog.Warn("Could not dispose the fixes HUD", ex); } };
            UpdateFixesHud();
        }
    }
}
