using System;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Extensions;

namespace Jellyfin.Server.Implementations.Search;

/// <summary>
/// Normalizes user-entered and persisted search terms consistently.
/// </summary>
public static partial class SearchTermNormalizer
{
    /// <summary>
    /// Gets the maximum persisted search-history term length.
    /// </summary>
    public const int MaxHistoryTermLength = 255;

    /// <summary>
    /// Normalizes a term for display and persistence without truncating it.
    /// </summary>
    /// <param name="value">The search term.</param>
    /// <returns>The trimmed term with collapsed whitespace.</returns>
    public static string NormalizeForDisplay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return WhitespaceRegex().Replace(value.Trim(), " ");
    }

    /// <summary>
    /// Normalizes a term for separator-aware matching.
    /// </summary>
    /// <param name="value">The search term.</param>
    /// <returns>The lowercase, diacritic-free term with collapsed separators.</returns>
    public static string NormalizeForSearch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var noDiacritics = value.RemoveDiacritics();
        var normalized = new StringBuilder(noDiacritics.Length);
        var previousWasSeparator = false;

        foreach (var character in noDiacritics)
        {
            if (char.IsLetterOrDigit(character))
            {
                normalized.Append(char.ToLowerInvariant(character));
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator && normalized.Length > 0)
            {
                normalized.Append(' ');
                previousWasSeparator = true;
            }
        }

        if (normalized.Length > 0 && normalized[^1] == ' ')
        {
            normalized.Length--;
        }

        return normalized.ToString();
    }

    /// <summary>
    /// Normalizes a term for separator-insensitive matching and uniqueness.
    /// </summary>
    /// <param name="value">The search term.</param>
    /// <returns>The normalized term without separators.</returns>
    public static string NormalizeForLookup(string? value)
        => NormalizeForSearch(value).Replace(" ", string.Empty, StringComparison.Ordinal);

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
