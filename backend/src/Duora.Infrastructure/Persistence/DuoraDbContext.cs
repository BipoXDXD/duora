using Duora.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Persistence;

/// <summary>
/// Contexto único do monólito (spec 3.3): transações que abrangem vários módulos ficam num só lugar.
/// Cada módulo mapeia suas tabelas em configurações próprias, com nomes físicos explícitos em snake_case.
/// Herda do IdentityDbContext para o cadastro gravar conta, aceites e outbox na mesma transação.
/// </summary>
internal sealed class DuoraDbContext(DbContextOptions<DuoraDbContext> options)
    : IdentityDbContext<DuoraUser, IdentityRole<Guid>, Guid>(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        IdentityModelConfiguration.Apply(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DuoraDbContext).Assembly);
    }
}
