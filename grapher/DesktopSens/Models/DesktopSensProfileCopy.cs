using System;
using System.Reflection;

namespace grapher.DesktopSens.Models
{
    /// <summary>
    /// Copies the public persisted members of a profile-like object while
    /// preserving the target instance identity used by live UI components.
    /// </summary>
    public static class DesktopSensProfileCopy
    {
        public static void CopyPublicMembers<T>(T source, T target) where T : class
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) throw new ArgumentNullException(nameof(target));

            Type type = typeof(T);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;

            foreach (FieldInfo field in type.GetFields(flags))
            {
                if (field.IsInitOnly || field.IsLiteral) continue;
                field.SetValue(target, field.GetValue(source));
            }

            foreach (PropertyInfo property in type.GetProperties(flags))
            {
                if (property.GetIndexParameters().Length != 0) continue;
                if (property.GetGetMethod(false) == null ||
                    property.GetSetMethod(false) == null)
                    continue;
                property.SetValue(target, property.GetValue(source, null), null);
            }

            // Frozen node arrays are mutable calibration state. Do not let
            // profile clones share the same array references.
            if (source is DesktopSensProfile sourceProfile &&
                target is DesktopSensProfile targetProfile &&
                sourceProfile.FrozenSampleLutNodeFactorsByAngleZone != null)
            {
                targetProfile.FrozenSampleLutNodeFactorsByAngleZone =
                    new System.Collections.Generic.Dictionary<int, double[]>();
                foreach (var pair in sourceProfile.FrozenSampleLutNodeFactorsByAngleZone)
                    targetProfile.FrozenSampleLutNodeFactorsByAngleZone[pair.Key] =
                        pair.Value != null ? (double[])pair.Value.Clone() : null;
            }
        }
    }
}