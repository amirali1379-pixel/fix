using System;
using grapher.DesktopSens.Models;

namespace grapher.DesktopSens.Views
{
    /// <summary>
    /// Callback adapter between the cardinal merger and the application's
    /// existing RawAccel activation path. The callback must return the actual
    /// activation result; a successful callback means the native settings
    /// activation was accepted, not merely that values were calculated.
    /// </summary>
    public sealed class SampleCardinalProfileApplier : ICardinalProfileApplier
    {
        private readonly Func<MergedCardinalProfile, DesktopSensProfile, bool> _applyCallback;

        public SampleCardinalProfileApplier(
            Func<MergedCardinalProfile, DesktopSensProfile, bool> applyCallback)
        {
            _applyCallback = applyCallback ?? throw new ArgumentNullException(nameof(applyCallback));
        }

        public bool Apply(MergedCardinalProfile merged, DesktopSensProfile sourceProfile)
        {
            if (merged == null) throw new ArgumentNullException(nameof(merged));
            if (sourceProfile == null) throw new ArgumentNullException(nameof(sourceProfile));
            return _applyCallback(merged, sourceProfile);
        }
    }
}
