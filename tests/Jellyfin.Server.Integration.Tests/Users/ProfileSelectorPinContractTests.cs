using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Jellyfin.Api.Models.ProfileSelectorsDtos;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Users;

public sealed class ProfileSelectorPinContractTests : IClassFixture<JellyfinApplicationFactory>
{
    private readonly JellyfinApplicationFactory _factory;

    public ProfileSelectorPinContractTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Pin_UsesFourToEightAsciiDigitsAcrossConfigurationActivationAndLockout()
    {
        var context = await ProfileSelectorApiTestContext.CreateAsync(_factory);

        using (var malformedUnprotectedActivation = await context.OwnerClient.PostAsJsonAsync(
                   context.ActivationUrl,
                   new ProfileActivationRequestDto { Pin = "12a4" },
                   context.JsonOptions,
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, malformedUnprotectedActivation.StatusCode);
        }

        foreach (var invalidPin in new string?[] { null, string.Empty, "123", "123456789", "١٢٣٤", "12a4", " 1234", "1234 " })
        {
            using var response = await context.OwnerClient.PostAsJsonAsync(
                context.PinUrl,
                new { Pin = invalidPin },
                context.JsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        foreach (var validPin in new[] { "1234", "0123", "123456", "1234567", "12345678" })
        {
            using var response = await context.OwnerClient.PostAsJsonAsync(
                context.PinUrl,
                new ProfilePinUpdateRequestDto { Pin = validPin },
                context.JsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        const string configuredPin = "00001234";
        using (var setPinResponse = await context.OwnerClient.PostAsJsonAsync(
                   context.PinUrl,
                   new ProfilePinUpdateRequestDto { Pin = configuredPin },
                   context.JsonOptions,
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, setPinResponse.StatusCode);
        }

        foreach (var invalidPin in new[] { "123", "123456789", "١٢٣٤", "12a4", " 1234", "1234 " })
        {
            using var response = await context.OwnerClient.PostAsJsonAsync(
                context.ActivationUrl,
                new ProfileActivationRequestDto { Pin = invalidPin },
                context.JsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using (var missingPinResponse = await context.OwnerClient.PostAsJsonAsync(
                   context.ActivationUrl,
                   new ProfileActivationRequestDto(),
                   context.JsonOptions,
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, missingPinResponse.StatusCode);
        }

        using (var emptyPinResponse = await context.OwnerClient.PostAsJsonAsync(
                   context.ActivationUrl,
                   new ProfileActivationRequestDto { Pin = string.Empty },
                   context.JsonOptions,
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, emptyPinResponse.StatusCode);
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var response = await context.OwnerClient.PostAsJsonAsync(
                context.ActivationUrl,
                new ProfileActivationRequestDto { Pin = "11111111" },
                context.JsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(attempt < 3 ? HttpStatusCode.Forbidden : HttpStatusCode.Locked, response.StatusCode);
        }

        using (var lockedResponse = await context.OwnerClient.PostAsJsonAsync(
                   context.ActivationUrl,
                   new ProfileActivationRequestDto { Pin = configuredPin },
                   context.JsonOptions,
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Locked, lockedResponse.StatusCode);
        }

        using (var resetPinResponse = await context.OwnerClient.PostAsJsonAsync(
                   context.PinUrl,
                   new ProfilePinUpdateRequestDto { Pin = configuredPin },
                   context.JsonOptions,
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, resetPinResponse.StatusCode);
        }

        using var validPinResponse = await context.OwnerClient.PostAsJsonAsync(
            context.ActivationUrl,
            new ProfileActivationRequestDto { Pin = configuredPin },
            context.JsonOptions,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, validPinResponse.StatusCode);
    }

    [Fact]
    public async Task ExpiredLockout_AllowsCorrectPinAndClearsFailedAttempts()
    {
        using var factory = new JellyfinApplicationFactory();
        var context = await ProfileSelectorApiTestContext.CreateAsync(factory);
        const string configuredPin = "1234";

        using (var response = await context.OwnerClient.PostAsJsonAsync(
                   context.PinUrl,
                   new ProfilePinUpdateRequestDto { Pin = configuredPin },
                   context.JsonOptions,
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var response = await context.OwnerClient.PostAsJsonAsync(
                context.ActivationUrl,
                new ProfileActivationRequestDto { Pin = "9999" },
                context.JsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(attempt < 3 ? HttpStatusCode.Forbidden : HttpStatusCode.Locked, response.StatusCode);
        }

        var dbContextFactory = factory.Services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>();
        await using (var dbContext = await dbContextFactory.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            var member = await dbContext.ProfileSelectorMembers.SingleAsync(
                entity => entity.ProfileUserId.Equals(context.ProfileId),
                TestContext.Current.CancellationToken);
            Assert.Equal(3, member.FailedPinAttemptCount);
            Assert.True(member.PinLockoutUntilUtc > DateTime.UtcNow);
            member.PinLockoutUntilUtc = DateTime.UtcNow.AddMinutes(-1);
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var response = await context.OwnerClient.PostAsJsonAsync(
                   context.ActivationUrl,
                   new ProfileActivationRequestDto { Pin = configuredPin },
                   context.JsonOptions,
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await using var verificationContext = await dbContextFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var recoveredMember = await verificationContext.ProfileSelectorMembers.SingleAsync(
            entity => entity.ProfileUserId.Equals(context.ProfileId),
            TestContext.Current.CancellationToken);
        Assert.Equal(0, recoveredMember.FailedPinAttemptCount);
        Assert.Null(recoveredMember.PinLockoutUntilUtc);
        Assert.Null(recoveredMember.LastFailedPinAttemptUtc);
    }
}
