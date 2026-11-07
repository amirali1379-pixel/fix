using System;
using System.Diagnostics;
using grapher.Models.Mouse;

namespace grapher.DesktopSens.Models
{
    /// <summary>
    /// One source of truth for RawAccel's instantaneous speed metric used by
    /// sample-fit, frozen-node ownership, and capture-time LUT snapshots.
    /// Speed is the 2-D mouse-vector magnitude; displacement remains projected
    /// onto the calibration target axis.
    /// </summary>
    public static class SampleLutSampleProcessor
    {
        /// <summary>
        /// Computes one cleaned sample. Short timestamp batches are ignored,
        /// and extreme velocity is winsorized to the named physical ceiling.
        /// </summary>
        public static bool TryProcess(
            double dxCounts,
            double dyCounts,
            long deltaTicks,
            double effectiveCpi,
            double targetAngleDegrees,
            out double speedCmPerSec,
            out double projectedDisplacementCm)
        {
            speedCmPerSec = 0.0;
            projectedDisplacementCm = 0.0;
            if (!IsFinite(dxCounts) || !IsFinite(dyCounts) ||
                !IsFinite(effectiveCpi) || effectiveCpi <= 0.0)
                return false;

            long minimumTicks = (long)(Stopwatch.Frequency *
                DesktopSensConstants.SampleLutMinimumSampleIntervalMs / 1000.0);
            if (deltaTicks < minimumTicks || deltaTicks <= 0)
                return false;

            double dtSec = deltaTicks / (double)Stopwatch.Frequency;
            if (!IsFinite(dtSec) || dtSec <= 0.0)
                return false;

            double cmPerCount = DesktopSensConstants.InchesToCm / effectiveCpi;
            double vectorCounts = Math.Sqrt(dxCounts * dxCounts + dyCounts * dyCounts);
            double speed = vectorCounts * cmPerCount / dtSec;
            if (!IsFinite(speed) || speed <= 0.0)
                return false;

            // Winsorize rather than dropping the interval, preserving its
            // displacement while preventing timestamp jitter from pinning LUT
            // nodes to the top of the fixed speed grid.
            speedCmPerSec = Math.Min(speed,
                DesktopSensConstants.SampleLutMaximumPhysicalSpeedCmPerSec);

            double angleRad = targetAngleDegrees * Math.PI / 180.0;
            double projectedCounts = dxCounts * Math.Cos(angleRad) +
                                     dyCounts * Math.Sin(angleRad);
            projectedDisplacementCm = projectedCounts * cmPerCount;
            return IsFinite(projectedDisplacementCm);
        }

        /// <summary>Raw, unfiltered 2-D sample speed for diagnostics and gate checks.</summary>
        public static double RawSpeedCmPerSec(
            double dxCounts, double dyCounts, long deltaTicks, double effectiveCpi)
        {
            if (!IsFinite(dxCounts) || !IsFinite(dyCounts) ||
                !IsFinite(effectiveCpi) || effectiveCpi <= 0.0 || deltaTicks <= 0)
                return double.NaN;
            double dtSec = deltaTicks / (double)Stopwatch.Frequency;
            if (!IsFinite(dtSec) || dtSec <= 0.0) return double.NaN;
            double speed = Math.Sqrt(dxCounts * dxCounts + dyCounts * dyCounts) *
                DesktopSensConstants.InchesToCm / effectiveCpi / dtSec;
            return IsFinite(speed) ? speed : double.NaN;
        }

        private static bool IsFinite(double value)
            => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
