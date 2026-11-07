using System;
using System.Collections.Generic;

namespace grapher.DesktopSens.Models
{
    /// <summary>
    /// Fits a periodic rotation-error curve over the 32 angular axes.
    /// This is calibration/preview math only; it does not modify mouse input.
    /// Coefficients are [a0, a1, b1, a2, b2, ...] for
    /// a0 + sum(ak*cos(k*angle) + bk*sin(k*angle)).
    /// </summary>
    public static class DesktopSensRotationModelFitter
    {
        private const double PivotEpsilon = 1e-10;
        private const double VarianceEpsilon = 1e-9;

        public static RotationModel Fit(
            double[] angleDegrees,
            double[] correctionDegrees,
            double[] weights,
            int fourierOrder)
        {
            if (angleDegrees == null) throw new ArgumentNullException(nameof(angleDegrees));
            if (correctionDegrees == null) throw new ArgumentNullException(nameof(correctionDegrees));
            if (weights == null) throw new ArgumentNullException(nameof(weights));
            if (angleDegrees.Length != correctionDegrees.Length || angleDegrees.Length != weights.Length)
                throw new ArgumentException("Angles, corrections, and weights must have the same length.");
            if (fourierOrder < 1 || fourierOrder > 3)
                throw new ArgumentOutOfRangeException(nameof(fourierOrder));

            int coefficientCount = 1 + 2 * fourierOrder;
            var model = new RotationModel
            {
                Type = fourierOrder >= 3 ? RotationModelType.FourierOrder3 : RotationModelType.FourierOrder2,
                Coefficients = new double[0],
                FitQuality = 0.0,
                MaxResidualDegrees = 0.0,
                MeanResidualDegrees = 0.0,
                SampleCount = 0
            };

            var angles = new List<double>();
            var corrections = new List<double>();
            var validWeights = new List<double>();
            double totalWeight = 0.0;

            for (int i = 0; i < angleDegrees.Length; i++)
            {
                if (!IsFinite(angleDegrees[i]) || !IsFinite(correctionDegrees[i]) ||
                    !IsFinite(weights[i]) || weights[i] <= 0.0)
                    continue;

                angles.Add(ZoneCalculations.NormalizeAngle(angleDegrees[i]));
                corrections.Add(NormalizeSignedAngle(correctionDegrees[i]));
                validWeights.Add(weights[i]);
                totalWeight += weights[i];
            }

            model.SampleCount = totalWeight >= int.MaxValue ? int.MaxValue : (int)Math.Round(totalWeight);
            // Leave at least three residual degrees of freedom so a high-order curve cannot
            // appear trustworthy merely by interpolating too few populated axes.
            if (angles.Count < coefficientCount + 3)
                return model;

            var normal = new double[coefficientCount, coefficientCount];
            var right = new double[coefficientCount];

            for (int sample = 0; sample < angles.Count; sample++)
            {
                double radians = angles[sample] * Math.PI / 180.0;
                double[] basis = BuildBasis(radians, fourierOrder);
                double weight = validWeights[sample];
                for (int row = 0; row < coefficientCount; row++)
                {
                    right[row] += weight * basis[row] * corrections[sample];
                    for (int col = 0; col < coefficientCount; col++)
                        normal[row, col] += weight * basis[row] * basis[col];
                }
            }

            double[] coefficients;
            if (!TrySolve(normal, right, out coefficients))
                return model;

            double weightedMean = 0.0;
            for (int i = 0; i < corrections.Count; i++)
                weightedMean += validWeights[i] * corrections[i];
            weightedMean /= totalWeight;

            double residualSumSquares = 0.0;
            double totalSumSquares = 0.0;
            double weightedAbsoluteResidual = 0.0;
            double maxResidual = 0.0;
            for (int i = 0; i < angles.Count; i++)
            {
                double radians = angles[i] * Math.PI / 180.0;
                double predicted = Evaluate(coefficients, radians);
                double residual = NormalizeSignedAngle(corrections[i] - predicted);
                double weight = validWeights[i];
                residualSumSquares += weight * residual * residual;
                double centered = NormalizeSignedAngle(corrections[i] - weightedMean);
                totalSumSquares += weight * centered * centered;
                weightedAbsoluteResidual += weight * Math.Abs(residual);
                maxResidual = Math.Max(maxResidual, Math.Abs(residual));
            }

            double fitQuality;
            if (totalSumSquares <= VarianceEpsilon)
                fitQuality = residualSumSquares <= VarianceEpsilon ? 1.0 : 0.0;
            else
                fitQuality = Math.Max(0.0, Math.Min(1.0, 1.0 - residualSumSquares / totalSumSquares));

            model.Coefficients = coefficients;
            model.FitQuality = fitQuality;
            model.MaxResidualDegrees = maxResidual;
            model.MeanResidualDegrees = weightedAbsoluteResidual / totalWeight;
            return model;
        }

        public static bool IsUsable(RotationModel model)
        {
            if (model == null || model.Coefficients == null ||
                model.Coefficients.Length < 7 ||
                model.FitQuality < DesktopSensConstants.RotationMinFitQuality ||
                model.MaxResidualDegrees > DesktopSensConstants.RotationMaxResidualDegrees)
                return false;

            for (int i = 0; i < model.Coefficients.Length; i++)
                if (!IsFinite(model.Coefficients[i])) return false;
            return IsFinite(model.FitQuality) && IsFinite(model.MaxResidualDegrees);
        }

        public static double EvaluateDegrees(RotationModel model, double angleDegrees)
        {
            if (model == null || model.Coefficients == null ||
                model.Coefficients.Length < 7 || !IsFinite(angleDegrees))
                return 0.0;

            double radians = ZoneCalculations.NormalizeAngle(angleDegrees) * Math.PI / 180.0;
            return NormalizeSignedAngle(Evaluate(model.Coefficients, radians));
        }

        private static double[] BuildBasis(double radians, int order)
        {
            var basis = new double[1 + 2 * order];
            basis[0] = 1.0;
            for (int harmonic = 1; harmonic <= order; harmonic++)
            {
                basis[1 + (harmonic - 1) * 2] = Math.Cos(harmonic * radians);
                basis[2 + (harmonic - 1) * 2] = Math.Sin(harmonic * radians);
            }
            return basis;
        }

        private static double Evaluate(double[] coefficients, double radians)
        {
            double value = coefficients[0];
            int order = (coefficients.Length - 1) / 2;
            for (int harmonic = 1; harmonic <= order; harmonic++)
                value += coefficients[1 + (harmonic - 1) * 2] * Math.Cos(harmonic * radians)
                    + coefficients[2 + (harmonic - 1) * 2] * Math.Sin(harmonic * radians);
            return value;
        }

        private static bool TrySolve(double[,] matrix, double[] vector, out double[] solution)
        {
            int size = vector.Length;
            var augmented = new double[size, size + 1];
            for (int row = 0; row < size; row++)
            {
                for (int col = 0; col < size; col++) augmented[row, col] = matrix[row, col];
                augmented[row, size] = vector[row];
            }

            for (int pivot = 0; pivot < size; pivot++)
            {
                int bestRow = pivot;
                double bestMagnitude = Math.Abs(augmented[pivot, pivot]);
                for (int row = pivot + 1; row < size; row++)
                {
                    double magnitude = Math.Abs(augmented[row, pivot]);
                    if (magnitude > bestMagnitude)
                    {
                        bestMagnitude = magnitude;
                        bestRow = row;
                    }
                }

                if (bestMagnitude < PivotEpsilon)
                {
                    solution = new double[0];
                    return false;
                }

                if (bestRow != pivot)
                {
                    for (int col = pivot; col <= size; col++)
                    {
                        double temp = augmented[pivot, col];
                        augmented[pivot, col] = augmented[bestRow, col];
                        augmented[bestRow, col] = temp;
                    }
                }

                double divisor = augmented[pivot, pivot];
                for (int col = pivot; col <= size; col++) augmented[pivot, col] /= divisor;

                for (int row = 0; row < size; row++)
                {
                    if (row == pivot) continue;
                    double factor = augmented[row, pivot];
                    if (Math.Abs(factor) < PivotEpsilon) continue;
                    for (int col = pivot; col <= size; col++)
                        augmented[row, col] -= factor * augmented[pivot, col];
                }
            }

            solution = new double[size];
            for (int row = 0; row < size; row++)
            {
                solution[row] = augmented[row, size];
                if (!IsFinite(solution[row]))
                {
                    solution = new double[0];
                    return false;
                }
            }
            return true;
        }

        private static double NormalizeSignedAngle(double angle)
        {
            angle %= 360.0;
            if (angle > 180.0) angle -= 360.0;
            if (angle < -180.0) angle += 360.0;
            return angle;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}