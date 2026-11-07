using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using grapher.Models.Mouse;
using grapher.DesktopSens.Models;
using Newtonsoft.Json;

namespace grapher.DesktopSens.Views
{
    public class DesktopSensConfigForm : Form
    {
        private readonly AccelGUI _accelGUI;
        private DesktopSensProfile _profile;
        private readonly TextBox _gameSensBox;
        private readonly TextBox _dpiBox;
        private readonly TextBox _pollingRateBox;
        private readonly Label _profileStatus;
        private readonly Label _cpiStatus;

        // Sensitivity Finder UI.
        private readonly NumericUpDown _finderTrialsPerDirection;

        // FIX (8/16-way): direction-count mode selector. Bound to
        // _profile.CalibrationDirectionCount via the SelectedIndexChanged
        // handler. The trainer reads this at startup to decide how many
        // axes to render and which cells to populate.
        private ComboBox _directionCountCombo;

        // Base sensitivity per-axis manual editor. Independent of acceleration.
        private readonly NumericUpDown _baseRight;
        private readonly NumericUpDown _baseLeft;
        private readonly NumericUpDown _baseUp;
        private readonly NumericUpDown _baseDown;
        private bool _updatingBaseSensInputs;

        // MicroBoost UI (1 cm boost curve, independent of acceleration).
        private readonly CheckBox _microBoostEnable;
        private readonly NumericUpDown _microBoostLowStart;
        private readonly NumericUpDown _microBoostRange;

        private string ProfilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RawAccel", "DesktopSensProfile.json");

        public DesktopSensConfigForm(AccelGUI accelGUI)
        {
            _accelGUI = accelGUI ?? throw new ArgumentNullException(nameof(accelGUI));
            _profile = LoadProfile();

            Text = "Desktop Sens Calibration";
            Width = 540;
            Height = 680;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            // ─── Row 1: Hardware inputs ───────────────────────────────────────────
            Controls.Add(new Label { Text = "CS2 Sens:", Left = 20, Top = 23, Width = 130 });
            Controls.Add(new Label { Text = "Mouse DPI:", Left = 20, Top = 58, Width = 130 });
            Controls.Add(new Label { Text = "Polling rate (Hz):", Left = 20, Top = 93, Width = 130 });
            _gameSensBox = new TextBox { Left = 165, Top = 20, Width = 180, Text = _profile.UserInputGameSensitivity.ToString(CultureInfo.InvariantCulture) };
            _dpiBox = new TextBox { Left = 165, Top = 55, Width = 180, Text = _profile.MouseDPI.ToString(CultureInfo.InvariantCulture) };
            _pollingRateBox = new TextBox { Left = 165, Top = 90, Width = 180, Text = _profile.PollingRateHz.ToString(CultureInfo.InvariantCulture) };
            Controls.Add(_gameSensBox);
            Controls.Add(_dpiBox);
            Controls.Add(_pollingRateBox);

            var measureCpiButton = new Button
            {
                Text = "Measure actual CPI",
                Left = 360, Top = 53, Width = 150, Height = 27
            };
            measureCpiButton.Click += (s, e) => MeasureCpi();
            Controls.Add(measureCpiButton);
            _cpiStatus = new Label
            {
                Left = 360, Top = 84, Width = 160, Height = 28,
                Text = _profile.MeasuredCpi > 0.0
                    ? "CPI: " + _profile.MeasuredCpi.ToString("0.0", CultureInfo.InvariantCulture)
                    : "CPI: nominal fallback"
            };
            Controls.Add(_cpiStatus);

            // ─── Row 2: Sensitivity Finder ────────────────────────────────────────
            var finderLabel = new Label
            {
                Text = "SENSITIVITY FINDER  ·  populates Base Sensitivity below (independent of acceleration)",
                Left = 20, Top = 130, Width = 500, Height = 20,
                Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold),
                ForeColor = System.Drawing.Color.Khaki
            };
            Controls.Add(finderLabel);

            Controls.Add(new Label { Text = "Trials per direction:", Left = 20, Top = 158, Width = 140 });
            _finderTrialsPerDirection = new NumericUpDown
            {
                Left = 165, Top = 155, Width = 80,
                Minimum = 1, Maximum = 100, Value = 40
            };
            Controls.Add(_finderTrialsPerDirection);

            var finderBtn = new Button
            {
                Text = "Start Sensitivity Finder",
                Left = 260, Top = 153, Width = 240, Height = 28
            };
            finderBtn.Click += (s, e) => StartSensitivityFinder();
            Controls.Add(finderBtn);

            // ─── Row 3: Base Sensitivity (per axis, manual) ──────────────────────
            var baseLabel = new Label
            {
                Text = "BASE SENSITIVITY (per axis)  ·  multipliers on top of CS2 sens  ·  independent of acceleration",
                Left = 20, Top = 195, Width = 500, Height = 20,
                Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold),
                ForeColor = System.Drawing.Color.LightGreen
            };
            Controls.Add(baseLabel);

            // 2x2 grid: Right/Left on top, Up/Down on bottom. Each row has its own
            // label + numeric so the user can edit any of the 4 directions directly.
            _baseRight = MakeBaseSensInput("Right:",  20,  225, _profile.RightSensitivityFactor);
            _baseLeft  = MakeBaseSensInput("Left:",   270, 225, _profile.LeftSensitivityFactor);
            _baseUp    = MakeBaseSensInput("Up:",     20,  255, _profile.UpSensitivityFactor);
            _baseDown  = MakeBaseSensInput("Down:",   270, 255, _profile.DownSensitivityFactor);
            _baseRight.ValueChanged += (s, e) => PersistBaseSensInputs(false);
            _baseLeft.ValueChanged  += (s, e) => PersistBaseSensInputs(false);
            _baseUp.ValueChanged    += (s, e) => PersistBaseSensInputs(false);
            _baseDown.ValueChanged  += (s, e) => PersistBaseSensInputs(false);

            var saveBaseBtn = new Button
            {
                Text = "Save Base Sens",
                Left = 20, Top = 285, Width = 200, Height = 28
            };
            saveBaseBtn.Click += (s, e) => SaveBaseSens();
            Controls.Add(saveBaseBtn);

            var resetBaseBtn = new Button
            {
                Text = "Reset All to 1.000",
                Left = 240, Top = 285, Width = 200, Height = 28
            };
            resetBaseBtn.Click += (s, e) => ResetBaseSens();
            Controls.Add(resetBaseBtn);

            // ─── Row 4: Main training + calibrate + apply ────────────────────────
            // FIX (8/16-way): direction-count selector. The user wants the
            // option to choose 4-way (legacy), 8-way, or 16-way calibration.
            // The choice is persisted on the profile so the trainer picks
            // it up at startup and the arena renders the right number of
            // axes. Default is 4-way to preserve backward compatibility.
            Controls.Add(new Label
            {
                Text = "Calibration axes:",
                Left = 20, Top = 320, Width = 110,
                Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold)
            });
            _directionCountCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Left = 130, Top = 317, Width = 130,
            };
            _directionCountCombo.Items.AddRange(new object[]
            {
                "4-way (cardinal)",
                "8-way (cardinal + diagonal)",
                "16-way (every 22.5°)"
            });
            int currentCount = _profile.CalibrationDirectionCount;
            int comboIndex = currentCount == 8 ? 1 : (currentCount == 16 ? 2 : 0);
            _directionCountCombo.SelectedIndex = comboIndex;
            _directionCountCombo.SelectedIndexChanged += (s, e) =>
            {
                int idx = _directionCountCombo.SelectedIndex;
                int newCount = idx == 1 ? 8 : (idx == 2 ? 16 : 4);
                if (_profile.CalibrationDirectionCount != newCount)
                {
                    _profile.CalibrationDirectionCount = newCount;
                    // Switching direction-count mode invalidates the
                    // canonical-marker cache so the trainer redraws with
                    // the right number of axes next time it opens.
                    try { SaveProfileToDisk(); } catch (Exception ex)
                    {
                        DesktopSensLog.Error("Could not save direction-count change", ex);
                    }
                    _profileStatus.Text = "Calibration mode set to " + newCount + "-way | " + BuildProfileStatusLine();
                }
            };
            Controls.Add(_directionCountCombo);

            var startBtn = new Button { Text = "Start Training (mixed distances)", Left = 20, Top = 350, Width = 292, Height = 30 };
            var resetTrainingBtn = new Button
            {
                Text = "Reset Calibration",
                Left = 320, Top = 350, Width = 180, Height = 30,
                BackColor = System.Drawing.Color.FromArgb(70, 32, 32),
                ForeColor = System.Drawing.Color.White,
                FlatStyle = FlatStyle.Flat
            };
            resetTrainingBtn.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(150, 75, 75);
            var saveBtn = new Button { Text = "Recalculate Train Calibration && Save", Left = 20, Top = 385, Width = 420, Height = 30 };
            var applyBtn = new Button { Text = "Apply Saved 4-Way Profile to RawAccel", Left = 20, Top = 420, Width = 320, Height = 30 };

            // DIAGNOSTIC: a "Show Diagnostic Log" button so the user can
            // see what the calibrator and apply path logged. The log
            // shows, per direction, the fitted curve mode, sample count,
            // RMSE, and parameters — which is exactly the information
            // needed to understand why an acceleration curve was or
            // wasn't applied to the driver.
            var diagLogBtn = new Button
            {
                Text = "Show Diag Log",
                Left = 350, Top = 420, Width = 90, Height = 30,
                BackColor = System.Drawing.Color.FromArgb(40, 40, 60),
                ForeColor = System.Drawing.Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            diagLogBtn.Click += (s, e) => ShowDiagnosticLog();
            Controls.Add(diagLogBtn);

            startBtn.Click += (s, e) => StartTraining();
            resetTrainingBtn.Click += (s, e) => ResetTrainingProfile();
            saveBtn.Click += (s, e) => CalibrateAndSave();
            applyBtn.Click += (s, e) => ApplyDirectionalProfile();
            Controls.Add(startBtn);
            Controls.Add(resetTrainingBtn);
            Controls.Add(saveBtn);
            Controls.Add(applyBtn);

            // CLAUDE FIX (round 2, directive #4) — Reset LUT button.
            // Restores the previous argsX/argsY mode+parameters from the
            // profile's PreviousArgsXBeforeLut/PreviousArgsYBeforeLut
            // snapshots (taken the first time the LUT applier overwrote
            // argsX/argsY). After a successful restore, clears
            // _profile.LutOverwriteActive AND _profile.LastAppliedMergedProfile
            // so the next Train apply can take a fresh snapshot.
            var resetLutBtn = new Button
            {
                Text = "Reset LUT to previous mode",
                Left = 350, Top = 420, Width = 200, Height = 30,
                FlatStyle = FlatStyle.Flat
            };
            resetLutBtn.Click += (s, e) =>
            {
                string statusMessage;
                bool ok = TryRestorePreviousArgs(out statusMessage);
                if (ok)
                {
                    DesktopSensToast.Show(this, statusMessage);
                }
                else
                {
                    MessageBox.Show(this, statusMessage,
                        "Reset LUT", MessageBoxButtons.OK,
                        ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                }
            };
            Controls.Add(resetLutBtn);

            // ─── Row 5: 1 cm Boost curve ─────────────────────────────────────────
            var boostLabel = new Label
            {
                Text = "1 CM BOOST CURVE  ·  low sensitivity at start of movement, capped at base  ·  independent of acceleration",
                Left = 20, Top = 465, Width = 500, Height = 20,
                Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold),
                ForeColor = System.Drawing.Color.Khaki
            };
            Controls.Add(boostLabel);

            _microBoostEnable = new CheckBox
            {
                Text = "Enable 1 cm boost",
                Left = 20, Top = 493, Width = 220,
                Checked = _profile.MicroBoostEnabled
            };
            Controls.Add(_microBoostEnable);

            Controls.Add(new Label { Text = "Low-start (0.1–1.0):", Left = 250, Top = 496, Width = 130 });
            _microBoostLowStart = new NumericUpDown
            {
                Left = 380, Top = 493, Width = 100,
                Minimum = (decimal)MicroBoostCurve.MinLowStart,
                Maximum = (decimal)MicroBoostCurve.MaxLowStart,
                DecimalPlaces = 2, Increment = 0.05m,
                Value = (decimal)_profile.MicroBoostLowStart
            };
            Controls.Add(_microBoostLowStart);

            Controls.Add(new Label { Text = "Boost range (cm/s, 1–30):", Left = 20, Top = 528, Width = 180 });
            _microBoostRange = new NumericUpDown
            {
                Left = 200, Top = 525, Width = 100,
                Minimum = (decimal)MicroBoostCurve.MinBoostRangeCmPerSec,
                Maximum = (decimal)MicroBoostCurve.MaxBoostRangeCmPerSec,
                DecimalPlaces = 1, Increment = 0.5m,
                Value = (decimal)_profile.MicroBoostRangeCmPerSec
            };
            Controls.Add(_microBoostRange);

            var saveBoostBtn = new Button
            {
                Text = "Save Boost Settings",
                Left = 320, Top = 523, Width = 160, Height = 28
            };
            saveBoostBtn.Click += (s, e) => SaveMicroBoostSettings();
            Controls.Add(saveBoostBtn);

            // ─── Row 6: Status ────────────────────────────────────────────────────
            _profileStatus = new Label
            {
                Left = 20, Top = 570, Width = 500, Height = 90,
                Text = BuildInitialStatusText()
            };
            Controls.Add(_profileStatus);

            // FIX (8/16-way): the form grew because we added a new row
            // for the direction-count selector. Bump the form height so
            // the status label is fully visible.
            Height = 705;
        }

        private NumericUpDown MakeBaseSensInput(string labelText, int left, int top, double initialValue)
        {
            Controls.Add(new Label { Text = labelText, Left = left, Top = top + 3, Width = 60 });
            var box = new NumericUpDown
            {
                Left = left + 65, Top = top, Width = 100,
                Minimum = 0.5m, Maximum = 2.0m,
                DecimalPlaces = 3, Increment = 0.01m,
                Value = (decimal)Math.Max(0.5, Math.Min(2.0, initialValue))
            };
            Controls.Add(box);
            return box;
        }

        private string BuildInitialStatusText()
        {
            return "Trials: " + _profile.Trials.Count + " | Profile loads from LocalAppData" +
                Environment.NewLine + "Base Sens: R " + _profile.RightSensitivityFactor.ToString("0.###", CultureInfo.InvariantCulture) +
                "  L " + _profile.LeftSensitivityFactor.ToString("0.###", CultureInfo.InvariantCulture) +
                "  U " + _profile.UpSensitivityFactor.ToString("0.###", CultureInfo.InvariantCulture) +
                "  D " + _profile.DownSensitivityFactor.ToString("0.###", CultureInfo.InvariantCulture) +
                (_profile.MicroBoostEnabled
                    ? "  |  Boost ON low=" + _profile.MicroBoostLowStart.ToString("0.00", CultureInfo.InvariantCulture) +
                      " range=" + _profile.MicroBoostRangeCmPerSec.ToString("0.0", CultureInfo.InvariantCulture) + "cm/s"
                    : "  |  Boost OFF");
        }

        private string BuildProfileStatusLine()
        {
            return "Base Sens saved | R " + FormatBaseFactor(_profile.RightSensitivityFactor) +
                " L " + FormatBaseFactor(_profile.LeftSensitivityFactor) +
                " U " + FormatBaseFactor(_profile.UpSensitivityFactor) +
                " D " + FormatBaseFactor(_profile.DownSensitivityFactor) +
                (_profile.MicroBoostEnabled
                    ? " | BOOST low=" + _profile.MicroBoostLowStart.ToString("0.00", CultureInfo.InvariantCulture) +
                      " range=" + _profile.MicroBoostRangeCmPerSec.ToString("0.0", CultureInfo.InvariantCulture) + "cm/s"
                    : " | boost off");
        }

        private static string FormatBaseFactor(double factor)
        {
            return (double.IsNaN(factor) || double.IsInfinity(factor) || factor <= 0.0 ? 1.0 : factor)
                .ToString("0.###", CultureInfo.InvariantCulture);
        }

        private string FormatDirection(int direction)
        {
            double factor;
            double confidence;
            switch (direction)
            {
                case 0: factor = _profile.RightSensitivityFactor; confidence = _profile.RightSensitivityConfidence; break;
                case 1: factor = _profile.DownSensitivityFactor; confidence = _profile.DownSensitivityConfidence; break;
                case 2: factor = _profile.LeftSensitivityFactor; confidence = _profile.LeftSensitivityConfidence; break;
                case 3: factor = _profile.UpSensitivityFactor; confidence = _profile.UpSensitivityConfidence; break;
                default: return "?";
            }
            AccelCurveMode mode = _profile.GetDirectionalCurveMode(direction);
            string modeTag;
            switch (mode)
            {
                case AccelCurveMode.Linear:     modeTag = "linear"; break;
                case AccelCurveMode.Natural:    modeTag = "natural"; break;
                case AccelCurveMode.Power:      modeTag = "power"; break;
                case AccelCurveMode.Synchronous:modeTag = "sync"; break;
                default:                        modeTag = "flat"; break;
            }
            return FormatBaseFactor(factor) +
                " [" + modeTag + "]" +
                " (" + (confidence * 100.0).ToString("0", CultureInfo.InvariantCulture) + "%)";
        }

        private void SaveProfileToDisk()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath));
            File.WriteAllText(ProfilePath, JsonConvert.SerializeObject(_profile, Formatting.Indented));
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Base Sensitivity (per axis, manual edit + Sensitivity Finder population)
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Persist the four manual base-sens inputs to the profile. This is the
        /// "Save Base Sens" button. The values are independent multipliers — they
        /// do NOT replace CS2 sens, they multiply on top of it. They are also
        /// independent of any acceleration curve; the curve handles speed-dependent
        /// gain, base sens handles direction-dependent baseline gain.
        /// </summary>
        private void SaveBaseSens()
        {
            PersistBaseSensInputs(true);
        }

        // Base Sens is profile data, not an Apply-time setting. Persist edits as
        // soon as a value changes so closing the window or skipping Apply cannot
        // discard the user's four directional multipliers.
        private void PersistBaseSensInputs(bool showFeedback)
        {
            if (_updatingBaseSensInputs) return;

            _profile.RightSensitivityFactor = ClampBaseSensInput((double)_baseRight.Value);
            _profile.LeftSensitivityFactor  = ClampBaseSensInput((double)_baseLeft.Value);
            _profile.UpSensitivityFactor    = ClampBaseSensInput((double)_baseUp.Value);
            _profile.DownSensitivityFactor  = ClampBaseSensInput((double)_baseDown.Value);
            _profile.ModifiedTimestamp = DateTime.UtcNow;
            try
            {
                SaveProfileToDisk();
                if (showFeedback)
                    _profileStatus.Text = "Base Sens saved | " + BuildProfileStatusLine() +
                        Environment.NewLine + "Saved to profile; Apply is only needed to activate it in RawAccel.";
            }
            catch (Exception ex)
            {
                DesktopSensLog.Error("Base Sens profile persistence failed", ex);
                if (showFeedback)
                    MessageBox.Show(this, "Could not save base sens:\n" + ex.Message, "Desktop Sens",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                else
                    _profileStatus.Text = "Base Sens could not be saved: " + ex.Message;
            }
        }

        private void SetBaseSensInputsFromProfile()
        {
            _updatingBaseSensInputs = true;
            try
            {
                _baseRight.Value = (decimal)ClampBaseSensInput(_profile.RightSensitivityFactor);
                _baseLeft.Value  = (decimal)ClampBaseSensInput(_profile.LeftSensitivityFactor);
                _baseUp.Value    = (decimal)ClampBaseSensInput(_profile.UpSensitivityFactor);
                _baseDown.Value  = (decimal)ClampBaseSensInput(_profile.DownSensitivityFactor);
            }
            finally
            {
                _updatingBaseSensInputs = false;
            }
        }

        private void ResetBaseSens()
        {
            _updatingBaseSensInputs = true;
            try
            {
                _baseRight.Value = 1.000m;
                _baseLeft.Value  = 1.000m;
                _baseUp.Value    = 1.000m;
                _baseDown.Value  = 1.000m;
            }
            finally
            {
                _updatingBaseSensInputs = false;
            }
            SaveBaseSens();
        }

        private static double ClampBaseSensInput(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 1.0;
            if (value < 0.5) return 0.5;
            if (value > 2.0) return 2.0;
            return value;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Physical CPI measurement (five repeated 20 cm sweeps).
        // RawAccel must be disabled or neutral during this measurement.
        // ─────────────────────────────────────────────────────────────────────────

        private void MeasureCpi()
        {
            if (!TryReadSettings()) return;

            const double measuredDistanceCm = 20.0;
            const int requiredSweeps = 5;
            var measuredValues = new System.Collections.Generic.List<double>();
            using (var wizard = new Form
            {
                Text = "Measure physical mouse CPI",
                Width = 520,
                Height = 250,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            })
            {
                var instructions = new Label
                {
                    Left = 16, Top = 14, Width = 475, Height = 52,
                    Text = "Disable RawAccel or set it to neutral. Place the mouse at the start of a ruler, " +
                           "click Start, move it exactly 20.0 cm in a straight horizontal line, then click Finish. " +
                           "Repeat five times."
                };
                var status = new Label
                {
                    Left = 16, Top = 76, Width = 475, Height = 48,
                    Text = "Sweep 0/5 — ready."
                };
                var start = new Button { Text = "Start sweep", Left = 16, Top = 145, Width = 110, Height = 30 };
                var finish = new Button { Text = "Finish sweep", Left = 136, Top = 145, Width = 110, Height = 30, Enabled = false };
                var restart = new Button { Text = "Restart set", Left = 256, Top = 145, Width = 100, Height = 30, Enabled = false };
                var accept = new Button { Text = "Use measured CPI", Left = 366, Top = 145, Width = 125, Height = 30, Enabled = false };
                var cancel = new Button { Text = "Cancel", Left = 366, Top = 181, Width = 125, Height = 26, DialogResult = DialogResult.Cancel };
                wizard.Controls.Add(instructions);
                wizard.Controls.Add(status);
                wizard.Controls.Add(start);
                wizard.Controls.Add(finish);
                wizard.Controls.Add(restart);
                wizard.Controls.Add(accept);
                wizard.Controls.Add(cancel);
                wizard.CancelButton = cancel;

                object gate = new object();
                long dxSum = 0;
                long dySum = 0;
                int sampleCount = 0;
                bool capturing = false;
                double acceptedMedian = 0.0;

                EventHandler<MouseSample> handler = (sender, sample) =>
                {
                    if ((object)sample == null) return;
                    lock (gate)
                    {
                        if (!capturing) return;
                        dxSum += sample.DX;
                        dySum += sample.DY;
                        sampleCount++;
                    }
                };

                Action resetSet = () =>
                {
                    measuredValues.Clear();
                    acceptedMedian = 0.0;
                    accept.Enabled = false;
                    restart.Enabled = false;
                    status.Text = "Sweep 0/5 — ready.";
                };

                start.Click += (sender, args) =>
                {
                    lock (gate)
                    {
                        dxSum = 0;
                        dySum = 0;
                        sampleCount = 0;
                        capturing = true;
                    }
                    start.Enabled = false;
                    finish.Enabled = true;
                    status.Text = "Sweep " + (measuredValues.Count + 1) + "/5 — move exactly 20.0 cm.";
                };

                finish.Click += (sender, args) =>
                {
                    long x, y;
                    int count;
                    lock (gate)
                    {
                        capturing = false;
                        x = dxSum;
                        y = dySum;
                        count = sampleCount;
                    }
                    start.Enabled = measuredValues.Count + 1 < requiredSweeps;
                    finish.Enabled = false;

                    double counts = Math.Abs((double)x);
                    if (count < 5 || counts < 10.0)
                    {
                        status.Text = "Sweep rejected: too few samples or movement. Repeat this sweep.";
                        start.Enabled = true;
                        return;
                    }
                    if (Math.Abs((double)y) > Math.Max(5.0, counts * 0.05))
                    {
                        status.Text = "Sweep rejected: too much vertical drift. Keep the movement horizontal and repeat.";
                        start.Enabled = true;
                        return;
                    }

                    double cpi = counts / (measuredDistanceCm / 2.54);
                    if (cpi < DesktopSensConstants.MinMouseDPI || cpi > DesktopSensConstants.MaxMouseDPI)
                    {
                        status.Text = "Sweep rejected: measured CPI is outside the supported hardware range.";
                        start.Enabled = true;
                        return;
                    }
                    measuredValues.Add(cpi);
                    status.Text = "Sweep " + measuredValues.Count + "/5: " +
                        cpi.ToString("0.0", CultureInfo.InvariantCulture) + " CPI.";

                    if (measuredValues.Count == requiredSweeps)
                    {
                        var sorted = measuredValues.ToArray();
                        Array.Sort(sorted);
                        acceptedMedian = sorted[sorted.Length / 2];
                        double spread = (sorted[sorted.Length - 1] - sorted[0]) / acceptedMedian;
                        if (spread <= 0.01)
                        {
                            status.Text += Environment.NewLine + "Median: " +
                                acceptedMedian.ToString("0.0", CultureInfo.InvariantCulture) +
                                " CPI; spread " + (spread * 100.0).ToString("0.00", CultureInfo.InvariantCulture) +
                                "%. Measurement accepted.";
                            accept.Enabled = true;
                        }
                        else
                        {
                            status.Text += Environment.NewLine + "Spread is " +
                                (spread * 100.0).ToString("0.00", CultureInfo.InvariantCulture) +
                                "% (>1%). Restart all five sweeps.";
                            restart.Enabled = true;
                        }
                    }
                };

                restart.Click += (sender, args) =>
                {
                    resetSet();
                    start.Enabled = true;
                };

                accept.Click += (sender, args) =>
                {
                    if (acceptedMedian <= 0.0) return;
                    _profile.MeasuredCpi = acceptedMedian;
                    try
                    {
                        SaveProfileToDisk();
                        _cpiStatus.Text = "CPI: " + acceptedMedian.ToString("0.0", CultureInfo.InvariantCulture);
                        wizard.DialogResult = DialogResult.OK;
                        wizard.Close();
                    }
                    catch (Exception ex)
                    {
                        DesktopSensLog.Error("Desktop Sens configuration operation failed", ex);
                        MessageBox.Show(wizard, "Could not save measured CPI:\\n" + ex.Message,
                            "Desktop Sens", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };

                _accelGUI.MouseWatcher.RawMouseSampleReceived += handler;
                try
                {
                    wizard.ShowDialog(this);
                }
                finally
                {
                    lock (gate) capturing = false;
                    _accelGUI.MouseWatcher.RawMouseSampleReceived -= handler;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Sensitivity Finder
        // ─────────────────────────────────────────────────────────────────────────

        private void StartSensitivityFinder()
        {
            if (!TryReadSettings()) return;
            int trials = (int)_finderTrialsPerDirection.Value;

            using (var trainer = new DesktopSensTrainerForm(_profile, true, trials, null, SaveProfileToDisk))
            {
                EventHandler<MouseSample> handler = (sender, sample) => trainer.OnMouseSample(sample);
                _accelGUI.MouseWatcher.RawMouseSampleReceived += handler;
                try
                {
                    Hide();
                    trainer.ShowDialog();
                }
                finally
                {
                    Show();
                    _accelGUI.MouseWatcher.RawMouseSampleReceived -= handler;
                }
            }

            // After the finder closes, recalibrate only if Finder has observations.
            // An empty run must not reset the previously saved Base Sens factors.
            if (_profile.SensitivityFinderTrials == null || _profile.SensitivityFinderTrials.Count == 0)
            {
                _profileStatus.Text = "Sensitivity Finder collected no trials; existing Base Sens values were preserved.";
                return;
            }

            try
            {
                DirectionalCalibrator.Calibrate(_profile, _profile.SensitivityFinderTrials, 0.0, preserveUnobservedDirections: true);
                SaveProfileToDisk();

                // Reflect the new values back into the manual input boxes so the
                // user sees what the finder measured.
                SetBaseSensInputsFromProfile();

                // AUTO-APPLY: previously the user had to click "Apply
                // Saved 4-Way Profile to RawAccel" themselves after the
                // Sens Finder finished, which made it look like "the
                // calibration did nothing" (the profile had new values
                // but the driver still had the old ones). We now apply
                // the calibrated factors to the driver immediately so
                // the user sees the change in real life. If the apply
                // fails (e.g. base sensitivity invalid), the error is
                // shown in the status bar but the profile is still
                // saved — the user can fix the input and click Apply
                // manually.
                bool autoApplied = TryApplyDirectionalProfile(false);
                if (autoApplied)
                {
                    _profileStatus.Text = "Sensitivity Finder complete & APPLIED to RawAccel | " + BuildProfileStatusLine() +
                        Environment.NewLine + "The calibrated factors are now live. Re-run Sens Finder to recalibrate.";
                }
                else
                {
                    _profileStatus.Text = "Sensitivity Finder complete | " + BuildProfileStatusLine() +
                        Environment.NewLine + "Base Sens and trial history are saved in the profile. " +
                        "Click 'Apply Saved 4-Way Profile to RawAccel' to activate them in the driver.";
                }
            }
            catch (Exception ex)
            {
                DesktopSensLog.Error("Desktop Sens configuration operation failed", ex);
                MessageBox.Show(this, "Could not calibrate after finder:\n" + ex.Message, "Desktop Sens",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Main training
        // ─────────────────────────────────────────────────────────────────────────

        private void ResetTrainingProfile()
        {
            DialogResult result = MessageBox.Show(
                this,
                "Reset the calibration profile for the next user?\\n\\n" +
                "This will clear all recorded training trials, yellow/green cell states, " +
                "verified calibration factors, direction corrections, and trainer-specific " +
                "overrides. The next person will start with a clean calibration.\\n\\n" +
                "Hardware values (mouse DPI, measured CPI, and polling rate) are preserved. " +
                "The current RawAccel activation is not changed until a new profile is applied.\\n\\n" +
                "This cannot be undone. Continue?",
                "Reset Calibration",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes) return;

            try
            {
                _profile.CurrentSessionId = Guid.NewGuid().ToString("N");
                _profile.SessionStartedAtUtc = DateTime.UtcNow;
                _profile.AllowSequentialOverride = false;
                _profile.MicroBoostEnabled = false;
                _profile.MicroBoostLowStart = MicroBoostCurve.DefaultLowStart;
                _profile.MicroBoostRangeCmPerSec = MicroBoostCurve.DefaultBoostRangeCmPerSec;

                if (_profile.CellTrainingStates == null)
                    _profile.CellTrainingStates = new Dictionary<int, TrainingCellState>();
                else
                    _profile.CellTrainingStates.Clear();

                // This is an explicit full-training reset. Frozen LUT node
                // ownership and its build caches must be reset with cell state.
                if (_profile.FrozenSampleLutNodeFactorsByAngleZone != null)
                    _profile.FrozenSampleLutNodeFactorsByAngleZone.Clear();
                if (_profile.LastBuiltLutPointsByAngleZone != null)
                    _profile.LastBuiltLutPointsByAngleZone.Clear();
                if (_profile.LastBuiltLutBaseFactorByAngleZone != null)
                    _profile.LastBuiltLutBaseFactorByAngleZone.Clear();
                CalibrationResidualCache.Clear(_profile);

                if (_profile.Trials == null)
                    _profile.Trials = new List<DesktopSensTrial>();
                else
                    _profile.Trials.Clear();
                if (_profile.SensitivityFinderTrials == null)
                    _profile.SensitivityFinderTrials = new List<DesktopSensTrial>();
                else
                    _profile.SensitivityFinderTrials.Clear();

                DesktopSensCalibrator.Calibrate(_profile);
                DirectionalCalibrator.Calibrate(_profile, _profile.SensitivityFinderTrials, 0.0);

                _profile.RightSensitivityFactor = 1.0;
                _profile.LeftSensitivityFactor = 1.0;
                _profile.UpSensitivityFactor = 1.0;
                _profile.DownSensitivityFactor = 1.0;
                _profile.RightSensitivityConfidence = 0.0;
                _profile.LeftSensitivityConfidence = 0.0;
                _profile.UpSensitivityConfidence = 0.0;
                _profile.DownSensitivityConfidence = 0.0;
                _profile.RightRotationOffsetDegrees = 0.0;
                _profile.DownRotationOffsetDegrees = 0.0;
                _profile.LeftRotationOffsetDegrees = 0.0;
                _profile.UpRotationOffsetDegrees = 0.0;
                _profile.RightCurveMode = (int)AccelCurveMode.NoAccel;
                _profile.DownCurveMode = (int)AccelCurveMode.NoAccel;
                _profile.LeftCurveMode = (int)AccelCurveMode.NoAccel;
                _profile.UpCurveMode = (int)AccelCurveMode.NoAccel;

                // Reset must also discard old fitted-curve anchors; otherwise
                // the next user's sample-fit would inherit the prior user's LUT.
                if (_profile.LastBuiltLutPointsByAngleZone == null)
                    _profile.LastBuiltLutPointsByAngleZone = new Dictionary<int, List<LutPoint>>();
                else
                    _profile.LastBuiltLutPointsByAngleZone.Clear();
                if (_profile.LastBuiltLutBaseFactorByAngleZone == null)
                    _profile.LastBuiltLutBaseFactorByAngleZone = new Dictionary<int, double>();
                else
                    _profile.LastBuiltLutBaseFactorByAngleZone.Clear();

                _profile.AllCardinalCellsVerified = false;
                _profile.LastMergeAppliedTimestampUtc = null;
                _profile.LastABTestResult = null;
                _profile.PreviousAppliedMergedProfile = null;
                _profile.LastAppliedMergedProfile = null;
                _profile.LastBackupPath = null;
                _profile.LastBackupAtUtc = null;
                _profile.NoiseFloorCm = 0.0;
                _profile.NoiseFloorSampleCount = 0;
                _profile.NoiseFloorEstimatedAtUtc = null;
                _profile.CellsStable = 0;
                _profile.CellsLowConfidence = 0;
                _profile.CellsInsufficientData =
                    DesktopSensConstants.AngularZoneCount * TrainingGeometry.Current.DistanceZoneCount;
                _profile.OverallCoverage = 0.0;
                _profile.OverallConfidence = 0.0;
                _profile.ModifiedTimestamp = DateTime.UtcNow;

                // Keep hardware/device identity and the user's current game
                // sensitivity input intact; clear only calibration-derived data.
                _baseRight.Value = 1.000m;
                _baseLeft.Value = 1.000m;
                _baseUp.Value = 1.000m;
                _baseDown.Value = 1.000m;
                _microBoostEnable.Checked = false;
                _microBoostLowStart.Value = (decimal)MicroBoostCurve.DefaultLowStart;
                _microBoostRange.Value = (decimal)MicroBoostCurve.DefaultBoostRangeCmPerSec;

                SaveProfileToDisk();
                _profileStatus.Text = "Calibration reset | Ready for a new user. Hardware settings preserved.";
                System.Media.SystemSounds.Asterisk.Play();
            }
            catch (Exception ex)
            {
                DesktopSensLog.Error("Could not reset Desktop Sens calibration profile", ex);
                MessageBox.Show(this, "Could not reset calibration profile:\\n" + ex.Message,
                    "Reset Calibration", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StartTraining()
        {
            if (!TryReadSettings()) return;
            using (var trainer = new DesktopSensTrainerForm(_profile, false, 40, CreateCardinalProfileApplier(), SaveProfileToDisk, () => _accelGUI.Settings.ActiveProfile))
            {
                EventHandler<MouseSample> handler = (sender, sample) => trainer.OnMouseSample(sample);
                _accelGUI.MouseWatcher.RawMouseSampleReceived += handler;
                try
                {
                    Hide();
                    trainer.ShowDialog();
                }
                finally
                {
                    Show();
                    _accelGUI.MouseWatcher.RawMouseSampleReceived -= handler;
                }
            }
            try
            {
                SaveProfileToDisk();
                _profileStatus.Text = "Trials saved: " + _profile.Trials.Count + " | Click 'Calibrate' to update factors";
            }
            catch (Exception ex)
            {
                DesktopSensLog.Error("Desktop Sens configuration operation failed", ex);
                MessageBox.Show(this, "Could not save training trials:\n" + ex.Message, "Desktop Sens",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void CalibrateAndSave()
        {
            if (!TryReadSettings()) return;
            if (_profile.Trials.Count == 0)
            {
                DesktopSensToast.Show(this, "No trials to calibrate yet.");
                return;
            }

            try
            {
                DesktopSensCalibrator.Calibrate(_profile);
                SaveProfileToDisk();

                // Train recalculation updates per-cell factors only. Base Sens
                // belongs exclusively to Sensitivity Finder, so this action
                // must not read/apply Finder's directional multipliers.
                _profileStatus.Text = "Train calibration recomputed and saved | " + BuildProfileStatusLine() +
                    Environment.NewLine + "Use the Train merge/apply action to activate verified training cells.";
            }
            catch (Exception ex)
            {
                DesktopSensLog.Error("Desktop Sens configuration operation failed", ex);
                MessageBox.Show(this, "Could not save profile:\n" + ex.Message, "Desktop Sens",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // 1 cm Boost settings
        // ─────────────────────────────────────────────────────────────────────────

        private void SaveMicroBoostSettings()
        {
            double lowStart = (double)_microBoostLowStart.Value;
            double range = (double)_microBoostRange.Value;
            var clamped = MicroBoostCurve.ClampParameters(lowStart, range);
            _profile.MicroBoostEnabled = _microBoostEnable.Checked;
            _profile.MicroBoostLowStart = clamped.lowStart;
            _profile.MicroBoostRangeCmPerSec = clamped.boostRange;
            try
            {
                SaveProfileToDisk();
                _profileStatus.Text = "Boost settings saved | " + BuildProfileStatusLine();
            }
            catch (Exception ex)
            {
                DesktopSensLog.Error("Desktop Sens configuration operation failed", ex);
                MessageBox.Show(this, "Could not save boost settings:\n" + ex.Message, "Desktop Sens",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Apply to RawAccel
        // ─────────────────────────────────────────────────────────────────────────

        private void ApplyDirectionalProfile()
        {
            TryApplyDirectionalProfile(true);
        }

        /// <summary>
        /// Shared activation path used by both the Apply button and the trainer's
        /// merge adapter. Returns the actual RawAccel activation result instead
        /// of swallowing failures behind a void method.
        /// </summary>
        private bool TryApplyDirectionalProfile(bool showErrors)
        {
            double right = PositiveOrOne(_profile.RightSensitivityFactor);
            double left = PositiveOrOne(_profile.LeftSensitivityFactor);
            double up = PositiveOrOne(_profile.UpSensitivityFactor);
            double down = PositiveOrOne(_profile.DownSensitivityFactor);

            // DIAGNOSTIC: log the four cardinal factors that are about
            // to be applied to the driver. If the user runs the
            // Sensitivity Finder / Cardinal Trainer and then clicks
            // Apply but sees no change in the driver behaviour, the
            // most common reason is that the calibrator's
            // Right/Down/Left/UpSensitivityFactor are still at 1.0 —
            // either because the trial set was empty (no trials
            // collected) or because every trial had |signedError| smaller
            // than the noise floor and got rejected, or because the
            // user's flicks were so accurate that the estimator's log-
            // gain came out as ~0 (factor = exp(0) = 1.0). The user can
            // read this log entry via the "Show Diag Log" button.
            DesktopSensLog.Warn(
                "Apply: factors being read from profile: R=" + right.ToString("0.000") +
                " D=" + down.ToString("0.000") +
                " L=" + left.ToString("0.000") +
                " U=" + up.ToString("0.000"));
            DesktopSensLog.Warn(
                "Apply: profile has " + (_profile.Trials != null ? _profile.Trials.Count : 0) +
                " trials. DirectionalCurveModes: R=" + _profile.GetDirectionalCurveMode(0) +
                " D=" + _profile.GetDirectionalCurveMode(1) +
                " L=" + _profile.GetDirectionalCurveMode(2) +
                " U=" + _profile.GetDirectionalCurveMode(3));
            DesktopSensLog.Warn(
                "Apply: profile.RawAccelBaseOutputDPI=" + _profile.RawAccelBaseOutputDPI.ToString("0.0"));
            DesktopSensLog.Warn(
                "Apply: profile.NoiseFloorCm=" + _profile.NoiseFloorCm.ToString("0.000") +
                " profile.SensitivityConfidence[Right]=" + _profile.RightSensitivityConfidence.ToString("0.000"));

            try
            {
                var settings = _accelGUI.MakeSettingsFromFields();
                if (settings.outputDPI <= 0.0 || double.IsNaN(settings.outputDPI) || double.IsInfinity(settings.outputDPI))
                {
                    if (showErrors)
                        DesktopSensToast.Show(this, "The current base sensitivity is invalid.");
                    return false;
                }

                double baseOutputDPI = _profile.RawAccelBaseOutputDPI;
                if (double.IsNaN(baseOutputDPI) || double.IsInfinity(baseOutputDPI) || baseOutputDPI <= 0.0)
                    baseOutputDPI = settings.outputDPI;

                int horizontalCurveDirection = PickDominantCurveDirection(0, 2);
                int verticalCurveDirection = PickDominantCurveDirection(1, 3);
                bool curveApplied = horizontalCurveDirection >= 0 || verticalCurveDirection >= 0;

                // DIAGNOSTIC: log the picked dominant directions so the user
                // can tell (from DesktopSensLog.GetRecentEntries) why a
                // curve was or wasn't applied. The most common reason
                // horizontalCurveDirection and verticalCurveDirection are
                // both -1 (so curveApplied = false) is that every per-axis
                // DirectionalCurveMode is still NoAccel — typically
                // because each direction has fewer than
                // CurveFittingMinSamples (5) trials collected, or because
                // the fitter could not find a non-degenerate curve.
                DesktopSensLog.Warn(
                    "Apply: horizontalCurveDirection=" + horizontalCurveDirection +
                    ", verticalCurveDirection=" + verticalCurveDirection +
                    ", curveApplied=" + curveApplied);
                if (!curveApplied)
                {
                    // Log each direction's current mode so the user can see
                    // which axes are still NoAccel.
                    DesktopSensLog.Warn(
                        "  Direction curve modes: R=" + _profile.GetDirectionalCurveMode(0) +
                        ", D=" + _profile.GetDirectionalCurveMode(1) +
                        ", L=" + _profile.GetDirectionalCurveMode(2) +
                        ", U=" + _profile.GetDirectionalCurveMode(3));
                    DesktopSensLog.Warn(
                        "  Direction sample counts: R=" + SafeGetCount(0) +
                        ", D=" + SafeGetCount(1) +
                        ", L=" + SafeGetCount(2) +
                        ", U=" + SafeGetCount(3) +
                        " (min required for fit: " + DesktopSensConstants.CurveFittingMinSamples + ")");
                }

                // MicroBoost is a capped low-speed ramp, not a RawAccel Power
                // curve. The previous approximation wrote a Power curve over
                // both axis fits and could produce gain > 1 instead of the
                // requested [lowStart, 1] ramp. The current RawAccel bridge
                // cannot represent it natively, so we log a warning and
                // SKIP MicroBoost — but we no longer abort the entire
                // Apply call. Aborting was a regression: a user with
                // MicroBoost enabled could not apply the cardinal factors
                // they had just calibrated. The right behaviour is to apply
                // what we can (the four cardinal factors and the per-axis
                // acceleration curves) and tell the user MicroBoost was
                // skipped, so they can disable it themselves or wait for
                // a LUT-capable bridge.
                bool microBoostSkipped = false;
                if (_profile.MicroBoostEnabled &&
                    !MicroBoostCurve.IsIdentity(_profile.MicroBoostLowStart))
                {
                    DesktopSensLog.Warn("MicroBoost was skipped because the current RawAccel bridge cannot represent its capped ramp. The cardinal factors and acceleration curves were still applied.",
                        new NotSupportedException("MicroBoost requires a LUT-capable or composed-curve adapter."));
                    microBoostSkipped = true;
                    if (showErrors)
                        DesktopSensToast.Show(this,
                            "MicroBoost was skipped (the current RawAccel bridge cannot represent its capped ramp). The rest of the calibrated profile is still applied; disable MicroBoost to silence this message.");
                }
                if (curveApplied && !TryApplyDirectionalCurves(settings,
                    horizontalCurveDirection, verticalCurveDirection))
                {
                    if (showErrors)
                        DesktopSensToast.Show(this, "RawAccel could not accept the calibrated horizontal/vertical curve fields.");
                    return false;
                }

                // RawAccel exposes one acceleration argument struct per axis.
                // Apply the best-supported horizontal fit to argsX and the
                // best-supported vertical fit to argsY. Opposite signs still
                // share their axis curve because the native interface has no
                // independent positive/negative LUT slots. Per-zone curves
                // remain stored in the profile; this adapter does not claim
                // four independent directional LUTs.
                settings.outputDPI = baseOutputDPI * right;
                settings.lrOutputDPIRatio = left / right;
                settings.yxOutputDPIRatio = down / right;
                settings.udOutputDPIRatio = up / down;

                // Do not send Train's far-distance fit to RawAccel's global
                // speed curve: that curve also affects short micro-flicks because
                // RawAccel exposes no distance-since-flick gate or per-zone LUT.
                // (The 3 cm Train-calibration exemption itself has been removed;
                // BaseZoneEndCm is now 0.0. This note is about RawAccel's native
                // adapter limitation, not about Train's calibration model.)
                string trainCurveNote =
                    "Train far-zone acceleration retained in profile but not applied: RawAccel has no distance-gated per-zone interface";

                if (!_accelGUI.Settings.TryActivate(settings, out string errors))
                {
                    if (showErrors)
                        MessageBox.Show(this, errors ?? "RawAccel rejected the calibrated profile.", "Cannot apply",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                _profile.RawAccelBaseOutputDPI = baseOutputDPI;
                try { SaveProfileToDisk(); }
                catch (Exception saveException)
                {
                    DesktopSensLog.Error("RawAccel activated but saving the profile failed", saveException);
                    if (showErrors)
                        MessageBox.Show(this, "RawAccel activated, but the profile could not be saved:\n" +
                            saveException.Message, "Desktop Sens", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                _accelGUI.RefreshActive();
                _accelGUI.RefreshUser();

                string curveNote = curveApplied
                    ? " | accel=axis-specific (best-supported direction per axis)"
                    : " | accel=unchanged";
                // FIX (MicroBoost regression): show three possible states —
                // off, identity (no-op), or skipped (the user enabled it
                // but the bridge cannot represent it natively).
                string boostNote;
                if (microBoostSkipped)
                    boostNote = " | BOOST skipped (bridge cannot represent its capped ramp)";
                else if (_profile.MicroBoostEnabled)
                    boostNote = " | BOOST identity (low-start=1.00; no effect)";
                else
                    boostNote = " | boost off";
                _profileStatus.Text = "Applied directional base profile to RawAccel | R " +
                    right.ToString("0.###", CultureInfo.InvariantCulture) +
                    " L " + left.ToString("0.###", CultureInfo.InvariantCulture) +
                    " U " + up.ToString("0.###", CultureInfo.InvariantCulture) +
                    " D " + down.ToString("0.###", CultureInfo.InvariantCulture) +
                    curveNote + boostNote +
                    " | " + trainCurveNote +
                    " | per-zone calibration factors retained in profile (not mapped by this adapter)";
                // Refresh the four NumericUpDown boxes so the user sees
                // the calibrated values (not the pre-Apply stale values).
                // Without this call the boxes keep showing whatever the
                // user last typed, even though the profile now holds the
                // calibrated values — which can make the user think
                // "the calibration did nothing" when in fact it just
                // did not reflect in the UI.
                SetBaseSensInputsFromProfile();
                DesktopSensLog.Warn(
                    "Apply: SUCCESS — settings activated in RawAccel, " +
                    "profile saved, base-sens boxes refreshed. " +
                    "settings.outputDPI=" + settings.outputDPI.ToString("0.0") +
                    " (baseOutputDPI " + baseOutputDPI.ToString("0.0") +
                    " * right " + right.ToString("0.000") + ")");
                return true;
            }
            catch (Exception ex)
            {
                DesktopSensLog.Error("Desktop Sens configuration operation failed", ex);
                if (showErrors)
                    MessageBox.Show(this, "Could not apply directional profile:\n" + ex.Message, "Desktop Sens",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private bool TryApplyTrainDirectionalFactors(
            MergedCardinalProfile merged,
            double rightFactor,
            double downFactor,
            double leftFactor,
            double upFactor)
        {
            // Train and Sensitivity Finder have independent calibration data.
            // With BaseZoneEndCm = 0.0 the Train per-cell model now covers the
            // full reachable range (including micro-flicks). The applier
            // builds a LUT (lookup-table) curve from the green (Verified +
            // PrecisionVerified) cells per axis and writes it to argsX/argsY.
            //
            // COMBINED FIX (Claude directive):
            //   • Before overwriting argsX/argsY, snapshot the user's
            //     previous mode+parameters into
            //     _profile.PreviousArgsXBeforeLut/PreviousArgsYBeforeLut
            //     so a Reset can restore them.
            //   • If no green cells exist on either direction of an axis,
            //     that axis is left untouched (the user's previous mode
            //     stays). The status message reports this honestly.
            //   • If green cells exist, argsX is the combined LUT of
            //     Right+Left; argsY is the combined LUT of Up+Down. The
            //     combination is explicit and logged.
            //   • After TryActivate, re-read the active profile and
            //     verify argsX/argsY mode and length match what we wrote.
            //     If they do not match, return false (the apply did not
            //     reach the driver).
            //   • The base directional multipliers (outputDPI,
            //     lrOutputDPIRatio, etc.) are still applied as before —
            //     they carry the directional base sensitivity, while the
            //     LUT carries the per-cell speed-dependent correction.
            try
            {
                if (merged == null)
                {
                    DesktopSensLog.Warn("TryApplyTrainDirectionalFactors: merged profile is null.");
                    return false;
                }

                var settings = _accelGUI.MakeSettingsFromFields();
                // Keep an untouched settings object so a post-activation
                // verification failure can restore the exact prior base
                // ratios and both axis acceleration modes atomically.
                var rollbackSettings = _accelGUI.MakeSettingsFromFields();
                if (rollbackSettings == null || settings == null)
                {
                    DesktopSensLog.Warn("Train apply aborted: could not snapshot the current RawAccel settings.");
                    return false;
                }
                double baseOutputDPI = _profile.RawAccelBaseOutputDPI;
                if (double.IsNaN(baseOutputDPI) || double.IsInfinity(baseOutputDPI) ||
                    baseOutputDPI <= 0.0)
                    baseOutputDPI = settings.outputDPI;
                if (double.IsNaN(baseOutputDPI) || double.IsInfinity(baseOutputDPI) ||
                    baseOutputDPI <= 0.0)
                    return false;

                double right = PositiveOrOne(rightFactor);
                double down = PositiveOrOne(downFactor);
                double left = PositiveOrOne(leftFactor);
                double up = PositiveOrOne(upFactor);

                settings.outputDPI = baseOutputDPI * right;
                settings.lrOutputDPIRatio = left / right;
                settings.yxOutputDPIRatio = down / right;
                settings.udOutputDPIRatio = up / down;

                // ── Build the LUT for each axis from the green cells. ──
                // argsX carries Right + Left combined.
                // argsY carries Up + Down combined.
                // Each direction's LutPoints come from
                // DesktopSensProfileMerger.BuildLutPoints, which only
                // includes green (Verified + PrecisionVerified) cells.
                string xCombinationNote, yCombinationNote;
                List<LutPoint> xPoints;
                List<LutPoint> yPoints;
                bool rightHasPoints = merged.Right?.LutPoints != null && merged.Right.LutPoints.Count > 0;
                bool leftHasPoints = merged.Left?.LutPoints != null && merged.Left.LutPoints.Count > 0;
                bool upHasPoints = merged.Up?.LutPoints != null && merged.Up.LutPoints.Count > 0;
                bool downHasPoints = merged.Down?.LutPoints != null && merged.Down.LutPoints.Count > 0;
                bool xUsesContinuousFit = rightHasPoints && leftHasPoints
                    ? merged.Right.UsesSampleFitLut && merged.Left.UsesSampleFitLut
                    : (rightHasPoints ? merged.Right.UsesSampleFitLut :
                       (leftHasPoints && merged.Left.UsesSampleFitLut));
                bool yUsesContinuousFit = upHasPoints && downHasPoints
                    ? merged.Up.UsesSampleFitLut && merged.Down.UsesSampleFitLut
                    : (upHasPoints ? merged.Up.UsesSampleFitLut :
                       (downHasPoints && merged.Down.UsesSampleFitLut));

                if (xUsesContinuousFit)
                {
                    xPoints = RawAccelSettingsBridge.CombineSampleFitLutPointsForAxis(
                        merged.Right?.LutPoints, merged.Left?.LutPoints,
                        out xCombinationNote, "Right", "Left");
                }
                else
                {
                    xPoints = RawAccelSettingsBridge.CombineLutPointsForAxis(
                        merged.Right?.LutPoints, merged.Left?.LutPoints, out xCombinationNote,
                        merged.Right?.MeanConfidence ?? 0.0, merged.Left?.MeanConfidence ?? 0.0,
                        "Right", "Left");
                }

                if (yUsesContinuousFit)
                {
                    yPoints = RawAccelSettingsBridge.CombineSampleFitLutPointsForAxis(
                        merged.Up?.LutPoints, merged.Down?.LutPoints,
                        out yCombinationNote, "Up", "Down");
                }
                else
                {
                    yPoints = RawAccelSettingsBridge.CombineLutPointsForAxis(
                        merged.Up?.LutPoints, merged.Down?.LutPoints, out yCombinationNote,
                        merged.Up?.MeanConfidence ?? 0.0, merged.Down?.MeanConfidence ?? 0.0,
                        "Up", "Down");
                }

                bool xHasLut = xPoints != null && xPoints.Count > 0;
                bool yHasLut = yPoints != null && yPoints.Count > 0;
                bool xHasDirectionalData = rightHasPoints || leftHasPoints;
                bool yHasDirectionalData = upHasPoints || downHasPoints;

                if ((!xHasLut && xHasDirectionalData) || (!yHasLut && yHasDirectionalData))
                {
                    string reason =
                        (!xHasLut && xHasDirectionalData
                            ? "X axis has calibrated points but no valid shared LUT range. " + (xCombinationNote ?? "")
                            : "") +
                        ((!yHasLut && yHasDirectionalData)
                            ? " Y axis has calibrated points but no valid shared LUT range. " + (yCombinationNote ?? "")
                            : "");
                    _profileStatus.Text = "Train apply rejected: " + reason;
                    DesktopSensToast.Show(this, "Train apply rejected: no compatible LUT speed range.");
                    DesktopSensLog.Warn("Train apply aborted: " + reason);
                    return false;
                }

                if (!xHasLut && !yHasLut)
                {
                    _profileStatus.Text = "No usable LUT samples from calibrated cells — nothing to apply.";
                    DesktopSensToast.Show(this, "No usable LUT samples from calibrated cells.");
                    DesktopSensLog.Warn(
                        "Train apply aborted: no usable LUT samples from calibrated cells. " +
                        "X: " + (xCombinationNote ?? "(none)") +
                        " | Y: " + (yCombinationNote ?? "(none)"));
                    return false;
                }

                // Snapshot the user's previous argsX/argsY (mode +
                // parameters) BEFORE overwriting, so a Reset button can
                // restore them. We only snapshot when we are about to
                // actually overwrite (i.e. when xHasLut or yHasLut).
                if (xHasLut || yHasLut)
                {
                    if (!_profile.LutOverwriteActive)
                    {
                        // Capture the previous mode only on the FIRST
                        // LUT overwrite; subsequent Train applies keep
                        // the same snapshot so the user can Reset back to
                        // the very first hand-set mode even after many
                        // Train applies.
                        _profile.PreviousArgsXBeforeLut = SnapshotAccelArgs(settings, "argsX");
                        _profile.PreviousArgsYBeforeLut = SnapshotAccelArgs(settings, "argsY");
                    }
                }

                // Apply the LUT to argsX (Right + Left combined).
                string xLutError = null;
                bool xLutApplied = false;
                if (xHasLut)
                {
                    Dictionary<string, object> xLutValues;
                    if (RawAccelSettingsBridge.TryBuildLutValues(xPoints, out xLutValues, out xLutError, _profile.LutUpperBookend))
                    {
                        xLutApplied = RawAccelSettingsBridge.TrySetNestedFields(
                            settings, "argsX", xLutValues, out xLutError);
                    }
                }

                // Apply the LUT to argsY (Up + Down combined).
                string yLutError = null;
                bool yLutApplied = false;
                if (yHasLut)
                {
                    Dictionary<string, object> yLutValues;
                    if (RawAccelSettingsBridge.TryBuildLutValues(yPoints, out yLutValues, out yLutError, _profile.LutUpperBookend))
                    {
                        yLutApplied = RawAccelSettingsBridge.TrySetNestedFields(
                            settings, "argsY", yLutValues, out yLutError);
                    }
                }

                // Determine the honest apply state.
                //   • xLutApplied + yLutApplied: full LUT apply
                //   • xLutApplied only (no y green cells): X LUT apply,
                //     Y stays as the user had it
                //   • yLutApplied only: symmetric
                //   • neither: pure base-factor apply (the user's
                //     argsX/argsY modes are unchanged). If the four base
                //     factors are all 1.0, this is a TRUE no-op.
                bool anyLutApplied = xLutApplied || yLutApplied;
                bool anyLutFailedButHadGreen = (xHasLut && !xLutApplied) || (yHasLut && !yLutApplied);

                // Never activate a partial/incorrect profile when a green-cell LUT
                // could not be built or written. The old code only logged this
                // condition after activation and still returned success.
                if (anyLutFailedButHadGreen)
                {
                    string failure = "LUT build/write failed. X: " + (xLutError ?? "(none)") +
                        " | Y: " + (yLutError ?? "(none)");
                    _profileStatus.Text = "Train apply rejected: " + failure;
                    DesktopSensToast.Show(this, "Train apply rejected: LUT build/write failed. See profile status.");
                    DesktopSensLog.Warn("Train apply aborted: " + failure);
                    return false;
                }

                if (!_accelGUI.Settings.TryActivate(settings, out string errors))
                {
                    DesktopSensLog.Warn(
                        "Train apply failed: RawAccel rejected the settings. " +
                        (errors ?? "No native error details.") +
                        " | xLutError=" + (xLutError ?? "(none)") +
                        " | yLutError=" + (yLutError ?? "(none)"));
                    return false;
                }

                // ── Re-read the active settings and verify the LUT
                // (if we wrote one) actually reached the driver. ──
                // The Settings.ActiveProfile is the profile the driver
                // now sees. We compare its argsX/argsY mode ("lut") and
                // length to what we wrote. If they do not match, the
                // apply did not reach the driver — return false.
                bool verified = VerifyLutApply(xLutApplied, yLutApplied,
                    xPoints != null ? xPoints.Count : 0,
                    yPoints != null ? yPoints.Count : 0);
                if (!verified)
                {
                    DesktopSensLog.Warn(
                        "Train apply failed: post-TryActivate verification — " +
                        "the active profile's argsX/argsY does not match what we wrote. " +
                        "Attempting to restore the complete pre-apply RawAccel settings.");
                    if (!_accelGUI.Settings.TryActivate(rollbackSettings, out string rollbackErrors))
                    {
                        DesktopSensLog.Error(
                            "Train apply rollback failed after post-activation verification failure. " +
                            "RawAccel may still contain the attempted settings. " +
                            (rollbackErrors ?? "(no native error details)"),
                            new InvalidOperationException("Could not restore the pre-apply RawAccel settings."));
                    }
                    else
                    {
                        _accelGUI.RefreshActive();
                        _accelGUI.RefreshUser();
                        DesktopSensLog.Warn("Train apply rollback succeeded; previous RawAccel settings restored.");
                    }
                    return false;
                }

                _profile.RawAccelBaseOutputDPI = baseOutputDPI;
                _profile.LutOverwriteActive = anyLutApplied;
                try { SaveProfileToDisk(); }
                catch (Exception saveException)
                {
                    DesktopSensLog.Error("Train factors activated but profile save failed", saveException);
                }
                _accelGUI.RefreshActive();
                _accelGUI.RefreshUser();

                // ── Build the honest status message. ──
                string applyState;
                if (anyLutApplied)
                {
                    applyState = "LUT applied";
                    if (xLutApplied && yLutApplied)
                        applyState += " to argsX (Right+Left) and argsY (Up+Down)";
                    else if (xLutApplied)
                        applyState += " to argsX (Right+Left); argsY unchanged";
                    else
                        applyState += " to argsY (Up+Down); argsX unchanged";
                    if (anyLutFailedButHadGreen)
                        applyState += " (some axis had green cells but LUT build failed — see log)";
                    applyState += " | LUT replaced previous argsX/argsY mode (snapshot saved in profile for Reset)";
                }
                else
                {
                    applyState = "base factors only (no LUT — no green cells)";
                }

                string axisCalibrationNote =
                    (xCombinationNote ?? "") + " " + (yCombinationNote ?? "");
                if (axisCalibrationNote.IndexOf("Only ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    axisCalibrationNote.IndexOf("conflict", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    axisCalibrationNote.IndexOf("selected for", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _profileStatus.Text = "Train applied with shared-axis calibration warning. " +
                        "RawAccel's LUT affects BOTH directions on each applied axis. " +
                        axisCalibrationNote.Trim();
                }
                else
                {
                    _profileStatus.Text = "Train calibration applied | " + applyState +
                        " | RawAccel's LUT affects BOTH directions on each applied axis. " +
                        axisCalibrationNote.Trim();
                }

                DesktopSensLog.Warn(
                    "Train apply: " + applyState +
                    " | R=" + right.ToString("0.000") +
                    " D=" + down.ToString("0.000") +
                    " L=" + left.ToString("0.000") +
                    " U=" + up.ToString("0.000") +
                    " | X points: " + (xPoints != null ? xPoints.Count : 0) +
                    " (" + xCombinationNote + ")" +
                    " | Y points: " + (yPoints != null ? yPoints.Count : 0) +
                    " (" + yCombinationNote + ")" +
                    " | xLutError=" + (xLutError ?? "(none)") +
                    " | yLutError=" + (yLutError ?? "(none)"));
                return true;
            }
            catch (Exception ex)
            {
                DesktopSensLog.Error("Could not activate Train directional factors", ex);
                return false;
            }
        }

        /// <summary>
        /// Snapshot the user's previous AccelArgs (mode + parameters) on
        /// the given nested field ("argsX" or "argsY") of the active
        /// RawAccel settings, as a dictionary that can later be passed
        /// back to RawAccelSettingsBridge.TrySetNestedFields to restore
        /// the previous mode.
        ///
        /// Returns null if the field is missing or cannot be read.
        /// </summary>
        private Dictionary<string, object> SnapshotAccelArgs(object settings, string fieldName)
        {
            try
            {
                if (settings == null || string.IsNullOrEmpty(fieldName)) return null;
                var type = settings.GetType();
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;
                var field = type.GetField(fieldName, flags);
                if (field == null) return null;
                object nested = field.GetValue(settings);
                if (nested == null) return null;

                return SnapshotAccelArgsFromValue(nested);
            }
            catch (Exception ex)
            {
                DesktopSensLog.Error("Could not snapshot AccelArgs " + fieldName, ex);
                return null;
            }
        }

        /// <summary>
        /// Snapshot an AccelArgs boxed value into a dictionary. The
        /// dictionary's keys are the public field names of AccelArgs
        /// ("mode", "gain", "inputOffset", "outputOffset",
        /// "acceleration", "decayRate", "gamma", "motivity",
        /// "exponentClassic", "scale", "exponentPower", "limit",
        /// "syncSpeed", "smooth", "cap", "capMode", "length", "data").
        ///
        /// The "cap" field is a Vec2<double> value struct (in the C++/CLI
        /// wrapper) — we snapshot it as a nested Dictionary with "x"
        /// and "y" keys so we can restore it via reflection. The "data"
        /// field is a float[] which we clone.
        ///
        /// The dictionary is JSON-serializable (Newtonsoft.Json can
        /// round-trip Dictionary&lt;string, object&gt; with nested
        /// dictionaries and float[] arrays). On deserialization, "data"
        /// comes back as a JArray — the Reset path must convert it
        /// back to float[] (see RestoreArgsFromSnapshot).
        /// </summary>
        private Dictionary<string, object> SnapshotAccelArgsFromValue(object accelArgs)
        {
            if (accelArgs == null) return null;
            var type = accelArgs.GetType();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;
            var snap = new Dictionary<string, object>();
            foreach (var f in type.GetFields(flags))
            {
                if (f.IsInitOnly) continue;
                object v = f.GetValue(accelArgs);
                if (v == null) continue;

                if (v is float[] arr)
                {
                    var clone = new float[arr.Length];
                    Array.Copy(arr, clone, arr.Length);
                    snap[f.Name] = clone;
                }
                else if (v is bool || v is int || v is double ||
                         v is float || v is string || v.GetType().IsEnum)
                {
                    snap[f.Name] = v;
                }
                else
                {
                    // Complex nested struct (e.g. Vec2<double> cap).
                    // Snapshot it as a nested dictionary of its
                    // public fields. This handles Vec2<double>'s x
                    // and y fields generically.
                    var nestedType = v.GetType();
                    if (nestedType.IsValueType)
                    {
                        var nestedSnap = new Dictionary<string, object>();
                        foreach (var nf in nestedType.GetFields(flags))
                        {
                            if (nf.IsInitOnly) continue;
                            object nv = nf.GetValue(v);
                            if (nv == null) continue;
                            if (nv is double || nv is float || nv is int || nv is bool)
                                nestedSnap[nf.Name] = nv;
                        }
                        if (nestedSnap.Count > 0)
                            snap[f.Name] = nestedSnap;
                    }
                }
            }
            return snap;
        }

        /// <summary>
        /// Restore an AccelArgs nested struct (argsX or argsY) from a
        /// previously-snapshotted dictionary. CLAUDE DIRECTIVE #4 — the
        /// Reset path must:
        ///   • Convert Newtonsoft.Json JArray values to float[] for the
        ///     "data" field (the JSON deserializer returns JArray for
        ///     arrays, not float[]).
        ///   • Restore the "cap" Vec2<double> field from its nested
        ///     dictionary representation.
        ///   • Set every simple scalar field (mode, gain, inputOffset,
        ///     etc.) from the snapshot.
        ///   • Set LutOverwriteActive = false on success.
        /// </summary>
        private bool RestoreArgsFromSnapshot(object settings, string fieldName,
            Dictionary<string, object> snapshot, out string error)
        {
            error = null;
            if (settings == null || string.IsNullOrEmpty(fieldName) || snapshot == null)
            {
                error = "RestoreArgs: settings, fieldName, or snapshot is null.";
                return false;
            }
            try
            {
                var type = settings.GetType();
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;
                var parentField = type.GetField(fieldName, flags);
                if (parentField == null || parentField.IsInitOnly)
                {
                    error = "RestoreArgs: parent field '" + fieldName + "' is missing or read-only.";
                    return false;
                }
                object nested = parentField.GetValue(settings);
                if (nested == null)
                {
                    error = "RestoreArgs: nested AccelArgs is null.";
                    return false;
                }
                var nestedType = nested.GetType();

                // Apply each snapshot entry via reflection. We use
                // RawAccelSettingsBridge.TrySetNestedFields-like logic
                // but inline because we need to handle Vec2<double>
                // and float[] specially.
                foreach (var entry in snapshot)
                {
                    var nestedField = nestedType.GetField(entry.Key, flags);
                    if (nestedField == null || nestedField.IsInitOnly) continue;
                    object snapshotValue = entry.Value;
                    object converted;

                    if (nestedField.FieldType == typeof(float[]))
                    {
                        // The "data" field. The snapshot stores it as
                        // float[] (when in-memory) or as a
                        // Newtonsoft.Json.Linq.JArray (when deserialized
                        // from JSON). Convert either to float[].
                        converted = ConvertToFloatArray(snapshotValue);
                        if (converted == null)
                        {
                            error = "RestoreArgs: could not convert 'data' to float[].";
                            return false;
                        }
                    }
                    else if (!nestedField.FieldType.IsPrimitive &&
                             !nestedField.FieldType.IsEnum &&
                             nestedField.FieldType.IsValueType)
                    {
                        // A nested value struct like Vec2<double>. The
                        // snapshot stores it as a Dictionary<string,
                        // object> with "x" and "y" keys. We need to
                        // write each sub-field via reflection.
                        var nestedDict = snapshotValue as Dictionary<string, object>;
                        if (nestedDict == null)
                        {
                            // Skip if we cannot restore — but don't fail
                            // the whole restore. The cap field will keep
                            // its current value.
                            continue;
                        }
                        // Get the current value (boxed copy of the
                        // value struct), mutate its fields, then write
                        // it back.
                        object currentNested = nestedField.GetValue(nested);
                        if (currentNested == null) continue;
                        var subType = currentNested.GetType();
                        foreach (var subEntry in nestedDict)
                        {
                            var subField = subType.GetField(subEntry.Key, flags);
                            if (subField == null || subField.IsInitOnly) continue;
                            object subConverted;
                            if (!TryConvertRestoreValue(subEntry.Value, subField.FieldType, out subConverted))
                                continue;
                            subField.SetValue(currentNested, subConverted);
                        }
                        nestedField.SetValue(nested, currentNested);
                        continue;
                    }
                    else
                    {
                        if (!TryConvertRestoreValue(snapshotValue, nestedField.FieldType, out converted))
                        {
                            error = "RestoreArgs: could not convert '" + entry.Key + "' to " + nestedField.FieldType.Name + ".";
                            return false;
                        }
                    }
                    nestedField.SetValue(nested, converted);
                }

                parentField.SetValue(settings, nested);
                return true;
            }
            catch (Exception ex)
            {
                error = "RestoreArgs: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Convert a snapshot value (which may be a Newtonsoft.Json
        /// JToken, JArray, JValue, or a native CLR type) to a float[]
        /// of size LUT_RAW_DATA_CAPACITY = 514. Used by
        /// RestoreArgsFromSnapshot for the AccelArgs.data field.
        ///
        /// If the input is a float[] of the correct size, it is
        /// returned as-is. If it is a JArray (JSON deserialization), we
        /// extract each element as a float. If it is shorter than
        /// 514, we pad with zeros.
        /// </summary>
        private static float[] ConvertToFloatArray(object value)
        {
            const int Capacity = 514; // LUT_RAW_DATA_CAPACITY
            if (value is float[] fa)
            {
                if (fa.Length == Capacity) return fa;
                var padded = new float[Capacity];
                Array.Copy(fa, padded, Math.Min(fa.Length, Capacity));
                return padded;
            }
            if (value == null) return null;

            // Newtonsoft.Json JArray path. We use reflection to avoid a
            // hard dependency on Newtonsoft.Json.Linq from this layer
            // (the wrapper assembly already depends on Newtonsoft.Json,
            // so the types are available at runtime).
            var valueType = value.GetType();
            if (valueType.FullName == "Newtonsoft.Json.Linq.JArray" ||
                valueType.Name == "JArray")
            {
                var result = new float[Capacity];
                int idx = 0;
                // Use IEnumerable iteration via reflection.
                var enumerable = value as System.Collections.IEnumerable;
                if (enumerable == null) return null;
                foreach (var item in enumerable)
                {
                    if (idx >= Capacity) break;
                    if (item == null) { idx++; continue; }
                    // JValue → use its Value via reflection or Convert.
                    try
                    {
                        result[idx] = Convert.ToSingle(item);
                    }
                    catch
                    {
                        // Try the JValue.Value path.
                        var itemType = item.GetType();
                        var valProp = itemType.GetProperty("Value");
                        if (valProp != null)
                        {
                            try
                            {
                                object inner = valProp.GetValue(item);
                                if (inner != null)
                                    result[idx] = Convert.ToSingle(inner);
                            }
                            catch { /* leave as 0 */ }
                        }
                    }
                    idx++;
                }
                return result;
            }
            return null;
        }

        /// <summary>
        /// Convert a snapshot value (which may be a Newtonsoft.Json
        /// JValue, a boxed primitive, or a string from JSON) to the
        /// target type. Used by RestoreArgsFromSnapshot for the simple
        /// scalar fields.
        /// </summary>
        private static bool TryConvertRestoreValue(object value, Type targetType, out object converted)
        {
            converted = null;
            if (value == null) return !targetType.IsValueType;
            try
            {
                Type effective = Nullable.GetUnderlyingType(targetType) ?? targetType;
                if (effective.IsInstanceOfType(value))
                {
                    converted = value;
                    return true;
                }
                if (effective.IsEnum)
                {
                    if (value is string s)
                    {
                        converted = Enum.Parse(effective, s, true);
                        return true;
                    }
                    converted = Enum.ToObject(effective, value);
                    return true;
                }
                // Try Newtonsoft.Json JValue → use its Value via
                // reflection if available.
                var vt = value.GetType();
                if (vt.FullName != null && vt.FullName.StartsWith("Newtonsoft.Json.Linq.J"))
                {
                    var valProp = vt.GetProperty("Value");
                    if (valProp != null)
                    {
                        object inner = valProp.GetValue(value);
                        if (inner != null)
                        {
                            converted = Convert.ChangeType(inner, effective, System.Globalization.CultureInfo.InvariantCulture);
                            return true;
                        }
                    }
                }
                converted = Convert.ChangeType(value, effective, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Restore the previous argsX/argsY from the profile's
        /// PreviousArgsXBeforeLut / PreviousArgsYBeforeLut snapshots
        /// and apply them to RawAccel. CLAUDE DIRECTIVE #4 — this is
        /// the actual Reset path that restores the user's previous
        /// mode + parameters (including the cap Vec2 field and the
        /// data float[] field) so the LUT overwrite is fully
        /// reversible.
        ///
        /// On success, sets _profile.LutOverwriteActive = false so
        /// the next Train apply can take a fresh snapshot.
        /// </summary>
        private bool TryRestorePreviousArgs(out string statusMessage)
        {
            statusMessage = null;
            if (_profile == null)
            {
                statusMessage = "Reset: profile is null.";
                return false;
            }
            if (!_profile.LutOverwriteActive)
            {
                statusMessage = "Reset: no LUT overwrite is active — nothing to restore.";
                return false;
            }

            var settings = _accelGUI.MakeSettingsFromFields();
            string xError = null, yError = null;
            bool xRestored = false, yRestored = false;
            if (_profile.PreviousArgsXBeforeLut != null)
                xRestored = RestoreArgsFromSnapshot(settings, "argsX",
                    _profile.PreviousArgsXBeforeLut, out xError);
            if (_profile.PreviousArgsYBeforeLut != null)
                yRestored = RestoreArgsFromSnapshot(settings, "argsY",
                    _profile.PreviousArgsYBeforeLut, out yError);

            if (!xRestored && !yRestored)
            {
                statusMessage = "Reset: could not restore either axis. xError=" +
                    (xError ?? "(none)") + " yError=" + (yError ?? "(none)");
                return false;
            }

            if (!_accelGUI.Settings.TryActivate(settings, out string errors))
            {
                statusMessage = "Reset: RawAccel rejected the restored settings. " +
                    (errors ?? "(no native details)");
                return false;
            }

            _profile.LutOverwriteActive = false;
            // CLAUDE FIX (round 2, directive #4) — clear
            // LastAppliedMergedProfile after a successful Reset so the
            // next Train apply takes a fresh snapshot (and the
            // FinishTrial lookup returns 1.0 — no LUT is active).
            _profile.LastAppliedMergedProfile = null;
            try { SaveProfileToDisk(); }
            catch (Exception ex)
            {
                DesktopSensLog.Error("Reset: profile save failed after restore", ex);
            }
            _accelGUI.RefreshActive();
            _accelGUI.RefreshUser();

            statusMessage = "Reset: restored previous argsX=" +
                (xRestored ? "OK" : "(no snapshot)") +
                ", argsY=" + (yRestored ? "OK" : "(no snapshot)") +
                ". LutOverwriteActive cleared.";
            DesktopSensLog.Warn(statusMessage +
                " | xError=" + (xError ?? "(none)") +
                " | yError=" + (yError ?? "(none)"));
            return true;
        }

        /// <summary>
        /// After TryActivate succeeded, re-read the active profile from
        /// _accelGUI.Settings and verify that the argsX/argsY mode is
        /// "lut" (when we wrote a LUT) and the length matches the
        /// expected entry count.
        ///
        /// This is the honest post-apply verification the Claude
        /// directive requires: if TryActivate returns true but the
        /// active profile does not reflect what we wrote, we return
        /// false (the apply did not actually reach the driver).
        /// </summary>
        private bool VerifyLutApply(bool xLutApplied, bool yLutApplied,
            int xExpectedPoints, int yExpectedPoints)
        {
            try
            {
                // _accelGUI.Settings.ActiveProfile is the profile the
                // driver now sees. We compare its argsX/argsY mode and
                // length to what we wrote.
                var activeProfile = _accelGUI.Settings.ActiveProfile;
                if (activeProfile == null)
                {
                    DesktopSensLog.Warn("Post-apply verify: ActiveProfile is null.");
                    return false;
                }
                var type = activeProfile.GetType();
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;

                if (xLutApplied)
                {
                    var xField = type.GetField("argsX", flags);
                    if (xField == null) return false;
                    object xArgs = xField.GetValue(activeProfile);
                    if (xArgs == null) return false;
                    string xMode = ReadModeField(xArgs);
                    if (xMode != "lut")
                    {
                        DesktopSensLog.Warn(
                            "Post-apply verify: argsX mode is '" + xMode + "' but expected 'lut'.");
                        return false;
                    }
                    int xLen = ReadLengthField(xArgs);
                    // TryBuildLutValues writes each supplied step-hold
                    // point once, plus the two xMin/xMax bookends. The
                    // RawAccel length is a float count (x,y per point).
                    int xExpectedEntries = (xExpectedPoints + 2) * 2;
                    if (xLen != xExpectedEntries)
                    {
                        DesktopSensLog.Warn(
                            "Post-apply verify: argsX length is " + xLen +
                            " but expected " + xExpectedEntries + ".");
                        return false;
                    }
                }
                if (yLutApplied)
                {
                    var yField = type.GetField("argsY", flags);
                    if (yField == null) return false;
                    object yArgs = yField.GetValue(activeProfile);
                    if (yArgs == null) return false;
                    string yMode = ReadModeField(yArgs);
                    if (yMode != "lut")
                    {
                        DesktopSensLog.Warn(
                            "Post-apply verify: argsY mode is '" + yMode + "' but expected 'lut'.");
                        return false;
                    }
                    int yLen = ReadLengthField(yArgs);
                    int yExpectedEntries = (yExpectedPoints + 2) * 2;
                    if (yLen != yExpectedEntries)
                    {
                        DesktopSensLog.Warn(
                            "Post-apply verify: argsY length is " + yLen +
                            " but expected " + yExpectedEntries + ".");
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                DesktopSensLog.Error("Post-apply verify failed", ex);
                return false;
            }
        }

        private static string ReadModeField(object accelArgs)
        {
            try
            {
                if (accelArgs == null) return null;
                var modeField = accelArgs.GetType().GetField("mode",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                if (modeField == null) return null;
                object modeVal = modeField.GetValue(accelArgs);
                if (modeVal == null) return null;
                return modeVal.ToString();
            }
            catch { return null; }
        }

        private static int ReadLengthField(object accelArgs)
        {
            try
            {
                if (accelArgs == null) return -1;
                var lengthField = accelArgs.GetType().GetField("length",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                if (lengthField == null) return -1;
                object lengthVal = lengthField.GetValue(accelArgs);
                if (lengthVal is int) return (int)lengthVal;
                return -1;
            }
            catch { return -1; }
        }

        private ICardinalProfileApplier CreateCardinalProfileApplier()
        {
            return new SampleCardinalProfileApplier((merged, sourceProfile) =>
            {
                if (merged == null || sourceProfile == null || !merged.PassedSanityCheck)
                    return false;

                return TryApplyTrainDirectionalFactors(
                    merged,
                    merged.Right.BaseSensitivityFactor,
                    merged.Down.BaseSensitivityFactor,
                    merged.Left.BaseSensitivityFactor,
                    merged.Up.BaseSensitivityFactor);
            });
        }

        private int PickDominantCurveDirection(params int[] candidates)
        {
            int bestDir = -1;
            int bestSamples = 0;
            if (candidates == null) return bestDir;

            foreach (int direction in candidates)
            {
                if (direction < 0 || direction >= DesktopSensConstants.DirectionalCount ||
                    _profile.GetDirectionalCurveMode(direction) == AccelCurveMode.NoAccel)
                    continue;

                int samples = _profile.DirectionalCurveSampleCount != null &&
                              direction < _profile.DirectionalCurveSampleCount.Length
                    ? _profile.DirectionalCurveSampleCount[direction]
                    : 0;
                if (samples > bestSamples)
                {
                    bestSamples = samples;
                    bestDir = direction;
                }
            }
            return bestDir;
        }

        /// <summary>
        /// DIAGNOSTIC helper: safely read DirectionalCurveSampleCount for
        /// a direction without throwing on null or out-of-range indices.
        /// </summary>
        private int SafeGetCount(int direction)
        {
            if (_profile == null || _profile.DirectionalCurveSampleCount == null) return 0;
            if (direction < 0 || direction >= _profile.DirectionalCurveSampleCount.Length) return 0;
            return _profile.DirectionalCurveSampleCount[direction];
        }

        /// <summary>
        /// DIAGNOSTIC: pop up a MessageBox with the most recent entries
        /// from the DesktopSensLog ring buffer. After running the
        /// Sensitivity Finder (or the Cardinal Trainer) and clicking
        /// Apply, this log shows why each direction's acceleration curve
        /// was or wasn't applied — including the fitted mode, sample
        /// count, RMSE, and parameter vector.
        /// </summary>
        private void ShowDiagnosticLog()
        {
            var entries = DesktopSensLog.GetRecentEntries();
            if (entries == null || entries.Length == 0)
            {
                MessageBox.Show(this,
                    "No diagnostic log entries yet.\n\n" +
                    "Try the following workflow to populate it:\n" +
                    "  1. Run the Sensitivity Finder (collect ≥5 trials per direction).\n" +
                    "  2. Click 'Calibrate 4 Directions && Save Profile'.\n" +
                    "  3. Click 'Apply Saved 4-Way Profile to RawAccel'.\n" +
                    "  4. Click 'Show Diag Log' again.\n\n" +
                    "The log will then show, per direction, why the curve was or wasn't fitted.",
                    "Desktop Sens — Diagnostic Log",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Format the most recent entries (cap at ~30 to fit in a
            // MessageBox). Reverse-chronological order so the most
            // recent entries are at the top.
            var sb = new System.Text.StringBuilder();
            int start = Math.Max(0, entries.Length - 30);
            for (int i = entries.Length - 1; i >= start; i--)
            {
                sb.AppendLine(entries[i]);
                sb.AppendLine();
            }
            MessageBox.Show(this, sb.ToString(),
                "Desktop Sens — Diagnostic Log (last " +
                (entries.Length - start) + " entries)",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private bool TryApplyDirectionalCurves(object settings, int horizontalDirection, int verticalDirection)
        {
            if (settings == null) return false;

            var type = settings.GetType();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;
            var xField = type.GetField("argsX", flags);
            var yField = type.GetField("argsY", flags);
            if (xField == null || yField == null || xField.IsInitOnly || yField.IsInitOnly)
            {
                DesktopSensLog.Warn("RawAccel Profile does not expose writable argsX/argsY fields.",
                    new InvalidOperationException("Missing nested acceleration fields."));
                return false;
            }

            object oldX = xField.GetValue(settings);
            object oldY = yField.GetValue(settings);
            string error = null;
            bool xApplied = horizontalDirection < 0 ||
                (TryBuildCurveValues(horizontalDirection, out var xValues) &&
                 RawAccelSettingsBridge.TrySetNestedFields(settings, "argsX", xValues, out error));
            if (!xApplied)
            {
                DesktopSensLog.Warn("Could not apply RawAccel horizontal acceleration curve.",
                    new InvalidOperationException(error ?? "Horizontal curve parameters are invalid."));
                return false;
            }

            error = null;
            bool yApplied = verticalDirection < 0 ||
                (TryBuildCurveValues(verticalDirection, out var yValues) &&
                 RawAccelSettingsBridge.TrySetNestedFields(settings, "argsY", yValues, out error));
            if (!yApplied)
            {
                xField.SetValue(settings, oldX);
                yField.SetValue(settings, oldY);
                DesktopSensLog.Warn("Could not apply RawAccel vertical acceleration curve; both axes were rolled back.",
                    new InvalidOperationException(error ?? "Vertical curve parameters are invalid."));
                return false;
            }

            return true;
        }

        private bool TryBuildCurveValues(int direction, out Dictionary<string, object> values)
        {
            AccelCurveMode mode = _profile.GetDirectionalCurveMode(direction);
            double[] parameters = _profile.GetDirectionalCurveParameters(direction);
            string error;
            bool success = RawAccelSettingsBridge.TryBuildDirectionalCurveValues(
                mode, parameters, out values, out error);
            if (!success)
                DesktopSensLog.Warn("Directional acceleration curve was rejected before RawAccel activation.",
                    new InvalidOperationException(error ?? "Invalid fitted curve."));
            return success;
        }

        private static double PositiveOrOne(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0 ? 1.0 : value;
        }

        private bool TryReadSettings()
        {
            if (!double.TryParse(_gameSensBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double gameSens) ||
                double.IsNaN(gameSens) || double.IsInfinity(gameSens) || gameSens <= 0.0)
            {
                DesktopSensToast.Show(this, "CS2 sensitivity must be a positive number.");
                return false;
            }
            if (!int.TryParse(_dpiBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dpi) ||
                dpi < DesktopSensConstants.MinMouseDPI || dpi > DesktopSensConstants.MaxMouseDPI)
            {
                DesktopSensToast.Show(this, "DPI must be between " + DesktopSensConstants.MinMouseDPI + " and " +
                    DesktopSensConstants.MaxMouseDPI + ".");
                return false;
            }
            if (!double.TryParse(_pollingRateBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double pollingRate) ||
                double.IsNaN(pollingRate) || double.IsInfinity(pollingRate) ||
                pollingRate < DesktopSensConstants.MinPollingRateHz || pollingRate > 8000.0)
            {
                DesktopSensToast.Show(this, "Polling rate must be between 125 and 8000 Hz.");
                return false;
            }
            _profile.UserInputGameSensitivity = gameSens;
            _profile.BaseSensitivity = gameSens;
            if (_profile.MouseDPI != dpi)
                _profile.MeasuredCpi = 0.0; // A changed hardware DPI invalidates the prior physical measurement.
            _profile.MouseDPI = dpi;
            _profile.PollingRateHz = pollingRate;
            return true;
        }

        private DesktopSensProfile LoadProfile()
        {
            try
            {
                if (File.Exists(ProfilePath))
                {
                    var profile = JsonConvert.DeserializeObject<DesktopSensProfile>(File.ReadAllText(ProfilePath));
                    if (profile != null)
                    {
                        if (profile.Trials == null) profile.Trials = new System.Collections.Generic.List<DesktopSensTrial>();
                        if (profile.SensitivityFinderTrials == null)
                            profile.SensitivityFinderTrials = new System.Collections.Generic.List<DesktopSensTrial>();
                        if (profile.SchemaVersion < DesktopSensConstants.CurrentSchemaVersion ||
                            profile.ModelVersion < DesktopSensConstants.CurrentModelVersion)
                        {
                            int previousModelVersion = profile.ModelVersion;
                            // Model v11 introduces persisted sample-fit frozen
                            // nodes. Legacy green cells are frozen at their
                            // first sample-fit build from the historical accepted
                            // raw trials; the fit may make a one-time migration
                            // adjustment, after which those nodes remain exact.
                            if (previousModelVersion < 11)
                            {
                                if (profile.FrozenSampleLutNodeFactorsByAngleZone == null)
                                    profile.FrozenSampleLutNodeFactorsByAngleZone =
                                        new Dictionary<int, double[]>();
                                bool hasPersistedFrozenNodes = false;
                                foreach (var nodeArray in profile.FrozenSampleLutNodeFactorsByAngleZone.Values)
                                {
                                    if (nodeArray == null) continue;
                                    foreach (double factor in nodeArray)
                                    {
                                        if (!double.IsNaN(factor) && !double.IsInfinity(factor) && factor > 0.0)
                                        {
                                            hasPersistedFrozenNodes = true;
                                            break;
                                        }
                                    }
                                    if (hasPersistedFrozenNodes) break;
                                }
                                if (hasPersistedFrozenNodes)
                                    DesktopSensLog.Info(
                                        "DesktopSens model v11 migration: persisted frozen LUT nodes " +
                                        "were found and will be preserved.");
                                else
                                    DesktopSensLog.Warn(
                                        "DesktopSens model v11 migration: pre-existing green cells will " +
                                        "freeze from their historical trials on first sample-fit LUT build.");
                                CalibrationResidualCache.Clear(profile);
                            }
                            if (previousModelVersion < 12)
                            {
                                DesktopSensLog.Info(
                                    "DesktopSens model v12 migration: divergence guard fields initialized " +
                                    "to safe defaults; existing trials and frozen LUT nodes are preserved.");
                            }
                            // Model v8 stores the Base Sens factor alongside each
                            // sample-fit anchor. Earlier versions cannot reliably
                            // renormalize anchors after a Base Sens change, so discard
                            // them and rebuild from verified cells.
                            if (previousModelVersion < 8)
                            {
                                if (profile.LastBuiltLutPointsByAngleZone == null)
                                    profile.LastBuiltLutPointsByAngleZone = new Dictionary<int, List<LutPoint>>();
                                else
                                    profile.LastBuiltLutPointsByAngleZone.Clear();
                                if (profile.LastBuiltLutBaseFactorByAngleZone == null)
                                    profile.LastBuiltLutBaseFactorByAngleZone = new Dictionary<int, double>();
                                else
                                    profile.LastBuiltLutBaseFactorByAngleZone.Clear();
                            }

                            foreach (DesktopSensTrial trial in profile.Trials)
                            {
                                if (trial == null) continue;
                                trial.AngleZoneIndex = ZoneCalculations.AngleToZoneIndex(trial.TargetAngleDegrees);
                                double trialSensitivity = trial.GameSensitivity > 0.0 ? trial.GameSensitivity : profile.UserInputGameSensitivity;
                                double trialDpi = trial.EffectiveCpi > 0.0 ? trial.EffectiveCpi :
                                    (trial.MouseDPI > 0 ? trial.MouseDPI : profile.EffectiveCpi);
                                double maxTurnDistanceCm = ZoneCalculations.MaxDistanceCmForTurnDegrees(trialDpi, trialSensitivity);
                                trial.DistanceZoneIndex = ZoneCalculations.DistanceToZoneIndex(trial.TargetDistanceCm, maxTurnDistanceCm);
                            }
                            DesktopSensCalibrator.Calibrate(profile);
                            // Legacy profiles predate separate Finder/Train trial histories.
                            // Their mixed history cannot be safely reclassified, so preserve
                            // the saved directional Base Sens factors during migration.
                            profile.SchemaVersion = DesktopSensConstants.CurrentSchemaVersion;
                            profile.ModelVersion = DesktopSensConstants.CurrentModelVersion;
                            profile.ModifiedTimestamp = DateTime.UtcNow;
                        }
                        return profile;
                    }
                }
            }
            catch (Exception ex)
            {
                DesktopSensLog.Error("Could not load the Desktop Sens profile; using a new profile", ex);
            }
            return new DesktopSensProfile();
        }
    }
}
