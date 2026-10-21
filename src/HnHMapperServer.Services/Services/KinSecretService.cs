using HnHMapperServer.Core.Cookbook;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Enums;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Auto Kin secret store. Client requests carry the tenant in HttpContext.Items rather than
/// claims, so every query names its tenant explicitly instead of relying on the query filter.
/// </summary>
public class KinSecretService : IKinSecretService
{
    private static readonly string AdminRole = TenantRole.TenantAdmin.ToString();

    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<KinSecretService> _logger;

    public KinSecretService(ApplicationDbContext dbContext, ILogger<KinSecretService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public Task<bool> IsPoolMemberAsync(string tenantId, string userId, CancellationToken ct = default)
    {
        return _dbContext.TenantUsers
            .AnyAsync(tu => tu.TenantId == tenantId && tu.UserId == userId && tu.JoinedAt != default && tu.RoleString == AdminRole, ct);
    }

    public async Task SetSecretAsync(string tenantId, string userId, KinSecretUploadDto upload, CancellationToken ct = default)
    {
        var genus = RequireGenus(upload.Genus);
        var charName = upload.CharName?.Trim();
        if (string.IsNullOrEmpty(charName) || charName.Length > KinSecretEntity.MaxCharNameLength)
        {
            throw new ArgumentException("Invalid character name");
        }

        var secret = upload.Secret?.Trim() ?? string.Empty;
        if (secret.Length > KinSecretEntity.MaxSecretLength || secret.Any(char.IsControl))
        {
            throw new ArgumentException("Invalid hearth secret");
        }

        var row = await _dbContext.KinSecrets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(k => k.TenantId == tenantId && k.Genus == genus && k.CharName == charName, ct);

        if (secret.Length == 0)
        {
            if (row != null)
            {
                _dbContext.KinSecrets.Remove(row);
                await _dbContext.SaveChangesAsync(ct);
            }
            return;
        }

        if (row == null)
        {
            row = new KinSecretEntity { TenantId = tenantId, Genus = genus, CharName = charName };
            _dbContext.KinSecrets.Add(row);
            _logger.LogInformation("Kin secret added for tenant {TenantId}, world {Genus}", tenantId, genus);
        }

        row.Secret = secret;
        row.UserId = userId;
        row.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task<List<KinSecretDto>> GetSecretsAsync(string tenantId, string? genus, CancellationToken ct = default)
    {
        var world = RequireGenus(genus);

        // Joined against current admins, so a demoted or removed admin's characters drop out
        // without having to delete their rows.
        var admins = _dbContext.TenantUsers
            .Where(tu => tu.TenantId == tenantId && tu.JoinedAt != default && tu.RoleString == AdminRole)
            .Select(tu => tu.UserId);

        return await _dbContext.KinSecrets.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(k => k.TenantId == tenantId && k.Genus == world && admins.Contains(k.UserId))
            .OrderBy(k => k.CharName)
            .Select(k => new KinSecretDto { CharName = k.CharName, Secret = k.Secret, UpdatedAt = k.UpdatedAt })
            .ToListAsync(ct);
    }

    private static string RequireGenus(string? genus)
    {
        return GameWorlds.Normalize(genus) ?? throw new ArgumentException("Invalid world");
    }
}
