using Duora.Infrastructure.Media;

namespace Duora.Worker.Media;

/// <summary>Do not consume the queue before the configured candidate profile is compatible.</summary>
internal sealed class MediaSanitizerReadiness(IServiceProvider provider, IsolatedPhotoSanitizer sanitizer) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => provider.MediaProcessingEnabled()
        ? sanitizer.EnsureReadyAsync(cancellationToken)
        : Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
