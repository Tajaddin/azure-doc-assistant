using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace DocAssistant.Api.Auth;

public sealed class AuthOptions
{
    public const string Section = "Auth";
    /// <summary>entra: Microsoft Entra ID bearer tokens (production). dev: tokens minted by POST /auth/dev-token (local, CI).</summary>
    public string Mode { get; set; } = "dev";
    public DevAuthOptions Dev { get; set; } = new();
}

public sealed class DevAuthOptions
{
    public string Issuer { get; set; } = "docassistant-dev";
    public string Audience { get; set; } = "docassistant";
    /// <summary>HS256 key, at least 32 bytes. Generated per machine in .env; the default only exists so tests start.</summary>
    public string SigningKey { get; set; } = "dev-only-signing-key-change-me-0123456789";
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
