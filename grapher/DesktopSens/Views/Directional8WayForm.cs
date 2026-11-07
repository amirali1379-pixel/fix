using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using grapher;

namespace grapher.DesktopSens.Views
{
    /// <summary>
    /// Four cardinal-direction sensitivity controls backed by RawAccel's existing
    /// profile fields. No separate mouse-input path is introduced.
    /// </summary>
    public sealed class Directional8WayForm : Form
    {
        private readonly AccelGUI _accelGUI;
        private readonly double _baseOutputDPI;
        private readonly TextBox _rightBox;
        private readonly TextBox _leftBox;
        private readonly TextBox _upBox;
        private readonly TextBox _downBox;
        private readonly Label _status;

        public Directional8WayForm(AccelGUI accelGUI)
        {
            _accelGUI = accelGUI ?? throw new ArgumentNullException(nameof(accelGUI));
            _baseOutputDPI = _accelGUI.MakeSettingsFromFields().outputDPI;

            Text = "Directional Sensitivity — 4 Way";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(520, 280);

            var active = _accelGUI.Settings.ActiveProfile;
            double down = PositiveOrOne(active.yxOutputDPIRatio);
            double up = PositiveOrOne(active.yxOutputDPIRatio * active.udOutputDPIRatio);
            double left = PositiveOrOne(active.lrOutputDPIRatio);

            Controls.Add(new Label {
                Text = "Set sensitivity multipliers for the four cardinal directions.",
                Left = 18, Top = 15, Width = 480, Height = 24,
                Font = new Font(Font, FontStyle.Bold)
            });
            Controls.Add(new Label {
                Text = "Values are relative to the current base sensitivity.",
                Left = 18, Top = 42, Width = 480, Height = 22
            });

            _rightBox = AddMultiplier("Right / East", 20, 82, 1.0);
            _leftBox = AddMultiplier("Left / West", 270, 82, left);
            _upBox = AddMultiplier("Up / North", 20, 125, up);
            _downBox = AddMultiplier("Down / South", 270, 125, down);

            _status = new Label {
                Left = 20, Top = 174, Width = 480, Height = 38,
                Text = "Changes use RawAccel's existing profile and IOCTL path."
            };
            Controls.Add(_status);

            var applyButton = new Button {
                Text = "Apply", Left = 285, Top = 226, Width = 95, Height = 32
            };
            applyButton.Click += ApplyClicked;
            Controls.Add(applyButton);

            var closeButton = new Button {
                Text = "Close", Left = 390, Top = 226, Width = 95, Height = 32,
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(closeButton);
            CancelButton = closeButton;
        }

        private TextBox AddMultiplier(string label, int left, int top, double value)
        {
            Controls.Add(new Label {
                Text = label, Left = left, Top = top + 5, Width = 115, Height = 22
            });
            var box = new TextBox {
                Left = left + 120, Top = top, Width = 105, Height = 24,
                Text = value.ToString("0.###", CultureInfo.InvariantCulture)
            };
            Controls.Add(box);
            return box;
        }

        private bool TryReadMultipliers(out double right, out double left, out double up, out double down)
        {
            // Initialize every out parameter before short-circuit evaluation.
            // If an earlier value is invalid, later TryReadPositive calls won't run.
            right = left = up = down = 0.0;

            return TryReadPositive(_rightBox.Text, out right) &&
                   TryReadPositive(_leftBox.Text, out left) &&
                   TryReadPositive(_upBox.Text, out up) &&
                   TryReadPositive(_downBox.Text, out down);
        }

        private static bool TryReadPositive(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                   !double.IsNaN(value) && !double.IsInfinity(value) && value > 0.0;
        }

        private static double PositiveOrOne(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0 ? 1.0 : value;
        }

        private void ApplyClicked(object sender, EventArgs e)
        {
            if (!TryReadMultipliers(out double right, out double left, out double up, out double down))
            {
                DesktopSensToast.Show(this, "All four multipliers must be finite numbers greater than zero.");
                return;
            }

            try
            {
                // Preserve the current acceleration curves and device configuration.
                // Map four cardinal gains onto fields supported by RawAccel already:
                // base DPI = right, LR ratio = left/right, Y/X ratio = down/right,
                // and up/down ratio = up/down.
                var settings = _accelGUI.MakeSettingsFromFields();
                if (settings.outputDPI <= 0.0 || double.IsNaN(settings.outputDPI) ||
                    double.IsInfinity(settings.outputDPI))
                {
                    DesktopSensToast.Show(this, "The current base sensitivity is invalid.");
                    return;
                }

                settings.outputDPI = _baseOutputDPI * right;
                settings.lrOutputDPIRatio = left / right;
                settings.yxOutputDPIRatio = down / right;
                settings.udOutputDPIRatio = up / down;

                if (!_accelGUI.Settings.TryActivate(settings, out string errors))
                {
                    DesktopSensToast.Show(this, errors ?? "RawAccel rejected the settings.");
                    return;
                }

                _accelGUI.RefreshActive();
                _accelGUI.RefreshUser();
                _status.Text = "Applied through RawAccel. Right, left, up and down are configured.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not apply directional sensitivity:\n" + ex.Message,
                    "Directional Sensitivity", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
