using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ShoppyShop.IntegrationTests;

public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
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
            .UseSetting("Database:AutoMigrate", "true");
    }
}