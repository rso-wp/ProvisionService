namespace Api.Features.DeleteProvisions;

/// <summary>Wolverine message: a provision was marked Deleting and its container and database should be removed.</summary>
public record DeprovisionService(Guid ProvisionId);
