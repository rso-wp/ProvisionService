using System.Net;
using Api.Domain;
using Api.Features.DeleteProvisions;
using Api.Features.PostProvisions;
using Api.Protos;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Tests;

/// <summary>POST /provisions: reserve the name, ask PaymentService, then queue the site or release the name.</summary>
public class CreateProvisionTests : IAsyncLifetime
{
    private ApiHost host = null!;
    private readonly Guid userId = Guid.NewGuid();

    public async Task InitializeAsync() => host = await ApiHost.StartAsync();
    public async Task DisposeAsync() => await host.DisposeAsync();

    private Task<HttpResponseMessage> Create(string name, string? paymentMethodId = null, Guid? user = null) =>
        host.SendAsync(HttpMethod.Post, "/provisions", user ?? userId, new { name, paymentMethodId });

    private Task<List<ProvisionedService>> Rows() => host.QueryAsync(db => db.ProvisionedServices.ToListAsync());

    private void NoPaymentCall() =>
        host.Payments.DidNotReceiveWithAnyArgs().TryMakeSubscriptionAsync(default!, default(Grpc.Core.Metadata), default, default);

    [Fact]
    public async Task WithoutAToken_IsRejected()
    {
        var response = await host.SendAsync(HttpMethod.Post, "/provisions", user: null, new { name = "blog" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Blog")]
    [InlineData("-blog")]
    [InlineData("blog-")]
    [InlineData("my_blog")]
    [InlineData("my.blog")]
    [InlineData("a-name-that-is-longer-than-thirty-characters")]
    public async Task InvalidName_Returns400_AndTouchesNothing(string name)
    {
        await host.GiveTenantAsync(userId, "acme");

        var response = await Create(name);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await Rows());
        NoPaymentCall();
    }

    [Fact]
    public async Task UserWithoutTenant_Returns409_NothingReserved_NoPayment()
    {
        var response = await Create("blog");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await Rows());
        NoPaymentCall();
    }

    [Fact]
    public async Task NameAlreadyUsedInTheTenant_Returns409_NoPayment()
    {
        await host.GiveTenantAsync(userId, "acme");
        await host.SeedAsync(db => db.ProvisionedServices.Add(ApiHost.Site(userId, "blog", ProvisioningStatus.Provisioned)));

        var response = await Create("blog");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single(await Rows());
        NoPaymentCall();
    }

    [Fact]
    public async Task PaymentSucceeds_SiteIsQueued_AndTheProvisionCommandIsStored()
    {
        await host.GiveTenantAsync(userId, "acme");
        host.PaymentAnswers("SUCCESSFUL", "sub_123");

        var response = await Create("blog");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var row = Assert.Single(await Rows());
        Assert.Equal(ProvisioningStatus.Queued, row.ProvisioningStatus);
        Assert.Equal("sub_123", row.StripeSubscriptionId);
        Assert.Equal(userId, row.UserId);
        Assert.Equal("acme", row.Tenant);

        var body = await ApiHost.JsonOf(response);
        Assert.Equal(row.Id, body.GetProperty("provisionId").GetGuid());
        Assert.Equal("blog.acme.example.test", body.GetProperty("host").GetString());
        Assert.Equal("Queued", body.GetProperty("provisioningStatus").GetString());
        Assert.Equal($"/provisions/{row.Id}", response.Headers.Location?.OriginalString);

        Assert.Equal(new ProvisionService(row.Id), Assert.Single(host.Published));
    }

    [Fact]
    public async Task AsksPaymentService_WithTheProvisionIdAsRequestId_AndTheConfiguredPrice()
    {
        await host.GiveTenantAsync(userId, "acme");
        host.PaymentAnswers("SUCCESSFUL", "sub_123");

        await Create("blog", paymentMethodId: "pm_card");

        var row = Assert.Single(await Rows());
        _ = host.Payments.Received(1).TryMakeSubscriptionAsync(
            Arg.Is<TryMakeSubscriptionRequest>(r =>
                r.UserId == userId.ToString()
                && r.RequestId == row.Id.ToString()
                && r.StripePriceId == ApiHost.PriceId
                && r.ServiceId == "wordpress"
                && r.PaymentMethodId == "pm_card"),
            Arg.Any<Grpc.Core.Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoPaymentMethodGiven_SendsAnEmptyOne_SoPaymentServiceUsesTheDefaultCard()
    {
        await host.GiveTenantAsync(userId, "acme");
        host.PaymentAnswers("SUCCESSFUL", "sub_123");

        await Create("blog");

        _ = host.Payments.Received(1).TryMakeSubscriptionAsync(
            Arg.Is<TryMakeSubscriptionRequest>(r => r.PaymentMethodId == ""),
            Arg.Any<Grpc.Core.Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PaymentDeclined_Returns402_ReleasesTheName_AndStoresNoCommand()
    {
        await host.GiveTenantAsync(userId, "acme");
        host.PaymentAnswers("FAILED", reason: "Your card was declined.");

        var response = await Create("blog");

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        Assert.Equal("Your card was declined.", (await ApiHost.JsonOf(response)).GetProperty("detail").GetString());
        Assert.Empty(await Rows());
        Assert.Empty(host.Published);
    }

    [Fact]
    public async Task NameIsFreeAgain_AfterADeclinedPayment()
    {
        await host.GiveTenantAsync(userId, "acme");
        host.PaymentAnswers("FAILED", reason: "declined");
        await Create("blog");

        host.PaymentAnswers("SUCCESSFUL", "sub_2");
        var retry = await Create("blog");

        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
    }

    [Fact]
    public async Task PaymentServiceUnreachable_Returns502_SiteStaysPendingPayment_NoCommand()
    {
        await host.GiveTenantAsync(userId, "acme");
        host.PaymentThrows();

        var response = await Create("blog");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var row = Assert.Single(await Rows());
        // The reconciler asks PaymentService later what became of the payment.
        Assert.Equal(ProvisioningStatus.PendingPayment, row.ProvisioningStatus);
        Assert.Null(row.StripeSubscriptionId);
        Assert.Empty(host.Published);
    }

    [Fact]
    public async Task PaymentStillPending_Returns502_SiteStaysPendingPayment_NoCommand()
    {
        await host.GiveTenantAsync(userId, "acme");
        host.PaymentAnswers("PENDING");

        var response = await Create("blog");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(ProvisioningStatus.PendingPayment, Assert.Single(await Rows()).ProvisioningStatus);
        Assert.Empty(host.Published);
    }

    [Fact]
    public async Task SameNameInAnotherTenant_IsAllowed()
    {
        var other = Guid.NewGuid();
        await host.GiveTenantAsync(userId, "acme");
        await host.SeedAsync(db => db.ProvisionedServices.Add(ApiHost.Site(other, "blog", ProvisioningStatus.Provisioned, tenant: "globex")));
        host.PaymentAnswers("SUCCESSFUL", "sub_1");

        var response = await Create("blog");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }
}

/// <summary>DELETE /provisions/{name}: stop the subscription first, then mark the site Deleting.</summary>
public class DeleteProvisionTests : IAsyncLifetime
{
    private ApiHost host = null!;
    private readonly Guid userId = Guid.NewGuid();

    public async Task InitializeAsync() => host = await ApiHost.StartAsync();
    public async Task DisposeAsync() => await host.DisposeAsync();

    private Task<HttpResponseMessage> Delete(string name, Guid? user = null) =>
        host.SendAsync(HttpMethod.Delete, $"/provisions/{name}", user ?? userId);

    private Task<ProvisionedService> Row(string name) =>
        host.QueryAsync(db => db.ProvisionedServices.SingleAsync(p => p.Name == name));

    private void StopNotCalled() =>
        host.Payments.DidNotReceiveWithAnyArgs().StopSubscriptionAsync(default!, default(Grpc.Core.Metadata), default, default);

    [Fact]
    public async Task WithoutAToken_IsRejected()
    {
        var response = await host.SendAsync(HttpMethod.Delete, "/provisions/blog", user: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnknownSite_Returns404()
    {
        var response = await Delete("nothing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        StopNotCalled();
    }

    [Fact]
    public async Task SiteOfAnotherUser_Returns404_AndIsNotStoppedOrChanged()
    {
        await host.SeedAsync(db => db.ProvisionedServices.Add(
            ApiHost.Site(Guid.NewGuid(), "blog", ProvisioningStatus.Provisioned, stripeSubscriptionId: "sub_1")));

        var response = await Delete("blog");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        StopNotCalled();
        Assert.Equal(ProvisioningStatus.Provisioned, (await Row("blog")).ProvisioningStatus);
        Assert.Empty(host.Published);
    }

    [Fact]
    public async Task Success_StopsTheSubscription_MarksDeleting_AndStoresTheDeprovisionCommand()
    {
        var site = ApiHost.Site(userId, "blog", ProvisioningStatus.Provisioned, stripeSubscriptionId: "sub_1");
        await host.SeedAsync(db => db.ProvisionedServices.Add(site));
        host.StopAnswers("SUCCESSFUL");

        var response = await Delete("blog");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        _ = host.Payments.Received(1).StopSubscriptionAsync(
            Arg.Is<StopSubscriptionRequest>(r => r.StripeSubscriptionId == "sub_1"),
            Arg.Any<Grpc.Core.Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
        Assert.Equal(ProvisioningStatus.Deleting, (await Row("blog")).ProvisioningStatus);
        Assert.Equal(new DeprovisionService(site.Id), Assert.Single(host.Published));
    }

    [Theory]
    [InlineData(ProvisioningStatus.Queued)]
    [InlineData(ProvisioningStatus.Failed)]
    [InlineData(ProvisioningStatus.Provisioned)]
    public async Task SiteCanBeDeleted_InAnyStateWithASubscription(ProvisioningStatus status)
    {
        await host.SeedAsync(db => db.ProvisionedServices.Add(ApiHost.Site(userId, "blog", status, stripeSubscriptionId: "sub_1")));
        host.StopAnswers("SUCCESSFUL");

        var response = await Delete("blog");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(ProvisioningStatus.Deleting, (await Row("blog")).ProvisioningStatus);
    }

    [Fact]
    public async Task AlreadyDeleting_Returns202_WithoutStoppingAgain()
    {
        await host.SeedAsync(db => db.ProvisionedServices.Add(
            ApiHost.Site(userId, "blog", ProvisioningStatus.Deleting, stripeSubscriptionId: "sub_1")));

        var response = await Delete("blog");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        StopNotCalled();
        Assert.Empty(host.Published);
    }

    [Fact]
    public async Task PaymentStillBeingConfirmed_Returns409_NothingStopped()
    {
        await host.SeedAsync(db => db.ProvisionedServices.Add(ApiHost.Site(userId, "blog", ProvisioningStatus.PendingPayment)));

        var response = await Delete("blog");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        StopNotCalled();
        Assert.Equal(ProvisioningStatus.PendingPayment, (await Row("blog")).ProvisioningStatus);
    }

    [Fact]
    public async Task SubscriptionCannotBeStopped_Returns502_SiteIsKept()
    {
        await host.SeedAsync(db => db.ProvisionedServices.Add(
            ApiHost.Site(userId, "blog", ProvisioningStatus.Provisioned, stripeSubscriptionId: "sub_1")));
        host.StopAnswers("FAILED");

        var response = await Delete("blog");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(ProvisioningStatus.Provisioned, (await Row("blog")).ProvisioningStatus);
        Assert.Empty(host.Published);
    }

    [Fact]
    public async Task PaymentServiceUnreachable_Returns502_SiteIsKept()
    {
        await host.SeedAsync(db => db.ProvisionedServices.Add(
            ApiHost.Site(userId, "blog", ProvisioningStatus.Provisioned, stripeSubscriptionId: "sub_1")));
        host.StopThrows();

        var response = await Delete("blog");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(ProvisioningStatus.Provisioned, (await Row("blog")).ProvisioningStatus);
        Assert.Empty(host.Published);
    }
}

/// <summary>GET /provisions.</summary>
public class GetProvisionsTests : IAsyncLifetime
{
    private ApiHost host = null!;
    private readonly Guid userId = Guid.NewGuid();

    public async Task InitializeAsync() => host = await ApiHost.StartAsync();
    public async Task DisposeAsync() => await host.DisposeAsync();

    [Fact]
    public async Task WithoutAToken_IsRejected()
    {
        var response = await host.SendAsync(HttpMethod.Get, "/provisions", user: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReturnsOnlyTheCallersSites_NewestFirst_WithTheirHost()
    {
        var now = DateTime.UtcNow;
        await host.SeedAsync(db => db.ProvisionedServices.AddRange(
            ApiHost.Site(userId, "old", ProvisioningStatus.Provisioned, startDate: now.AddDays(-2)),
            ApiHost.Site(userId, "new", ProvisioningStatus.Queued, startDate: now),
            ApiHost.Site(Guid.NewGuid(), "someone-elses", ProvisioningStatus.Provisioned)));

        var response = await host.SendAsync(HttpMethod.Get, "/provisions", userId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiHost.JsonOf(response);
        Assert.Equal(userId, body.GetProperty("userId").GetGuid());
        var services = body.GetProperty("services").EnumerateArray().ToList();
        Assert.Equal(["new", "old"], services.Select(s => s.GetProperty("name").GetString()));
        Assert.Equal("new.acme.example.test", services[0].GetProperty("host").GetString());
        Assert.Equal("Queued", services[0].GetProperty("provisioningStatus").GetString());
    }

    [Fact]
    public async Task UserWithoutSites_GetsAnEmptyList()
    {
        var response = await host.SendAsync(HttpMethod.Get, "/provisions", userId);

        Assert.Empty((await ApiHost.JsonOf(response)).GetProperty("services").EnumerateArray());
    }
}
