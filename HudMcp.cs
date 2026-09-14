using System;
using System.IO;
using System.Security.Cryptography;
using ExileCore2;
using ImGuiNET;
using Newtonsoft.Json.Linq;

namespace HudMcp;

public class HudMcp : BaseSettingsPlugin<HudMcpSettings>
{
    private readonly MainThreadDispatcher _dispatcher = new();
    private McpServer _server;
    private string _status = "Not started";

    private string Url => $"http://localhost:{Settings.Port.Value}/mcp";

    private string ClaudeCommand =>
        $"claude mcp add --transport http --scope user poe2-hud {Url} --header \"Authorization: Bearer {Settings.Token.Value}\"";

    public override bool Initialise()
    {
        if (string.IsNullOrWhiteSpace(Settings.Token.Value))
        {
            Settings.Token.Value = NewToken();
        }

        Settings.RestartServer.OnPressed += StartServer;
        Settings.RegenerateToken.OnPressed += () =>
        {
            Settings.Token.Value = NewToken();
            StartServer();
        };
        Settings.CopyClaudeCommand.OnPressed += () => ImGui.SetClipboardText(ClaudeCommand);
        StartServer();
        return true;
    }

    public override void Tick()
    {
        _dispatcher.RunPending();
    }

    public override void Render()
    {
        _dispatcher.RunPending();
    }

    public override void DrawSettings()
    {
        ImGui.TextWrapped($"Status: {_status}");
        base.DrawSettings();
    }

    public override void OnClose()
    {
        StopServer();
    }

    public override void OnPluginDestroyForHotReload()
    {
        StopServer();
    }

    public override void Dispose()
    {
        StopServer();
        base.Dispose();
    }

    private void StartServer()
    {
        StopServer();
        try
        {
            var tools = new HudTools(GameController, Settings, _dispatcher).All();
            var server = new McpServer(Settings.Port.Value, Settings.Token.Value, tools,
                () => Settings.Enable.Value,
                () => Settings.MaxResponseKiloChars.Value * 1000);
            server.Start();
            _server = server;
            _status = $"Listening on {server.Url}";
            WriteConnectionFile();
            LogMessage($"HudMcp: {_status}");
        }
        catch (Exception ex)
        {
            _status = $"Failed to start on port {Settings.Port.Value}: {ex.Message}";
            LogError($"HudMcp: {_status}");
        }
    }

    private void StopServer()
    {
        _server?.Dispose();
        _server = null;
        _status = "Stopped";
    }

    //Lets local tooling pick up the endpoint without digging through HUD settings
    private void WriteConnectionFile()
    {
        try
        {
            var info = new JObject
            {
                ["url"] = Url,
                ["token"] = Settings.Token.Value,
                ["claudeCommand"] = ClaudeCommand,
            };
            File.WriteAllText(Path.Combine(DirectoryFullName, "connection.json"), info.ToString());
        }
        catch (Exception ex)
        {
            LogError($"HudMcp: could not write connection.json: {ex.Message}");
        }
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
}
