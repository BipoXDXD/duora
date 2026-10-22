using System.Diagnostics;
using Duora.MediaProtocol;
using Duora.Worker.Media;

namespace Duora.MediaSanitizer;

/// <summary>One synthetic candidate invocation over stdin/stdout. Installation and containment belong to S2.</summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 0)
        {
            return 64;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(ProtocolBudget.Candidate.DeadlineMilliseconds));
        var elapsed = Stopwatch.StartNew();
        try
        {
            await using var input = Console.OpenStandardInput();
            await using var output = Console.OpenStandardOutput();
            var request = await SanitizerProtocol.ReadRequestAsync(input, timeout.Token);
            var remaining = TimeSpan.FromMilliseconds(request.Budget.DeadlineMilliseconds) - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return 75;
            }
            timeout.CancelAfter(remaining);
            var sanitizer = new ImageSharpPhotoSanitizer(new PhotoSanitizerOptions());
            var outcome = await sanitizer.SanitizeAsync(request.Format, request.Content, timeout.Token);
            await SanitizerProtocol.WriteResponseAsync(output, request, outcome, timeout.Token);
            return 0;
        }
        catch (IOException)
        {
            // No bytes, paths or exception details on stderr. No response is accepted without confirmed exit 0.
            return 65;
        }
        catch (OperationCanceledException)
        {
            return 75;
        }
    }
}
