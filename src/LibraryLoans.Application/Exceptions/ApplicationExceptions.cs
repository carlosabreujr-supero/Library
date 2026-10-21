namespace LibraryLoans.Application.Exceptions;

// Exceções de aplicação independentes de HTTP; a camada de API as traduz para status codes.
public abstract class ApplicationException(string message) : Exception(message);

// Reason: código curto e de cardinalidade limitada (ex.: "unavailable"), usado em métricas e logs.
public sealed class NotFoundException(string message, string? reason = null) : ApplicationException(message)
{
    public string? Reason { get; } = reason;
}

public sealed class ConflictException(string message, string? reason = null) : ApplicationException(message)
{
    public string? Reason { get; } = reason;
}

public sealed class ValidationException : ApplicationException
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public ValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("Um ou mais erros de validação ocorreram.") => Errors = errors;

    public ValidationException(string field, string error)
        : this(new Dictionary<string, string[]> { [field] = [error] }) { }
}
