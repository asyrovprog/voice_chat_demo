using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using OpenAI.Realtime;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

#pragma warning disable OPENAI002

public static class PluginUtilities
{
    public sealed class FunctionYaml
    {
        public string? Description { get; set; }
        public Dictionary<string, string> Parameters { get; set; } = new(); // name -> description
    }

    // --- existing helper (minor tweak: prune empty "required", map floats to "number") ---
    public static ConversationTool ToRealtimeTool(this KernelFunction f)
    {
        var y = TryLoadYaml(f.Name);

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
            ["required"] = new JsonArray()
        };

        static string MapClrToJsonType(Type t) =>
            t == typeof(bool) ? "boolean" :
            t == typeof(int) ? "integer" :
            t == typeof(long) ? "integer" :
            t == typeof(float) ? "number" :
            t == typeof(double) ? "number" :
            "string";

        foreach (var p in f.Metadata.Parameters)
        {
            if (p.ParameterType == null) continue;

            // description: YAML > existing metadata > empty
            var desc = (y != null && y.Parameters.TryGetValue(p.Name, out var yd)) ? yd : (p.Description ?? "");

            // reflect back into SK metadata (best-effort; some SK versions allow it)
            try { p.Description = desc; } catch { /* ignore if not supported */ }

            var prop = new JsonObject
            {
                ["type"] = MapClrToJsonType(p.ParameterType),
                ["description"] = desc
            };

            ((JsonObject)schema["properties"]!).Add(p.Name, prop);
            if (p.IsRequired) ((JsonArray)schema["required"]!).Add(p.Name);
        }

        if (((JsonArray)schema["required"]!).Count == 0)
            schema.Remove("required");

        var parametersJson = schema.ToJsonString();

        var tool = ConversationFunctionTool.CreateFunctionTool(
            f.Name,
            description: y?.Description ?? f.Description,
            parameters: new BinaryData(parametersJson));

        return tool;
    }

    // --- run a realtime tool call against an SK plugin ---
    public static async Task<bool> TryRunAsync(
        Kernel kernel,
        KernelPlugin plugin,
        RealtimeSession session,
        OutputStreamingFinishedUpdate update,
        ILogger? logger = null,
        CancellationToken ct = default)
    { 
        try
        {
            var functionName = update.FunctionName;
            var functionCallId = update.FunctionCallId;
            var functionCallArguments = update.FunctionCallArguments;

            await TryRunFunctionAsyncImpl(kernel, plugin, session, functionName, functionCallId, functionCallArguments, logger, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Unexpected error running function '{Function}'", update.FunctionName);
            return false;
        }
    }

    public static async Task<bool> TryRunFunctionAsyncImpl(
        Kernel kernel,
        KernelPlugin plugin,
        RealtimeSession session,
        string functionName,
        string functionCallId,
        string functionCallArguments,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        // 1) find the function
        var fn = plugin.FirstOrDefault(f => string.Equals(f.Name, functionName, StringComparison.Ordinal));
        if (fn is null) return false;

        // 2) parse args json -> KernelArguments (with light type coercion)
        var args = new KernelArguments();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(functionCallArguments) ? "{}" : functionCallArguments);
            var root = doc.RootElement;

            foreach (var p in fn.Metadata.Parameters)
            {
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(p.Name, out var v) && p.ParameterType != null)
                {
                    args[p.Name] = CoerceJsonToClr(v, p.ParameterType);
                }
                else if (p.IsRequired)
                {
                    logger?.LogError("Missing required argument '{Param}' for function '{Function}'", p.Name, functionName);
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            // Return a tool error back to the session (optional), or just log.
            logger?.LogError(ex, "Argument parsing failed for function '{Function}'", functionName);
            await SendToolErrorAsync(session, functionCallId, functionName, $"Argument parsing failed: {ex.Message}", ct);
            return false;
        }

        // 3) invoke SK
        object? value;
        try
        {
            var result = await kernel.InvokeAsync(fn, args, ct);
            value = result?.GetValue<object>();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Function execution failed for '{Function}'", functionName);
            await SendToolErrorAsync(session, functionCallId, functionName, $"Function execution failed: {ex.Message}", ct);
            return false;
        }

        // 4) send tool result back to the realtime session
        var payload = JsonSerializer.Serialize(value);
        await SendToolResultAsync(session, functionCallId, functionName, payload ?? "completed", ct);
        return true;
    }

    // --- helpers ---

    static FunctionYaml? TryLoadYaml(string funcName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Prompts", "Functions", $"{funcName}.yaml");
        if (!File.Exists(path)) return null;

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return deserializer.Deserialize<FunctionYaml>(File.ReadAllText(path));
    }

    private static object? CoerceJsonToClr(JsonElement v, Type target)
    {
        if (target == typeof(string)) return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        if (target == typeof(bool)) return v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.False ? false : bool.Parse(v.GetRawText()));
        if (target == typeof(int)) return v.ValueKind == JsonValueKind.Number ? v.GetInt32() : int.Parse(v.GetString()!);
        if (target == typeof(long)) return v.ValueKind == JsonValueKind.Number ? v.GetInt64() : long.Parse(v.GetString()!);
        if (target == typeof(float)) return v.ValueKind == JsonValueKind.Number ? v.GetSingle() : float.Parse(v.GetString()!, CultureInfo.InvariantCulture);
        if (target == typeof(double)) return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.Parse(v.GetString()!, CultureInfo.InvariantCulture);

        // arrays of primitives (simple support)
        if (target.IsArray && target.GetElementType() == typeof(string) && v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray().Select(e => e.ToString()).ToArray();
        if (target.IsArray && target.GetElementType() == typeof(int) && v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray().Select(e => e.GetInt32()).ToArray();

        // fallback: raw JSON -> string
        return v.GetRawText();
    }

    private static async Task SendToolResultAsync(
        RealtimeSession session,
        string functionCallId,
        string functionName,
        string result,
        CancellationToken ct)
    {
        if (!functionName.StartsWith("Update"))
        {
            RealtimeItem functionOutputItem = RealtimeItem.CreateFunctionCallOutput(callId: functionCallId, output: result);
            await session.AddItemAsync(functionOutputItem, ct).ConfigureAwait(false);
        }
    }

    private static Task SendToolErrorAsync(RealtimeSession session, string functionCallId, string functionName, string message, CancellationToken ct)
    {
        // If your SDK has a dedicated "tool_error", use it. Otherwise send a result with an error field.

        var json = JsonSerializer.Serialize(new { error = message });
        return SendToolResultAsync(session, functionCallId, functionName, json, ct);
    }
}
