using System.Text.Json;

using Anthropic;
using Anthropic.Models.Messages;

using Microsoft.Extensions.Options;

namespace ShoppyShop.Infrastructure.Assistant;

internal sealed class AnthropicAssistantModelClient : IAssistantModelClient, IDisposable
{
    private static readonly string[] SortValues = ["featured", "price-asc", "price-desc", "name"];
    private static readonly string[] PriceValues = ["all", "0-50", "50-200", "200+"];

    private const string SystemPrompt = """
        You are ShoppyShop's shopping assistant. You help visitors find products in the
        catalogue and answer short questions about what's available. You are not a
        general-purpose chatbot: politely decline anything unrelated to shopping on
        ShoppyShop and steer back to how you can help them find products.

        Use the search_products tool whenever the user is looking for products, asks
        about price or availability, or a prior message implies a search would help.
        Only ever mention, recommend, or describe products that were returned by a
        search_products tool call in this conversation - never invent a product,
        price, or availability that did not come from a tool result.

        Keep replies short and conversational (1-3 sentences). Reply in the same
        language the user is writing in. After search_products returns results, do
        not restate every field (price, brand, etc.) in prose - product cards render
        that detail separately; just give a brief conversational summary.

        If you used search_products and are recommending specific results, end your
        reply on its own final line formatted exactly as:
        RECOMMENDED_IDS: id1,id2,id3
        List only the product ids (from the tool result) you are actually
        recommending, most relevant first, comma-separated, no spaces around commas.
        Omit this line entirely if you found no relevant products or did not search.
        """;

    private readonly AnthropicClient client;
    private readonly string model;
    private readonly Tool searchProductsTool;

    public AnthropicAssistantModelClient(IOptions<AnthropicOptions> options)
    {
        var value = options.Value;
        client = new AnthropicClient { ApiKey = value.ApiKey, Timeout = TimeSpan.FromSeconds(20) };
        model = value.Model;
        searchProductsTool = BuildSearchProductsTool();
    }

    public async Task<AssistantModelTurn> SendAsync(
        IReadOnlyList<AssistantModelMessage> messages,
        CancellationToken cancellationToken)
    {
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = 1024,
            System = SystemPrompt,
            Tools = [searchProductsTool],
            Messages = messages.Select(ToSdkMessage).ToList(),
        };

        var response = await client.Messages.Create(parameters, cancellationToken: cancellationToken);

        var textBlocks = new List<AssistantTextBlock>();
        var toolUseBlocks = new List<AssistantToolUseBlock>();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var text))
            {
                textBlocks.Add(new AssistantTextBlock(text.Text));
            }
            else if (block.TryPickToolUse(out var toolUse))
            {
                toolUseBlocks.Add(new AssistantToolUseBlock(toolUse.ID, toolUse.Name, toolUse.Input));
            }
        }

        return new AssistantModelTurn(textBlocks, toolUseBlocks);
    }

    public void Dispose() => client.Dispose();

    private static MessageParam ToSdkMessage(AssistantModelMessage message) => new()
    {
        Role = message.Role == "assistant" ? Role.Assistant : Role.User,
        Content = message.Content.Select(ToSdkContentBlock).ToList(),
    };

    private static ContentBlockParam ToSdkContentBlock(AssistantContentBlock block) => block switch
    {
        AssistantTextBlock text => new TextBlockParam(text.Text),
        AssistantToolUseBlock toolUse => new ToolUseBlockParam
        {
            ID = toolUse.Id,
            Name = toolUse.Name,
            Input = toolUse.Input,
        },
        AssistantToolResultBlock toolResult => new ToolResultBlockParam
        {
            ToolUseID = toolResult.ToolUseId,
            Content = toolResult.Content,
            IsError = toolResult.IsError,
        },
        _ => throw new InvalidOperationException($"Unsupported content block type: {block.GetType()}"),
    };

    private static Tool BuildSearchProductsTool() => new()
    {
        Name = "search_products",
        Description = """
            Search the ShoppyShop product catalogue. Returns matching products with id,
            name, brand, price, salePrice, and inStock. Use this whenever the user asks
            to find, browse, or filter products, or mentions a price constraint. Call it
            again with different parameters if the first results do not seem to match
            what the user wants (e.g. narrow by price after an initial broad search).
            """,
        InputSchema = new InputSchema
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["search"] = JsonSerializer.SerializeToElement(new
                {
                    type = "string",
                    description = "Free-text keywords to match against product name, brand, or description, e.g. 'waterproof jacket'. Omit to match all products.",
                }),
                ["sort"] = JsonSerializer.SerializeToElement(new
                {
                    type = "string",
                    @enum = SortValues,
                    description = "Sort order. Use 'price-asc' for 'cheapest first' style requests.",
                }),
                ["price"] = JsonSerializer.SerializeToElement(new
                {
                    type = "string",
                    @enum = PriceValues,
                    description = "Price range preset. Use '0-50' for 'under $50', '50-200' for 'between 50 and 200', '200+' for 'over 200'.",
                }),
            },
            Required = [],
        },
    };
}