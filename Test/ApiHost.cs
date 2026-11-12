using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Domain;
using Api.Features.DeleteProvisions;
using Api.Features.GetProvisions;
using Api.Features.PostProvisions;
using Api.Features.Tenants;
using Api.Infrastructure.Persistance;
using Api.Protos;
using Grpc.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Test;

/// <summary>
/// The real provisioning endpoints (POST / GET / DELETE /provisions) in an in-process server, with an in-memory database,
/// a mocked PaymentService client and a recording outbox. A request is signed in by sending the user id in
/// <see cref="UserHeader"/>.
///
/// The in-memory database does not enforce unique indexes, so races on the same name are not covered here.
/// </summary>
public sealed class ApiHost : IAsyncDisposable
{
    public const string UserHeader = "X-Test-User";
    public const string BaseDomain = "example.test";
    public const string PriceId = "price_wp";

    private readonly string dbName = Guid.NewGuid().ToString();
    private WebApplication app = null!;

    public Payment.PaymentClient Payments { get; } = Substitute.For<Payment.PaymentClient>();

    /// <summary>Messages the endpoints stored in the outbox.</summary>
    public List<object> Published { get; } = [];

    public HttpClient Client { get; private set; } = null!;

    public static async Task<ApiHost> StartAsync()
    {
        var host = new ApiHost();
        await host.BuildAsync();
        return host;
    }

    private async Task BuildAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddDbContext<ProvisioningDbContext>(o => o.UseInMemoryDatabase(dbName));
        builder.Services.AddScoped(sp => CreateOutbox(sp.GetRequiredService<ProvisioningDbContext>()));
        builder.Services.AddScoped(sp => new UserTenants(
            sp.GetRequiredService<ProvisioningDbContext>(),
            new HttpClient(new NotFoundHandler()) { BaseAddress = new Uri("http://users.test/") },
            NullLogger<UserTenants>.Instance));
        builder.Services.AddSingleton(Payments);
        builder.Services.Configure<ProvisioningOptions>(o =>
        {
            o.StripePriceId = PriceId;
            o.BaseDomain = BaseDomain;
        });
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();

        app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGetProvisions();
        app.MapPostProvisions();
        app.MapDeleteProvisions();

        await app.StartAsync();
        Client = app.GetTestClient();
    }

    /// <summary>An outbox that saves through the DbContext and records what was published.</summary>
    private IDbContextOutbox<ProvisioningDbContext> CreateOutbox(ProvisioningDbContext db)
    {
        var outbox = Substitute.For<IDbContextOutbox<ProvisioningDbContext>>();
        outbox.DbContext.Returns(db);
        outbox.SaveChangesAndFlushMessagesAsync(Arg.Any<CancellationToken>()).Returns(_ => db.SaveChangesAsync());
        outbox.PublishAsync(Arg.Any<ProvisionService>(), Arg.Any<DeliveryOptions?>()).Returns(call =>
        {
            Published.Add(call.Arg<ProvisionService>());
            return ValueTask.CompletedTask;
        });
        outbox.PublishAsync(Arg.Any<DeprovisionService>(), Arg.Any<DeliveryOptions?>()).Returns(call =>
        {
            Published.Add(call.Arg<DeprovisionService>());
            return ValueTask.CompletedTask;
        });
        return outbox;
    }

    // ---- requests ----

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Guid? user, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (user is { } id) request.Headers.Add(UserHeader, id.ToString());
        if (body is not null) request.Content = JsonContent.Create(body);
        return Client.SendAsync(request);
    }

    public static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    // ---- database ----

    public async Task SeedAsync(Action<ProvisioningDbContext> seed)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProvisioningDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    public async Task<T> QueryAsync<T>(Func<ProvisioningDbContext, Task<T>> query)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<ProvisioningDbContext>());
    }

    public Task GiveTenantAsync(Guid userId, string tenant) =>
        SeedAsync(db => db.UserTenants.Add(new UserTenant { UserId = userId, Tenant = tenant }));

    public static ProvisionedService Site(
        Guid userId, string name, ProvisioningStatus status, string tenant = "acme", string? stripeSubscriptionId = null,
        DateTime? startDate = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Tenant = tenant,
        Name = name,
        ProvisioningStatus = status,
        StripeSubscriptionId = stripeSubscriptionId,
        StartDate = startDate ?? DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    // ---- PaymentService (gRPC) answers ----

    public void PaymentAnswers(string status, string? stripeSubscriptionId = null, string? reason = null) =>
        Payments.TryMakeSubscriptionAsync(
                Arg.Any<TryMakeSubscriptionRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(Call(new TryMakeSubscriptionResponse
            {
                Status = status, StripeSubscriptionId = stripeSubscriptionId ?? "", Reason = reason ?? ""
            }));

    public void PaymentThrows() =>
        Payments.TryMakeSubscriptionAsync(
                Arg.Any<TryMakeSubscriptionRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns<AsyncUnaryCall<TryMakeSubscriptionResponse>>(_ => throw new RpcException(new Status(StatusCode.Unavailable, "down")));

    public void StopAnswers(string status) =>
        Payments.StopSubscriptionAsync(
                Arg.Any<StopSubscriptionRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(Call(new StopSubscriptionResponse { Status = status }));

    public void StopThrows() =>
        Payments.StopSubscriptionAsync(
                Arg.Any<StopSubscriptionRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns<AsyncUnaryCall<StopSubscriptionResponse>>(_ => throw new RpcException(new Status(StatusCode.Unavailable, "down")));

    private static AsyncUnaryCall<T> Call<T>(T response) => new(
        Task.FromResult(response), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.DisposeAsync();
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var user)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim("sub", user.ToString())], "Test");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}
