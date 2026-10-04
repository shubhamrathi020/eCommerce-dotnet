using System.ComponentModel.DataAnnotations;
using EcomApi.Config;
using EcomApi.Domain;
using EcomApi.Http;
using EcomApi.Services;
using EcomApi.Services.Commerce;
using Microsoft.AspNetCore.Mvc;

namespace EcomApi.Controllers;

/// <summary>Shared helpers: every response is written the way Express would (JSON, text, or nothing, with an explicit status).</summary>
[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    protected static IActionResult Ok(object? body, int status = 200) => new ApiResult(status, body);

    protected static IActionResult Created(object? body) => new ApiResult(201, body);

    protected static IActionResult NoBody(int status = 204) => new ApiResult(status, null);

    protected static IActionResult Text(string text) => new ApiResult(200, null, text);

    protected AuthUser CurrentUser => HttpContext.MustUser();

    protected AuthUser? OptionalUser => HttpContext.UserOf();

    /// <summary>Who is asking: the signed-in user, or the browser identified by the guest cart cookie.</summary>
    protected OwnerContext Owner()
    {
        var user = OptionalUser;
        return user is not null ? new OwnerContext { UserId = user.Id } : new OwnerContext { GuestToken = Cookies.Read(Request, Cookies.Guest) ?? "" };
    }

    /// <summary>The cart/order key of this request, minting the guest cookie the first time it is needed.</summary>
    protected string CartKey(ApiConfig config) => Cookies.OwnerKeyFor(OptionalUser?.Id, Cookies.EnsureGuestToken(Request, Response, config.Production));
}

public sealed class RegisterBody
{
    [MaxLength(200)] public required string Name { get; set; }
    [MaxLength(320)] public required string Email { get; set; }
    [MaxLength(200)] public required string Password { get; set; }
}

public sealed class LoginBody
{
    [MaxLength(320)] public required string Email { get; set; }
    [MaxLength(200)] public required string Password { get; set; }
}

public sealed class TokenBody
{
    [MaxLength(200)] public required string Token { get; set; }
}

public sealed class EmailBody
{
    [MaxLength(320)] public required string Email { get; set; }
}

public sealed class ResetPasswordBody
{
    [MaxLength(200)] public required string Token { get; set; }
    [MaxLength(200)] public required string Password { get; set; }
}

public sealed class SignInResponse
{
    public Session Session { get; set; } = new();
    public string AccessToken { get; set; } = "";
}

[Route("auth")]
public sealed class AuthController(AuthService auth, CartService carts, ApiConfig config) : ApiControllerBase
{
    private SignInResponse Respond(SignedIn signedIn)
    {
        Cookies.SetRefresh(Response, signedIn.Refresh.Token, signedIn.Refresh.ExpiresAt, config.Production);
        return new SignInResponse { Session = signedIn.Session, AccessToken = signedIn.AccessToken };
    }

    /// <summary>Whatever was in the guest cart (identified by the gcid cookie, if any) moves into the now-signed-in user's own cart.</summary>
    private async Task MergeGuestCartAsync(SignedIn signedIn)
    {
        var guest = Cookies.Read(Request, Cookies.Guest);
        if (guest is not null) await carts.MergeGuestIntoUserAsync($"guest:{guest}", signedIn.Session.User.Id);
    }

    [HttpPost("register"), StrictThrottle]
    public async Task<IActionResult> Register([FromBody] RegisterBody body)
    {
        var signedIn = await auth.RegisterAsync(body.Name, body.Email, body.Password);
        await MergeGuestCartAsync(signedIn);
        return Created(Respond(signedIn));
    }

    [HttpPost("login"), StrictThrottle]
    public async Task<IActionResult> Login([FromBody] LoginBody body)
    {
        var signedIn = await auth.LoginAsync(body.Email, body.Password);
        await MergeGuestCartAsync(signedIn);
        return Ok(Respond(signedIn));
    }

    [HttpPost("refresh"), Csrf]
    public async Task<IActionResult> Refresh()
    {
        try
        {
            return Ok(Respond(await auth.RefreshAsync(Cookies.Read(Request, Cookies.Refresh))));
        }
        catch (Exception)
        {
            Cookies.ClearRefresh(Response, config.Production);
            throw;
        }
    }

    [HttpPost("logout"), Csrf]
    public async Task<IActionResult> Logout()
    {
        await auth.LogoutAsync(Cookies.Read(Request, Cookies.Refresh));
        Cookies.ClearRefresh(Response, config.Production);
        return NoBody();
    }

    [HttpGet("me"), RequireAuth]
    public async Task<IActionResult> Me() => Ok(await auth.SessionAsync(CurrentUser.Id));

    [HttpPost("verify-email"), StrictThrottle]
    public async Task<IActionResult> VerifyEmail([FromBody] TokenBody body)
    {
        await auth.VerifyEmailAsync(body.Token);
        return NoBody();
    }

    [HttpPost("password-reset/request"), StrictThrottle]
    public async Task<IActionResult> RequestReset([FromBody] EmailBody body)
    {
        await auth.RequestPasswordResetAsync(body.Email);
        return NoBody();
    }

    [HttpPost("password-reset"), StrictThrottle]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordBody body)
    {
        await auth.ResetPasswordAsync(body.Token, body.Password);
        return NoBody();
    }
}

public sealed class ProfileBody
{
    [MaxLength(200)] public required string Name { get; set; }
    [MaxLength(20)] public string? Phone { get; set; }
}

public sealed class ChangePasswordBody
{
    [MaxLength(200)] public required string CurrentPassword { get; set; }
    [MaxLength(200)] public required string NewPassword { get; set; }
}

public sealed class PasswordBody
{
    [MaxLength(200)] public required string Password { get; set; }
}

public sealed class AccountsController(AccountsService accounts, ApiConfig config) : ApiControllerBase
{
    [HttpPatch("account/profile"), RequireAuth("profile:write:own")]
    public async Task<IActionResult> UpdateProfile([FromBody] ProfileBody body) => Ok(await accounts.UpdateProfileAsync(CurrentUser.Id, body.Name, body.Phone));

    /// <summary>Under /auth so the browser sends the refresh cookie, which identifies the device to keep signed in.</summary>
    [HttpPost("auth/change-password"), RequireAuth("profile:write:own")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordBody body)
    {
        await accounts.ChangePasswordAsync(CurrentUser.Id, body.CurrentPassword, body.NewPassword, Cookies.Read(Request, Cookies.Refresh));
        return NoBody();
    }

    [HttpGet("account/export"), RequireAuth]
    public async Task<IActionResult> ExportData() => Ok(await accounts.ExportDataAsync(CurrentUser.Id));

    [HttpPost("account/delete"), RequireAuth("profile:write:own")]
    public async Task<IActionResult> DeleteAccount([FromBody] PasswordBody body)
    {
        await accounts.DeleteAccountAsync(CurrentUser.Id, body.Password);
        Cookies.ClearRefresh(Response, config.Production);
        return NoBody();
    }
}

[Route("account/addresses"), RequireAuth("address:write:own")]
public sealed class AddressesController(AddressesService addresses) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List() => Ok(await addresses.ListAsync(CurrentUser.Id));

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddressInput body) => Created(await addresses.AddAsync(CurrentUser.Id, body));

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] AddressInput body) => Ok(await addresses.UpdateAsync(CurrentUser.Id, id, body));

    [HttpDelete("{id}")]
    public async Task<IActionResult> Remove(string id) => Ok(await addresses.RemoveAsync(CurrentUser.Id, id));

    [HttpPost("{id}/default")]
    public async Task<IActionResult> SetDefault(string id) => Ok(await addresses.SetDefaultAsync(CurrentUser.Id, id));
}
