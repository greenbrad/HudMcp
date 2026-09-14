using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HudMcp;

public record McpTool(string Name, string Description, JObject InputSchema, Func<JObject, string> Handler);

//Minimal MCP "Streamable HTTP" server: JSON-RPC over POST, plain JSON responses, no SSE streams or sessions
public sealed class McpServer : IDisposable
{
    private static readonly string[] SupportedProtocolVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];
    private const string Instructions =
        "Live view into Path of Exile 2 through the ExileCore2 HUD. Start with hud_status, find entities with list_entities, " +
        "drill in with inspect_entity/inspect_object, and use eval_csharp for anything else (GameController, Player and Entities are in scope). " +
        "Addresses and entity ids change on every area change.";

    private readonly HttpListener _listener = new();
    private readonly byte[] _expectedAuthorization;
    private readonly Dictionary<string, McpTool> _tools;
    private readonly Func<bool> _isEnabled;
    private readonly Func<int> _maxResponseChars;

    public McpServer(int port, string token, IEnumerable<McpTool> tools, Func<bool> isEnabled, Func<int> maxResponseChars)
    {
        Url = $"http://localhost:{port}/mcp";
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _expectedAuthorization = Encoding.UTF8.GetBytes($"Bearer {token}");
        _tools = tools.ToDictionary(x => x.Name);
        _isEnabled = isEnabled;
        _maxResponseChars = maxResponseChars;
    }

    public string Url { get; }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public void Dispose()
    {
        try
        {
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task AcceptLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (!_listener.IsListening)
            {
                return;
            }
            catch (HttpListenerException)
            {
                continue;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            if (request.Url?.AbsolutePath.TrimEnd('/') != "/mcp")
            {
                response.StatusCode = 404;
                return;
            }

            //Browsers attach Origin; refuse anything that isn't a local page to block DNS-rebinding/CSRF
            if (request.Headers["Origin"] is { Length: > 0 } origin &&
                !origin.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) &&
                !origin.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = 403;
                return;
            }

            var authorization = Encoding.UTF8.GetBytes(request.Headers["Authorization"] ?? "");
            if (!CryptographicOperations.FixedTimeEquals(authorization, _expectedAuthorization))
            {
                response.StatusCode = 401;
                return;
            }

            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                response.AddHeader("Allow", "POST");
                return;
            }

            if (!_isEnabled())
            {
                response.StatusCode = 503;
                await WriteText(response, "HudMcp plugin is disabled in the HUD");
                return;
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }

            JToken message;
            try
            {
                message = JToken.Parse(body);
            }
            catch (JsonException ex)
            {
                await WriteJson(response, Error(null, -32700, $"Parse error: {ex.Message}"));
                return;
            }

            JToken reply;
            if (message is JArray batch)
            {
                var replies = new JArray();
                foreach (var item in batch.OfType<JObject>())
                {
                    if (await HandleMessage(item) is { } itemReply)
                    {
                        replies.Add(itemReply);
                    }
                }

                reply = replies.Count > 0 ? replies : null;
            }
            else if (message is JObject single)
            {
                reply = await HandleMessage(single);
            }
            else
            {
                reply = Error(null, -32600, "Invalid request");
            }

            if (reply == null)
            {
                response.StatusCode = 202;
                return;
            }

            await WriteJson(response, reply);
        }
        catch (Exception ex)
        {
            try
            {
                response.StatusCode = 500;
                await WriteText(response, ex.Message);
            }
            catch (Exception)
            {
            }
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception)
            {
            }
        }
    }

    private async Task<JObject> HandleMessage(JObject message)
    {
        var id = message["id"];
        var method = (string)message["method"];
        if (method == null || id == null)
        {
            //Notifications and client-side responses need no answer
            return null;
        }

        switch (method)
        {
            case "initialize":
            {
                var requested = (string)message["params"]?["protocolVersion"];
                return Result(id, new JObject
                {
                    ["protocolVersion"] = SupportedProtocolVersions.Contains(requested) ? requested : SupportedProtocolVersions[0],
                    ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JObject { ["name"] = "poe2-hud", ["version"] = "1.0.0" },
                    ["instructions"] = Instructions,
                });
            }
            case "ping":
                return Result(id, new JObject());
            case "tools/list":
                return Result(id, new JObject
                {
                    ["tools"] = new JArray(_tools.Values.Select(t => new JObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["inputSchema"] = t.InputSchema,
                    })),
                });
            case "tools/call":
            {
                var name = (string)message["params"]?["name"];
                var arguments = message["params"]?["arguments"] as JObject ?? new JObject();
                if (name == null || !_tools.TryGetValue(name, out var tool))
                {
                    return Error(id, -32602, $"Unknown tool: {name}");
                }

                string text;
                var isError = false;
                try
                {
                    text = await Task.Run(() => tool.Handler(arguments));
                }
                catch (Exception ex)
                {
                    var inner = ex is AggregateException { InnerException: { } e } ? e : ex;
                    text = $"{inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}";
                    isError = true;
                }

                var maxChars = _maxResponseChars();
                if (text.Length > maxChars)
                {
                    text = text[..maxChars] + $"\n…[truncated {text.Length - maxChars} chars; narrow the query or lower depth/limits]";
                }

                return Result(id, new JObject
                {
                    ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }),
                    ["isError"] = isError,
                });
            }
            default:
                return Error(id, -32601, $"Method not found: {method}");
        }
    }

    private static JObject Result(JToken id, JObject result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JObject Error(JToken id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JObject { ["code"] = code, ["message"] = message } };

    private static Task WriteJson(HttpListenerResponse response, JToken json)
    {
        response.ContentType = "application/json";
        return WriteBody(response, json.ToString(Formatting.None));
    }

    private static Task WriteText(HttpListenerResponse response, string text)
    {
        response.ContentType = "text/plain";
        return WriteBody(response, text);
    }

    private static async Task WriteBody(HttpListenerResponse response, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }
}
