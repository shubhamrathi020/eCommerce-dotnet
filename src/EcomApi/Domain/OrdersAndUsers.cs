using System.Text.RegularExpressions;

namespace EcomApi.Domain;

public sealed class ContactDetails
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
}

public sealed class Address
{
    public string Line1 { get; set; } = "";
    public string? Line2 { get; set; }
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string Pincode { get; set; } = "";
}

public sealed class TimelineEntry
{
    public string Status { get; set; } = "";
    public string Label { get; set; } = "";
    public string? At { get; set; }
}

/// <summary>An order as shoppers see it.</summary>
public class Order
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "";
    public string PaymentStatus { get; set; } = "";
    public string PaymentMethod { get; set; } = "";
    public List<CartLine> Lines { get; set; } = new();
    public CartTotals Totals { get; set; } = new();
    public string? CouponCode { get; set; }
    public string ShippingMethod { get; set; } = "";
    public ContactDetails Contact { get; set; } = new();
    public Address Address { get; set; } = new();
    public List<TimelineEntry> Timeline { get; set; } = new();
    public string CreatedAt { get; set; } = "";
    public string? UserId { get; set; }
}

public sealed class OrderNote
{
    public string Id { get; set; } = "";
    public string At { get; set; } = "";
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>An order with its staff-only fields.</summary>
public sealed class AdminOrderDetail : Order
{
    public List<OrderNote> Notes { get; set; } = new();
    public List<string> AllowedNext { get; set; } = new();
}

public sealed class AdminOrderRow
{
    public string Id { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public long ItemCount { get; set; }
    public Money Total { get; set; } = new();
    public string Status { get; set; } = "";
    public string PaymentStatus { get; set; } = "";
    public string PaymentMethod { get; set; } = "";
}

public sealed class PaymentSession
{
    public string OrderId { get; set; } = "";
    public string ProviderOrderId { get; set; } = "";
    public string KeyId { get; set; } = "";
    public long Amount { get; set; }
    public string Currency { get; set; } = "INR";
}

public static class Orders
{
    /// <summary>The allowed order state machine: the one place this is defined.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Transitions = new Dictionary<string, string[]>
    {
        ["pending_payment"] = ["cancelled"],
        ["confirmed"] = ["packed", "cancelled"],
        ["packed"] = ["shipped", "cancelled"],
        ["shipped"] = ["delivered"],
        ["delivered"] = [],
        ["cancelled"] = [],
    };

    public static bool CanTransition(string from, string to) => Transitions.TryGetValue(from, out var next) && next.Contains(to);
}

/// <summary>A user role stored in the database. "seller" exists only in the frontend mock.</summary>
public static class Roles
{
    public const string Customer = "customer";
    public const string Admin = "admin";

    private static readonly string[] CustomerPermissions = ["profile:write:own", "address:write:own", "order:read:own", "order:cancel:own", "return:write:own", "support:write:own"];
    private static readonly string[] SellerPermissions = ["seller:portal"];
    private static readonly string[] AdminPermissions =
    [
        "seller:manage", "product:read", "product:write", "order:read:any", "order:refund", "user:read", "coupon:write", "review:moderate", "content:write",
        "inventory:write", "notification:manage", "return:manage", "support:manage", "promotion:manage", "recommendation:manage", "analytics:read", "system:read", "system:write",
    ];

    /// <summary>The permissions a set of roles grants; customer first, then seller, then admin, like the TypeScript original.</summary>
    public static List<string> PermissionsFor(IEnumerable<string> roles)
    {
        var list = roles.ToList();
        var seen = new HashSet<string>();
        var result = new List<string>();
        void Add(IEnumerable<string> permissions)
        {
            foreach (var p in permissions)
                if (seen.Add(p)) result.Add(p);
        }
        Add(CustomerPermissions);
        if (list.Contains("seller")) Add(SellerPermissions);
        if (list.Contains("admin")) Add(AdminPermissions);
        return result;
    }

    private static readonly Regex Lower = new("[a-z]", RegexOptions.Compiled);
    private static readonly Regex Upper = new("[A-Z]", RegexOptions.Compiled);
    private static readonly Regex Digit = new("[0-9]", RegexOptions.Compiled);

    /// <summary>Why a password is too weak, or null when it is fine: at least 8 characters with upper case, lower case and a digit.</summary>
    public static string? PasswordProblem(string password)
    {
        // JavaScript counts UTF-16 units for .length, so a surrogate pair counts as two there.
        if (password.Length < 8) return "Use at least 8 characters.";
        if (!Lower.IsMatch(password) || !Upper.IsMatch(password) || !Digit.IsMatch(password)) return "Use upper case, lower case and a number.";
        return null;
    }
}

public sealed class User
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
    public List<string> Roles { get; set; } = new();
    public List<string> Permissions { get; set; } = new();
    public bool EmailVerified { get; set; }
    public string CreatedAt { get; set; } = "";
}

public sealed class Session
{
    public User User { get; set; } = new();
    public string ExpiresAt { get; set; } = "";
}

public sealed class SavedAddress
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public Address Address { get; set; } = new();
    public bool IsDefault { get; set; }
}

public sealed class ProfileExport
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
    public List<string> Roles { get; set; } = new();
    public bool EmailVerified { get; set; }
    public string CreatedAt { get; set; } = "";
}

public sealed class AccountExport
{
    public string ExportedAt { get; set; } = "";
    public ProfileExport Profile { get; set; } = new();
    public List<SavedAddress> Addresses { get; set; } = new();
    public List<string> OrderIds { get; set; } = new();
}
