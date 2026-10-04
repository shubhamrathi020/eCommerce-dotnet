namespace EcomApi.Data;

// Rows of the tables the NestJS API's Prisma migrations create. This application never migrates the schema: these
// classes only describe it. Property names equal the Prisma field names (camelCase columns).

/// <summary>The PostgreSQL enum "Role".</summary>
public enum RoleKind
{
    customer,
    admin,
}

public sealed class UserRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
    public RoleKind[] Roles { get; set; } = [RoleKind.customer];
    public string PasswordHash { get; set; } = "";
    public bool EmailVerified { get; set; }
    public string? VerifyTokenHash { get; set; }
    public string? ResetTokenHash { get; set; }
    public DateTime? ResetExpires { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<SavedAddressRow> Addresses { get; set; } = [];
}

public sealed class RefreshTokenRow
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string? ReplacedBy { get; set; }
    public string Family { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class LoginAttemptRow
{
    public string Email { get; set; } = "";
    public int Count { get; set; }
    public DateTime FirstAt { get; set; }
    public DateTime? LockedUntil { get; set; }
}

public sealed class SavedAddressRow
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Label { get; set; } = "";
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Line1 { get; set; } = "";
    public string? Line2 { get; set; }
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string Pincode { get; set; } = "";
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class CartRow
{
    public string Id { get; set; } = "";
    public string OwnerKey { get; set; } = "";
    /// <summary>jsonb: the persisted cart items.</summary>
    public string Items { get; set; } = "[]";
    public string? CouponCode { get; set; }
    public string ShippingMethod { get; set; } = "standard";
    public DateTime? ReminderSentAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class OutboxEventRow
{
    public string Id { get; set; } = "";
    public string RoutingKey { get; set; } = "";
    /// <summary>jsonb.</summary>
    public string Payload { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

public sealed class OrderRow
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "";
    public string PaymentStatus { get; set; } = "";
    public string PaymentMethod { get; set; } = "";
    public string? UserId { get; set; }
    public string? GuestToken { get; set; }
    /// <summary>jsonb: the priced cart lines.</summary>
    public string Lines { get; set; } = "[]";
    /// <summary>jsonb: the cart totals.</summary>
    public string Totals { get; set; } = "{}";
    public string? CouponCode { get; set; }
    public string ShippingMethod { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string ContactEmail { get; set; } = "";
    public string ContactPhone { get; set; } = "";
    public string AddressLine1 { get; set; } = "";
    public string? AddressLine2 { get; set; }
    public string AddressCity { get; set; } = "";
    public string AddressState { get; set; } = "";
    public string AddressPincode { get; set; } = "";
    /// <summary>jsonb.</summary>
    public string Timeline { get; set; } = "[]";
    /// <summary>jsonb.</summary>
    public string Notes { get; set; } = "[]";
    public string IdempotencyKey { get; set; } = "";
    public DateTime? PaymentDeadline { get; set; }
    public string? ProviderOrderId { get; set; }
    public string? ProviderPaymentId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
