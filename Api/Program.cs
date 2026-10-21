using System.Text.Json.Serialization;
using Api.Features.DeleteProvisions;
using Api.Features.GetProvisions;
using Api.Features.PostProvisions;
using Api.Infrastructure.ApiDocs;
using Api.Infrastructure.Auth;
using Api.Infrastructure.Messaging;
using Api.Infrastructure.Observability;
using Api.Features.ProvisioningEvents;
using Api.Features.Reconciliation;
using Api.Features.Tenants;
using Api.Infrastructure.Persistance;
using Api.Protos;
using Microsoft.EntityFrameworkCore;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProvisioningPersistence(builder.Configuration);
builder.Services.AddJwtAuth(builder.Configuration);
builder.Services.AddApiDocs("ProvisioningService API");
builder.Services.AddServiceHealthChecks<ProvisioningDbContext>();
builder.Services.AddObservability("provisioning-service");
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddGrpcClient<Payment.PaymentClient>(o =>
    o.Address = new Uri(builder.Configuration["PaymentService:GrpcUrl"]
        ?? throw new InvalidOperationException("'PaymentService:GrpcUrl' is not configured.")));
builder.Services.AddOptions<ProvisioningOptions>()
    .Bind(builder.Configuration.GetSection(ProvisioningOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddNats(builder.Configuration);
builder.Services.AddProvisioningQueues(builder.Configuration);
builder.Services.AddUserTenants(builder.Configuration);
builder.Services.AddHostedService<ProvisioningEventsConsumer>();
builder.Services.AddHostedService<SubscriptionEndedConsumer>();
builder.Services.AddHostedService<TenantAssignedConsumer>();
builder.Services.AddHostedService<ProvisioningReconciler>();

builder.Host.UseWolverine(opts => opts.AddProvisioningMessaging(builder.Configuration));

var app = builder.Build();

// Always in Development; elsewhere when Database:MigrateOnStartup is set (the helm chart's migrateOnStartup).
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ProvisioningDbContext>();
    await db.Database.MigrateAsync();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGetProvisions();
app.MapPostProvisions();
app.MapDeleteProvisions();
app.MapObservability();
app.MapApiDocs("ProvisioningService API");

await app.RunAsync();
