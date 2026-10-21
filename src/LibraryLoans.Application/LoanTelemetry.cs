using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace LibraryLoans.Application;

// Instrumentos OpenTelemetry-compatíveis (System.Diagnostics). Sem IDs de negócio em tags de métricas:
// apenas valores de cardinalidade limitada (outcome/reason). IDs ficam em spans e logs.
public static class LoanTelemetry
{
    public const string Name = "LibraryLoans";

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> LoansCreated =
        Meter.CreateCounter<long>("library.loans.created", description: "Empréstimos criados.");

    public static readonly Counter<long> LoansRejected =
        Meter.CreateCounter<long>("library.loans.rejected", description: "Empréstimos rejeitados, por motivo.");

    public static readonly Counter<long> IdempotentReplays =
        Meter.CreateCounter<long>("library.loans.idempotent_replays", description: "Repetições idempotentes de POST /loans.");

    public static readonly Counter<long> LoansClosed =
        Meter.CreateCounter<long>("library.loans.closed", description: "Empréstimos devolvidos ou cancelados.");

    public static readonly Histogram<double> CreateLoanDuration =
        Meter.CreateHistogram<double>("library.loans.create.duration", unit: "ms", description: "Latência do caso de uso CreateLoan.");

    public static class Reasons
    {
        public const string Unavailable = "unavailable";
        public const string BookInactive = "book_inactive";
        public const string ReaderInactive = "reader_inactive";
        public const string NotFound = "not_found";
        public const string KeyReuse = "idempotency_key_reuse";
        public const string KeyInProgress = "idempotency_in_progress";
        public const string Validation = "validation";
        public const string Other = "other";
    }
}
