using System.Text.Json;

namespace ShoppyShop.Infrastructure.Assistant;

public interface IAssistantModelClient
{
    Task<AssistantModelTurn> SendAsync(IReadOnlyList<AssistantModelMessage> messages, CancellationToken cancellationToken);
}

public sealed record AssistantModelMessage(string Role, IReadOnlyList<AssistantContentBlock> Content);

public abstract record AssistantContentBlock;

public sealed record AssistantTextBlock(string Text) : AssistantContentBlock;

public sealed record AssistantToolUseBlock(string Id, string Name, IReadOnlyDictionary<string, JsonElement> Input) : AssistantContentBlock;

public sealed record AssistantToolResultBlock(string ToolUseId, string Content, bool IsError = false) : AssistantContentBlock;

public sealed record AssistantModelTurn(
    IReadOnlyList<AssistantTextBlock> TextBlocks,
    IReadOnlyList<AssistantToolUseBlock> ToolUseBlocks);