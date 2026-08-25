using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Jellyfin.Api.Middleware;

internal static class RequestDisplaySanitizer
{
    private const int MaximumDecodePasses = 5;
    private const string RedactedQuery = "?query=<redacted>";
    private const string RedactedValue = "<redacted>";

    private static readonly HashSet<string> SensitiveNames = new(StringComparer.Ordinal)
    {
        "apikey",
        "authorizationcode",
        "code",
        "credential",
        "password",
        "pin",
        "pw",
        "quickconnectcode",
        "quickconnectsecret",
        "secret"
    };

    public static string Sanitize(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Sanitize(request, request.PathBase);
    }

    public static string Sanitize(HttpRequest request, PathString pathBase)
    {
        ArgumentNullException.ThrowIfNull(request);

        var method = ContainsControlCharacter(request.Method) ? "<redacted-method>" : request.Method;
        var path = SanitizePath(request, pathBase);

        return string.Concat(method, " ", path, SanitizeQuery(request.QueryString.Value));
    }

    private static string SanitizeQuery(string? rawQuery)
    {
        if (string.IsNullOrEmpty(rawQuery) || string.Equals(rawQuery, "?", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var query = rawQuery[0] == '?' ? rawQuery[1..] : rawQuery;
        if (!TryDecodeWithoutCreatingBoundaries(query, out var decodedQuery))
        {
            return RedactedQuery;
        }

        var sanitizedSegments = new List<string>();
        foreach (var segment in decodedQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equalsIndex = segment.IndexOf('=', StringComparison.Ordinal);
            var name = equalsIndex < 0 ? segment : segment[..equalsIndex];
            var value = equalsIndex < 0 ? string.Empty : segment[(equalsIndex + 1)..];
            if (IsSensitiveName(name))
            {
                sanitizedSegments.Add(string.Concat(name, "=", RedactedValue));
            }
            else if (ContainsCredentialBearingValue(value))
            {
                return RedactedQuery;
            }
            else
            {
                sanitizedSegments.Add(segment);
            }
        }

        return sanitizedSegments.Count == 0
            ? string.Empty
            : string.Concat("?", string.Join('&', sanitizedSegments));
    }

    private static bool TryDecodeWithoutCreatingBoundaries(string query, out string decodedQuery)
    {
        decodedQuery = query;
        for (var pass = 0; pass < MaximumDecodePasses; pass++)
        {
            string next;
            try
            {
                next = Uri.UnescapeDataString(decodedQuery);
            }
            catch (UriFormatException)
            {
                return false;
            }

            if (string.Equals(next, decodedQuery, StringComparison.Ordinal))
            {
                return next.IndexOf('%', StringComparison.Ordinal) < 0
                    && !ContainsControlCharacter(next);
            }

            if (IntroducesAmbiguousBoundary(decodedQuery, next))
            {
                return false;
            }

            decodedQuery = next;
        }

        return decodedQuery.IndexOf('%', StringComparison.Ordinal) < 0 && !ContainsControlCharacter(decodedQuery);
    }

    private static bool IntroducesAmbiguousBoundary(string encoded, string decoded)
    {
        ReadOnlySpan<char> boundaries = ['&', '=', ',', ';', '#', '?', ':', '@', '{', '}', '[', ']', '\r', '\n', '\t', ' '];
        foreach (var boundary in boundaries)
        {
            if (Count(encoded, boundary) < Count(decoded, boundary))
            {
                return true;
            }
        }

        return false;
    }

    private static int Count(string value, char character)
    {
        var count = 0;
        foreach (var candidate in value)
        {
            if (candidate == character)
            {
                count++;
            }
        }

        return count;
    }

    private static string SanitizePath(HttpRequest request, PathString pathBase)
    {
        var routePattern = (request.HttpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        if (!string.IsNullOrEmpty(routePattern))
        {
            var normalizedRoutePattern = routePattern[0] == '/' ? routePattern : string.Concat("/", routePattern);
            return EnsureSafePath(string.Concat(pathBase.Value?.TrimEnd('/'), normalizedRoutePattern));
        }

        var path = pathBase.Add(request.Path).Value ?? "/";
        const string ApiKeyRoutePrefix = "/Auth/Keys/";
        var credentialIndex = path.IndexOf(ApiKeyRoutePrefix, StringComparison.OrdinalIgnoreCase);
        if (credentialIndex >= 0)
        {
            return EnsureSafePath(string.Concat(path.AsSpan(0, credentialIndex + ApiKeyRoutePrefix.Length), RedactedValue));
        }

        return EnsureSafePath(path);
    }

    private static string EnsureSafePath(string path)
    {
        return ContainsControlCharacter(path) ? "<redacted-path>" : path;
    }

    private static bool ContainsCredentialBearingValue(string value)
    {
        foreach (var candidate in value.Split(['?', '#', ':', '&', ';', ',', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var equalsIndex = candidate.IndexOf('=', StringComparison.Ordinal);
            if (equalsIndex > 0 && IsSensitiveName(candidate[..equalsIndex]))
            {
                return true;
            }
        }

        return ContainsUriUserInfo(value) || ContainsSensitiveJsonProperty(value);
    }

    private static bool ContainsUriUserInfo(string value)
    {
        var candidate = value.Trim('"', '\'');
        if (candidate.StartsWith("//", StringComparison.Ordinal))
        {
            candidate = string.Concat("https:", candidate);
        }

        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.UserInfo);
    }

    private static bool ContainsSensitiveJsonProperty(string value)
    {
        var candidate = value.AsSpan().Trim();
        if (candidate.IsEmpty || (candidate[0] != '{' && candidate[0] != '['))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return ContainsSensitiveJsonProperty(document.RootElement);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool ContainsSensitiveJsonProperty(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (IsSensitiveName(property.Name) || ContainsSensitiveJsonProperty(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsSensitiveJsonProperty(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsSensitiveName(string name)
    {
        var normalizedName = NormalizeName(name);
        return SensitiveNames.Contains(normalizedName)
            || normalizedName.EndsWith("token", StringComparison.Ordinal)
            || normalizedName.EndsWith("secret", StringComparison.Ordinal)
            || normalizedName.EndsWith("password", StringComparison.Ordinal)
            || normalizedName.EndsWith("credential", StringComparison.Ordinal);
    }

    private static string NormalizeName(string name)
    {
        var normalizedName = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character))
            {
                normalizedName.Append(char.ToLowerInvariant(character));
            }
        }

        return normalizedName.ToString();
    }

    private static bool ContainsControlCharacter(string value)
    {
        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                return true;
            }
        }

        return false;
    }
}
