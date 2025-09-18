using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using OpenAI.Realtime;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

#pragma warning disable OPENAI002

public static class PluginUtilities
{
    // --- existing helper (minor tweak: prune empty "required", map floats to "number") ---
    public static ConversationTool ToRealtimeTool(this KernelFunction f)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
            ["required"] = new JsonArray()
        };

        foreach (var p in f.Metadata.Parameters)
        {
            var type =
                p.ParameterType == typeof(bool) ? "boolean" :
                p.ParameterType == typeof(int) ? "integer" :
                p.ParameterType == typeof(long) ? "integer" :
                p.ParameterType == typeof(float) ? "number" :
                p.ParameterType == typeof(double) ? "number" :
                "string";

            var prop = new JsonObject
            {
                ["type"] = type,
                ["description"] = p.Description ?? ""
            };
            ((JsonObject)schema["properties"]!).Add(p.Name, prop);
            if (p.IsRequired) ((JsonArray)schema["required"]!).Add(p.Name);
        }

        if (((JsonArray)schema["required"]!).Count == 0)
            schema.Remove("required");

        var parametersJson = schema.ToJsonString();

        var tool = ConversationFunctionTool.CreateFunctionTool(
            f.Name, description: 
            f.Description, 
            parameters: new BinaryData(parametersJson));

        return tool;
    }

    // --- run a realtime tool call against an SK plugin ---
    public static async Task<bool> TryRunFunctionAsync(
        Kernel kernel,
        KernelPlugin plugin,
        RealtimeSession session,
        string functionName,
        string functionCallId,
        string functionCallArguments,
        ILogger? logger = null,
        CancellationToken ct = default)
    { 
        try
        {
            logger?.LogInformation("Running function '{Function}' with args: {Args}", functionName, functionCallArguments.Replace("\n", ""));
            await TryRunFunctionAsyncImpl(kernel, plugin, session, functionName, functionCallId, functionCallArguments, logger, ct);
            logger?.LogInformation("Function '{Function}' completed", functionName);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Unexpected error running function '{Function}'", functionName);
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
            await SendToolErrorAsync(session, functionCallId, $"Argument parsing failed: {ex.Message}", ct);
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
            await SendToolErrorAsync(session, functionCallId, $"Function execution failed: {ex.Message}", ct);
            return false;
        }

        // 4) send tool result back to the realtime session
        var payload = JsonSerializer.Serialize(value);
        await SendToolResultAsync(session, functionCallId, payload ?? "completed", ct);
        return true;
    }

    // --- helpers ---

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
        string result,
        CancellationToken ct)
    {

        RealtimeItem functionOutputItem = RealtimeItem.CreateFunctionCallOutput(callId: functionCallId, output: result);
        await session.AddItemAsync(functionOutputItem, ct).ConfigureAwait(false);
    }

    private static Task SendToolErrorAsync(RealtimeSession session, string functionCallId, string message, CancellationToken ct)
    {
        // If your SDK has a dedicated "tool_error", use it. Otherwise send a result with an error field.

        var json = JsonSerializer.Serialize(new { error = message });
        return SendToolResultAsync(session, functionCallId, json, ct);
    }
}
