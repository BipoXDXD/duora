using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Duora.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

// Read-only configuration replay, never an application host start or a live managed-memory inspection.
internal static class Program
{
    private static string _stage = "capture";
    private const string Approved = "support@meetduora.com";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static int Main()
    {
        var output = Console.Out;
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        try
        {
            var capture = JsonSerializer.Deserialize<Capture>(Console.In.ReadToEnd(), Json)
                ?? throw new InvalidDataException();
            if (capture.Role is not ("web" or "worker")
                || !Path.IsPathFullyQualified(capture.ContentRoot)
                || !Path.IsPathFullyQualified(capture.ApplicationAssembly)
                || capture.Environment.Any(pair => !string.IsNullOrEmpty(pair.Value)
                    && (pair.Key.Equals("DOTNET_STARTUP_HOOKS", StringComparison.OrdinalIgnoreCase)
                        || pair.Key.Equals("ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", StringComparison.OrdinalIgnoreCase))))
            {
                output.WriteLine(JsonSerializer.Serialize(new { status = "unknown", reason = "unsupported_context" }));
                return 2;
            }

            _stage = "environment";
            // Only this helper process is affected. The capture is private stdin, never a file or log.
            foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>().ToArray())
            {
                Environment.SetEnvironmentVariable(key, null);
            }
            foreach (var pair in capture.Environment)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }

            // Omit copied application assemblies from the delivered probe: resolve against the actual read-only image.
            _stage = "application_assembly";
            var resolver = new AssemblyDependencyResolver(capture.ApplicationAssembly);
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                var path = resolver.ResolveAssemblyToPath(name);
                return path is null ? null : AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            };
            AssemblyLoadContext.Default.LoadFromAssemblyPath(capture.ApplicationAssembly);
            return Evaluate(capture, output);
        }
        catch (Exception)
        {
            // Never serialize exception messages, configuration, addresses, arguments or paths.
            output.WriteLine(JsonSerializer.Serialize(new { status = "unknown", reason = "configuration_replay_failed", stage = _stage }));
            return 2;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Evaluate(Capture capture, TextWriter output)
    {
        _stage = "host_defaults";
        ConfigurationManager configuration;
        IServiceCollection services;
        string environmentName;
        if (capture.Role == "web")
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = capture.Arguments,
                ApplicationName = "Duora.Web",
                ContentRootPath = capture.ContentRoot,
            });
            configuration = builder.Configuration;
            services = builder.Services;
            environmentName = builder.Environment.EnvironmentName;
        }
        else
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = capture.Arguments,
                ApplicationName = "Duora.Worker",
                ContentRootPath = capture.ContentRoot,
            });
            configuration = builder.Configuration;
            services = builder.Services;
            environmentName = builder.Environment.EnvironmentName;
        }
        if (!string.Equals(environmentName, Environments.Production, StringComparison.Ordinal))
        {
            output.WriteLine(JsonSerializer.Serialize(new { status = "unknown", reason = "production_profile_required" }));
            return 2;
        }

        _stage = "key_per_file";
        configuration.AddSecretFiles();
        _stage = "registrations";
        services.AddInfrastructure(configuration, capture.Role == "web" ? ConnectionShare.Web : ConnectionShare.Worker);
        using var provider = services.BuildServiceProvider();
        var configured = configuration[SupportContactOptions.Key];
        _stage = "ioptions";
        var value = provider.GetRequiredService<IOptions<SupportContactOptions>>().Value.Value;
        var winner = ((IConfigurationRoot)configuration).Providers.Reverse()
            .FirstOrDefault(candidate => candidate.TryGet(SupportContactOptions.Key, out _));
        var kind = winner?.GetType().Name switch
        {
            "KeyPerFileConfigurationProvider" => "key_per_file",
            "EnvironmentVariablesConfigurationProvider" => "environment",
            "CommandLineConfigurationProvider" => "command_line",
            "JsonConfigurationProvider" => "json",
            _ => "other",
        };
        output.WriteLine(JsonSerializer.Serialize(new
        {
            status = "replayed",
            configuration_matches_approved = string.Equals(configured, Approved, StringComparison.Ordinal),
            ioptions_matches_approved = string.Equals(value, Approved, StringComparison.Ordinal),
            source = kind,
            cached_runtime_options_observed = false,
            infrastructure_module_id = typeof(InfrastructureServiceCollectionExtensions).Assembly.ManifestModule.ModuleVersionId,
        }));
        ((IDisposable)configuration).Dispose();
        return 0;
    }

    private sealed record Capture(
        string Role,
        string ApplicationAssembly,
        string ContentRoot,
        Dictionary<string, string?> Environment,
        string[] Arguments);
}
