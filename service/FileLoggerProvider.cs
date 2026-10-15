using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Glowpilot.Service;

/// <summary>
/// Rolling plain-text file log. The Service runs windowless (no console), so this file is
/// where its diagnostics end up.
/// <para>
/// Entries are appended to <c>service.log</c>. Once it reaches the size limit it is rolled to
/// <c>service.1.log</c>; older backups shift up to <c>service.N.log</c> and the oldest is dropped.
/// The file is opened lazily and shared for reading, so it can be tailed while the Service runs.
/// </para>
/// <para>
/// Logging never takes the Service down: any I/O failure drops the entry and delays the next
/// attempt by a short back-off.
/// </para>
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const string FileName = "service.log";

    private const long DefaultMaxFileBytes = 2 * 1024 * 1024;
    private const int  DefaultRetainedFiles = 3;
    private const long RetryBackoffMs = 30_000;
    private const string CategoryPrefix = "Glowpilot.Service.";

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly long _maxFileBytes;
    private readonly int _retainedFiles;
    private StreamWriter? _writer;
    private long _size;
    private long _retryAtMs;
    private bool _disposed;

    public FileLoggerProvider(
        string directory,
        long maxFileBytes = DefaultMaxFileBytes,
        int retainedFiles = DefaultRetainedFiles)
    {
        _directory     = directory;
        _maxFileBytes  = maxFileBytes;
        _retainedFiles = Math.Max(1, retainedFiles);
    }

    /// <summary>
    /// <c>%LOCALAPPDATA%\Glowpilot\logs</c> (Windows) or <c>~/.local/share/Glowpilot/logs</c> (Linux/macOS).
    /// Falls back to the temp directory when the platform has no per-user data folder.
    /// </summary>
    public static string DefaultDirectory
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(root))
                root = Path.GetTempPath();
            return Path.Combine(root, "Glowpilot", "logs");
        }
    }

    /// <summary>
    /// Parses a level name from the config file (case-insensitive: Trace, Debug, Information,
    /// Warning, Error, Critical, None). Anything else falls back to Information.
    /// </summary>
    public static LogLevel ParseLevel(string? name) =>
        Enum.TryParse<LogLevel>(name, ignoreCase: true, out var level) && Enum.IsDefined(level)
            ? level
            : LogLevel.Information;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CloseWriter();
        }
    }

    // Writing ─────────────────────────────────────────────────────────────────

    private string CurrentPath => Path.Combine(_directory, FileName);

    private string BackupPath(int index) =>
        Path.Combine(_directory, $"{Path.GetFileNameWithoutExtension(FileName)}.{index}{Path.GetExtension(FileName)}");

    private void Write(string entry)
    {
        lock (_gate)
        {
            if (_disposed || Environment.TickCount64 < _retryAtMs)
                return;

            try
            {
                EnsureWriter();
                _writer!.Write(entry);   // AutoFlush: one write per entry, so a crash loses nothing
                _size += Encoding.UTF8.GetByteCount(entry);
            }
            catch (Exception)
            {
                // Disk full, read-only or locked file, missing profile folder, … — logging must
                // never take the Service down. Drop the entry and try again after the back-off.
                CloseWriter();
                _retryAtMs = Environment.TickCount64 + RetryBackoffMs;
                return;
            }

            if (_size >= _maxFileBytes)
                Roll();
        }
    }

    private void EnsureWriter()
    {
        if (_writer is not null)
            return;

        Directory.CreateDirectory(_directory);
        var stream = new FileStream(
            CurrentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        _size = stream.Length;
    }

    private void Roll()
    {
        CloseWriter();
        _size = 0;   // even when rolling fails, only retry after another full file's worth of entries

        try
        {
            for (var i = _retainedFiles - 1; i >= 1; i--)
            {
                if (File.Exists(BackupPath(i)))
                    File.Move(BackupPath(i), BackupPath(i + 1), overwrite: true);
            }
            File.Move(CurrentPath, BackupPath(1), overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Another instance still holds a file open — keep appending to the current one.
        }
    }

    private void CloseWriter()
    {
        try { _writer?.Dispose(); }
        catch (Exception) { /* already failing; nothing useful left to do */ }
        _writer = null;
    }

    // Formatting ──────────────────────────────────────────────────────────────

    private static string Format(LogLevel level, string category, string message, Exception? exception)
    {
        var sb = new StringBuilder(128);
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
          .Append(" [").Append(Abbreviate(level)).Append("] ")
          .Append(category.StartsWith(CategoryPrefix, StringComparison.Ordinal) ? category[CategoryPrefix.Length..] : category)
          .Append(": ").Append(message);

        if (exception is not null)
            sb.AppendLine().Append(exception);

        return sb.AppendLine().ToString();
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace       => "TRC",
        LogLevel.Debug       => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning     => "WRN",
        LogLevel.Error       => "ERR",
        LogLevel.Critical    => "CRT",
        _                    => "???"
    };

    // ILogger ─────────────────────────────────────────────────────────────────

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            try
            {
                _provider.Write(Format(logLevel, _category, formatter(state, exception), exception));
            }
            catch (Exception)
            {
                // A throwing formatter or ToString() must not propagate into the caller either.
            }
        }
    }
}
