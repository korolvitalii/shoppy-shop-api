using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShoppyShop.Application;

/// <summary>
/// A keyset ("seek") pagination cursor: the sort key of the last row of the page just served.
/// The next page is then <c>WHERE (key, id) > (@key, @id) ORDER BY key, id LIMIT n</c>, which an
/// index can seek straight to — unlike <c>OFFSET</c>, which makes the database walk and discard
/// every preceding row, so page 40,000 costs the same as page 1.
/// </summary>
/// <param name="Sort">The <see cref="ProductSorts"/> value the cursor was produced under.</param>
/// <param name="Key">The leading sort key of the last row, formatted invariantly.</param>
/// <param name="Id">The last row's id, which breaks ties on <paramref name="Key"/>.</param>
public sealed record ProductCursor(
    [property: JsonPropertyName("s")] string Sort,
    [property: JsonPropertyName("k")] string Key,
    [property: JsonPropertyName("i")] string Id)
{
    public string Encode()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(this);
        return Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Decodes a client-supplied cursor, or returns <c>null</c> when there is none (the first page).
    /// </summary>
    /// <exception cref="AppValidationException">
    /// The cursor is malformed, or was issued for a different sort. Replaying a <c>name</c> cursor
    /// against a <c>price-asc</c> query would compare a name against a price and silently return the
    /// wrong window, so a mismatch is rejected rather than guessed at.
    /// </exception>
    public static ProductCursor? Decode(string? value, string expectedSort)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cursor = TryDecode(value) ?? throw Invalid("Pagination cursor is not valid.");
        return cursor.Sort == expectedSort
            ? cursor
            : throw Invalid("Pagination cursor does not match the requested sort order.");
    }

    private static ProductCursor? TryDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '=');
        var buffer = new byte[padded.Length];
        if (!Convert.TryFromBase64String(padded, buffer, out var written))
        {
            return null;
        }

        try
        {
            var cursor = JsonSerializer.Deserialize<ProductCursor>(Encoding.UTF8.GetString(buffer, 0, written));
            return cursor is null || string.IsNullOrEmpty(cursor.Sort) || string.IsNullOrEmpty(cursor.Id)
                ? null
                : cursor;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AppValidationException Invalid(string message) =>
        new(message, new Dictionary<string, string[]> { ["cursor"] = [message] });
}