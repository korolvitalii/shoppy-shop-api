using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using ShoppyShop.Application;
using ShoppyShop.Infrastructure.Assistant;

using Testcontainers.PostgreSql;

namespace ShoppyShop.IntegrationTests;

public sealed class AssistantFlowTests : IAsyncLifetime, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private ApiFactory? factory;
    private HttpClient? client;

    public async Task InitializeAsync() => await postgres.StartAsync();

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (factory is not null)
        {
            await factory.DisposeAsync();
        }

        await postgres.DisposeAsync();
    }

    public void Dispose()
    {
        client?.Dispose();
        factory?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AssistantChatSearchesRealCatalogueAndReturnsRecommendedProducts()
    {
        CreateFactory(_ => new StubAssistantModelClient());
        var underFifty = await Client.GetFromJsonAsync<ProductPageDto>("/api/products?price=0-50", JsonOptions);
        var recommendedId = underFifty!.Items.First().Id;

        var model = new QueuedAssistantModelClient(
            new AssistantModelTurn(
                [],
                [new AssistantToolUseBlock("tool-1", "search_products", ToolInput(new { price = "0-50" }))]),
            new AssistantModelTurn(
                [new AssistantTextBlock($"Here you go!\nRECOMMENDED_IDS: {recommendedId}")],
                []));
        CreateFactory(_ => model);

        var response = await Client.PostAsJsonAsync(
            "/api/assistant/chat",
            new { message = "show me something cheap" },
            JsonOptions);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AssistantChatResponseBody>(JsonOptions);
        Assert.Equal("Here you go!", body!.Reply);
        Assert.Contains(body.Products, p => p.Id == recommendedId);
    }

    [Fact]
    public async Task AssistantChatRejectsEmptyMessageWithBadRequest()
    {
        CreateFactory(_ => new QueuedAssistantModelClient(new AssistantModelTurn([new AssistantTextBlock("ok")], [])));

        var response = await Client.PostAsJsonAsync("/api/assistant/chat", new { message = "" }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AssistantChatIsRateLimitedPerIpAfterTwentyRequestsPerHour()
    {
        CreateFactory(_ => new QueuedAssistantModelClient(new AssistantModelTurn([new AssistantTextBlock("ok")], [])));

        HttpResponseMessage? last = null;
        for (var i = 0; i < 21; i++)
        {
            last?.Dispose();
            last = await Client.PostAsJsonAsync("/api/assistant/chat", new { message = $"message {i}" }, JsonOptions);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
    }

    private void CreateFactory(Func<IServiceProvider, IAssistantModelClient> assistantModelClientFactory)
    {
        client?.Dispose();
        factory?.Dispose();
        factory = new ApiFactory(postgres.GetConnectionString(), assistantModelClientFactory);
        client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });
    }

    private HttpClient Client => client ?? throw new InvalidOperationException("Test client has not been initialized.");

    private static Dictionary<string, JsonElement> ToolInput(object payload) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(payload))!;

    private sealed record AssistantChatResponseBody(string Reply, IReadOnlyList<ProductDto> Products);
}

internal sealed class QueuedAssistantModelClient(params AssistantModelTurn[] responses) : IAssistantModelClient
{
    private int index;

    public Task<AssistantModelTurn> SendAsync(IReadOnlyList<AssistantModelMessage> messages, CancellationToken cancellationToken)
    {
        var turn = responses[Math.Min(index, responses.Length - 1)];
        index++;
        return Task.FromResult(turn);
    }
}