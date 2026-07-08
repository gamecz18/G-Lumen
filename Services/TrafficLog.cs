using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Avalonia.Threading;

namespace G_Lumen.Services
{
    /// <summary>
    /// Jedna transakce směrem k monitoru (→) nebo od něj (←).
    /// Zobrazuje se v diagnostickém panelu popupu.
    /// </summary>
    public sealed class TrafficEntry
    {
        public DateTime Time { get; init; }

        /// <summary>true = zápis do monitoru (→), false = čtení / odpověď (←).</summary>
        public bool Outgoing { get; init; }

        /// <summary>Kanál: "DDC/CI", "DispCfg" (DisplayConfig), "GDI" (enumerace).</summary>
        public string Channel { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;

        public bool Success { get; init; }

        /// <summary>Win32 / DisplayConfig chybový kód, pokud operace selhala.</summary>
        public int? Error { get; init; }

        public string TimeText => Time.ToString("HH:mm:ss.fff");
        public string Arrow => Outgoing ? "→" : "←";
        public string StatusText => Success ? "OK" : Error is int e ? $"ERR {e}" : "ERR";

        /// <summary>Tooltip řádku: celá zpráva + lidské vysvětlení chybového kódu.</summary>
        public string Tooltip => Success
            ? Message
            : $"{Message}\n{StatusText}{ErrorHint(Error)}";

        private static string ErrorHint(int? error) => error switch
        {
            31 => " · ERROR_GEN_FAILURE — zařízení neodpovědělo (adaptér/ovladač nepotvrdil I2C transakci)",
            5 => " · ERROR_ACCESS_DENIED — přístup odepřen",
            6 => " · ERROR_INVALID_HANDLE — neplatný handle monitoru (zkus Obnovit monitory)",
            87 => " · ERROR_INVALID_PARAMETER — neplatný parametr",
            1359 => " · ERROR_INTERNAL_ERROR — interní chyba ovladače",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// In-memory ring buffer transakcí s monitory (DDC/CI, DisplayConfig, GDI).
    /// Nejnovější položky jsou na začátku kolekce (žádný autoscroll není potřeba).
    /// Zápis je bezpečný z libovolného vlákna — mimo UI thread se přehodí přes Dispatcher.
    ///
    /// Každá položka se zároveň appenduje do souboru traffic-YYYYMMDD.log
    /// (pokud byl při konstrukci předán <c>filePath</c>), aby šel provoz
    /// analyzovat zpětně i po zavření aplikace.
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
                    _filePath = null; // diagnostika nesmí shodit appku
                }
            }
        }

        public ObservableCollection<TrafficEntry> Entries { get; } = new();

        /// <summary>Zápis do monitoru (→).</summary>
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

        /// <summary>Čtení z monitoru / systému (←).</summary>
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
                // Logování nikdy nesmí shodit appku.
            }
        }
    }
}
