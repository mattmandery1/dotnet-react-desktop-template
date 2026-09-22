using Dotnet10Template.Domain.Entities;

namespace Dotnet10Template.Application.Interfaces;

public interface IPersonRepository
{
    Task<IReadOnlyList<Person>> GetAllAsync(
        CancellationToken cancellationToken = default);
}