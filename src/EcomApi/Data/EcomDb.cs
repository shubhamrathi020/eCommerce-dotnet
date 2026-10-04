using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql;
using NpgsqlTypes;

namespace EcomApi.Data;

/// <summary>
/// The database context over the tables the NestJS API's Prisma migrations own. Column names are the Prisma
/// field names (camelCase), timestamps are naive UTC, and JSON columns are jsonb read and written as text.
/// </summary>
public sealed class EcomDb(DbContextOptions<EcomDb> options) : DbContext(options)
{
    /// <summary>Enum labels are stored exactly as written (the Prisma enum values are lower case).</summary>
    public static readonly Npgsql.NameTranslation.NpgsqlNullNameTranslator NoTranslation = new();

    public DbSet<UserRow> Users => Set<UserRow>();
    public DbSet<RefreshTokenRow> RefreshTokens => Set<RefreshTokenRow>();
    public DbSet<LoginAttemptRow> LoginAttempts => Set<LoginAttemptRow>();
    public DbSet<SavedAddressRow> SavedAddresses => Set<SavedAddressRow>();
    public DbSet<CartRow> Carts => Set<CartRow>();
    public DbSet<OutboxEventRow> OutboxEvents => Set<OutboxEventRow>();
    public DbSet<OrderRow> Orders => Set<OrderRow>();

    /// <summary>Builds the Npgsql data source for a Prisma-style DATABASE_URL (its ?schema= parameter is translated).</summary>
    public static NpgsqlDataSource CreateDataSource(string databaseUrl)
    {
        var uri = new Uri(databaseUrl.Replace("postgresql://", "postgres://", StringComparison.Ordinal));
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
        };
        var schema = query["schema"];
        if (!string.IsNullOrEmpty(schema) && schema != "public") builder.SearchPath = schema;
        var dataSource = new NpgsqlDataSourceBuilder(builder.ConnectionString);
        dataSource.MapEnum<RoleKind>("Role", NoTranslation);
        return dataSource.Build();
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // The database stores naive UTC timestamps; Npgsql only accepts Unspecified kinds for those.
        builder.Properties<DateTime>().HaveConversion<UtcConverter>().HaveColumnType("timestamp(3) without time zone");
        builder.Properties<DateTime?>().HaveConversion<NullableUtcConverter>().HaveColumnType("timestamp(3) without time zone");
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasPostgresEnum("Role", new[] { "customer", "admin" });
        model.Entity<UserRow>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.HasMany(x => x.Addresses).WithOne().HasForeignKey(a => a.UserId);
        });
        model.Entity<RefreshTokenRow>(e => { e.ToTable("refresh_tokens"); e.HasKey(x => x.Id); });
        model.Entity<LoginAttemptRow>(e => { e.ToTable("login_attempts"); e.HasKey(x => x.Email); });
        model.Entity<SavedAddressRow>(e => { e.ToTable("saved_addresses"); e.HasKey(x => x.Id); });
        model.Entity<CartRow>(e => { e.ToTable("carts"); e.HasKey(x => x.Id); e.Property(x => x.Items).HasColumnType("jsonb"); });
        model.Entity<OutboxEventRow>(e => { e.ToTable("outbox_events"); e.HasKey(x => x.Id); e.Property(x => x.Payload).HasColumnType("jsonb"); });
        model.Entity<OrderRow>(e =>
        {
            e.ToTable("orders");
            e.HasKey(x => x.Id);
            foreach (var column in new[] { nameof(OrderRow.Lines), nameof(OrderRow.Totals), nameof(OrderRow.Timeline), nameof(OrderRow.Notes) }) e.Property(column).HasColumnType("jsonb");
        });

        foreach (var entity in model.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                property.SetColumnName(char.ToLowerInvariant(property.Name[0]) + property.Name[1..]);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // Prisma's @updatedAt has no database default, so the application stamps it.
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            var updated = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "UpdatedAt");
            if (updated is not null && entry.State == EntityState.Modified) updated.CurrentValue = now;
            if (entry.State == EntityState.Added)
            {
                if (updated is not null && (DateTime)updated.CurrentValue! == default) updated.CurrentValue = now;
                var created = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "CreatedAt");
                if (created is not null && (DateTime)created.CurrentValue! == default) created.CurrentValue = now;
            }
        }
        return base.SaveChangesAsync(cancellationToken);
    }

    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(v => DateTime.SpecifyKind(v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v, DateTimeKind.Unspecified), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcConverter() : ValueConverter<DateTime?, DateTime?>(v => v.HasValue ? DateTime.SpecifyKind(v.Value.Kind == DateTimeKind.Local ? v.Value.ToUniversalTime() : v.Value, DateTimeKind.Unspecified) : v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}

/// <summary>Prisma-style ids: a cuid is 'c' + time + counter + fingerprint + random, 25 characters.</summary>
public static class Ids
{
    private static int counter;

    public static string NewId()
    {
        var ts = Base36(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var count = Base36((uint)Interlocked.Increment(ref counter) % 1_679_616u).PadLeft(4, '0');
        var fingerprint = Base36(RandomNumberGenerator.GetInt32(0, 1_679_616)).PadLeft(4, '0');
        var random = Base36((long)RandomNumberGenerator.GetInt32(0, int.MaxValue) * 1000 + RandomNumberGenerator.GetInt32(0, 1000)).PadLeft(8, '0');
        return "c" + ts + count + fingerprint + random[^8..];
    }

    public static string Base36(long value)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (value == 0) return "0";
        var sb = new StringBuilder();
        while (value > 0)
        {
            sb.Insert(0, digits[(int)(value % 36)]);
            value /= 36;
        }
        return sb.ToString();
    }

    /// <summary>Formats a database timestamp (naive UTC) as an ISO string.</summary>
    public static string Iso(DateTime t) => EcomApi.Domain.Derive.Iso(DateTime.SpecifyKind(t, DateTimeKind.Utc));
}
