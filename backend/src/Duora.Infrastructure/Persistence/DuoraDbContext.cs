using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Persistence;

/// <summary>
/// Contexto único do monólito (spec 3.3): transações que abrangem vários módulos ficam num só lugar.
/// Cada módulo mapeia suas tabelas em configurações próprias, com nomes físicos explícitos em snake_case.
/// </summary>
internal sealed class DuoraDbContext(DbContextOptions<DuoraDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DuoraDbContext).Assembly);
}
