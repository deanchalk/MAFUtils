using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace MAFUtils.SqlServer.Tests;

public sealed class AgentIntegrationTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    [SqlServerFact]
    public async Task AgentCanReceiveCompleteSchemaTableInventoryInOneToolCall()
    {
        using var client = new ScriptedChatClient(inventory: true);
        var service = new SqlServerMetadataService(database.ConnectionString, new() { MaxPageSize = 1 });
        AIAgent agent = client.AsAIAgent(name: "DatabaseExplorer", instructions: SqlServerDiscoveryTools.RecommendedInstructions,
            tools: [.. SqlServerDiscoveryTools.Create(service)]);
        var session = await agent.CreateSessionAsync();
        var response = await agent.RunAsync("Give me a grid containing all schemas and their tables.", session);
        Assert.Contains("Discovery complete", response.ToString());
        Assert.True(client.ReceivedMetadata);
    }

    [SqlServerFact]
    public async Task AgentFrameworkInvokesDiscoveryAndReceivesMetadata()
    {
        using var client = new ScriptedChatClient();
        var service = new SqlServerMetadataService(database.ConnectionString);
        AIAgent agent = client.AsAIAgent(name: "DatabaseExplorer", tools: [.. SqlServerDiscoveryTools.Create(service)]);
        var response = await agent.RunAsync("Describe the Customers table.");
        Assert.Contains("Discovery complete", response.ToString());
        Assert.True(client.ReceivedMetadata);
    }

    // Deterministic provider substitute: exercises the real agent/tool loop without an API key.
    private sealed class ScriptedChatClient(bool inventory = false) : IChatClient
    {
        public bool ReceivedMetadata { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
            if (result is not null)
            {
                var json = JsonSerializer.Serialize(result.Result);
                if (inventory)
                {
                    Assert.Contains("Customers", json);
                    Assert.Contains("Orders", json);
                    Assert.Contains("Hidden", json);
                    Assert.Contains("Empty%_", json);
                    Assert.Contains("\"isComplete\":true", json);
                }
                else Assert.Contains("Customer business description", json);
                ReceivedMetadata = true;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Discovery complete")));
            }
            var toolName = inventory ? "GetDatabaseInventory" : "DescribeObject";
            Assert.Contains(options!.Tools!, t => t is AIFunction f && f.Name == toolName);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("call-1", toolName, inventory ? new Dictionary<string, object?> { ["kind"] = "Table" } : new Dictionary<string, object?>
                {
                    ["schema"] = "sales", ["name"] = "Customers"
                })
            ])));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

