namespace Api.Domain;

public class ServiceVersion
{
    public int Id { get; set; }
    public required string ServiceId { get; set; }
    public required string Version { get; set; }
}
