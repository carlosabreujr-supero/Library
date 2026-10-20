namespace LibraryLoans.Application.Exceptions;

// Exceções de aplicação independentes de HTTP; a camada de API as traduz para status codes.
public abstract class ApplicationException(string message) : Exception(message);

public sealed class NotFoundException(string message) : ApplicationException(message);

public sealed class ConflictException(string message) : ApplicationException(message);

public sealed class ValidationException : ApplicationException
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public ValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("Um ou mais erros de validação ocorreram.") => Errors = errors;

    public ValidationException(string field, string error)
        : this(new Dictionary<string, string[]> { [field] = [error] }) { }
}
