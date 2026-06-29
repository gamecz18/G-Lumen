using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace G_Lumen.Services
{
    /// <summary>
    /// Globální bootstrap logování. Zapisuje do souboru
    /// %AppData%\G-Lumen\logs\g-lumen-YYYYMMDD.log a registruje
    /// zachytávače neošetřených výjimek, aby pády byly dohledatelné.
    /// </summary>
    public static class AppLog
    {
        public static ILoggerFactory Factory { get; private set; } =
            LoggerFactory.Create(_ => { });

        private static ILogger _root = Factory.CreateLogger("G-Lumen");

        public static string LogPath { get; private set; } = string.Empty;

        public static void Init()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "G-Lumen", "logs");
            LogPath = Path.Combine(dir, $"g-lumen-{DateTime.Now:yyyyMMdd}.log");

            Factory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Debug);
                builder.AddProvider(new FileLoggerProvider(LogPath));
            });
            _root = Factory.CreateLogger("G-Lumen");
            _root.LogInformation("=== G-Lumen started (log: {Path}) ===", LogPath);
        }

        public static ILogger CreateLogger(string category) => Factory.CreateLogger(category);

        public static ILogger<T> CreateLogger<T>() => Factory.CreateLogger<T>();

        public static void Fatal(string source, Exception ex)
            => _root.LogCritical(ex, "Unhandled exception from {Source}", source);

        public static void InstallGlobalHandlers()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    Fatal("AppDomain", ex);
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Fatal("TaskScheduler", e.Exception);
                e.SetObserved();
            };
        }
    }

    /// <summary>Jednoduchý souborový <see cref="ILoggerProvider"/> (thread-safe append).</summary>
    public sealed class FileLoggerProvider : ILoggerProvider
    {
        private readonly string _path;
        private readonly object _gate = new();

        public FileLoggerProvider(string path)
        {
            _path = path;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }

        public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

        public void Dispose() { }

        internal void Write(string line)
        {
            try
            {
                lock (_gate)
                    File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logování nikdy nesmí shodit appku.
            }
        }
    }

    internal sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly FileLoggerProvider _provider;

        public FileLogger(string category, FileLoggerProvider provider)
        {
            _category = category;
            _provider = provider;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            sb.Append(" [").Append(Level(logLevel)).Append("] ");
            sb.Append(ShortCategory(_category)).Append(": ");
            sb.Append(formatter(state, exception));
            if (exception is not null)
                sb.Append(Environment.NewLine).Append(exception);

            _provider.Write(sb.ToString());
        }

        private static string Level(LogLevel l) => l switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };

        private static string ShortCategory(string c)
        {
            int i = c.LastIndexOf('.');
            return i >= 0 ? c[(i + 1)..] : c;
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
