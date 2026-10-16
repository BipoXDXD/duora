using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Duora.Core.Identity;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Identity;

namespace Duora.Web.Admin;

/// <summary>Argumentos já convertidos de <c>admin grant</c>; <paramref name="Reissue"/> pede a reemissão.</summary>
internal sealed record AdminGrantArguments(EmailAddress Email, string Role, OperatorName Operator, bool Reissue);

/// <summary>
/// Comando operacional do bootstrap administrativo (spec 11.1; auth.md, D4), fora da API pública: o operador o roda
/// no host, dentro do container do web em execução, com a configuração, o banco e o key ring dele
/// (<c>docker compose exec -T web dotnet Duora.Web.dll admin grant ...</c>, docs/operations/deploy.md; o
/// <c>exec</c> não passa a saída ao log driver do Docker). Cria a conta privilegiada, ou com <c>--reissue</c> emite
/// outra senha para ela, e mostra a senha temporária uma única vez, na saída padrão; ela não vai a log, arquivo nem
/// banco (lá fica só o hash), nem entra por argumento ou variável de ambiente.
/// </summary>
internal static class AdminCommand
{
    public const int RefusedExitCode = 1;
    public const int UsageExitCode = 2;

    /// <summary>Início da linha da saída que traz a senha, para quem a copia (ou um script) achá-la sem ambiguidade.</summary>
    public const string PasswordLinePrefix = "Senha temporária: ";

    private const string Verb = "admin";
    private const string GrantVerb = "grant";
    private const string ReissueFlag = "--reissue";

    private static readonly string Usage =
        $"Uso: duora-web admin grant --email <e-mail> --role <{string.Join('|', AccountRoles.Privileged)}> --operator <usuário> [--reissue]";

    /// <summary>
    /// Com o comando, o host fica quieto: só avisos e erros vão para o log, e a saída padrão é do operador. Nenhum
    /// nível de log carrega a senha; isto só evita misturar a linha dela com o log do EF.
    /// </summary>
    private static readonly string[] QuietHostArguments =
    [
        "--Logging:LogLevel:Default=Warning",
        "--Logging:LogLevel:Microsoft=Warning",
    ];

    public static bool IsInvocation(IReadOnlyList<string> args) => args.Count > 0 && args[0] == Verb;

    /// <summary>Ponto de entrada do processo: valida os argumentos antes de montar o host, que exige configuração.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (!TryParse(args, out var arguments, out var problem))
        {
            return WriteUsage(Console.Error, problem);
        }

        await using var app = DuoraWebApp.Build(QuietHostArguments);
        return await IssueAsync(app.Services, arguments, Console.Out, Console.Error, CancellationToken.None);
    }

    /// <summary>O comando inteiro sobre um host já montado; os testes de integração entram por aqui.</summary>
    public static async Task<int> ExecuteAsync(
        IServiceProvider services,
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(error);
        return TryParse(args, out var arguments, out var problem)
            ? await IssueAsync(services, arguments, output, error, cancellationToken)
            : WriteUsage(error, problem);
    }

    private static async Task<int> IssueAsync(
        IServiceProvider services,
        AdminGrantArguments arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var bootstrap = scope.ServiceProvider.GetRequiredService<AdministratorBootstrap>();
        var (email, role, operatorName, reissue) = arguments;
        var credential = reissue
            ? await bootstrap.ReissueAsync(email, role, operatorName, cancellationToken)
            : await bootstrap.GrantAsync(email, role, operatorName, cancellationToken);
        switch (credential)
        {
            case AdministratorCredential.Issued issued:
                await WriteIssuedAsync(output, arguments, issued);
                return 0;
            case AdministratorCredential.Refused:
                await error.WriteLineAsync(RefusalMessage(arguments));
                return RefusedExitCode;
            default:
                throw new InvalidOperationException("Resultado do bootstrap não mapeado.");
        }
    }

    private static string RefusalMessage(AdminGrantArguments arguments) =>
        arguments.Reissue
            ? $"Recusado: {arguments.Email} não é uma conta {arguments.Role} ativa sem MFA. Nada mudou; {ReissueFlag} não "
              + "cria conta nem promove participante, e conta com MFA ativo segue o procedimento de recuperação (TM-22)."
            : $"Recusado: já existe conta com {arguments.Email}. Nada mudou; o comando não promove conta existente (D13). "
              + $"Se ela é a conta {arguments.Role} deste bootstrap e ainda não ativou o MFA, rode de novo com {ReissueFlag}.";

    private static async Task WriteIssuedAsync(
        TextWriter output,
        AdminGrantArguments arguments,
        AdministratorCredential.Issued issued)
    {
        var what = arguments.Reissue
            ? "Senha temporária reemitida para a conta (sessões encerradas, titular avisado por e-mail):"
            : "Conta privilegiada criada:";
        var expiresAt = issued.ExpiresAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        await output.WriteLineAsync($"{what} {issued.UserId:D} ({arguments.Email}, {arguments.Role}).");
        await output.WriteLineAsync($"{PasswordLinePrefix}{issued.TemporaryPassword}");
        await output.WriteLineAsync(
            $"Ela vale até {expiresAt} e aparece só agora: o Duora guarda apenas o hash. No primeiro login, a única ação "
            + "possível é trocá-la; depois, a sessão pede o cadastro do MFA.");
    }

    private static int WriteUsage(TextWriter error, string problem)
    {
        error.WriteLine(problem);
        error.WriteLine(Usage);
        return UsageExitCode;
    }

    /// <summary>
    /// <c>admin grant</c> com <c>--email</c>, <c>--role</c> e <c>--operator</c>, cada um exatamente uma vez, mais o
    /// <c>--reissue</c> opcional, sem valor, e nada além. O papel é o nome exato do catálogo e precisa ser
    /// privilegiado; o operador, um nome de usuário de sistema.
    /// </summary>
    private static bool TryParse(
        IReadOnlyList<string> args,
        [NotNullWhen(true)] out AdminGrantArguments? arguments,
        out string problem)
    {
        arguments = null;
        if (args.Count < 2 || args[0] != Verb || args[1] != GrantVerb)
        {
            problem = "Comando desconhecido.";
            return false;
        }

        if (!TryReadOptions([.. args.Skip(2)], out var options, out var reissue, out problem))
        {
            return false;
        }

        if (!EmailAddress.TryParse(options.GetValueOrDefault("--email"), out var email))
        {
            problem = "--email precisa ser um endereço de e-mail válido.";
            return false;
        }

        if (options.GetValueOrDefault("--role") is not { } role || !AccountRoles.IsPrivileged(role))
        {
            problem = $"--role precisa ser um papel privilegiado: {string.Join(", ", AccountRoles.Privileged)}.";
            return false;
        }

        if (!OperatorName.TryParse(options.GetValueOrDefault("--operator"), out var operatorName))
        {
            problem = "--operator é obrigatório: o seu usuário no host (minúsculas, dígitos, '.', '_' ou '-').";
            return false;
        }

        arguments = new AdminGrantArguments(email, role, operatorName, reissue);
        problem = "";
        return true;
    }

    private static bool TryReadOptions(
        IReadOnlyList<string> rest,
        out Dictionary<string, string> options,
        out bool reissue,
        out string problem)
    {
        string[] known = ["--email", "--role", "--operator"];
        var read = new Dictionary<string, string>(StringComparer.Ordinal);
        options = read;
        reissue = false;
        var index = 0;
        while (index < rest.Count)
        {
            var name = rest[index];
            if (name == ReissueFlag)
            {
                if (reissue)
                {
                    problem = $"{name} repetido.";
                    return false;
                }

                reissue = true;
                index++;
                continue;
            }

            if (!known.Contains(name, StringComparer.Ordinal) || index + 1 >= rest.Count)
            {
                problem = $"Argumento inesperado ou sem valor: {name}.";
                return false;
            }

            if (!read.TryAdd(name, rest[index + 1]))
            {
                problem = $"{name} repetido.";
                return false;
            }

            index += 2;
        }

        var missing = known.FirstOrDefault(name => !read.ContainsKey(name));
        problem = missing is null ? "" : $"{missing} é obrigatório.";
        return missing is null;
    }
}
