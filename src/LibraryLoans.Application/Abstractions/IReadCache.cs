namespace LibraryLoans.Application.Abstractions;

// Cache de leitura (informativo). Nunca é fonte autoritativa: não deve ser usado para validar ou confirmar empréstimos.
public interface IReadCache
{
    // Retorna o valor em cache; em miss, indisponibilidade ou erro do cache, executa a fábrica (PostgreSQL).
    // Exceções da fábrica sempre propagam e nunca são armazenadas.
    Task<T> GetOrCreateAsync<T>(string area, string key, Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken);

    // Invalida todas as entradas das áreas informadas. Nunca lança: falhas do cache não afetam a operação de negócio.
    Task InvalidateAsync(params string[] areas);
}

public static class CacheAreas
{
    public const string BookList = "books:list";

    public static string Availability(Guid bookId) => $"availability:{bookId:N}";

    // Áreas afetadas por qualquer mudança em um livro (dados, quantidades ou empréstimos).
    public static string[] ForBook(Guid bookId) => [BookList, Availability(bookId)];
}

// Implementação sem cache: usada quando o cache está desabilitado e em testes unitários.
public sealed class NoReadCache : IReadCache
{
    public Task<T> GetOrCreateAsync<T>(string area, string key, Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken) =>
        factory(cancellationToken);

    public Task InvalidateAsync(params string[] areas) => Task.CompletedTask;
}
