using LibraryLoans.Application.Abstractions;
using LibraryLoans.Application.Exceptions;
using LibraryLoans.Domain.Entities;

namespace LibraryLoans.Application.Readers;

public sealed record ReaderDto(Guid Id, string Name, bool IsActive, DateTime CreatedAtUtc)
{
    public static ReaderDto From(Reader r) => new(r.Id, r.Name, r.IsActive, r.CreatedAtUtc);
}

public sealed record CreateReaderCommand(string? Name);

public sealed class CreateReaderHandler(IReaderRepository readers, IUnitOfWork unitOfWork, TimeProvider clock)
{
    public const int MaxNameLength = 200;

    public async Task<ReaderDto> HandleAsync(CreateReaderCommand command, CancellationToken ct)
    {
        var name = command.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            throw new ValidationException("name", "Nome é obrigatório.");
        if (name.Length > MaxNameLength)
            throw new ValidationException("name", $"Nome não pode exceder {MaxNameLength} caracteres.");

        var reader = new Reader
        {
            Id = Guid.NewGuid(),
            Name = name,
            IsActive = true,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime
        };

        readers.Add(reader);
        await unitOfWork.SaveChangesAsync(ct);
        return ReaderDto.From(reader);
    }
}
