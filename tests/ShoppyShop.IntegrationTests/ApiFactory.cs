using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using ShoppyShop.Infrastructure.Assistant;

namespace ShoppyShop.IntegrationTests;

public sealed class ApiFactory(
    string connectionString,
    Func<IServiceProvider, IAssistantModelClient>? assistantModelClientFactory = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder
            .UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:Postgres", connectionString)
            .UseSetting("Jwt:Issuer", "ShoppyShop.Tests")
            .UseSetting("Jwt:Audience", "ShoppyShop.Tests")
            .UseSetting("Jwt:SigningKey", "integration-test-signing-key-that-is-at-least-thirty-two-bytes")
            .UseSetting("BootstrapAdmin:Email", "admin@example.test")
            .UseSetting("BootstrapAdmin:Password", "Admin!IntegrationPassword123")
            .UseSetting("Database:AutoMigrate", "true")
            .ConfigureTestServices(services =>
            {
                // Never call the real Anthropic API from tests — swap in a fake/stub model client.
                services.RemoveAll<IAssistantModelClient>();
                services.AddSingleton(assistantModelClientFactory ?? (_ => new StubAssistantModelClient()));
            });
    }
}

internal sealed class StubAssistantModelClient : IAssistantModelClient
{
    public Task<AssistantModelTurn> SendAsync(IReadOnlyList<AssistantModelMessage> messages, CancellationToken cancellationToken) =>
        Task.FromResult(new AssistantModelTurn([new AssistantTextBlock("stub reply")], []));
}