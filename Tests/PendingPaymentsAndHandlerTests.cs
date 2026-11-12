using Api.Domain;
using Api.Features.DeleteProvisions;
using Api.Features.PostProvisions;
using Api.Infrastructure.Messaging;
using Api.Infrastructure.Persistance;
using Api.Protos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Tests;

/// <summary>What PaymentService's answer does to a PendingPayment site.</summary>
public class PendingPaymentsTests : IDisposable
{
    private readonly ProvisioningDbContext db = new(
        new DbContextOptionsBuilder<ProvisioningDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly IDbContextOutbox<ProvisioningDbContext> outbox = Substitute.For<IDbContextOutbox<ProvisioningDbContext>>();
    private readonly ProvisionedService site;

    public PendingPaymentsTests()
    {
        outbox.DbContext.Returns(db);
        outbox.SaveChangesAndFlushMessagesAsync(Arg.Any<CancellationToken>()).Returns(_ => db.SaveChangesAsync());
        site = ApiHost.Site(Guid.NewGuid(), "blog", ProvisioningStatus.PendingPayment);
        site.UpdatedAt = DateTime.UtcNow.AddHours(-1);
        db.ProvisionedServices.Add(site);
        db.SaveChanges();
    }

    public void Dispose() => db.Dispose();

    private static TryMakeSubscriptionResponse Answer(string status, string? subscription = null) =>
        new() { Status = status, StripeSubscriptionId = subscription ?? "" };

    [Fact]
    public async Task Successful_QueuesTheSite_WithItsSubscription_AndStoresTheCommand()
    {
        var before = site.UpdatedAt;

        await PendingPayments.ApplyAsync(outbox, site, Answer(PendingPayments.Successful, "sub_1"));

        var saved = await db.ProvisionedServices.SingleAsync();
        Assert.Equal(ProvisioningStatus.Queued, saved.ProvisioningStatus);
        Assert.Equal("sub_1", saved.StripeSubscriptionId);
        Assert.True(saved.UpdatedAt > before);
        await outbox.Received(1).PublishAsync(new ProvisionService(site.Id), Arg.Any<DeliveryOptions?>());
        await outbox.Received(1).SaveChangesAndFlushMessagesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_RemovesTheSite_SoTheNameIsFree_AndPublishesNothing()
    {
        await PendingPayments.ApplyAsync(outbox, site, Answer(PendingPayments.Failed));

        Assert.Empty(db.ProvisionedServices);
        await outbox.DidNotReceiveWithAnyArgs().PublishAsync(default(ProvisionService)!, default);
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData("")]
    [InlineData("SOMETHING_ELSE")]
    public async Task AnyOtherAnswer_LeavesTheSiteAlone(string status)
    {
        await PendingPayments.ApplyAsync(outbox, site, Answer(status));

        var saved = await db.ProvisionedServices.SingleAsync();
        Assert.Equal(ProvisioningStatus.PendingPayment, saved.ProvisioningStatus);
        Assert.Null(saved.StripeSubscriptionId);
        await outbox.DidNotReceiveWithAnyArgs().PublishAsync(default(ProvisionService)!, default);
        await outbox.DidNotReceiveWithAnyArgs().SaveChangesAndFlushMessagesAsync(default);
    }
}

/// <summary>The handlers that forward a committed command to ProvisionFunction's queues.</summary>
public class CommandHandlerTests : IDisposable
{
    private readonly ProvisioningDbContext db = new(
        new DbContextOptionsBuilder<ProvisioningDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly IProvisioningQueue queue = Substitute.For<IProvisioningQueue>();

    public void Dispose() => db.Dispose();

    private async Task<ProvisionedService> Add(ProvisioningStatus status)
    {
        var site = ApiHost.Site(Guid.NewGuid(), "blog", status, tenant: "acme");
        db.ProvisionedServices.Add(site);
        await db.SaveChangesAsync();
        return site;
    }

    private Task Provision(Guid id) =>
        ProvisionServiceHandler.Handle(new ProvisionService(id), db, queue, NullLogger<ProvisionService>.Instance, default);

    private Task Deprovision(Guid id) =>
        DeprovisionServiceHandler.Handle(new DeprovisionService(id), db, queue, NullLogger<DeprovisionService>.Instance, default);

    [Theory]
    [InlineData(ProvisioningStatus.Queued)]
    [InlineData(ProvisioningStatus.Failed)]
    public async Task Provision_SitesThatShouldBeRunning_AreSentToTheQueue(ProvisioningStatus status)
    {
        var site = await Add(status);

        await Provision(site.Id);

        await queue.Received(1).ProvisionAsync(Arg.Is<ProvisionedService>(p => p.Id == site.Id), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ProvisioningStatus.Deleting)]
    [InlineData(ProvisioningStatus.Provisioned)]
    [InlineData(ProvisioningStatus.PendingPayment)]
    public async Task Provision_SitesInAnyOtherState_AreNotSent(ProvisioningStatus status)
    {
        // E.g. deleted while the message waited: it must not bring the site back.
        var site = await Add(status);

        await Provision(site.Id);

        await queue.DidNotReceiveWithAnyArgs().ProvisionAsync(default!, default);
    }

    [Fact]
    public async Task Provision_MissingSite_IsDropped()
    {
        await Provision(Guid.NewGuid());

        await queue.DidNotReceiveWithAnyArgs().ProvisionAsync(default!, default);
    }

    [Fact]
    public async Task Deprovision_SendsTheCommand_WithTheSitesTenantAndName()
    {
        var site = await Add(ProvisioningStatus.Deleting);

        await Deprovision(site.Id);

        await queue.Received(1).DeprovisionAsync(
            Arg.Is<DeprovisionTenantSite>(c => c.ProvisionId == site.Id && c.Tenant == "acme" && c.Name == "blog"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deprovision_MissingSite_IsDropped()
    {
        await Deprovision(Guid.NewGuid());

        await queue.DidNotReceiveWithAnyArgs().DeprovisionAsync(default!, default);
    }
}
