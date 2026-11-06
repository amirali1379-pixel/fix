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
        }
    }
}