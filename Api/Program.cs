using System.Text.Json.Serialization;
using Api.Features.DeleteProvisions;
using Api.Features.GetProvisions;
using Api.Features.PostProvisions;
using Api.Infrastructure.Auth;
using Api.Infrastructure.Messaging;
using Api.Features.ProvisioningEvents;
using Api.Features.Tenants;
using Api.Infrastructure.Persistance;
using Api.Protos;
using Microsoft.EntityFrameworkCore;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProvisioningPersistence(builder.Configuration);
builder.Services.AddJwtAuth(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddGrpcClient<Payment.PaymentClient>(o =>
    o.Address = new Uri(builder.Configuration["PaymentService:GrpcUrl"]
        ?? throw new InvalidOperationException("'PaymentService:GrpcUrl' is not configured.")));
builder.Services.AddOptions<ProvisioningOptions>()
    .Bind(builder.Configuration.GetSection(ProvisioningOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddProvisioningQueue(builder.Configuration);
builder.Services.AddHostedService<ProvisioningEventsConsumer>();
builder.Services.AddHostedService<SubscriptionEndedConsumer>();
builder.Services.AddHostedService<TenantAssignedConsumer>();

builder.Host.UseWolverine(opts => opts.AddProvisioningMessaging(builder.Configuration));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ProvisioningDbContext>();
    await db.Database.MigrateAsync();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGetProvisions();
app.MapPostProvisions();
app.MapDeleteProvisions();

await app.RunAsync();
