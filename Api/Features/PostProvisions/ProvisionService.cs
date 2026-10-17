namespace Api.Features.PostProvisions;

/// <summary>Wolverine message: a provision row was created and the service should now be started.</summary>
public record ProvisionService(Guid ProvisionId);
