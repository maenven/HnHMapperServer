using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Core.Enums;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Infrastructure.Data;
using HnHMapperServer.Infrastructure.Identity;
using HnHMapperServer.Services.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// Auto Kin secret store. Real SQLite, and no tenant context, exactly like a game client
/// request (the tenant comes from the token, not from claims): the unique key, the upsert and
/// the admin-only pool are all exercised for real.
/// </summary>
public class KinSecretServiceTests : IDisposable
{
    private const string TenantA = "kin-a";
    private const string TenantB = "kin-b";
    private const string W1 = "fd63ddee958da329";
    private const string W2 = "b7c199a4557503a8";
    private const string AdminA = "admin-a";
    private const string AdminA2 = "admin-a2";
    private const string UserA = "user-a";
    private const string AdminB = "admin-b";

    private readonly string _dbPath;
    private readonly ApplicationDbContext _db;
    private readonly KinSecretService _service;

    public KinSecretServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hnh-kinsecret-test-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;
        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();
        Seed();

        _service = new KinSecretService(_db, Mock.Of<ILogger<KinSecretService>>());
    }

    [Fact]
    public async Task OnlyJoinedAdminsAreInThePool()
    {
        Assert.True(await _service.IsPoolMemberAsync(TenantA, AdminA));
        Assert.False(await _service.IsPoolMemberAsync(TenantA, UserA));
        Assert.False(await _service.IsPoolMemberAsync(TenantA, AdminB)); // admin elsewhere only
        Assert.False(await _service.IsPoolMemberAsync(TenantA, "nobody"));
    }

    [Fact]
    public async Task Set_UpsertsOneRowPerCharacterAndWorld()
    {
        await _service.SetSecretAsync(TenantA, AdminA, Upload(W1, "Alice", "first"));
        await _service.SetSecretAsync(TenantA, AdminA2, Upload(W1, "Alice", " second "));

        var row = await _db.KinSecrets.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("second", row.Secret);
        Assert.Equal(AdminA2, row.UserId);

        var secrets = await _service.GetSecretsAsync(TenantA, W1);
        Assert.Equal("Alice", Assert.Single(secrets).CharName);
    }

    [Fact]
    public async Task Set_EmptySecretDeletesTheRow()
    {
        await _service.SetSecretAsync(TenantA, AdminA, Upload(W1, "Alice", "secret"));
        await _service.SetSecretAsync(TenantA, AdminA, Upload(W1, "Alice", ""));
        await _service.SetSecretAsync(TenantA, AdminA, Upload(W1, "Nobody", "")); // no row: no error

        Assert.Empty(await _db.KinSecrets.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Get_NeverLeaksAcrossTenantsOrWorlds()
    {
        await _service.SetSecretAsync(TenantA, AdminA, Upload(W1, "Alice", "a1"));
        await _service.SetSecretAsync(TenantA, AdminA, Upload(W2, "Alice", "a2"));
        await _service.SetSecretAsync(TenantB, AdminB, Upload(W1, "Bob", "b1"));

        var a1 = await _service.GetSecretsAsync(TenantA, W1);
        Assert.Equal("a1", Assert.Single(a1).Secret);

        var a2 = await _service.GetSecretsAsync(TenantA, W2);
        Assert.Equal("a2", Assert.Single(a2).Secret);

        var b1 = await _service.GetSecretsAsync(TenantB, W1);
        Assert.Equal("Bob", Assert.Single(b1).CharName);
    }

    [Fact]
    public async Task Get_DropsCharactersOfUsersNoLongerAdmin()
    {
        await _service.SetSecretAsync(TenantA, AdminA, Upload(W1, "Alice", "a"));
        await _service.SetSecretAsync(TenantA, AdminA2, Upload(W1, "Carol", "c"));

        var membership = await _db.TenantUsers.SingleAsync(tu => tu.TenantId == TenantA && tu.UserId == AdminA2);
        membership.Role = TenantRole.TenantUser;
        await _db.SaveChangesAsync();

        var secrets = await _service.GetSecretsAsync(TenantA, W1);
        Assert.Equal("Alice", Assert.Single(secrets).CharName);
    }

    [Theory]
    [InlineData(null, "Alice", "s")]
    [InlineData("  ", "Alice", "s")]
    [InlineData(W1, "", "s")]
    [InlineData(W1, "Alice", "bad\nsecret")]
    public async Task Set_RejectsInvalidInput(string? genus, string charName, string secret)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.SetSecretAsync(TenantA, AdminA, Upload(genus, charName, secret)));
    }

    [Fact]
    public async Task Set_RejectsOverlongValues()
    {
        var tooLong = new string('x', KinSecretEntity.MaxSecretLength + 1);
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.SetSecretAsync(TenantA, AdminA, Upload(W1, "Alice", tooLong)));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.SetSecretAsync(TenantA, AdminA, Upload(W1, tooLong, "s")));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.GetSecretsAsync(TenantA, new string('f', 65)));
    }

    private static KinSecretUploadDto Upload(string? genus, string charName, string secret) =>
        new() { Genus = genus, CharName = charName, Secret = secret };

    private void Seed()
    {
        var now = DateTime.UtcNow;
        foreach (var id in new[] { AdminA, AdminA2, UserA, AdminB })
        {
            _db.Users.Add(new ApplicationUser { Id = id, UserName = id, NormalizedUserName = id.ToUpperInvariant() });
        }
        _db.Tenants.AddRange(
            new TenantEntity { Id = TenantA, Name = "A", CreatedAt = now, IsActive = true },
            new TenantEntity { Id = TenantB, Name = "B", CreatedAt = now, IsActive = true });
        _db.TenantUsers.AddRange(
            Member(TenantA, AdminA, TenantRole.TenantAdmin, now),
            Member(TenantA, AdminA2, TenantRole.TenantAdmin, now),
            Member(TenantA, UserA, TenantRole.TenantUser, now),
            Member(TenantB, AdminB, TenantRole.TenantAdmin, now));
        _db.SaveChanges();
    }

    private static TenantUserEntity Member(string tenantId, string userId, TenantRole role, DateTime joinedAt) =>
        new() { TenantId = tenantId, UserId = userId, Role = role, JoinedAt = joinedAt };

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }
}
