using Duora.Core.Profiles;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Duora.Infrastructure.Profiles;

internal sealed class CountryRecord
{
    public string Code { get; set; } = null!;
}

internal sealed class SubdivisionRecord
{
    public string Code { get; set; } = null!;
    public string CountryCode { get; set; } = null!;
}

internal sealed class CountryRecordConfiguration : IEntityTypeConfiguration<CountryRecord>
{
    public void Configure(EntityTypeBuilder<CountryRecord> builder)
    {
        builder.ToTable("countries", DatabaseSchemas.Profiles);
        builder.HasKey(p => p.Code).HasName("pk_countries");
        builder.Property(p => p.Code).HasColumnName("code");
        builder.HasData(ProfileLocationCatalog.Countries.Select(code => new CountryRecord { Code = code }));
    }
}

internal sealed class SubdivisionRecordConfiguration : IEntityTypeConfiguration<SubdivisionRecord>
{
    public void Configure(EntityTypeBuilder<SubdivisionRecord> builder)
    {
        builder.ToTable("subdivisions", DatabaseSchemas.Profiles);
        builder.HasKey(p => p.Code).HasName("pk_subdivisions");
        builder.Property(p => p.Code).HasColumnName("code");
        builder.Property(p => p.CountryCode).HasColumnName("country_code");
        builder.HasAlternateKey(p => new { p.CountryCode, p.Code }).HasName("ak_subdivisions_country_code");
        builder.HasOne<CountryRecord>().WithMany().HasForeignKey(p => p.CountryCode)
            .HasConstraintName("fk_subdivisions_countries").OnDelete(DeleteBehavior.Restrict);
        builder.HasData(ProfileLocationCatalog.Subdivisions.Select(location => new SubdivisionRecord { Code = location.SubdivisionCode!, CountryCode = location.CountryCode! }));
    }
}
