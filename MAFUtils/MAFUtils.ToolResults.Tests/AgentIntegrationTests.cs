using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using static MAFUtils.ToolResults.Tests.TestTools;

namespace MAFUtils.ToolResults.Tests;

public sealed class AgentIntegrationTests
{
    [Fact]
    public async Task AgentReceivesPreviewThenSearchesAndReadsTheStoredResult()
    {
        var store = new ToolResultStore();
        using var client = new ScriptedChatClient();
        AIAgent agent = client.AsAIAgent(name: "Invoices", instructions: StoredResultTools.RecommendedInstructions,
            tools: [.. new AITool[] { Returning(Items(1000), "ListInvoices") }.WithResultLimits(store), .. StoredResultTools.Create(store)]);
        var response = await agent.RunAsync("What is the status of item-00994?");
        Assert.Equal("item-00994 is Overdue", response.ToString());
        Assert.Equal(["ListInvoices", "SearchStoredResult", "ReadStoredResult"], client.Calls);
    }

    [Fact]
    public async Task WrappedApprovalRequiredToolStillRequestsApproval()
    {
        var store = new ToolResultStore();
        var inner = new FixedFunction(() => Items(1000));
        using var client = new ScriptedChatClient(callOnly: "Fixed");
        AIAgent agent = client.AsAIAgent(tools: [new ApprovalRequiredAIFunction(inner).WithResultLimits(store)]);
        var response = await agent.RunAsync("List everything.");
        Assert.Contains(response.Messages.SelectMany(m => m.Contents), c => c is ToolApprovalRequestContent);
        Assert.Equal(0, inner.Invocations);
    }

    // Deterministic provider substitute: exercises the real agent/tool loop without an API key.
    private sealed class ScriptedChatClient(string? callOnly = null) : IChatClient
    {
        public List<string> Calls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (callOnly is not null) return Call(callOnly, []);
            var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
            var json = result is null ? default : JsonSerializer.SerializeToElement(result.Result, AIJsonUtilities.DefaultOptions);
            switch (Calls.LastOrDefault())
            {
                case null:
                    return Call("ListInvoices", []);
                case "ListInvoices":
                    Assert.DoesNotContain("item-00994", json.GetRawText());
                    Assert.Equal("array[1000] of object{id, total, status}", json.GetProperty("shape").GetString());
                    handle = json.GetProperty("resultHandle").GetString();
                    return Call("SearchStoredResult", new() { ["resultHandle"] = handle, ["text"] = "item-00994" });
                case "SearchStoredResult":
                    var line = json.GetProperty("data").GetProperty("matches")[0].GetProperty("line").GetInt32();
                    return Call("ReadStoredResult", new() { ["resultHandle"] = handle, ["offset"] = line, ["limit"] = 3 });
                default:
                    var text = json.GetProperty("data").GetProperty("text").GetString()!;
                    Assert.Contains("\"status\": \"Overdue\"", text);
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "item-00994 is Overdue")));
            }
        }

        private string? handle;

        private Task<ChatResponse> Call(string name, Dictionary<string, object?> arguments)
        {
            Calls.Add(name);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"call-{Calls.Count}", name, arguments)])));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
