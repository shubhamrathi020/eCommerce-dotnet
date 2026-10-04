using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using EcomApi.Data;
using EcomApi.Domain;
using EcomApi.Http;
using Microsoft.EntityFrameworkCore;

namespace EcomApi.Services;

public sealed class AccountsService(EcomDb db, PasswordService passwords, TokenService tokens)
{
    private static readonly Regex Phone = new("^[6-9][0-9]{9}$", RegexOptions.Compiled);

    private async Task<UserRow> UserAsync(string userId) => await db.Users.FirstOrDefaultAsync(u => u.Id == userId) ?? throw new AppError(ApiErrorCode.Unauthorized, "Please sign in.");

    public async Task<User> UpdateProfileAsync(string userId, string rawName, string? phone)
    {
        var user = await UserAsync(userId);
        var fields = new Dictionary<string, string>();
        var name = rawName.Trim();
        if (name.Length == 0) fields["name"] = "Name is required";
        else if (name.Length > 80) fields["name"] = "Use 80 characters or fewer";
        if (!string.IsNullOrEmpty(phone) && !Phone.IsMatch(phone)) fields["phone"] = "Enter a valid 10-digit mobile number";
        AppError.AssertNoFieldErrors(fields);
        user.Name = name;
        user.Phone = string.IsNullOrEmpty(phone) ? null : phone;
        await db.SaveChangesAsync();
        return UserMapper.ToUser(user);
    }

    /// <summary>Signs out every other device; the one making the change (identified by its refresh cookie) stays signed in.</summary>
    public async Task ChangePasswordAsync(string userId, string current, string next, string? refreshToken)
    {
        var user = await UserAsync(userId);
        if (!await passwords.VerifyAsync(user.PasswordHash, current))
            throw new AppError(ApiErrorCode.Validation, "Your current password is incorrect.", new Dictionary<string, string> { ["currentPassword"] = "Incorrect password" });
        var problem = Roles.PasswordProblem(next);
        if (problem is not null) throw new AppError(ApiErrorCode.Validation, problem, new Dictionary<string, string> { ["newPassword"] = problem });
        user.PasswordHash = await passwords.HashAsync(next);
        await db.SaveChangesAsync();
        await tokens.RevokeAllForUserAsync(userId, await tokens.FamilyOfAsync(refreshToken));
    }

    public async Task<AccountExport> ExportDataAsync(string userId)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.Addresses).FirstOrDefaultAsync(u => u.Id == userId) ?? throw new AppError(ApiErrorCode.Unauthorized, "Please sign in.");
        var profile = UserMapper.ToUser(user);
        return new AccountExport
        {
            ExportedAt = Derive.Iso(DateTimeOffset.UtcNow),
            Profile = new ProfileExport { Id = profile.Id, Name = profile.Name, Email = profile.Email, Phone = profile.Phone, Roles = profile.Roles, EmailVerified = profile.EmailVerified, CreatedAt = profile.CreatedAt },
            // Orders still live in the frontend mock until they move to this API.
            Addresses = user.Addresses.OrderBy(a => a.CreatedAt).Select(AddressesService.ToSavedAddress).ToList(),
            OrderIds = [],
        };
    }

    /// <summary>Deletes the account and everything that belongs only to it (addresses, sessions) in one step.</summary>
    public async Task DeleteAccountAsync(string userId, string password)
    {
        var user = await UserAsync(userId);
        if (!await passwords.VerifyAsync(user.PasswordHash, password))
            throw new AppError(ApiErrorCode.Validation, "That password is not correct.", new Dictionary<string, string> { ["password"] = "Incorrect password" });
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.LoginAttempts.Where(a => a.Email == user.Email).ExecuteDeleteAsync();
        await db.RefreshTokens.Where(r => r.UserId == userId).ExecuteDeleteAsync();
        await db.SavedAddresses.Where(a => a.UserId == userId).ExecuteDeleteAsync();
        await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync();
        await tx.CommitAsync();
    }
}

public sealed class AddressInput
{
    [MaxLength(60)] public required string Label { get; set; }
    [MaxLength(120)] public required string Name { get; set; }
    [MaxLength(20)] public required string Phone { get; set; }
    public required AddressBody Address { get; set; }
}

public sealed class AddressBody
{
    [MaxLength(200)] public required string Line1 { get; set; }
    [MaxLength(200)] public string? Line2 { get; set; }
    [MaxLength(100)] public required string City { get; set; }
    [MaxLength(100)] public required string State { get; set; }
    [MaxLength(10)] public required string Pincode { get; set; }
}

public sealed class AddressesService(EcomDb db)
{
    private static readonly Regex Phone = new("^[6-9][0-9]{9}$", RegexOptions.Compiled);
    private static readonly Regex Pincode = new("^[1-9][0-9]{5}$", RegexOptions.Compiled);
    public const int MaxAddresses = 10;

    public static SavedAddress ToSavedAddress(SavedAddressRow row) => new()
    {
        Id = row.Id,
        Label = row.Label,
        Name = row.Name,
        Phone = row.Phone,
        Address = new Address { Line1 = row.Line1, Line2 = string.IsNullOrEmpty(row.Line2) ? null : row.Line2, City = row.City, State = row.State, Pincode = row.Pincode },
        IsDefault = row.IsDefault,
    };

    /// <summary>Same rules and messages as the mock address book the frontend was built against.</summary>
    private static void Validate(AddressInput input)
    {
        var fields = new Dictionary<string, string>();
        if (input.Label.Trim().Length == 0 || input.Label.Trim().Length > 30) fields["label"] = "Give this address a short label";
        if (input.Name.Trim().Length == 0) fields["name"] = "Name is required";
        if (!Phone.IsMatch(input.Phone)) fields["phone"] = "Enter a valid 10-digit mobile number";
        if (input.Address.Line1.Trim().Length == 0) fields["line1"] = "Address is required";
        if (input.Address.City.Trim().Length == 0) fields["city"] = "City is required";
        if (input.Address.State.Trim().Length == 0) fields["state"] = "State is required";
        if (!Pincode.IsMatch(input.Address.Pincode)) fields["pincode"] = "Enter a valid 6-digit pin code";
        AppError.AssertNoFieldErrors(fields);
    }

    private static void Apply(SavedAddressRow row, AddressInput input)
    {
        row.Label = input.Label.Trim();
        row.Name = input.Name.Trim();
        row.Phone = input.Phone;
        row.Line1 = input.Address.Line1.Trim();
        var line2 = input.Address.Line2?.Trim();
        row.Line2 = string.IsNullOrEmpty(line2) ? null : line2;
        row.City = input.Address.City.Trim();
        row.State = input.Address.State.Trim();
        row.Pincode = input.Address.Pincode;
    }

    public async Task<List<SavedAddress>> ListAsync(string userId) =>
        (await db.SavedAddresses.AsNoTracking().Where(a => a.UserId == userId).OrderBy(a => a.CreatedAt).ToListAsync()).Select(ToSavedAddress).ToList();

    public async Task<List<SavedAddress>> AddAsync(string userId, AddressInput input)
    {
        Validate(input);
        var count = await db.SavedAddresses.CountAsync(a => a.UserId == userId);
        if (count >= MaxAddresses) throw new AppError(ApiErrorCode.Validation, $"You can save up to {MaxAddresses} addresses.");
        var row = new SavedAddressRow { Id = Ids.NewId(), UserId = userId, IsDefault = count == 0 };
        Apply(row, input);
        db.SavedAddresses.Add(row);
        await db.SaveChangesAsync();
        return await ListAsync(userId);
    }

    public async Task<List<SavedAddress>> UpdateAsync(string userId, string id, AddressInput input)
    {
        Validate(input);
        // The user id is in every query: one customer can never touch another's address, even by guessing ids.
        var row = await db.SavedAddresses.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId) ?? throw new AppError(ApiErrorCode.NotFound, "Address not found");
        Apply(row, input);
        await db.SaveChangesAsync();
        return await ListAsync(userId);
    }

    public async Task<List<SavedAddress>> RemoveAsync(string userId, string id)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var removed = await db.SavedAddresses.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId);
        if (removed is not null)
        {
            db.SavedAddresses.Remove(removed);
            await db.SaveChangesAsync();
            // Removing the default promotes the oldest remaining address.
            if (removed.IsDefault)
            {
                var next = await db.SavedAddresses.Where(a => a.UserId == userId).OrderBy(a => a.CreatedAt).FirstOrDefaultAsync();
                if (next is not null)
                {
                    next.IsDefault = true;
                    await db.SaveChangesAsync();
                }
            }
        }
        await tx.CommitAsync();
        return await ListAsync(userId);
    }

    public async Task<List<SavedAddress>> SetDefaultAsync(string userId, string id)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        if (!await db.SavedAddresses.AnyAsync(a => a.Id == id && a.UserId == userId)) throw new AppError(ApiErrorCode.NotFound, "Address not found");
        await db.SavedAddresses.Where(a => a.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDefault, false));
        await db.SavedAddresses.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDefault, true));
        await tx.CommitAsync();
        return await ListAsync(userId);
    }
}
