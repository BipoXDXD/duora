using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Duora.Infrastructure.Persistence;

/// <summary>
/// Usado só pelo dotnet-ef. Gerar migration não abre conexão; para aplicar, passe <c>--connection</c>
/// ao <c>database update</c> ou ao bundle. Assim nenhuma credencial fica no repositório.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DuoraDbContext>
{
    private const string PlaceholderConnectionString = "Host=localhost;Database=duora";

    public DuoraDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<DuoraDbContext>();
        builder.UseDuoraNpgsql(PlaceholderConnectionString);
        return new DuoraDbContext(builder.Options);
    }
}
