using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace DocAssistant.Api.Auth;

public sealed class AuthOptions
{
    public const string Section = "Auth";
    public const string EntraMode = "entra";
    public const string DevMode = "dev";

    /// <summary>entra: Microsoft Entra ID bearer tokens (production). dev: tokens minted by POST /auth/dev-token (Development environment only).</summary>
    public string Mode { get; set; } = EntraMode;
    public DevAuthOptions Dev { get; set; } = new();

    public void Validate(IHostEnvironment env)
    {
        switch (Mode)
        {
            case EntraMode:
                return;
            case DevMode when !env.IsDevelopment():
                throw new InvalidOperationException(
                    "Auth:Mode=dev lets anyone mint a token for any user and only runs in the Development environment " +
                    $"(this is {env.EnvironmentName}); use Auth:Mode=entra");
            case DevMode when Encoding.UTF8.GetByteCount(Dev.SigningKey) < 32:
                throw new InvalidOperationException("Auth:Dev:SigningKey must be at least 32 bytes");
            case DevMode:
                return;
            default:
                throw new InvalidOperationException($"unknown Auth:Mode '{Mode}'; expected '{EntraMode}' or '{DevMode}'");
        }
    }
}

public sealed class DevAuthOptions
{
    public string Issuer { get; set; } = "docassistant-dev";
    public string Audience { get; set; } = "docassistant";
    /// <summary>HS256 key, at least 32 bytes. Set in appsettings.Development.json or .env; there is no default.</summary>
    public string SigningKey { get; set; } = "";
    public int LifetimeMinutes { get; set; } = 480;
}

/// <summary>Issues development JWTs with the same claims Entra ID provides (oid, name, preferred_username).</summary>
public sealed class DevTokenIssuer(DevAuthOptions options)
{
    public SymmetricSecurityKey Key { get; } = new(Encoding.UTF8.GetBytes(options.SigningKey));

    public string Issue(string user)
    {
        var id = Guid.NewGuid();
        var deterministic = DeterministicId(user);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, deterministic.ToString()),
            new Claim("oid", deterministic.ToString()),
            new Claim("name", user),
            new Claim("preferred_username", $"{user}@dev.local"),
            new Claim(JwtRegisteredClaimNames.Jti, id.ToString()),
        };
        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(options.LifetimeMinutes),
            signingCredentials: new SigningCredentials(Key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>The same user name always maps to the same object id, so a dev user keeps their documents across tokens.</summary>
    public static Guid DeterministicId(string user)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("docassistant-dev:" + user.ToLowerInvariant()));
        return new Guid(bytes.AsSpan(0, 16));
    }
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>Entra's object id when present (stable across apps), otherwise the subject.</summary>
    public static string OwnerId(this ClaimsPrincipal user) =>
        user.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
        ?? user.FindFirstValue("oid")
        ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? user.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? throw new InvalidOperationException("token has no oid or sub claim");
}
