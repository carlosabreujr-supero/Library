using LibraryLoans.Application.Exceptions;

namespace LibraryLoans.Application.Books;

internal static class BookValidation
{
    public const int MaxTitleLength = 300;
    public const int MaxAuthorLength = 200;
    public const int MaxCopies = 100_000;

    public static string? ValidateTitle(string? title) => Text(title, MaxTitleLength, "Título");

    public static string? ValidateAuthor(string? author) => Text(author, MaxAuthorLength, "Autor");

    public static string? ValidateCopies(int? copies) => copies switch
    {
        null => "Quantidade de exemplares é obrigatória.",
        < 0 => "Quantidade de exemplares não pode ser negativa.",
        > MaxCopies => $"Quantidade de exemplares não pode exceder {MaxCopies}.",
        _ => null
    };

    // Normaliza (remove hífens/espaços, caixa alta) e valida dígito verificador de ISBN-10/13.
    public static string? ValidateIsbn(string? isbn, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(isbn))
            return "ISBN é obrigatório.";

        normalized = new string(isbn.Where(c => c is not ('-' or ' ')).ToArray()).ToUpperInvariant();

        var valid = normalized.Length switch
        {
            13 => normalized.All(char.IsAsciiDigit) && Isbn13Checksum(normalized),
            10 => Isbn10Checksum(normalized),
            _ => false
        };
        return valid ? null : "ISBN inválido. Informe um ISBN-10 ou ISBN-13 válido.";
    }

    public static void ThrowIfAny(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
            throw new ValidationException(errors);
    }

    public static void Add(Dictionary<string, string[]> errors, string field, string? error)
    {
        if (error is not null)
            errors[field] = [error];
    }

    private static string? Text(string? value, int max, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return $"{label} é obrigatório.";
        return value.Trim().Length > max ? $"{label} não pode exceder {max} caracteres." : null;
    }

    private static bool Isbn13Checksum(string s)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (s[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (10 - sum % 10) % 10 == s[12] - '0';
    }

    private static bool Isbn10Checksum(string s)
    {
        var sum = 0;
        for (var i = 0; i < 10; i++)
        {
            int value;
            if (s[i] == 'X' && i == 9) value = 10;
            else if (char.IsAsciiDigit(s[i])) value = s[i] - '0';
            else return false;
            sum += value * (10 - i);
        }
        return sum % 11 == 0;
    }
}
