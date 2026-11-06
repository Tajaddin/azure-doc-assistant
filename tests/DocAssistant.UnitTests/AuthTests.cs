using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using DocAssistant.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace DocAssistant.UnitTests;

public class AuthRegistrationTests
{
    private const string Key = "unit-test-signing-key-0123456789abcdef";

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "DocAssistant.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value))).Build();

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Dev_mode_refuses_to_start_outside_development(string environment)
    {
        var config = Config(("Auth:Mode", "dev"), ("Auth:Dev:SigningKey", Key));
        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddDocAssistantAuth(config, new Env(environment)));
        Assert.Contains("Development environment", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Entra", Key, "Production")]   // a typo used to fall through to dev mode
    [InlineData("none", Key, "Development")]
    [InlineData("dev", "", "Development")]       // no built-in signing key any more
    [InlineData("dev", "too-short", "Development")]
    public void Unknown_modes_and_weak_dev_keys_fail_at_start_up(string mode, string signingKey, string environment)
    {
        var config = Config(("Auth:Mode", mode), ("Auth:Dev:SigningKey", signingKey));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddDocAssistantAuth(config, new Env(environment)));
    }

    [Fact]
    public void Without_configuration_the_api_expects_entra_and_registers_no_token_issuer()
    {
        var services = new ServiceCollection();
        var auth = services.AddDocAssistantAuth(Config(), new Env("Production"));
        Assert.Equal(AuthOptions.EntraMode, auth.Mode);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(DevTokenIssuer));

        var dev = new ServiceCollection();
        Assert.Equal(AuthOptions.DevMode, dev.AddDocAssistantAuth(Config(("Auth:Mode", "dev"), ("Auth:Dev:SigningKey", Key)), new Env("Development")).Mode);
        Assert.Contains(dev, d => d.ServiceType == typeof(DevTokenIssuer));
    }

    [Fact]
    public void Committed_settings_select_dev_auth_only_in_development()
    {
        var api = Path.Combine(RepoFiles.Root, "src", "DocAssistant.Api");
        IConfiguration Settings(string environment) => new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(api, "appsettings.json"))
            .AddJsonFile(Path.Combine(api, $"appsettings.{environment}.json"), optional: true)
            .Build();

        var production = Settings("Production");
        Assert.Equal(AuthOptions.EntraMode, production["Auth:Mode"]);
        Assert.Null(production["Auth:Dev:SigningKey"]);
        Assert.Equal(AuthOptions.DevMode, new ServiceCollection().AddDocAssistantAuth(Settings("Development"), new Env("Development")).Mode);
    }
}

public class ApiScopePolicyTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "DocAssistant.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static async Task<bool> AllowedAsync(string environment, IEnumerable<(string Key, string Value)> settings, ClaimsPrincipal user)
    {
        var services = new ServiceCollection().AddLogging();
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value))).Build();
        services.AddSingleton<IConfiguration>(config); // the web host registers it; Microsoft.Identity.Web's handlers need it
        services.AddDocAssistantAuth(config, new Env(environment));
        await using var provider = services.BuildServiceProvider();
        var policy = await provider.GetRequiredService<IAuthorizationPolicyProvider>().GetDefaultPolicyAsync();
        var result = await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, null, policy);
        return result.Succeeded;
    }

    private static ClaimsPrincipal Token(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)).Prepend(new Claim("oid", "user-1")), "Bearer"));

    public static TheoryData<string, bool> EntraTokens => new()
    {
        { "scp=access_as_user", true },
        { "scp=User.Read access_as_user", true },
        { "mapped-scope=access_as_user", true },
        { "scp=User.Read", false },
        { "roles=Admin", false },     // app-only (client credentials) token: no delegated scope
        { "none", false },
    };

    [Theory]
    [MemberData(nameof(EntraTokens))]
    public async Task Entra_mode_requires_the_api_scope(string claims, bool allowed)
    {
        var principal = claims switch
        {
            "scp=access_as_user" => Token(("scp", "access_as_user")),
            "scp=User.Read access_as_user" => Token(("scp", "User.Read access_as_user")),
            "mapped-scope=access_as_user" => Token(("http://schemas.microsoft.com/identity/claims/scope", "access_as_user")),
            "scp=User.Read" => Token(("scp", "User.Read")),
            "roles=Admin" => Token(("roles", "Admin"), ("http://schemas.microsoft.com/ws/2008/06/identity/claims/role", "Admin")),
            _ => Token(),
        };
        Assert.Equal(allowed, await AllowedAsync("Production", [("Auth:Mode", "entra")], principal));
    }

    [Fact]
    public async Task Dev_tokens_carry_the_scope_and_pass_the_same_policy()
    {
        var options = new DevAuthOptions { SigningKey = "unit-test-signing-key-0123456789abcdef" };
        var issuer = new DevTokenIssuer(options);
        var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(issuer.Issue("alice"), new TokenValidationParameters
        {
            ValidIssuer = options.Issuer,
            ValidAudience = options.Audience,
            IssuerSigningKey = issuer.Key,
        }, out _);
        Assert.True(principal.HasScope(AuthOptions.ApiScope));
        Assert.True(await AllowedAsync("Development", [("Auth:Mode", "dev"), ("Auth:Dev:SigningKey", options.SigningKey)], principal));
        Assert.False(await AllowedAsync("Development", [("Auth:Mode", "dev"), ("Auth:Dev:SigningKey", options.SigningKey)], Token()));
    }
}
