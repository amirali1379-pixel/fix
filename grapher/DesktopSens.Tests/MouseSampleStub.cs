using System;

namespace grapher.Models.Mouse
{
    // Minimal test-only shape for the mouse sample type consumed by
    // DesktopSensTrial. The production project supplies the real type.
    public class MouseSample
    {
        public int DX;
        public int DY;
        public long Timestamp;
    }
}