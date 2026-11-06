using DocAssistant.Api.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

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
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocAssistant.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        var api = Path.Combine(dir.FullName, "src", "DocAssistant.Api");
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
