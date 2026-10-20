using LibraryLoans.Application.Abstractions;

namespace LibraryLoans.Api.Infrastructure;

public sealed class HttpRequestContext(IHttpContextAccessor accessor) : IRequestContext
{
    public const string ActorHeader = "X-Actor-Id";
    public const string Anonymous = "anonymous";

    public string CorrelationId =>
        accessor.HttpContext?.Items[CorrelationIdMiddleware.ItemKey] as string ?? Guid.NewGuid().ToString("N");

    // Sem autenticação nesta fase: o ator vem do header opcional, com a mesma sanitização do correlation id.
    public string ActorId
    {
        get
        {
            var value = accessor.HttpContext?.Request.Headers[ActorHeader].ToString();
            return value is { Length: > 0 and <= 100 } && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '@')
                ? value
                : Anonymous;
        }
    }
}
