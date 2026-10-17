using System.Text.Json.Serialization;
using Api.Features.GetProvisions;
using Api.Features.GetServices;
using Api.Features.PostProvisions;
using Api.Infrastructure.Auth;
using Api.Infrastructure.CloudFunction;
using Api.Infrastructure.Persistance;
using Api.Protos;
using Microsoft.EntityFrameworkCore;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProvisioningPersistence(builder.Configuration);
builder.Services.AddEntraAuth(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddGrpcClient<Payment.PaymentClient>(o =>
    o.Address = new Uri(builder.Configuration["PaymentService:GrpcUrl"]
        ?? throw new InvalidOperationException("'PaymentService:GrpcUrl' is not configured.")));
builder.Services.AddScoped<ICloudFunctionTrigger, CloudFunctionTrigger>();

builder.Host.UseWolverine(opts => opts.AddProvisioningMessaging(builder.Configuration));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ProvisioningDbContext>();
    await db.Database.MigrateAsync();
    await DevSeed.SeedAsync(db);
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGetServices();
app.MapGetProvisions();
app.MapPostProvisions();

await app.RunAsync();
