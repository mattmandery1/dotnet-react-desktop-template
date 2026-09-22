using PokerTrainer.Domain.Entities;

namespace PokerTrainer.Application.Interfaces;

public interface IPersonRepository
{
    Task<IReadOnlyList<Person>> GetAllAsync(
        CancellationToken cancellationToken = default);
}