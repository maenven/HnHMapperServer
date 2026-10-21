using HnHMapperServer.Core.DTOs;

namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// Auto Kin: hearth secrets shared between a tenant's admins so their game clients can kin
/// each other's characters. Only users who are TenantAdmin of the tenant are in the pool:
/// they alone may publish, and reads return only rows published by current admins.
/// Validation failures throw ArgumentException (→ 400).
/// </summary>
public interface IKinSecretService
{
    /// <summary>True when the user is a TenantAdmin of the tenant.</summary>
    Task<bool> IsPoolMemberAsync(string tenantId, string userId, CancellationToken ct = default);

    /// <summary>
    /// Stores the character's secret for its world, replacing any earlier one. An empty
    /// secret deletes the row.
    /// </summary>
    Task SetSecretAsync(string tenantId, string userId, KinSecretUploadDto upload, CancellationToken ct = default);

    /// <summary>Every secret in the tenant for one world, from characters of current admins.</summary>
    Task<List<KinSecretDto>> GetSecretsAsync(string tenantId, string? genus, CancellationToken ct = default);
}
