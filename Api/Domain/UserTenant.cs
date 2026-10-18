namespace Api.Domain;

/// <summary>Local copy of a user's tenant slug, from UserService's TenantAssigned event.</summary>
public class UserTenant
{
    public Guid UserId { get; set; }
    public required string Tenant { get; set; }
}
