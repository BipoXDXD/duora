using System.Globalization;
using System.Security.Cryptography;
using Duora.Infrastructure.Safety;
using Microsoft.AspNetCore.DataProtection;

namespace Duora.Web.Safety;

/// <summary>Opaque continuation bound to the authenticated owner and an isolated Data Protection purpose.</summary>
internal static class OwnerPhotoCursor
{
    private const string Purpose = "Duora.PhotoReview.OwnerPhotoCursor.v1";
    private const int MaxLength = 512;

    internal static string? Write(IDataProtectionProvider protection, Guid owner, ReviewPhotoCursor? cursor) =>
        cursor is { } value ? protection.CreateProtector(Purpose, owner.ToString("N")).Protect(Format(value)) : null;

    internal static bool TryRead(IDataProtectionProvider protection, Guid owner, string? token, out ReviewPhotoCursor? cursor)
    {
        cursor = null;
        if (token is null)
        {
            return true;
        }
        if (token.Length is < 1 or > MaxLength)
        {
            return false;
        }
        string plain;
        try
        {
            plain = protection.CreateProtector(Purpose, owner.ToString("N")).Unprotect(token);
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            return false;
        }
        var parts = plain.Split('.');
        if (plain.Length > 64 || parts.Length != 3 || parts[0] != "1"
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks <= 0 || ticks > DateTimeOffset.MaxValue.UtcTicks
            || !Guid.TryParseExact(parts[2], "N", out var id) || id == Guid.Empty)
        {
            return false;
        }
        var value = new ReviewPhotoCursor(new(ticks, TimeSpan.Zero), id);
        if (Format(value) != plain)
        {
            return false;
        }
        cursor = value;
        return true;
    }

    private static string Format(ReviewPhotoCursor cursor) =>
        $"1.{cursor.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}.{cursor.PhotoId:N}";
}
