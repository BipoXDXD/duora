using System.Diagnostics;
using System.Globalization;
using System.Text;
using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Diagnóstico da #455 (PR de diagnóstico, nunca mesclado): middleware mais externo dos hosts do recorder. Para cada
/// resposta 5xx grava rota, endpoint, código do problem, <c>Retry-After</c>, a exceção tratada pelo
/// <c>UseExceptionHandler</c> (tipos da cadeia, SQLSTATE, <c>IsTransient</c>) e o caminho que gerou o 503: pool ou
/// conexão do Npgsql, 55P03/conta ocupada, outro SQLSTATE ou 503 sem exceção (<c>RegistrationAvailable</c> falso ou
/// elegibilidade sem banco).
/// </summary>
internal sealed class Diag455ResponseProbe(Diag455Recorder recorder, string source) : IStartupFilter
{
    private const int BodyPrefixBytes = 1024;

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(ObserveAsync);
        next(app);
    };

    private async Task ObserveAsync(HttpContext context, Func<Task> next)
    {
        var started = Stopwatch.GetTimestamp();
        var original = context.Response.Body;
        await using var tee = new PrefixTeeStream(original, BodyPrefixBytes);
        context.Response.Body = tee;
        try
        {
            await next();
        }
        finally
        {
            context.Response.Body = original;
        }

        if (context.Response.StatusCode < StatusCodes.Status500InternalServerError)
        {
            return;
        }

        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var line = string.Create(CultureInfo.InvariantCulture,
            $"status={context.Response.StatusCode} cause={Classify(exception, context)} " +
            $"{context.Request.Method} {context.Request.Path} endpoint=\"{context.GetEndpoint()?.DisplayName}\" " +
            $"elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} " +
            $"retryAfter={context.Response.Headers.RetryAfter} problem={ProblemCode(tee.Prefix)} " +
            $"exception={Describe(exception)}");
        recorder.RecordFailedResponse(source, line, context.Response.StatusCode);
    }

    private static string Classify(Exception? exception, HttpContext context)
    {
        if (exception is null)
        {
            // Sem exceção no UseExceptionHandler: o endpoint ou a autorização devolveu o 503 por conta própria.
            var legal = context.RequestServices.GetService<LegalPublication>();
            return legal is null
                ? "no-exception"
                : "no-exception(registrationAvailable=" + legal.RegistrationAvailable.ToString(CultureInfo.InvariantCulture) + ")";
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AccountBusyException:
                    return "account-busy";
                case PostgresException server:
                    return "postgres-sqlstate-" + server.SqlState;
                case NpgsqlException client when HasTimeout(client):
                    return "npgsql-timeout(transient=" + client.IsTransient.ToString(CultureInfo.InvariantCulture) + ")";
                case NpgsqlException client:
                    return "npgsql(transient=" + client.IsTransient.ToString(CultureInfo.InvariantCulture) + ")";
            }
        }

        return "other:" + exception.GetType().Name;
    }

    private static bool HasTimeout(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException || current.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string ProblemCode(byte[] prefix)
    {
        const string CodeField = "\"code\":\"";
        var body = Encoding.UTF8.GetString(prefix);
        var start = body.IndexOf(CodeField, StringComparison.Ordinal);
        if (start < 0)
        {
            return "n/a";
        }

        start += CodeField.Length;
        var end = body.IndexOf('"', start);
        return end < 0 ? "n/a" : body[start..end];
    }

    public static string Describe(Exception? exception)
    {
        if (exception is null)
        {
            return "none";
        }

        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var detail = current switch
            {
                PostgresException server => " sqlstate=" + server.SqlState,
                NpgsqlException client => " transient=" + client.IsTransient.ToString(CultureInfo.InvariantCulture),
                _ => "",
            };
            parts.Add(current.GetType().FullName + detail + ": " + current.Message.ReplaceLineEndings(" "));
        }
        return string.Join(" <- ", parts) + " | stack: " + exception.StackTrace?.ReplaceLineEndings(" / ");
    }

    /// <summary>Repassa tudo ao corpo original e guarda só o começo, para ler o código do problem.</summary>
    private sealed class PrefixTeeStream(Stream inner, int capacity) : Stream
    {
        private readonly MemoryStream _prefix = new();

        public byte[] Prefix => _prefix.ToArray();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Keep(buffer.AsSpan(offset, count));
            inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Keep(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private void Keep(ReadOnlySpan<byte> data)
        {
            var room = capacity - (int)_prefix.Length;
            if (room > 0)
            {
                _prefix.Write(data[..Math.Min(room, data.Length)]);
            }
        }

        // O corpo original pertence ao servidor: só o buffer local é liberado aqui.
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _prefix.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
