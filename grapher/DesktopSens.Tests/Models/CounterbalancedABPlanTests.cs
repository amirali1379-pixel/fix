using System;
using grapher.DesktopSens.Models;
using Xunit;

namespace DesktopSens.Tests.Models
{
    public class CounterbalancedABPlanTests
    {
        [Fact]
        public void Plan_UsesABBAOrBAABBlocksAndOneAOneBPerPair()
        {
            var plan = new CounterbalancedABPlan(12, new Random(123));
            var observedOrder = new bool[24];
            for (int trial = 0; trial < observedOrder.Length; trial++)
            {
                observedOrder[trial] = plan.CurrentConditionIsB;
                Assert.True(plan.RecordCurrent(0.1, 0.1));
            }

            for (int block = 0; block < 6; block++)
            {
                int offset = block * 4;
                bool abba = !observedOrder[offset] && observedOrder[offset + 1] &&
                    observedOrder[offset + 2] && !observedOrder[offset + 3];
                bool baab = observedOrder[offset] && !observedOrder[offset + 1] &&
                    !observedOrder[offset + 2] && observedOrder[offset + 3];
                Assert.True(abba || baab);
                Assert.NotEqual(observedOrder[offset], observedOrder[offset + 1]);
                Assert.NotEqual(observedOrder[offset + 2], observedOrder[offset + 3]);
            }
            Assert.True(plan.IsComplete);
            Assert.Equal(12, plan.CompletedPairs);
        }

        [Fact]
        public void BuildResult_UsesPairedBootstrapAndRecognizesConsistentImprovement()
        {
            var plan = new CounterbalancedABPlan(12, new Random(321));
            while (!plan.IsComplete)
            {
                bool isB = plan.CurrentConditionIsB;
                double error = isB ? 0.01 : 0.08;
                Assert.True(plan.RecordCurrent(Math.Abs(Math.Log(1.0 + error)), error));
            }

            ABTestResult result = plan.BuildResult(iterations: 2000, seed: 99);
            Assert.NotNull(result);
            Assert.True(result.HasBaselineComparison);
            Assert.True(result.ImprovementStatisticallySignificant);
            Assert.True(result.PairedImprovementCiLower > 0.0);
        }
    }
}
