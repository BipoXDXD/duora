using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Operations;

namespace Duora.Web.Admin;

/// <summary>Host-only operator command, with the same access boundary as admin grant; never a public endpoint.</summary>
internal static class RetentionCommand
{
    public static bool IsInvocation(IReadOnlyList<string> args) => args.Count > 0 && args[0] == "retention";

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (!TryParse(args, out _, out _, out _, out _))
        {
            return await Usage(Console.Error);
        }
        await using var app = DuoraWebApp.Build(["--Logging:LogLevel:Default=Warning", "--Logging:LogLevel:Microsoft=Warning"]);
        return await ExecuteAsync(app.Services, args, Console.Out, Console.Error, CancellationToken.None);
    }

    public static async Task<int> ExecuteAsync(IServiceProvider services, IReadOnlyList<string> args,
        TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (!TryParse(args, out var resume, out var id, out var actor, out var resolution))
        {
            return await Usage(error);
        }
        var service = services.GetRequiredService<RetentionService>();
        var changed = resume
            ? await service.ResumeAsync(actor!, ct)
            : await service.ReviewFailureAsync(id, actor!, resolution, ct);
        await output.WriteLineAsync(changed ? "Revisão registrada." : "Nada alterado: estado incompatível ou revisão já registrada.");
        return changed ? 0 : 1;
    }

    private static bool TryParse(IReadOnlyList<string> args, out bool resume, out Guid id,
        out OperatorName? actor, out FailureResolution resolution)
    {
        resume = false;
        id = default;
        actor = null;
        resolution = default;
        if (args.Count == 4 && args[0] == "retention" && args[1] == "resume" && args[2] == "--operator")
        {
            resume = true;
            return OperatorName.TryParse(args[3], out actor);
        }
        if (args.Count != 8 || args[0] != "retention" || args[1] != "review-delivery" || args[2] != "--id"
            || args[4] != "--operator" || args[6] != "--resolution" || !Guid.TryParse(args[3], out id)
            || !OperatorName.TryParse(args[5], out actor))
        {
            return false;
        }
        resolution = args[7] == "accepted-loss" ? FailureResolution.AcceptedLoss : FailureResolution.Resolved;
        return args[7] is "resolved" or "accepted-loss";
    }

    private static async Task<int> Usage(TextWriter error)
    {
        await error.WriteLineAsync("Uso: retention review-delivery --id <uuid> --operator <nome> --resolution <resolved|accepted-loss>; ou retention resume --operator <nome>");
        return 2;
    }
}
