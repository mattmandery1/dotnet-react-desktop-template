using Dotnet10Template.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dotnet10Template.Infrastructure.Data.Configurations;

public sealed class PersonConfiguration : IEntityTypeConfiguration<Person>
{
    public void Configure(EntityTypeBuilder<Person> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name)
            .HasMaxLength(100)
            .IsRequired();

        builder.HasData(
            new Person { Id = 1, Name = "Matt" },
            new Person { Id = 2, Name = "Tony" },
            new Person { Id = 3, Name = "Bob" }
        );
    }
}