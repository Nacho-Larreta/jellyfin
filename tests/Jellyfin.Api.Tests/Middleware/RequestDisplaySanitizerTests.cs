using System;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Api.Middleware.Tests;

public static class RequestDisplaySanitizerTests
{
    [Theory]
    [InlineData("?ApiKey=RAW_MARKER&safe=one", "GET /socket?ApiKey=<redacted>&safe=one")]
    [InlineData("?api_key=RAW_MARKER&APIKEY=SECOND_MARKER&safe=one", "GET /socket?api_key=<redacted>&APIKEY=<redacted>&safe=one")]
    [InlineData("?%41piKey=ENCODED_MARKER&safe=one", "GET /socket?ApiKey=<redacted>&safe=one")]
    [InlineData("?%2541piKey=DOUBLE_ENCODED_NAME_MARKER&safe=one", "GET /socket?ApiKey=<redacted>&safe=one")]
    [InlineData("?ApiKey=%22ENCODED_QUOTED_MARKER%22&safe=one", "GET /socket?ApiKey=<redacted>&safe=one")]
    [InlineData("?\"Secret\"=QUOTED_MARKER&code=CODE_MARKER&safe=one", "GET /socket?\"Secret\"=<redacted>&code=<redacted>&safe=one")]
    [InlineData("?access-token=TOKEN_MARKER&password=PASSWORD_MARKER&credential=CREDENTIAL_MARKER", "GET /socket?access-token=<redacted>&password=<redacted>&credential=<redacted>")]
    [InlineData("?pw=PASSWORD_MARKER&safe=one", "GET /socket?pw=<redacted>&safe=one")]
    public static void Sanitize_RedactsCredentialAliasesAndPreservesSafeContext(string query, string expected)
    {
        var context = CreateContext(query);
        var originalQuery = context.Request.QueryString;

        var display = RequestDisplaySanitizer.Sanitize(context.Request);

        Assert.Equal(expected, display);
        Assert.Equal(originalQuery, context.Request.QueryString);
        Assert.DoesNotContain("MARKER", display, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("?ApiKey=prefix%0ANEWLINE_MARKER")]
    [InlineData("?ApiKey=prefix%2CCOMMA_MARKER")]
    [InlineData("?safe=one%26ApiKey%3DENCODED_DELIMITER_MARKER")]
    [InlineData("?safe=one%2526ApiKey%253DDOUBLE_ENCODED_MARKER")]
    [InlineData("?safe=one&note=MALFORMED_PERCENT_MARKER%zz")]
    [InlineData("?safe=one%23ApiKey=FRAGMENT_MARKER")]
    [InlineData("?safe=one%3FApiKey=QUESTION_MARKER")]
    [InlineData("?safe=one%3AApiKey=COLON_MARKER")]
    [InlineData("?safe=one#ApiKey=RAW_FRAGMENT_MARKER")]
    [InlineData("?safe=one:ApiKey=RAW_COLON_MARKER")]
    [InlineData("?payload=%7B%22ApiKey%22%3A%22JSON_MARKER%22%7D")]
    [InlineData("?redirect=https%3A%2F%2Fviewer%3AUSERINFO_MARKER%40example.test%2Ffeed")]
    public static void Sanitize_FailsClosedWhenDecodingCreatesQueryBoundaries(string query)
    {
        var context = CreateContext(query);

        var display = RequestDisplaySanitizer.Sanitize(context.Request);

        Assert.Equal("GET /socket?query=<redacted>", display);
        Assert.DoesNotContain("MARKER", display, StringComparison.Ordinal);
    }

    [Fact]
    public static void Sanitize_PreservesMethodPathAndOrdinaryQuery()
    {
        var context = CreateContext("?safe=one&mode=direct-play");
        context.Request.Method = HttpMethods.Post;
        context.Request.PathBase = "/jellyfin";

        var display = RequestDisplaySanitizer.Sanitize(context.Request);

        Assert.Equal("POST /jellyfin/socket?safe=one&mode=direct-play", display);
    }

    [Fact]
    public static void Sanitize_RedactsCredentialRouteWithoutEndpointMetadata()
    {
        var context = CreateContext(string.Empty);
        context.Request.Method = HttpMethods.Delete;
        context.Request.Path = "/Auth/Keys/ROUTE_MARKER";

        var display = RequestDisplaySanitizer.Sanitize(context.Request);

        Assert.Equal("DELETE /Auth/Keys/<redacted>", display);
        Assert.DoesNotContain("ROUTE_MARKER", display, StringComparison.Ordinal);
    }

    private static DefaultHttpContext CreateContext(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/socket";
        context.Request.QueryString = new QueryString(query);
        return context;
    }
}
