using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using ShoppyShop.Application;

namespace ShoppyShop.Infrastructure.Assistant;

public sealed class AssistantService(
    IAssistantModelClient modelClient,
    ICatalogueService catalogueService,
    ILogger<AssistantService> logger) : IAssistantService
{
    private const int MaxHistoryTurns = 10;
    private const int MaxMessageLength = 1000;
    private const int MaxToolIterations = 4;
    private const int MaxProductsReturned = 6;

    private static readonly Regex RecommendedIdsPattern = new(
        @"(?m)^RECOMMENDED_IDS:\s*(.+)$",
        RegexOptions.Compiled);

    private static readonly Action<ILogger, Exception> LogAssistantModelCallFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(1, nameof(LogAssistantModelCallFailed)), "Assistant model call failed");

    public async Task<AssistantChatResponse> ChatAsync(AssistantChatRequest request, CancellationToken cancellationToken)
    {
        Validate(request);

        var messages = BuildInitialMessages(request);
        var seenProducts = new Dictionary<string, ProductDto>();

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            AssistantModelTurn turn;
            try
            {
                turn = await modelClient.SendAsync(messages, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogAssistantModelCallFailed(logger, ex);
                return FallbackResponse(seenProducts);
            }

            if (turn.ToolUseBlocks.Count == 0)
            {
                var replyText = string.Concat(turn.TextBlocks.Select(t => t.Text));
                return ExtractResponse(replyText, seenProducts);
            }

            messages.Add(new AssistantModelMessage(
                "assistant",
                [.. turn.TextBlocks.Cast<AssistantContentBlock>(), .. turn.ToolUseBlocks]));

            var toolResults = new List<AssistantContentBlock>();
            foreach (var toolUse in turn.ToolUseBlocks)
            {
                if (string.Equals(toolUse.Name, "search_products", StringComparison.Ordinal))
                {
                    var (resultJson, products, isError) = await ExecuteSearchProductsAsync(toolUse.Input, cancellationToken);
                    foreach (var product in products)
                    {
                        seenProducts.TryAdd(product.Id, product);
                    }

                    toolResults.Add(new AssistantToolResultBlock(toolUse.Id, resultJson, isError));
                    continue;
                }

                if (string.Equals(toolUse.Name, "list_categories", StringComparison.Ordinal))
                {
                    var resultJson = await ExecuteListCategoriesAsync(cancellationToken);
                    toolResults.Add(new AssistantToolResultBlock(toolUse.Id, resultJson, IsError: false));
                    continue;
                }

                toolResults.Add(new AssistantToolResultBlock(
                    toolUse.Id,
                    JsonSerializer.Serialize(new { error = $"Unknown tool '{toolUse.Name}'." }),
                    IsError: true));
            }

            messages.Add(new AssistantModelMessage("user", toolResults));
        }

        return FallbackResponse(seenProducts);
    }

    private async Task<(string ResultJson, IReadOnlyCollection<ProductDto> Products, bool IsError)> ExecuteSearchProductsAsync(
        IReadOnlyDictionary<string, JsonElement> input,
        CancellationToken cancellationToken)
    {
        if (!TryGetOptionalString(input, "search", out var search) ||
            !TryGetOptionalString(input, "sort", out var sort) ||
            !TryGetOptionalString(input, "price", out var price) ||
            !TryGetOptionalString(input, "groupId", out var groupId))
        {
            return (JsonSerializer.Serialize(new { error = "Tool arguments must be strings." }), [], true);
        }

        var (min, max) = PricePresets.Resolve(price, null, null);
        var query = new ProductQuery(search, sort, min, max);

        var products = string.IsNullOrWhiteSpace(groupId)
            ? await catalogueService.GetProductsAsync(query, cancellationToken)
            : await catalogueService.GetGroupProductsAsync(groupId, query, cancellationToken);
        var capped = products.Take(MaxProductsReturned).ToList();

        var trimmed = capped.Select(p => new
        {
            id = p.Id,
            groupId = p.GroupId,
            name = p.Name,
            brand = p.Brand,
            price = p.Price,
            salePrice = p.SalePrice,
            inStock = p.InStock,
        });
        return (JsonSerializer.Serialize(trimmed), capped, false);
    }

    private async Task<string> ExecuteListCategoriesAsync(CancellationToken cancellationToken)
    {
        var groups = await catalogueService.GetGroupsAsync(includeDeleted: false, cancellationToken);
        var trimmed = groups.Select(group => new
        {
            id = group.Id,
            name = group.Name,
            description = group.Description,
            productCount = group.ItemCount,
        });
        return JsonSerializer.Serialize(trimmed);
    }

    private static AssistantChatResponse ExtractResponse(string replyText, Dictionary<string, ProductDto> seenProducts)
    {
        var match = RecommendedIdsPattern.Match(replyText);
        var visibleReply = match.Success ? replyText[..match.Index].TrimEnd() : replyText.Trim();

        if (match.Success)
        {
            var ids = match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var recommended = ids.Where(seenProducts.ContainsKey).Select(id => seenProducts[id]).ToList();
            if (recommended.Count > 0)
            {
                return new AssistantChatResponse(visibleReply, recommended);
            }
        }

        return new AssistantChatResponse(visibleReply, seenProducts.Values.ToList());
    }

    private static AssistantChatResponse FallbackResponse(Dictionary<string, ProductDto> seenProducts) =>
        new(
            "Sorry, I'm having trouble responding right now. Please try again in a moment.",
            seenProducts.Values.ToList());

    private static List<AssistantModelMessage> BuildInitialMessages(AssistantChatRequest request)
    {
        var history = (request.History ?? [])
            .TakeLast(MaxHistoryTurns)
            .Select(t => new AssistantModelMessage(
                t.Role == "assistant" ? "assistant" : "user",
                [new AssistantTextBlock(Truncate(t.Content, MaxMessageLength))]));

        var messages = new List<AssistantModelMessage>(history)
        {
            new("user", [new AssistantTextBlock(request.Message.Trim())]),
        };
        return messages;
    }

    private static void Validate(AssistantChatRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var message = request.Message?.Trim() ?? "";
        if (message.Length == 0)
        {
            errors["message"] = ["Message is required."];
        }
        else if (message.Length > MaxMessageLength)
        {
            errors["message"] = [$"Message must be {MaxMessageLength} characters or fewer."];
        }

        if (request.History?.Any(t => t.Role is not ("user" or "assistant")) == true)
        {
            errors["history"] = ["History entries must have role 'user' or 'assistant'."];
        }
        else if (request.History?.Any(t => t.Content is null) == true)
        {
            errors["history"] = ["History entry content is required."];
        }

        if (errors.Count > 0)
        {
            throw new AppValidationException("Assistant chat request is invalid.", errors);
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static bool TryGetOptionalString(
        IReadOnlyDictionary<string, JsonElement> input,
        string name,
        out string? value)
    {
        if (!input.TryGetValue(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return true;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return true;
        }

        value = null;
        return false;
    }
}