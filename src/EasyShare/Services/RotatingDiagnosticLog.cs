using System.Text;
using System.Text.Json;
using EasyShare.Models;

namespace EasyShare.Services;

public sealed class RotatingDiagnosticLog
{
    private readonly string _logPath;
    private readonly DiagnosticLogOptions _options;
    private readonly SensitiveDataRedactor _redactor;
    private readonly object _gate = new();
    private bool _validatedCurrentFormat;

    public RotatingDiagnosticLog(
        string logPath,
        DiagnosticLogOptions? options = null,
        SensitiveDataRedactor? redactor = null)
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            throw new ArgumentException("A diagnostic log path is required.", nameof(logPath));
        }

        _logPath = Path.GetFullPath(logPath);
        _options = options ?? new DiagnosticLogOptions();
        _options.Validate();
        _redactor = redactor ?? new SensitiveDataRedactor();
    }

    public string LogPath => _logPath;

    public string LogDirectory => Path.GetDirectoryName(_logPath)!;

    public void Write(DiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        try
        {
            var record = Serialize(diagnosticEvent);
            var bytes = Encoding.UTF8.GetBytes(record + Environment.NewLine);
            if (bytes.LongLength > _options.MaxFileBytes)
            {
                var compact = diagnosticEvent with
                {
                    Message = Limit(
                        diagnosticEvent.Message,
                        checked((int)Math.Min(_options.MaxFileBytes / 8, int.MaxValue))),
                    Exception = null,
                    Properties = null
                };
                bytes = Encoding.UTF8.GetBytes(Serialize(compact) + Environment.NewLine);
            }

            lock (_gate)
            {
                Directory.CreateDirectory(LogDirectory);
                PrivateFilePermissions.TryHardenDirectory(LogDirectory);
                PurgeExpiredArchives(DateTimeOffset.UtcNow);
                EnsureStructuredCurrentLog();
                if (TryGetLength(_logPath) + bytes.Length > _options.MaxFileBytes)
                {
                    Rotate();
                }

                var existed = File.Exists(_logPath);
                using var stream = new FileStream(
                    _logPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 16 * 1024,
                    FileOptions.SequentialScan);
                stream.Write(bytes);
                stream.Flush(flushToDisk: false);
                if (!existed)
                {
                    PrivateFilePermissions.TryHardenFile(_logPath);
                }
            }
        }
        catch (Exception ex) when (ex is SystemException || ex is JsonException)
        {
            // Diagnostics must never prevent the app from starting or continuing work.
        }
    }

    public IReadOnlyList<string> GetLogFiles()
    {
        lock (_gate)
        {
            var paths = new List<string>();
            if (File.Exists(_logPath))
            {
                paths.Add(_logPath);
            }

            for (var index = 1; index <= _options.MaxArchiveFiles; index++)
            {
                var archive = GetArchivePath(index);
                if (File.Exists(archive))
                {
                    paths.Add(archive);
                }
            }

            return paths;
        }
    }

    private string Serialize(DiagnosticEvent diagnosticEvent)
    {
        var eventName = Limit(_redactor.Redact(diagnosticEvent.EventName), 256);
        var message = Limit(_redactor.Redact(diagnosticEvent.Message), _options.MaxEventCharacters);
        Dictionary<string, string>? properties = null;
        if (diagnosticEvent.Properties is not null)
        {
            properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in diagnosticEvent.Properties.Take(32))
            {
                var key = Limit(_redactor.Redact(pair.Key), 128);
                if (string.IsNullOrWhiteSpace(key) || properties.ContainsKey(key))
                {
                    continue;
                }

                properties[key] = _redactor.IsSensitiveKey(pair.Key) || _redactor.IsPersonalDataKey(pair.Key)
                    ? SensitiveDataRedactor.RedactedValue
                    : Limit(_redactor.Redact(pair.Value), 1024);
            }
        }

        object? exception = null;
        if (diagnosticEvent.Exception is not null)
        {
            exception = new
            {
                type = diagnosticEvent.Exception.GetType().FullName,
                message = Limit(
                    _redactor.Redact(diagnosticEvent.Exception.Message),
                    _options.MaxEventCharacters / 2),
                stack = Limit(
                    _redactor.Redact(diagnosticEvent.Exception.StackTrace),
                    _options.MaxEventCharacters)
            };
        }

        return JsonSerializer.Serialize(new
        {
            timestamp = diagnosticEvent.Timestamp.ToUniversalTime(),
            level = diagnosticEvent.Level.ToString(),
            eventName,
            message,
            properties,
            exception
        });
    }

    private void Rotate()
    {
        if (_options.MaxArchiveFiles == 0)
        {
            TryDelete(_logPath);
            return;
        }

        TryDelete(GetArchivePath(_options.MaxArchiveFiles));
        for (var index = _options.MaxArchiveFiles - 1; index >= 1; index--)
        {
            var source = GetArchivePath(index);
            if (File.Exists(source))
            {
                File.Move(source, GetArchivePath(index + 1), overwrite: true);
            }
        }

        if (File.Exists(_logPath))
        {
            File.Move(_logPath, GetArchivePath(1), overwrite: true);
        }
    }

    private void EnsureStructuredCurrentLog()
    {
        if (_validatedCurrentFormat)
        {
            return;
        }

        _validatedCurrentFormat = true;
        if (!File.Exists(_logPath) || TryGetLength(_logPath) == 0)
        {
            return;
        }

        using var input = new FileStream(
            _logPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16,
            FileOptions.SequentialScan);
        int first;
        do
        {
            first = input.ReadByte();
        }
        while (first is ' ' or '\t' or '\r' or '\n');

        if (first != '{')
        {
            input.Dispose();
            Rotate();
        }
    }

    private void PurgeExpiredArchives(DateTimeOffset now)
    {
        for (var index = 1; index <= _options.MaxArchiveFiles; index++)
        {
            var archive = GetArchivePath(index);
            try
            {
                if (File.Exists(archive) && now - File.GetLastWriteTimeUtc(archive) > _options.Retention)
                {
                    File.Delete(archive);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Retention is best effort; the file-count bound still applies on rotation.
            }
        }
    }

    private string GetArchivePath(int index)
    {
        var directory = LogDirectory;
        var name = Path.GetFileNameWithoutExtension(_logPath);
        var extension = Path.GetExtension(_logPath);
        return Path.Combine(directory, $"{name}.{index}{extension}");
    }

    private static string Limit(string? value, int maxCharacters)
    {
        var text = value ?? string.Empty;
        return text.Length <= maxCharacters ? text : text[..maxCharacters] + "[TRUNCATED]";
    }

    private static long TryGetLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Rotation cleanup is best effort.
        }
    }
}
