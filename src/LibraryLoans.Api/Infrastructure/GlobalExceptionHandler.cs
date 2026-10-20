using LibraryLoans.Application.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace LibraryLoans.Api.Infrastructure;

public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // Cliente desconectou: não é falha do servidor.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation("Requisição cancelada pelo cliente.");
            httpContext.Response.StatusCode = 499;
            return true;
        }

        var (status, title, detail) = exception switch
        {
            ValidationException => (StatusCodes.Status400BadRequest, "Requisição inválida.", exception.Message),
            NotFoundException => (StatusCodes.Status404NotFound, "Recurso não encontrado.", exception.Message),
            ConflictException => (StatusCodes.Status409Conflict, "Conflito de regra de negócio.", exception.Message),
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "Requisição inválida.", "Corpo ou parâmetros malformados."),
            _ => (StatusCodes.Status500InternalServerError, "Erro interno.", "Ocorreu um erro inesperado.")
        };

        if (status >= 500)
            logger.LogError(exception, "Exceção não tratada.");
        else
            logger.LogWarning("Requisição rejeitada ({Status}): {Message}", status, exception.Message);

        httpContext.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };

        if (exception is ValidationException validation)
            problem.Extensions["errors"] = validation.Errors;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });
    }
}
