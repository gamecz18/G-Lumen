using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Avalonia.Threading;

namespace G_Lumen.Services
{
    /// <summary>
    /// One transaction toward the monitor (→) or from it (←).
    /// Shown in the popup's diagnostics panel.
    /// </summary>
    public sealed class TrafficEntry
    {
        public DateTime Time { get; init; }

        /// <summary>true = write to the monitor (→), false = read / response (←).</summary>
        public bool Outgoing { get; init; }

        /// <summary>Channel: "DDC/CI", "DispCfg" (DisplayConfig), "GDI" (enumeration).</summary>
        public string Channel { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;

        public bool Success { get; init; }

        /// <summary>Win32 / DisplayConfig error code, if the operation failed.</summary>
        public int? Error { get; init; }

        public string TimeText => Time.ToString("HH:mm:ss.fff");
        public string Arrow => Outgoing ? "→" : "←";
        public string StatusText => Success ? "OK" : Error is int e ? $"ERR {e}" : "ERR";

        /// <summary>Row tooltip: full message + a human-readable explanation of the error code.</summary>
        public string Tooltip => Success
            ? Message
            : $"{Message}\n{StatusText}{ErrorHint(Error)}";

        private static string ErrorHint(int? error) => error switch
        {
            31 => " · ERROR_GEN_FAILURE — device did not respond (adapter/driver did not acknowledge the I2C transaction)",
            5 => " · ERROR_ACCESS_DENIED — access denied",
            6 => " · ERROR_INVALID_HANDLE — invalid monitor handle (try Refresh monitors)",
            87 => " · ERROR_INVALID_PARAMETER — invalid parameter",
            1359 => " · ERROR_INTERNAL_ERROR — internal driver error",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// In-memory ring buffer of monitor transactions (DDC/CI, DisplayConfig, GDI).
    /// Newest entries are at the front of the collection (no autoscroll needed).
    /// Writing is safe from any thread — calls off the UI thread are marshalled via Dispatcher.
    ///
    /// Each entry is also appended to a traffic-YYYYMMDD.log file
    /// (if <c>filePath</c> was passed to the constructor), so the traffic
    /// can be analyzed later even after the app is closed.
    /// </summary>
    public sealed class TrafficLog
    {
        private const int Capacity = 300;

        private readonly string? _filePath;
        private readonly object _fileGate = new();

        public TrafficLog(string? filePath = null)
        {
            _filePath = filePath;
            if (filePath is not null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                }
                catch
                {
                    _filePath = null; // diagnostics must never crash the app
                }
            }
        }

        public ObservableCollection<TrafficEntry> Entries { get; } = new();

        /// <summary>Write to the monitor (→).</summary>
        public void Out(string channel, string message, bool success, int? error = null)
            => Add(new TrafficEntry
            {
                Time = DateTime.Now,
                Outgoing = true,
                Channel = channel,
                Message = message,
                Success = success,
                Error = error,
            });

        /// <summary>Read from the monitor / system (←).</summary>
        public void In(string channel, string message, bool success, int? error = null)
            => Add(new TrafficEntry
            {
                Time = DateTime.Now,
                Outgoing = false,
                Channel = channel,
                Message = message,
                Success = success,
                Error = error,
            });

        public void Clear()
        {
            if (Dispatcher.UIThread.CheckAccess())
                Entries.Clear();
            else
                Dispatcher.UIThread.Post(Entries.Clear);
        }

        private void Add(TrafficEntry entry)
        {
            WriteFile(entry);

            if (Dispatcher.UIThread.CheckAccess())
                AddCore(entry);
            else
                Dispatcher.UIThread.Post(() => AddCore(entry));
        }

        private void AddCore(TrafficEntry entry)
        {
            Entries.Insert(0, entry);
            while (Entries.Count > Capacity)
                Entries.RemoveAt(Entries.Count - 1);
        }

        private void WriteFile(TrafficEntry e)
        {
            if (_filePath is null)
                return;

            try
            {
                string line =
                    $"{e.Time:yyyy-MM-dd HH:mm:ss.fff} {e.Arrow} {e.Channel,-7} {e.StatusText,-8} {e.Message}";
                lock (_fileGate)
                    File.AppendAllText(_filePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logging must never crash the app.
            }
        }
    }
}
