using System;
using System.Windows.Forms;
using grapher.DesktopSens.Models;

namespace grapher.DesktopSens.Views
{
    /// <summary>
    /// Non-modal feedback for recoverable validation and informational messages.
    /// Destructive confirmations and operation failures remain modal.
    /// </summary>
    internal static class DesktopSensToast
    {
        public static void Show(Control owner, string message, int durationMilliseconds = 3500)
        {
            if (owner == null || owner.IsDisposed || string.IsNullOrWhiteSpace(message))
                return;

            if (owner.InvokeRequired)
            {
                try
                {
                    owner.BeginInvoke((MethodInvoker)(() => Show(owner, message, durationMilliseconds)));
                }
                catch (InvalidOperationException ex)
                {
                    DesktopSensLog.Warn("Could not dispatch a Desktop Sens toast to the UI thread", ex);
                }
                return;
            }

            ToolTip tip = new ToolTip
            {
                IsBalloon = true,
                ToolTipIcon = ToolTipIcon.Info,
                ToolTipTitle = "Desktop Sens"
            };
            int x = Math.Max(0, owner.ClientSize.Width / 2);
            int y = Math.Max(0, owner.ClientSize.Height / 2);
            int duration = Math.Max(1000, durationMilliseconds);
            tip.Show(message, owner, x, y, duration);

            var timer = new Timer { Interval = duration + 250 };
            EventHandler cleanup = null;
            cleanup = (sender, args) =>
            {
                timer.Stop();
                timer.Tick -= cleanup;
                owner.Disposed -= cleanup;
                try
                {
                    tip.Hide(owner);
                }
                catch (Exception ex)
                {
                    DesktopSensLog.Warn("Could not hide a Desktop Sens toast cleanly", ex);
                }
                tip.Dispose();
                timer.Dispose();
            };
            timer.Tick += cleanup;
            owner.Disposed += cleanup;
            timer.Start();
        }
    }
}
