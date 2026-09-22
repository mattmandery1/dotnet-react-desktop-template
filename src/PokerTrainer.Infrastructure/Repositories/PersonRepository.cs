using PokerTrainer.Application.Interfaces;
using PokerTrainer.Domain.Entities;
using PokerTrainer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace PokerTrainer.Infrastructure.Repositories;

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