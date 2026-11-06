using System;
using System.Reflection;
using grapher.DesktopSens.Models;
using Xunit;

namespace DesktopSens.Tests.Models
{
    public class DesktopSensProfileCopyTests
    {
        [Fact]
        public void CopyPublicMembers_CopiesEveryWritablePublicFieldAndProperty()
        {
            var source = new DesktopSensProfile();
            var target = new DesktopSensProfile();

            // Make identity and several representative persisted values differ.
            source.ProfileId = "source-profile";
            source.MouseDPI = 1600;
            source.MeasuredCpi = 1575.5;
            source.BaseSensitivity = 1.125;
            source.LastBackupPath = "source-backup.json";
            source.CellCurveParameters = new double[] { 1.0, 2.0, 3.0 };

            DesktopSensProfileCopy.CopyPublicMembers(source, target);

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            Type type = typeof(DesktopSensProfile);

            foreach (FieldInfo field in type.GetFields(flags))
            {
                if (field.IsInitOnly || field.IsLiteral)
                    continue;

                Assert.Equal(field.GetValue(source), field.GetValue(target));
            }

            foreach (PropertyInfo property in type.GetProperties(flags))
            {
                if (property.GetIndexParameters().Length != 0 ||
                    property.GetGetMethod(false) == null ||
                    property.GetSetMethod(false) == null)
                    continue;

                Assert.Equal(property.GetValue(source, null), property.GetValue(target, null));
            }
        }
    }
}
