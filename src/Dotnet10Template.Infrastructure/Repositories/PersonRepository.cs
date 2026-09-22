using Dotnet10Template.Application.Interfaces;
using Dotnet10Template.Domain.Entities;
using Dotnet10Template.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Dotnet10Template.Infrastructure.Repositories;

public sealed class PersonRepository(
    AppDbContext dbContext) : IPersonRepository
{
    public async Task<IReadOnlyList<Person>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        return await dbContext.People
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
    }
}