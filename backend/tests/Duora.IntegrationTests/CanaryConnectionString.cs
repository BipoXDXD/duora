namespace Duora.IntegrationTests;

/// <summary>Connection string para um banco que recusa conexão, com uma senha canário única por teste.</summary>
internal sealed class CanaryConnectionString
{
    public string Password { get; } = $"CANARY-{Guid.NewGuid():N}";

    public string Value => $"Host=127.0.0.1;Port=1;Database=duora;Username=duora;Password={Password}";
}
