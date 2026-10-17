namespace Api.Domain;

public class Service
{
    public required string Id { get; set; }
    public required string DockerImageName { get; set; }
    public required string StripePriceId { get; set; }
    public List<ServiceVersion> Versions { get; set; } = [];
}
