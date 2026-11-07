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
    public struct SampleLutCachedSample
    {
        public double RawSpeedCmPerSec;
        public double SpeedCmPerSec;
        public double ProjectedDisplacementCm;
        public double AccumulatedDxCounts;
        public double AccumulatedDyCounts;
        public long AccumulatedDeltaTicks;
        public int RawSampleCount;
        public bool IsUsable;
    }

    public static class SampleLutSampleProcessor
    {
        private sealed class CacheEntry
        {
            public ulong Key;
            public System.Collections.Generic.List<SampleLutCachedSample> Samples;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
            DesktopSensTrial, CacheEntry> SampleCache =
                new System.Runtime.CompilerServices.ConditionalWeakTable<DesktopSensTrial, CacheEntry>();
        private static readonly object SampleCacheLock = new object();

        /// <summary>
        /// Cached per-trial speed/displacement extraction. The key includes
        /// schema, immutable raw samples, CPI/target axis, and the captured
        /// per-sample LUT-factor snapshot so stale trial data cannot be reused.
        /// </summary>
        public static System.Collections.Generic.IList<SampleLutCachedSample> GetCachedSamples(
            DesktopSensTrial trial)
        {
            if (trial == null || trial.RawSamples == null || trial.RawSamples.Count < 2)
                return new System.Collections.Generic.List<SampleLutCachedSample>();

            ulong key = ComputeCacheKey(trial);
            lock (SampleCacheLock)
            {
                if (SampleCache.TryGetValue(trial, out CacheEntry cached) &&
                    cached != null && cached.Key == key && cached.Samples != null)
                    return cached.Samples;

                var samples = new System.Collections.Generic.List<SampleLutCachedSample>(
                    trial.RawSamples.Count - 1);
                long previousTimestamp = trial.RawSamples[0].Timestamp;
                long minimumTicks = (long)(Stopwatch.Frequency *
                    DesktopSensConstants.SampleLutMinimumSampleIntervalMs / 1000.0);
                double pendingDx = 0.0, pendingDy = 0.0;
                long pendingTicks = 0;
                int pendingSampleCount = 0;
                for (int i = 1; i < trial.RawSamples.Count; i++)
                {
                    MouseSample sample = trial.RawSamples[i];
                    long deltaTicks = sample.Timestamp - previousTimestamp;
                    previousTimestamp = sample.Timestamp;
                    if (deltaTicks <= 0) continue;

                    // Coalesce adjacent timestamp-batched samples instead of
                    // discarding their displacement when dt is below threshold.
                    pendingDx += sample.DX;
                    pendingDy += sample.DY;
                    pendingTicks += deltaTicks;
                    pendingSampleCount++;
                    if (pendingTicks < minimumTicks) continue;

                    AddProcessedSample(samples, pendingDx, pendingDy, pendingTicks,
                        trial.EffectiveCpi, trial.TargetAngleDegrees, pendingSampleCount);
                    pendingDx = pendingDy = 0.0;
                    pendingTicks = 0;
                    pendingSampleCount = 0;
                }
                if (pendingTicks > 0)
                {
                    if (samples.Count > 0)
                    {
                        SampleLutCachedSample last = samples[samples.Count - 1];
                        AddProcessedSample(samples, last.AccumulatedDxCounts + pendingDx,
                            last.AccumulatedDyCounts + pendingDy,
                            last.AccumulatedDeltaTicks + pendingTicks,
                            trial.EffectiveCpi, trial.TargetAngleDegrees,
                            last.RawSampleCount + pendingSampleCount);
                        samples.RemoveAt(samples.Count - 2);
                    }
                    else
                    {
                        AddProcessedSample(samples, pendingDx, pendingDy, pendingTicks,
                            trial.EffectiveCpi, trial.TargetAngleDegrees,
                            pendingSampleCount, allowShortInterval: true);
                    }
                }

                SampleCache.Remove(trial);
                SampleCache.Add(trial, new CacheEntry { Key = key, Samples = samples });
                return samples;
            }
        }

        private static void AddProcessedSample(
            System.Collections.Generic.List<SampleLutCachedSample> samples,
            double dx, double dy, long ticks, double cpi, double targetAngle,
            int rawSampleCount, bool allowShortInterval = false)
        {
            double rawSpeed = RawSpeedCmPerSec(dx, dy, ticks, cpi);
            double speed, displacement;
            bool usable = allowShortInterval
                ? TryProcessWithoutMinimumInterval(dx, dy, ticks, cpi, targetAngle, out speed, out displacement)
                : TryProcess(dx, dy, ticks, cpi, targetAngle, out speed, out displacement);
            samples.Add(new SampleLutCachedSample
            {
                RawSpeedCmPerSec = rawSpeed,
                SpeedCmPerSec = speed,
                ProjectedDisplacementCm = displacement,
                AccumulatedDxCounts = dx,
                AccumulatedDyCounts = dy,
                AccumulatedDeltaTicks = ticks,
                RawSampleCount = rawSampleCount,
                IsUsable = usable && Math.Abs(displacement) > 1e-12
            });
        }

        private static bool TryProcessWithoutMinimumInterval(
            double dx, double dy, long ticks, double cpi, double angle,
            out double speed, out double displacement)
        {
            speed = displacement = 0.0;
            if (ticks <= 0 || !IsFinite(cpi) || cpi <= 0.0 ||
                !IsFinite(dx) || !IsFinite(dy)) return false;
            double dt = ticks / (double)Stopwatch.Frequency;
            double cmPerCount = DesktopSensConstants.InchesToCm / cpi;
            speed = Math.Min(Math.Sqrt(dx * dx + dy * dy) * cmPerCount / dt,
                DesktopSensConstants.SampleLutMaximumPhysicalSpeedCmPerSec);
            displacement = ProjectedDisplacementCm(dx, dy, cpi, angle);
            return IsFinite(speed) && speed > 0.0 && IsFinite(displacement);
        }

        private static double ProjectedDisplacementCm(double dx, double dy, double cpi, double angle)
        {
            double rad = angle * Math.PI / 180.0;
            return (dx * Math.Cos(rad) + dy * Math.Sin(rad)) *
                DesktopSensConstants.InchesToCm / cpi;
        }

        private static ulong ComputeCacheKey(DesktopSensTrial trial)
        {
            ulong hash = 14695981039346656037UL;
            AddHash(ref hash, DesktopSensConstants.SampleLutSampleCacheSchemaVersion);
            AddHash(ref hash, trial.RawSamples.Count);
            AddHash(ref hash, BitConverter.DoubleToInt64Bits(trial.EffectiveCpi));
            AddHash(ref hash, BitConverter.DoubleToInt64Bits(trial.TargetAngleDegrees));
            foreach (MouseSample sample in trial.RawSamples)
            {
                AddHash(ref hash, sample.Timestamp);
                AddHash(ref hash, sample.DX);
                AddHash(ref hash, sample.DY);
            }
            if (trial.AppliedLutFactorsByRawSample != null)
            {
                AddHash(ref hash, trial.AppliedLutFactorsByRawSample.Count);
                foreach (double factor in trial.AppliedLutFactorsByRawSample)
                    AddHash(ref hash, BitConverter.DoubleToInt64Bits(factor));
            }
            else
            {
                AddHash(ref hash, 0);
            }
            return hash;
        }

        private static void AddHash(ref ulong hash, long value)
        {
            unchecked
            {
                hash ^= (ulong)value;
                hash *= 1099511628211UL;
            }
        }

        private static void AddHash(ref ulong hash, int value)
            => AddHash(ref hash, (long)value);

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
