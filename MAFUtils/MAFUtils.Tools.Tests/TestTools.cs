using System.Text.Json;
using Microsoft.Extensions.AI;

namespace MAFUtils.Tools.Tests;

internal static class TestTools
{
    internal static JsonElement Json(string json) => JsonElement.Parse(json);
    internal static AIFunctionDeclaration Function(string? schema = "{}", string name = "Lookup", string description = "Find a customer.")
        => new Declaration(name, description, schema is null ? default : Json(schema));

    internal sealed class Declaration(string name, string description, JsonElement schema) : AIFunctionDeclaration
    {
        public override string Name => name;
        public override string Description => description;
        public override JsonElement JsonSchema => schema;
        public override JsonElement? ReturnJsonSchema => throw new InvalidOperationException("Return metadata must not be read.");
    }

    internal sealed class Hosted(string name, string description) : AITool
    {
        public override string Name => name;
        public override string Description => description;
    }

    internal sealed class Throwing(string member, Exception? error = null) : AIFunctionDeclaration
    {
        private T Read<T>(string property, T value) => member == property
            ? throw error ?? new InvalidOperationException("secret exception detail") : value;
        public override string Name => Read("Name", "Lookup");
        public override string Description => Read("Description", "Find a customer.");
        public override JsonElement JsonSchema => Read("JsonSchema", Json("{}"));
    }
}
