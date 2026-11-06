using System;
using System.Collections.Generic;

namespace grapher.DesktopSens.Models
{
    /// <summary>Counterbalanced paired A/B schedule; positive paired improvement favours B.</summary>
    public sealed class CounterbalancedABPlan
    {
        private readonly List<bool> _conditions = new List<bool>();
        private readonly List<double> _aLog = new List<double>();
        private readonly List<double> _bLog = new List<double>();
        private readonly List<double> _aCm = new List<double>();
        private readonly List<double> _bCm = new List<double>();
        private readonly List<double> _differences = new List<double>();
        private double _pendingA = double.NaN, _pendingB = double.NaN;
        private int _cursor;

        public CounterbalancedABPlan(int pairCount, Random random)
        {
            if (pairCount < 2 || pairCount % 2 != 0) throw new ArgumentOutOfRangeException(nameof(pairCount));
            if (random == null) throw new ArgumentNullException(nameof(random));
            PairCount = pairCount;
            while (_conditions.Count < pairCount * 2)
                _conditions.AddRange(random.Next(2) == 0
                    ? new[] { false, true, true, false }
                    : new[] { true, false, false, true });
        }
        public int PairCount { get; private set; }
        public int CompletedPairs { get { return _differences.Count; } }
        public int CurrentPairIndex { get { return _cursor / 2; } }
        public bool IsComplete { get { return _cursor >= _conditions.Count; } }
        public bool CurrentConditionIsB
        {
            get { if (IsComplete) throw new InvalidOperationException("Plan complete."); return _conditions[_cursor]; }
        }

        public bool RecordCurrent(double absoluteLogError, double absoluteErrorCm)
        {
            if (IsComplete || !Finite(absoluteLogError) || absoluteLogError < 0.0 ||
                !Finite(absoluteErrorCm) || absoluteErrorCm < 0.0) return false;
            if (CurrentConditionIsB) { _pendingB = absoluteLogError; _bLog.Add(absoluteLogError); _bCm.Add(absoluteErrorCm); }
            else { _pendingA = absoluteLogError; _aLog.Add(absoluteLogError); _aCm.Add(absoluteErrorCm); }
            _cursor++;
            if ((_cursor % 2) == 0)
            {
                if (!Finite(_pendingA) || !Finite(_pendingB)) return false;
                _differences.Add(_pendingA - _pendingB);
                _pendingA = double.NaN; _pendingB = double.NaN;
            }
            return true;
        }

        public ABTestResult BuildResult(int iterations = 4000, int seed = 12345)
        {
            if (!IsComplete || CompletedPairs != PairCount || CompletedPairs < 8) return null;
            double lo, hi;
            PairedBootstrapCI(_differences, iterations, seed, out lo, out hi);
            double meanD = Mean(_differences);
            return new ABTestResult
            {
                TrialsRun = CompletedPairs,
                MeanAbsoluteErrorBeforeCm = Mean(_aCm),
                MeanAbsoluteErrorAfterCm = Mean(_bCm),
                MedianAbsoluteErrorBeforeCm = RobustStatistics.Median(_aCm),
                MedianAbsoluteErrorAfterCm = RobustStatistics.Median(_bCm),
                ImprovementPercent = (Math.Exp(meanD) - 1.0) * 100.0,
                ImprovementStatisticallySignificant = lo > 0.0,
                HasBaselineComparison = true,
                MeanAbsoluteLogErrorBefore = Mean(_aLog),
                MeanAbsoluteLogErrorAfter = Mean(_bLog),
                MedianAbsoluteLogErrorBefore = RobustStatistics.Median(_aLog),
                MedianAbsoluteLogErrorAfter = RobustStatistics.Median(_bLog),
                MeanPairedLogImprovement = meanD,
                MedianPairedLogImprovement = RobustStatistics.Median(_differences),
                PairedImprovementCiLower = lo,
                PairedImprovementCiUpper = hi,
                Notes = "Randomized ABBA/BAAB blocks; each A/B pair uses the same target. Paired bootstrap resamples complete pair differences. Statistical pass requires CI lower > 0; absolute accuracy additionally requires median B |log error| <= 0.015."
            };
        }

        public static void PairedBootstrapCI(IList<double> values, int iterations, int seed, out double lower, out double upper)
        {
            lower = double.NaN; upper = double.NaN;
            if (values == null || values.Count < 2 || iterations < 200) return;
            var rng = new Random(seed);
            var means = new double[iterations];
            for (int b = 0; b < iterations; b++)
            {
                double sum = 0.0;
                for (int i = 0; i < values.Count; i++)
                {
                    double v = values[rng.Next(values.Count)];
                    if (!Finite(v)) return;
                    sum += v;
                }
                means[b] = sum / values.Count;
            }
            Array.Sort(means);
            lower = means[(int)Math.Floor(0.025 * (iterations - 1))];
            upper = means[(int)Math.Ceiling(0.975 * (iterations - 1))];
        }
        private static double Mean(IList<double> values)
        {
            if (values == null || values.Count == 0) return 0.0;
            double sum = 0.0; for (int i = 0; i < values.Count; i++) sum += values[i];
            return sum / values.Count;
        }
        private static bool Finite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }
    }
}