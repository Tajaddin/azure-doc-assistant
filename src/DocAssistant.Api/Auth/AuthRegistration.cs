using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Tokens;

namespace DocAssistant.Api.Auth;

public static class AuthRegistration
{
    /// <summary>
    /// Bearer authentication for the configured mode. Fails at start-up rather than falling back: dev mode mints a
    /// token for any user name, so it refuses to run outside the Development environment.
    /// </summary>
    public static AuthOptions AddDocAssistantAuth(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var auth = config.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
        auth.Validate(env);
        services.AddSingleton(auth);
        if (auth.Mode == AuthOptions.EntraMode)
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApi(config.GetSection("AzureAd"));
        }
        else
        {
            var issuer = new DevTokenIssuer(auth.Dev);
            services.AddSingleton(issuer);
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
            {
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = auth.Dev.Issuer,
                    ValidAudience = auth.Dev.Audience,
                    IssuerSigningKey = issuer.Key,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "name",
                };
                o.MapInboundClaims = false;
            });
        }
        // Every RequireAuthorization() endpoint: a signed-in user whose token was issued for this API's scope.
        services.AddAuthorization(o => o.DefaultPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => ctx.User.HasScope(AuthOptions.ApiScope))
            .Build());
        return auth;
    }
}
