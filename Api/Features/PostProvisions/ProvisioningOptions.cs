using System.ComponentModel.DataAnnotations;

namespace Api.Features.PostProvisions;

public class ProvisioningOptions
{
    public const string SectionName = "Provisioning";

    /// <summary>Id of the only service we sell; sent to PaymentService as service_id.</summary>
    public const string ServiceId = "wordpress";

    [Required]
    public string StripePriceId { get; set; } = "";

    /// <summary>Sites are served at site.tenant.BaseDomain.</summary>
    [Required]
    public string BaseDomain { get; set; } = "";
}
