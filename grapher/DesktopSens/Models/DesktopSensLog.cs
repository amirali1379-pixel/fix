using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace grapher.DesktopSens.Models
{
    /// <summary>
    /// Small in-memory diagnostic ring buffer for calibration and UI failures.
    /// It never throws and keeps the most recent 50 entries for support reports.
    /// </summary>
    public static class DesktopSensLog
    {
        private const int Capacity = 50;
        private static readonly object Sync = new object();
        private static readonly Queue<string> Entries = new Queue<string>(Capacity);
        private static readonly string LogPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopSens", "diag.log");

        static DesktopSensLog()
        {
            var asm = typeof(DesktopSensLog).Assembly;
            Write("WARN", "=== SESSION START === build=" +
                System.IO.File.GetLastWriteTime(asm.Location).ToString("yyyy-MM-dd HH:mm:ss") +
                " path=" + asm.Location, null);
        }

        public static void Info(string message, Exception exception = null)
        {
            Write("INFO", message, exception);
        }

        public static void Warn(string message, Exception exception = null)
        {
            Write("WARN", message, exception);
        }

        public static void Error(string message, Exception exception = null)
        {
            Write("ERROR", message, exception);
        }

        public static string[] GetRecentEntries()
        {
            lock (Sync)
                return Entries.ToArray();
        }

        private static void Write(string level, string message, Exception exception)
        {
            string entry = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) +
                " [" + level + "] " + (message ?? "Unspecified calibration error");
            if (exception != null)
                entry += " | " + exception.ToString();

            lock (Sync)
            {
                while (Entries.Count >= Capacity)
                    Entries.Dequeue();
                Entries.Enqueue(entry);
            }

            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath));
                System.IO.File.AppendAllText(LogPath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + entry + Environment.NewLine);
            }
            catch { }

            Debug.WriteLine("[DesktopSens] " + entry);
        }
    }
}