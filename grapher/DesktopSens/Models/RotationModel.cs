namespace grapher.DesktopSens.Models
{
    public enum RotationModelType
    {
        FourierOrder2,
        FourierOrder3
    }

    public class RotationModel
    {
        public RotationModelType Type;
        public double[] Coefficients;
        public double FitQuality;
        public double MaxResidualDegrees;
        public double MeanResidualDegrees;
        public int SampleCount;
    }
}