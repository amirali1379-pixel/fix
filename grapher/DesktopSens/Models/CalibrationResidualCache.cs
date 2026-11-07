using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace grapher.DesktopSens.Models
{
    /// <summary>
    /// Per-profile, per-cell cache for the expensive ordered-chain residual
    /// reconstruction. Entries are reusable only when the caller supplies an
    /// exact dependency key; a changed upstream factor or session bias therefore
    /// invalidates the affected cell and its downstream suffix naturally.
    /// </summary>
    public static class CalibrationResidualCache
    {
        private sealed class Entry
        {
            public byte[] Key;
            public double[] Residuals;
        }

        private sealed class ProfileCache
        {
            public readonly object Sync = new object();
            public readonly Dictionary<int, Entry> Cells = new Dictionary<int, Entry>();
            public int Hits;
            public int Misses;
        }

        private static readonly ConditionalWeakTable<DesktopSensProfile, ProfileCache> Caches =
            new ConditionalWeakTable<DesktopSensProfile, ProfileCache>();

        internal static bool TryGet(
            DesktopSensProfile profile,
            int cellIndex,
            byte[] exactDependencyKey,
            out double[] residuals)
        {
            residuals = null;
            if (profile == null || exactDependencyKey == null) return false;

            ProfileCache cache = Caches.GetValue(profile, _ => new ProfileCache());
            lock (cache.Sync)
            {
                Entry entry;
                if (cache.Cells.TryGetValue(cellIndex, out entry) &&
                    KeysEqual(entry.Key, exactDependencyKey) &&
                    entry.Residuals != null)
                {
                    residuals = (double[])entry.Residuals.Clone();
                    cache.Hits++;
                    return true;
                }

                cache.Misses++;
                return false;
            }
        }

        internal static void Store(
            DesktopSensProfile profile,
            int cellIndex,
            byte[] exactDependencyKey,
            double[] residuals)
        {
            if (profile == null || exactDependencyKey == null || residuals == null) return;

            ProfileCache cache = Caches.GetValue(profile, _ => new ProfileCache());
            lock (cache.Sync)
            {
                cache.Cells[cellIndex] = new Entry
                {
                    Key = (byte[])exactDependencyKey.Clone(),
                    Residuals = (double[])residuals.Clone()
                };
            }
        }

        private static bool KeysEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i]) return false;
            return true;
        }

        public static void PruneToCellCount(DesktopSensProfile profile, int cellCount)
        {
            if (profile == null) return;
            int safeCount = Math.Max(0, cellCount);
            ProfileCache cache = Caches.GetValue(profile, _ => new ProfileCache());
            lock (cache.Sync)
            {
                var stale = new List<int>();
                foreach (int cellIndex in cache.Cells.Keys)
                    if (cellIndex < 0 || cellIndex >= safeCount)
                        stale.Add(cellIndex);
                foreach (int cellIndex in stale)
                    cache.Cells.Remove(cellIndex);
            }
        }

        public static void Clear(DesktopSensProfile profile)
        {
            if (profile == null) return;
            ProfileCache cache;
            if (!Caches.TryGetValue(profile, out cache)) return;
            lock (cache.Sync)
            {
                cache.Cells.Clear();
                cache.Hits = 0;
                cache.Misses = 0;
            }
        }

        public static void GetStatistics(
            DesktopSensProfile profile,
            out int hits,
            out int misses,
            out int cachedCells)
        {
            hits = misses = cachedCells = 0;
            if (profile == null) return;
            ProfileCache cache;
            if (!Caches.TryGetValue(profile, out cache)) return;
            lock (cache.Sync)
            {
                hits = cache.Hits;
                misses = cache.Misses;
                cachedCells = cache.Cells.Count;
            }
        }
    }

    /// <summary>
    /// Reuses the robust per-cell estimate after its exact residual and
    /// auxiliary measurement inputs are unchanged. Aggregate profile arrays
    /// are still rebuilt from these cell results on every calibration pass.
    /// </summary>
    internal struct CalibrationCellResult
    {
        public double SensitivityFactor;
        public double SensitivityConfidence;
        public double AccelerationFactor;
        public double AccelerationConfidence;
        public double RotationOffsetDegrees;
        public double RotationConfidence;
        public bool PrecisionStable;
        public bool IsBaseDimension;
        public bool IncludeDistanceAggregate;
        public int SampleWeight;
        public double OverallConfidence;
    }

    public static class CalibrationCellResultCache
    {
        private sealed class Entry
        {
            public byte[] Key;
            public CalibrationCellResult Result;
        }

        private sealed class ProfileCache
        {
            public readonly object Sync = new object();
            public readonly Dictionary<int, Entry> Cells = new Dictionary<int, Entry>();
            public int Hits;
            public int Misses;
        }

        private static readonly ConditionalWeakTable<DesktopSensProfile, ProfileCache> Caches =
            new ConditionalWeakTable<DesktopSensProfile, ProfileCache>();

        internal static bool TryGet(DesktopSensProfile profile, int cellIndex, byte[] key, out CalibrationCellResult result)
        {
            result = default(CalibrationCellResult);
            if (profile == null || key == null) return false;
            ProfileCache cache = Caches.GetValue(profile, _ => new ProfileCache());
            lock (cache.Sync)
            {
                if (cache.Cells.TryGetValue(cellIndex, out Entry entry) &&
                    KeysEqual(entry.Key, key))
                {
                    result = entry.Result;
                    cache.Hits++;
                    return true;
                }
                cache.Misses++;
                return false;
            }
        }

        internal static void Store(DesktopSensProfile profile, int cellIndex, byte[] key, CalibrationCellResult result)
        {
            if (profile == null || key == null) return;
            ProfileCache cache = Caches.GetValue(profile, _ => new ProfileCache());
            lock (cache.Sync)
            {
                cache.Cells[cellIndex] = new Entry
                {
                    Key = (byte[])key.Clone(),
                    Result = result
                };
            }
        }

        public static void PruneToCellCount(DesktopSensProfile profile, int cellCount)
        {
            if (profile == null) return;
            int safeCount = Math.Max(0, cellCount);
            ProfileCache cache = Caches.GetValue(profile, _ => new ProfileCache());
            lock (cache.Sync)
            {
                var stale = new List<int>();
                foreach (int cellIndex in cache.Cells.Keys)
                    if (cellIndex < 0 || cellIndex >= safeCount) stale.Add(cellIndex);
                foreach (int cellIndex in stale) cache.Cells.Remove(cellIndex);
            }
        }

        public static void Clear(DesktopSensProfile profile)
        {
            if (profile == null) return;
            if (!Caches.TryGetValue(profile, out ProfileCache cache)) return;
            lock (cache.Sync)
            {
                cache.Cells.Clear();
                cache.Hits = 0;
                cache.Misses = 0;
            }
        }

        public static void GetStatistics(DesktopSensProfile profile, out int hits, out int misses, out int cachedCells)
        {
            hits = misses = cachedCells = 0;
            if (profile == null || !Caches.TryGetValue(profile, out ProfileCache cache)) return;
            lock (cache.Sync)
            {
                hits = cache.Hits;
                misses = cache.Misses;
                cachedCells = cache.Cells.Count;
            }
        }

        private static bool KeysEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i]) return false;
            return true;
        }
    }
}
