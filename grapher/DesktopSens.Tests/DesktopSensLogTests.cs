using System.Linq;
using grapher.DesktopSens.Models;
using Xunit;

namespace grapher.DesktopSens.Tests
{
    public class DesktopSensLogTests
    {
        [Fact]
        public void Info_WritesInfoLevelEntryToRecentEntries()
        {
            const string marker = "DesktopSensLogTests.Info marker";
            DesktopSensLog.Info(marker);

            Assert.Contains(
                DesktopSensLog.GetRecentEntries(),
                entry => entry.Contains("[INFO]") && entry.Contains(marker));
        }
    }
}
