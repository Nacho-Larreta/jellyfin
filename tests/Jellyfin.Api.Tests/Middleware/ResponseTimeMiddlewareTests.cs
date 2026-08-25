using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Api.Middleware.Tests;

public class ResponseTimeMiddlewareTests
{
    [Theory]
    [InlineData("/socket?ApiKey=RESPONSE_TIME_LOG_MARKER&safe=one", "GET /socket?ApiKey=<redacted>&safe=one")]
    [InlineData("/socket?ApiKey=prefix%0ARESPONSE_TIME_LOG_MARKER&safe=one", "GET /socket?query=<redacted>")]
    public async Task Invoke_LogsOnlySanitizedRequestDisplay(string requestUri, string expectedDisplay)
    {
        const string Marker = "RESPONSE_TIME_LOG_MARKER";
        var logger = new CapturingLogger<ResponseTimeMiddleware>();
        await using var application = CreateApplication(logger);
        application.MapGet("/socket", () => "ok");
        await application.StartAsync(TestContext.Current.CancellationToken);
        using var client = application.GetTestClient();

        using var response = await client.GetAsync(
            requestUri,
            TestContext.Current.CancellationToken);

        var message = Assert.Single(logger.Messages);
        Assert.Contains(expectedDisplay, message, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.StructuredValues, value => value.Contains(Marker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invoke_LiveTvPasswordQueryNeverReachesLog()
    {
        const string Marker = "LIVETV_PASSWORD_LOG_MARKER";
        var logger = new CapturingLogger<ResponseTimeMiddleware>();
        await using var application = CreateApplication(logger);
        application.MapPost("/LiveTv/ListingProviders", () => TypedResults.Ok());
        await application.StartAsync(TestContext.Current.CancellationToken);
        using var client = application.GetTestClient();

        using var response = await client.PostAsync(
            "/LiveTv/ListingProviders?pw=" + Marker + "&validateLogin=true",
            content: null,
            TestContext.Current.CancellationToken);

        var message = Assert.Single(logger.Messages);
        Assert.Contains("POST /LiveTv/ListingProviders?pw=<redacted>&validateLogin=true", message, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.StructuredValues, value => value.Contains(Marker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invoke_CredentialRouteUsesMatchedTemplateWithPathBase()
    {
        const string Marker = "ROUTE_ACCESS_TOKEN_MARKER";
        var logger = new CapturingLogger<ResponseTimeMiddleware>();
        await using var application = CreateApplication(logger, "/jellyfin");
        application.MapDelete("/Auth/Keys/{key}", (string key) => TypedResults.NoContent());
        await application.StartAsync(TestContext.Current.CancellationToken);
        using var client = application.GetTestClient();

        using var response = await client.DeleteAsync(
            "/jellyfin/Auth/Keys/" + Marker,
            TestContext.Current.CancellationToken);

        var message = Assert.Single(logger.Messages);
        Assert.Contains("DELETE /jellyfin/Auth/Keys/{key}", message, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.StructuredValues, value => value.Contains(Marker, StringComparison.Ordinal));
    }

    private static WebApplication CreateApplication(
        CapturingLogger<ResponseTimeMiddleware> logger,
        string? pathBase = null)
    {
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager
            .SetupGet(manager => manager.Configuration)
            .Returns(new ServerConfiguration
            {
                EnableSlowResponseWarning = true,
                SlowResponseThresholdMs = -1
            });
        var applicationBuilder = WebApplication.CreateBuilder();
        applicationBuilder.WebHost.UseTestServer();
        applicationBuilder.Services.AddSingleton(configurationManager.Object);
        applicationBuilder.Services.AddSingleton<ILogger<ResponseTimeMiddleware>>(logger);
        var application = applicationBuilder.Build();
        if (pathBase is not null)
        {
            application.UsePathBase(pathBase);
        }

        application.UseMiddleware<ResponseTimeMiddleware>();
        return application;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public List<string> StructuredValues { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var value in values)
                {
                    StructuredValues.Add(value.Value?.ToString() ?? string.Empty);
                }
            }

            Messages.Add(formatter(state, exception));
        }
    }
}
