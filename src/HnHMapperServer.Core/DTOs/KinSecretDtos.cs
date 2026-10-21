namespace HnHMapperServer.Core.DTOs;

/// <summary>
/// Body of POST /client/{token}/kinSecret, sent by the game client's Auto Kin feature.
/// An empty <see cref="Secret"/> removes the character's row.
/// </summary>
public class KinSecretUploadDto
{
    public string? Genus { get; set; }

    public string? CharName { get; set; }

    public string? Secret { get; set; }
}

/// <summary>One entry of GET /client/{token}/kinSecrets.</summary>
public class KinSecretDto
{
    public string CharName { get; set; } = string.Empty;

    public string Secret { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }
}
