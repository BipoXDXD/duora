namespace Duora.Infrastructure;

internal sealed class DatabaseOptions
{
    public const string ConnectionStringName = "Duora";

    public string ConnectionString { get; set; } = "";
}
