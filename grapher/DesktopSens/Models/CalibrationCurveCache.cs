using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace grapher.DesktopSens.Models
{
    /// <summary>
    /// Per-profile, per-cell cache for expensive acceleration-curve fitting.
    /// A cache entry is reused only when the complete ordered fitting input
    /// (speed, ratio, and weight for every sample) is bit-for-bit unchanged.
    /// The profile key is weak so discarded profiles do not remain rooted.
    ///
    /// Curve fitting is cached separately from ordered-chain residuals and
    /// robust per-cell estimates. Session biases and ordered residual inputs are
    /// still validated/reconstructed each pass; final angle/distance aggregates
    /// are always rebuilt from the current per-cell results.
    /// </summary>
    public static class CalibrationCurveCache
    {
        private sealed class Entry
        {
            public ulong Fingerprint;
            public int SampleCount;
            public SensitivitySample[] Snapshot;
            public CurveFitResult Result;
        }

        private sealed class ProfileCache
        {
            public readonly object Gate = new object();
            public readonly Dictionary<int, Entry> Cells = new Dictionary<int, Entry>();
            public int Hits;
            public int Misses;
        }

        private static readonly ConditionalWeakTable<DesktopSensProfile, ProfileCache> Profiles =
            new ConditionalWeakTable<DesktopSensProfile, ProfileCache>();

        public static CurveFitResult GetOrFit(
            DesktopSensProfile profile,
            int cellIndex,
            IList<SensitivitySample> samples)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (cellIndex < 0) throw new ArgumentOutOfRangeException(nameof(cellIndex));
            if (samples == null) throw new ArgumentNullException(nameof(samples));

            ulong fingerprint = Fingerprint(samples);
            ProfileCache cache = Profiles.GetValue(profile, _ => new ProfileCache());
            lock (cache.Gate)
            {
                if (cache.Cells.TryGetValue(cellIndex, out Entry existing) &&
                    existing.SampleCount == samples.Count &&
                    existing.Fingerprint == fingerprint &&
                    Matches(existing.Snapshot, samples))
                {
                    cache.Hits++;
                    return Clone(existing.Result);
                }

                CurveFitResult fitted = AccelCurveFitter.FitBest(samples);
                cache.Cells[cellIndex] = new Entry
                {
                    Fingerprint = fingerprint,
                    SampleCount = samples.Count,
                    Snapshot = CopySamples(samples),
                    Result = Clone(fitted)
                };
                cache.Misses++;
                return Clone(fitted);
            }
        }

        public static void GetStatistics(
            DesktopSensProfile profile,
            out int hits,
            out int misses,
            out int cachedCells)
        {
            hits = 0;
            misses = 0;
            cachedCells = 0;
            if (profile == null || !Profiles.TryGetValue(profile, out ProfileCache cache))
                return;

            lock (cache.Gate)
            {
                hits = cache.Hits;
                misses = cache.Misses;
                cachedCells = cache.Cells.Count;
            }
        }

        public static void Invalidate(DesktopSensProfile profile)
        {
            if (profile != null)
                Profiles.Remove(profile);
        }

        private static SensitivitySample[] CopySamples(IList<SensitivitySample> samples)
        {
            var copy = new SensitivitySample[samples.Count];
            for (int i = 0; i < samples.Count; i++) copy[i] = samples[i];
            return copy;
        }

        private static bool Matches(SensitivitySample[] snapshot, IList<SensitivitySample> samples)
        {
            if (snapshot == null || snapshot.Length != samples.Count) return false;
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (BitConverter.DoubleToInt64Bits(snapshot[i].InputSpeedCmPerSec) !=
                    BitConverter.DoubleToInt64Bits(samples[i].InputSpeedCmPerSec) ||
                    BitConverter.DoubleToInt64Bits(snapshot[i].ObservedRatio) !=
                    BitConverter.DoubleToInt64Bits(samples[i].ObservedRatio) ||
                    BitConverter.DoubleToInt64Bits(snapshot[i].Weight) !=
                    BitConverter.DoubleToInt64Bits(samples[i].Weight))
                    return false;
            }
            return true;
        }

        private static CurveFitResult Clone(CurveFitResult source)
        {
            CurveFitResult clone = source;
            clone.Parameters = source.Parameters == null ? null : (double[])source.Parameters.Clone();
            return clone;
        }

        private static ulong Fingerprint(IList<SensitivitySample> samples)
        {
            unchecked
            {
                const ulong offset = 14695981039346656037UL;
                const ulong prime = 1099511628211UL;
                ulong hash = offset;
                hash = (hash ^ (uint)samples.Count) * prime;
                for (int i = 0; i < samples.Count; i++)
                {
                    hash = (hash ^ (ulong)BitConverter.DoubleToInt64Bits(samples[i].InputSpeedCmPerSec)) * prime;
                    hash = (hash ^ (ulong)BitConverter.DoubleToInt64Bits(samples[i].ObservedRatio)) * prime;
                    hash = (hash ^ (ulong)BitConverter.DoubleToInt64Bits(samples[i].Weight)) * prime;
                }
                return hash;
            }
        }
    }
}
