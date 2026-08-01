using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PolymarketBot.Services;

/// <summary>
/// Minimal ILoggerProvider that writes JSON lines to a file, matching Python's JsonFormatter.
/// </summary>
public sealed class JsonFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly StreamWriter _writer;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public JsonFileLoggerProvider(StreamWriter writer) => _writer = writer;

    public ILogger CreateLogger(string categoryName) =>
        new JsonFileLogger(categoryName, _writer, () => _scopeProvider);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    public void Dispose() { }
}

internal sealed class JsonFileLogger : ILogger
{
    private readonly string _category;
    private readonly StreamWriter _writer;
    private readonly Func<IExternalScopeProvider> _scopeProvider;

    public JsonFileLogger(string category, StreamWriter writer, Func<IExternalScopeProvider> scopeProvider)
    {
        _category = category;
        _writer = writer;
        _scopeProvider = scopeProvider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
        _scopeProvider().Push(state);

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var properties = new Dictionary<string, object?>();
        _scopeProvider().ForEachScope((scope, target) => AddProperties(scope, target), properties);
        AddProperties(state, properties);

        var entry = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["level"] = logLevel.ToString().ToUpperInvariant(),
            ["logger"] = _category,
            ["message"] = formatter(state, exception),
        };
        if (properties.Count > 0) entry["properties"] = properties;
        if (exception is not null) entry["exception"] = exception.ToString();

        var json = JsonSerializer.Serialize(entry);
        lock (_writer)
        {
            _writer.WriteLine(json);
        }
    }

    private static void AddProperties(object? state, Dictionary<string, object?> properties)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> values) return;
        foreach (var (key, value) in values)
            if (key != "{OriginalFormat}") properties[key] = value;
    }
}
