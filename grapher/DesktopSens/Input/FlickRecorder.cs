using System;
using System.Collections.Generic;
using System.Diagnostics;
using grapher.Models.Mouse;
using grapher.DesktopSens.Models;

namespace grapher.DesktopSens.Input
{
    public class FlickRecorder
    {
        public bool IsRecording { get; private set; }
        public double StartX { get; set; }
        public double StartY { get; set; }
        public double TargetX { get; set; }
        public double TargetY { get; set; }
        public int MouseDPI { get; set; } = 800;
        public double MeasuredCpi { get; set; }
        public double EffectiveCpi => MeasuredCpi > 0.0 && !double.IsNaN(MeasuredCpi) && !double.IsInfinity(MeasuredCpi)
            ? MeasuredCpi : MouseDPI;
        public double PollingRateHz { get; set; } = 1000.0;

        private List<MouseSample> _samples = new List<MouseSample>();
        private Stopwatch _stopwatch = new Stopwatch();

        public void StartRecording()
        {
            _samples.Clear();
            _stopwatch.Restart();
            IsRecording = true;
        }

        public void OnSample(MouseSample sample)
        {
            if (!IsRecording) return;
            _samples.Add(sample);
        }

        public DesktopSensTrial StopRecording(FlickCompletionMode mode)
        {
            IsRecording = false;
            _stopwatch.Stop();

            if (_samples.Count < 2 || EffectiveCpi <= 0.0 || double.IsNaN(EffectiveCpi) || double.IsInfinity(EffectiveCpi)) return null;

            double startX = StartX;
            double startY = StartY;

            double cumulativeX = startX;
            double cumulativeY = startY;
            double peakVelocity = 0;
            double prevTimestamp = _samples[0].Timestamp;
            double prevX = cumulativeX;
            double prevY = cumulativeY;

            for (int i = 0; i < _samples.Count; i++)
            {
                var s = _samples[i];
                cumulativeX += s.DX;
                cumulativeY += s.DY;

                double dtTicks = s.Timestamp - prevTimestamp;
                if (dtTicks > 0)
                {
                    double dtSeconds = dtTicks / (double)Stopwatch.Frequency;
                    double dx = cumulativeX - prevX;
                    double dy = cumulativeY - prevY;
                    double velocity = Math.Sqrt(dx * dx + dy * dy) / dtSeconds;
                    if (velocity > peakVelocity) peakVelocity = velocity;
                }

                prevTimestamp = s.Timestamp;
                prevX = cumulativeX;
                prevY = cumulativeY;
            }

            double landedX = cumulativeX;
            double landedY = cumulativeY;

            double targetDx = TargetX - startX;
            double targetDy = TargetY - startY;
            double targetDistancePx = Math.Sqrt(targetDx * targetDx + targetDy * targetDy);
            double targetDistanceCm = targetDistancePx / EffectiveCpi * 2.54;

            double flickDx = landedX - startX;
            double flickDy = landedY - startY;
            double flickDistancePx = Math.Sqrt(flickDx * flickDx + flickDy * flickDy);
            double flickDistanceCm = flickDistancePx / EffectiveCpi * 2.54;

            double errorDx = landedX - TargetX;
            double errorDy = landedY - TargetY;
            double finalErrorPx = Math.Sqrt(errorDx * errorDx + errorDy * errorDy);
            double finalErrorCm = finalErrorPx / EffectiveCpi * 2.54;

            // محاسبه زاویه فلیک
            double flickAngle = Math.Atan2(flickDy, flickDx) * 180.0 / Math.PI;
            if (flickAngle < 0) flickAngle += 360;

            // محاسبه زاویه هدف
            double targetAngle = Math.Atan2(targetDy, targetDx) * 180.0 / Math.PI;
            if (targetAngle < 0) targetAngle += 360;

            // تشخیص Under/Over
            double targetDirX = targetDistancePx > 0 ? targetDx / targetDistancePx : 0;
            double targetDirY = targetDistancePx > 0 ? targetDy / targetDistancePx : 0;
            double projection = flickDx * targetDirX + flickDy * targetDirY;
            bool isUnder = projection < targetDistancePx;
            bool isOver = projection > targetDistancePx;

            var trial = new DesktopSensTrial
            {
                StartX = startX,
                StartY = startY,
                TargetX = TargetX,
                TargetY = TargetY,
                TargetAngleDegrees = targetAngle,
                TargetDistanceCm = targetDistanceCm,
                RawSamples = new List<MouseSample>(_samples),
                LandedX = landedX,
                LandedY = landedY,
                FinalErrorCm = finalErrorCm,
                IsUnderFlick = isUnder,
                IsOverFlick = isOver,
                FlickAngleDegrees = flickAngle, // پر می‌شود
                FlickDistanceCm = flickDistanceCm,
                DurationMs = _stopwatch.Elapsed.TotalMilliseconds,
                PeakVelocityPxPerSec = peakVelocity,
                AngleZoneIndex = ZoneCalculations.AngleToZoneIndex(targetAngle),
                DistanceZoneIndex = ZoneCalculations.DistanceToZoneIndex(targetDistanceCm),
                CompletionMode = mode,
                CompletionTimestamp = _samples[_samples.Count - 1].Timestamp,
                MouseDPI = MouseDPI,
                EffectiveCpi = EffectiveCpi,
                EffectivePollingRateHz = PollingRateHz
            };

            return trial;
        }
    }
}