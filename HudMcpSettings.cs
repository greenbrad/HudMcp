using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using Newtonsoft.Json;

namespace HudMcp;

public class HudMcpSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    [Menu(null, "Port of the MCP endpoint (http://localhost:PORT/mcp). Press Restart Server after changing it.")]
    public RangeNode<int> Port { get; set; } = new RangeNode<int>(18765, 1024, 65535);

    [Menu(null, "Allow the eval_csharp tool, which runs arbitrary C# inside the HUD. Only local clients that send the token can reach it.")]
    public ToggleNode AllowEval { get; set; } = new ToggleNode(true);

    [Menu(null, "Tool responses longer than this many thousand characters are cut off")]
    public RangeNode<int> MaxResponseKiloChars { get; set; } = new RangeNode<int>(60, 5, 500);

    [Menu(null, "Bearer token every request must carry. Generated automatically.")]
    public TextNode Token { get; set; } = new TextNode("");

    [JsonIgnore]
    [Menu(null, "Copies the 'claude mcp add' command that registers this server with Claude Code")]
    public ButtonNode CopyClaudeCommand { get; set; } = new ButtonNode();

    [JsonIgnore]
    public ButtonNode RestartServer { get; set; } = new ButtonNode();

    [JsonIgnore]
    [Menu(null, "Creates a new token and restarts the server. Registered clients must be updated.")]
    public ButtonNode RegenerateToken { get; set; } = new ButtonNode();
}
