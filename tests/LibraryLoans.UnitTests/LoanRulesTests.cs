using LibraryLoans.Application.Books;
using LibraryLoans.Application.Exceptions;
using LibraryLoans.Application.Loans;
using LibraryLoans.Domain.Entities;

namespace LibraryLoans.UnitTests;

public class LoanRulesTests
{
    private readonly FakeBooks _books = new();
    private readonly FakeReaders _readers = new();
    private readonly FakeLoans _loans = new();
    private readonly FakeIdempotency _idempotency = new();
    private readonly FakeAudit _audit = new();
    private readonly FakeUnitOfWork _uow = new();
    private readonly TimeProvider _clock = TimeProvider.System;

    private CreateLoanHandler Create() =>
        new(_books, _readers, _loans, _idempotency, _audit, _uow, new LoanSettings { DefaultLoanDays = 14 }, _clock);

    private CloseLoanService Close() => new(_books, _loans, _audit, _uow, _clock);

    private Task<CreateLoanResult> Borrow(Book b, Reader r, string key = "k1") =>
        Create().HandleAsync(new CreateLoanCommand(b.Id, r.Id, key), CancellationToken.None);

    [Fact]
    public async Task Inactive_book_cannot_be_borrowed()
    {
        var book = _books.Seed(active: false);
        var reader = _readers.Seed();

        await Assert.ThrowsAsync<ConflictException>(() => Borrow(book, reader));

        Assert.Equal(1, book.AvailableCopies);
        Assert.Empty(_loans.Items);
    }

    [Fact]
    public async Task Inactive_reader_cannot_borrow()
    {
        var book = _books.Seed();
        var reader = _readers.Seed(active: false);

        await Assert.ThrowsAsync<ConflictException>(() => Borrow(book, reader));

        Assert.Equal(1, book.AvailableCopies);
        Assert.Empty(_loans.Items);
    }

    [Fact]
    public async Task Insufficient_availability_is_rejected()
    {
        var book = _books.Seed(total: 1, available: 0);
        var reader = _readers.Seed();

        await Assert.ThrowsAsync<ConflictException>(() => Borrow(book, reader));

        Assert.Equal(0, book.AvailableCopies);
        Assert.Empty(_loans.Items);
        Assert.Empty(_audit.Events);
    }

    [Fact]
    public async Task Successful_loan_reserves_one_copy_and_audits()
    {
        var book = _books.Seed(total: 2);
        var reader = _readers.Seed();

        var result = await Borrow(book, reader);

        Assert.Equal(201, result.StatusCode);
        Assert.Equal(1, book.AvailableCopies);
        var loan = Assert.Single(_loans.Items.Values);
        Assert.Equal(TimeSpan.FromDays(14), loan.DueAtUtc - loan.BorrowedAtUtc);
        Assert.Single(_audit.Events, e => e.Action == "LoanCreated");
    }

    [Fact]
    public async Task Same_key_and_payload_replays_without_decrementing_again()
    {
        var book = _books.Seed(total: 2);
        var reader = _readers.Seed();

        var first = await Borrow(book, reader);
        var second = await Borrow(book, reader);

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.Body, second.Body);
        Assert.Equal(1, book.AvailableCopies);
        Assert.Single(_loans.Items);
    }

    [Fact]
    public async Task Same_key_with_different_payload_is_a_conflict()
    {
        var book = _books.Seed(total: 2);
        var reader = _readers.Seed();
        await Borrow(book, reader);

        await Assert.ThrowsAsync<ConflictException>(() => Borrow(book, _readers.Seed()));
        Assert.Equal(1, book.AvailableCopies);
    }

    [Fact]
    public async Task Missing_idempotency_key_is_a_validation_error()
    {
        var book = _books.Seed();
        var reader = _readers.Seed();

        await Assert.ThrowsAsync<ValidationException>(() => Borrow(book, reader, key: ""));
    }

    [Theory]
    [InlineData(LoanStatus.Returned)]
    [InlineData(LoanStatus.Cancelled)]
    public async Task Closing_a_non_active_loan_is_rejected_and_stock_unchanged(LoanStatus current)
    {
        var book = _books.Seed(total: 2, available: 1);
        var loan = _loans.Seed(book.Id, current);

        await Assert.ThrowsAsync<ConflictException>(() =>
            Close().CloseAsync(loan.Id, LoanStatus.Returned, "LoanReturned", CancellationToken.None));
        await Assert.ThrowsAsync<ConflictException>(() =>
            Close().CloseAsync(loan.Id, LoanStatus.Cancelled, "LoanCancelled", CancellationToken.None));

        Assert.Equal(current, loan.Status);
        Assert.Equal(1, book.AvailableCopies);
        Assert.Empty(_audit.Events);
    }

    [Fact]
    public async Task Closing_an_unknown_loan_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Close().CloseAsync(Guid.NewGuid(), LoanStatus.Returned, "LoanReturned", CancellationToken.None));
    }

    [Theory]
    [InlineData(LoanStatus.Returned, "LoanReturned")]
    [InlineData(LoanStatus.Cancelled, "LoanCancelled")]
    public async Task Closing_an_active_loan_restores_stock_once(LoanStatus target, string action)
    {
        var book = _books.Seed(total: 1, available: 0);
        var loan = _loans.Seed(book.Id);

        var dto = await Close().CloseAsync(loan.Id, target, action, CancellationToken.None);
        await Assert.ThrowsAsync<ConflictException>(() => Close().CloseAsync(loan.Id, target, action, CancellationToken.None));

        Assert.Equal(target.ToString(), dto.Status);
        Assert.Equal(1, book.AvailableCopies);
        Assert.Single(_audit.Events, e => e.Action == action);
    }

    [Fact]
    public async Task Total_copies_cannot_drop_below_borrowed_quantity()
    {
        var book = _books.Seed(total: 5, available: 2); // 3 emprestados
        var handler = new UpdateBookHandler(_books, _audit, _uow, _clock);

        await Assert.ThrowsAsync<ConflictException>(() =>
            handler.HandleAsync(new UpdateBookCommand(book.Id, null, null, null, 2), CancellationToken.None));

        Assert.Equal(5, book.TotalCopies);
        Assert.Equal(2, book.AvailableCopies);
    }

    [Fact]
    public async Task Total_copies_equal_to_borrowed_quantity_is_accepted()
    {
        var book = _books.Seed(total: 5, available: 2);
        var handler = new UpdateBookHandler(_books, _audit, _uow, _clock);

        var dto = await handler.HandleAsync(new UpdateBookCommand(book.Id, null, null, null, 3), CancellationToken.None);

        Assert.Equal(3, dto.TotalCopies);
        Assert.Equal(0, dto.AvailableCopies);
    }
}
