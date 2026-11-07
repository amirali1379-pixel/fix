using System;
using System.Diagnostics;
using grapher.Models.Mouse;

namespace grapher.DesktopSens.Models
{
    public sealed class DesktopSensTrialGateResult
    {
        public bool Accepted { get; private set; }
        public string Reason { get; private set; }

        public DesktopSensTrialGateResult(bool accepted, string reason)
        {
            Accepted = accepted;
            Reason = reason ?? TrialRejectionReason.None;
        }
    }

    /// <summary>
    /// Pure training-trial gate. It intentionally uses the raw mouse flick,
    /// never the post-ASDW landing, and does not mutate the supplied trial.
    /// </summary>
    public static class DesktopSensTrialGate
    {
        public static DesktopSensTrialGateResult Evaluate(
            DesktopSensTrial trial,
            double zoneStartCm,
            bool isFirstZone)
        {
            if (trial == null)
                return Reject(TrialRejectionReason.SensorGlitch);

            if (!IsFinite(trial.DurationMs) ||
                trial.DurationMs < DesktopSensCalibrator.MinFlickDurationMs ||
                trial.DurationMs > DesktopSensConstants.TrainingTrialMaxDurationMs)
                return Reject(TrialRejectionReason.DurationOutOfRange);

            if (!IsFinite(trial.TargetDistanceCm) || trial.TargetDistanceCm <= 0.0 ||
                !IsFinite(trial.FlickDistanceCm) || trial.FlickDistanceCm <= 0.0)
                return Reject(TrialRejectionReason.SensorGlitch);

            if (trial.FlickDistanceCm >
                trial.TargetDistanceCm * DesktopSensConstants.TrainingTrialMaxOvershootRatio)
                return Reject(TrialRejectionReason.OvershootOutOfRange);

            double minimumEntryDistance = isFirstZone
                ? trial.TargetDistanceCm * DesktopSensConstants.TrainingTrialFirstZoneMinDistanceRatio
                : Math.Max(0.0, zoneStartCm - DesktopSensConstants.TrainingTrialZoneEntryToleranceCm);
            if (trial.FlickDistanceCm < minimumEntryDistance)
                return Reject(TrialRejectionReason.DidNotEnterZone);

            if (trial.RawSamples == null || trial.RawSamples.Count < 2 ||
                !IsFinite(trial.EffectiveCpi) || trial.EffectiveCpi <= 0.0)
                return Reject(TrialRejectionReason.SensorGlitch);

            double peakSpeedCmPerSec = 0.0;
            long previousTimestamp = trial.RawSamples[0].Timestamp;
            for (int i = 1; i < trial.RawSamples.Count; i++)
            {
                MouseSample sample = trial.RawSamples[i];
                long deltaTicks = sample.Timestamp - previousTimestamp;
                previousTimestamp = sample.Timestamp;

                double rawSpeed = SampleLutSampleProcessor.RawSpeedCmPerSec(
                    sample.DX, sample.DY, deltaTicks, trial.EffectiveCpi);
                if (IsFinite(rawSpeed) && rawSpeed > peakSpeedCmPerSec)
                    peakSpeedCmPerSec = rawSpeed;

                // Ignore the initial onset and final release. A long internal
                // gap is a pause/aborted motion, not a valid continuous flick.
                if (i < trial.RawSamples.Count - 1 && i > 1 && deltaTicks > 0)
                {
                    double gapMs = deltaTicks * 1000.0 / Stopwatch.Frequency;
                    if (gapMs > DesktopSensConstants.TrainingTrialMaxInternalPauseMs)
                        return Reject(TrialRejectionReason.LongPause);
                }
            }

            if (peakSpeedCmPerSec < DesktopSensConstants.TrainingTrialMinPeakSpeedCmPerSec)
                return Reject(TrialRejectionReason.PeakVelocityTooLow);

            return new DesktopSensTrialGateResult(true, TrialRejectionReason.None);
        }

        private static DesktopSensTrialGateResult Reject(string reason)
            => new DesktopSensTrialGateResult(false, reason);

        private static bool IsFinite(double value)
            => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
