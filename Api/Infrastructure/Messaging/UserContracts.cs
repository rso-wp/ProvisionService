namespace Api.Infrastructure.Messaging;

// Wire contract shared with UserService (copy in UserService/Api/Infrastructure/Messaging/UserContracts.cs).
// Keep both copies in sync. Payloads are JSON (camelCase) on NATS JetStream stream USERS.

public static class UserSubjects
{
    public const string Stream = "USERS";
    public const string All = "users.>";

    public const string TenantAssigned = "users.events.tenant-assigned";
}

/// <summary>The user got their tenant slug (first time the company was set).</summary>
public record TenantAssigned(Guid UserId, string Tenant);
