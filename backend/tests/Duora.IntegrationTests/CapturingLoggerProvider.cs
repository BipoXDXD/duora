using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Duora.IntegrationTests;

public sealed record CapturedLog(string Category, LogLevel Level, string Text, Exception? Exception);

/// <summary>Guarda tudo o que a aplicação loga, com a mensagem formatada e a exceção, para os testes inspecionarem.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();
    private readonly TaskCompletionSource<CapturedLog> _firstWarning =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CapturedLog>> _firstWarningByCategory = new();

    public IReadOnlyCollection<CapturedLog> Entries => _entries;

    /// <summary>Completa no primeiro log de nível Warning ou acima.</summary>
    public Task<CapturedLog> FirstWarning => _firstWarning.Task;

    /// <summary>Completa no primeiro log de nível Warning ou acima da categoria indicada.</summary>
    public Task<CapturedLog> FirstWarningFrom(string category) => WarningSource(category).Task;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

    public void Dispose()
    {
    }

    private void Add(CapturedLog entry)
    {
        _entries.Enqueue(entry);
        if (entry.Level >= LogLevel.Warning)
        {
            _firstWarning.TrySetResult(entry);
            WarningSource(entry.Category).TrySetResult(entry);
        }
    }

    private TaskCompletionSource<CapturedLog> WarningSource(string category) =>
        _firstWarningByCategory.GetOrAdd(category, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    private sealed class CapturingLogger(string category, CapturingLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception) + Environment.NewLine + exception;
            provider.Add(new CapturedLog(category, logLevel, text, exception));
        }
    }
}
