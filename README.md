# HudMcp

An [ExileCore2](https://github.com/exCore2) plugin that exposes the running Path of Exile 2 client to AI agents and other tools through the [Model Context Protocol](https://modelcontextprotocol.io) (MCP).

While the HUD runs, HudMcp serves an MCP endpoint on `localhost`. An MCP client such as Claude Code can then look at the live game the way you would with DevTree, but from a conversation:

- check the current area, the player and entity counts
- search entities by metadata path, type, distance or animation
- dump any entity's components, stats and addresses
- walk any object reachable from `GameController`
- run C# against the live `GameController`
- read raw process memory

It's meant for plugin development and reverse engineering: finding the metadata of a new league mechanic, checking what a component really contains, or confirming an offset before you write plugin code against it.

---

## Contents

- [Requirements](#requirements)
- [Installation](#installation)
- [Connecting a client](#connecting-a-client)
- [Settings](#settings)
- [Tools](#tools)
- [How it works](#how-it-works)
- [Security](#security)
- [Recipes](#recipes)
- [Gotchas and limitations](#gotchas-and-limitations)
- [Development](#development)

---

## Requirements

- Windows, with an ExileCore2 HUD that compiles plugins from `Plugins/Source`
- .NET 8 (the HUD's target runtime)
- The Roslyn assemblies that ship next to `ExileCore2.dll` (`Microsoft.CodeAnalysis*.dll`; DevTree uses the same copies). They're only needed for `eval_csharp`, but the project references them.
- An MCP client that supports the Streamable HTTP transport, for example Claude Code

## Installation

1. Clone into the HUD's source plugin folder:
   ```
   cd <HUD folder>/Plugins/Source
   git clone https://github.com/greenbrad/HudMcp.git
   ```
2. Start or restart the HUD. It compiles the plugin into `Plugins/Temp/HudMcp`.
3. Enable **HudMcp** in the HUD's plugin list.
4. Open its settings. The status line should read `Listening on http://localhost:18765/mcp`.

The first start generates a random access token and writes the connection details to `connection.json` in the plugin's runtime directory. For source plugins that's `Plugins/Temp/HudMcp/connection.json`:

```json
{
  "url": "http://localhost:18765/mcp",
  "token": "<48 hex chars>",
  "claudeCommand": "claude mcp add --transport http --scope user poe2-hud http://localhost:18765/mcp --header \"Authorization: Bearer <token>\""
}
```

## Connecting a client

### Claude Code

Press **Copy Claude Command** in the plugin settings, or take `claudeCommand` from `connection.json`, and run it:

```
claude mcp add --transport http --scope user poe2-hud http://localhost:18765/mcp --header "Authorization: Bearer <token>"
```

Restart Claude Code. The tools appear as `mcp__poe2-hud__hud_status`, `mcp__poe2-hud__list_entities` and so on. The server also sends usage instructions during the MCP handshake, so the agent knows to start with `hud_status`.

### Any MCP client

Point the client at `http://localhost:18765/mcp` with the header `Authorization: Bearer <token>`. Use the **Streamable HTTP** transport (not the legacy SSE transport).

### Without an MCP client (curl / scripts)

The endpoint is plain JSON-RPC over POST, so you can call tools directly:

```bash
curl -s -X POST http://localhost:18765/mcp \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"hud_status","arguments":{}}}'
```

The tool's output is in `result.content[0].text`, usually as a JSON string. `result.isError` is `true` when the tool threw.

## Settings

| Setting | Default | Description |
|---|---|---|
| Enable | off | While disabled, the server answers every request with `503` |
| Port | 18765 | Port of the endpoint. Press **Restart Server** after changing it |
| Allow Eval | on | Enables `eval_csharp`, which runs arbitrary C# inside the HUD |
| Max Response Kilo Chars | 60 | Longer tool output is cut off, with a note saying how much was dropped |
| Token | generated | Bearer token clients must send |
| Copy Claude Command | button | Copies the `claude mcp add` command to the clipboard |
| Restart Server | button | Rebinds the listener, e.g. after a port change |
| Regenerate Token | button | Issues a new token and restarts. Registered clients must be updated |

## Tools

Every tool returns text. Object dumps are compact JSON produced by the [object dumper](#object-dumps).

### `hud_status`

No arguments. Returns whether you're in game and loading, the current area (name, level, act, hash and other `AreaInstance` fields), the player entity row, and counts of valid entities per `EntityType`.

### `list_entities`

Searches live entities and returns them nearest to the player first.

| Argument | Type | Default | Description |
|---|---|---|---|
| `pathContains` | string | | Comma-separated text. Keeps entities whose metadata path contains any of it (case-insensitive) |
| `animatedContains` | string | | Comma-separated text matched against the `Animated` component's base object metadata. Slower; turns on `includeAnimated` |
| `type` | string | | An `EntityType` name, e.g. `IngameIcon`, `Terrain`, `Monster`, `MiscellaneousObjects`, `Chest`. An invalid name returns the list of valid ones |
| `maxDistance` | number | | Maximum grid distance from the player |
| `limit` | integer | 50 | Maximum rows returned (`matched` still reports the full count) |
| `includeAnimated` | boolean | false | Add the animated base metadata to each row. Useful when the entity path is generic but the model isn't |
| `includeInvalid` | boolean | false | Search `NotOnlyValidEntities` as well (see [gotchas](#gotchas-and-limitations)) |

Each row has `id`, `type`, `path`, `renderName`, `gridPos`, `distance`, `address`, `isValid`, `isTargetable`, `isHostile`, and `animated` when requested.

### `inspect_entity`

Everything about one entity.

| Argument | Type | Default | Description |
|---|---|---|---|
| `id` | integer | required | Entity id from `list_entities` |
| `components` | string | | Comma-separated component names to expand, e.g. `Animated,StateMachine,Render`, or `*` for all |
| `depth` | integer | 1 | How deep expanded components are dumped |
| `maxItems` | integer | 30 | Items shown per collection |

Returns the basic row plus `metadata`, `worldPos`, `isAlive` and `rarity`, and:

- `components`: every component name mapped to its address (from `CacheComp`)
- `hashComponents`: the entity's hash-keyed components (`0xKEY` mapped to an address). Some newer mechanics only live here
- `stats`: the entity's `Stats` dictionary
- `expanded`: a reflection dump of each requested component, using the matching `ExileCore2.PoEMemory.Components.*` wrapper. Components without a wrapper return a note; use `read_memory` on their address instead

### `inspect_object`

A DevTree-style dump of anything reachable from `GameController`.

| Argument | Type | Default | Description |
|---|---|---|---|
| `path` | string | `""` (GameController itself) | Member path |
| `depth` | integer | 1 | Expansion depth |
| `maxItems` | integer | 30 | Items shown per collection |

Path syntax:

- Dot-separated public properties and fields, matched case-insensitively. A leading `GameController.` is optional.
- `[n]` indexes lists and other enumerables. Negative indexes count from the end of a list.
- `["key"]` looks up a dictionary entry by its key's `ToString()`.

```
IngameState.IngameUi.ExpeditionDetonatorElement.Info
Files.Expedition2Recipes.EntriesList[0]
EntityListWrapper.ValidEntitiesByType["IngameIcon"][0]
IngameState.Data.MapStatsVisible
```

If a member doesn't exist, the error lists every member of that type, so you can explore step by step.

### `eval_csharp`

Compiles and runs C# against the live game and dumps the result.

| Argument | Type | Default | Description |
|---|---|---|---|
| `code` | string | required | An expression, or statements that end in `return ...;` |
| `depth` | integer | 2 | Expansion depth of the result |
| `maxItems` | integer | 30 | Items shown per collection |

In scope:

| Name | Value |
|---|---|
| `GameController` | The HUD's `GameController` |
| `Player` | `GameController.Player` |
| `Entities` | `GameController.EntityListWrapper.OnlyValidEntities` |

Imported namespaces: `System`, `System.Linq`, `System.Collections.Generic`, `System.Numerics`, `ExileCore2`, `ExileCore2.PoEMemory`, `ExileCore2.PoEMemory.Components`, `ExileCore2.PoEMemory.MemoryObjects`, `ExileCore2.PoEMemory.Elements`, `ExileCore2.PoEMemory.FilesInMemory`, `ExileCore2.Shared.Enums`, `ExileCore2.Shared.Helpers` and `GameOffsets2.Native`. The script can reference ExileCore2, GameOffsets2 and every loaded `System.*` assembly.

Rules:

- If the code contains the word `return`, it's treated as statements. Otherwise it's wrapped as `return (object)(<code>);`, and a trailing `;` is fine.
- The code runs inside a lambda, so `using` directives aren't allowed. Fully qualify the type (`System.Reflection.BindingFlags`) instead.
- Local functions, lambdas, anonymous types and LINQ all work.
- Compilation happens off the HUD thread. Compile errors come back with line and column, counted from line 1 of your code.

```csharp
// Distinct entity paths near the player
Entities.Where(e => e.DistancePlayer < 100).Select(e => e.Path).Distinct().OrderBy(p => p)

// StateMachine states of every entity whose path contains "Expedition"
Entities.Where(e => e.Path.Contains("Expedition"))
        .Select(e => new { e.Id, e.Path, states = e.GetComponent<StateMachine>()?.States.Select(s => s.Name + "=" + s.Value) })
```

### `read_memory`

Reads raw memory from the game process.

| Argument | Type | Default | Description |
|---|---|---|---|
| `address` | string | required | Hex with a `0x` prefix, or decimal |
| `size` | integer | 256 | Bytes to read, clamped to 1–16384 |
| `format` | string | `hex` | `hex` (hexdump with ASCII), `qwords` (8-byte values; good for spotting pointers), `dwords` (hex and decimal), `floats`, `utf16` (PoE strings), `ascii` |

Returns plain text. Reads go through `GameController.Memory`.

## How it works

```
MCP client ──POST /mcp (JSON-RPC)──▶ McpServer (HttpListener, thread pool)
                                          │ auth + origin checks
                                          ▼
                                     HudTools.<tool>(args)
                                          │ OnMain(...)
                                          ▼
                              MainThreadDispatcher queue ──▶ drained in the plugin's Tick()/Render()
                                          │
                                          ▼
                              ExileCore2 memory objects ──▶ ObjectDumper ──▶ JSON text
```

| File | Responsibility |
|---|---|
| `HudMcp.cs` | Plugin entry point: token generation, server start/stop (including on hot reload and close), settings buttons, `connection.json` |
| `HudMcpSettings.cs` | Settings nodes |
| `McpServer.cs` | Minimal MCP Streamable HTTP server: JSON-RPC routing, auth, origin validation, response truncation |
| `HudTools.cs` | Tool definitions and schemas, plus the implementations and the Roslyn eval setup |
| `MainThreadDispatcher.cs` | Runs work on the HUD thread, with a fallback |
| `ObjectDumper.cs` | Reflection-based JSON view of HUD objects |

### Protocol support

- MCP protocol versions `2025-06-18`, `2025-03-26` and `2024-11-05`. The server echoes the client's version if it supports it, otherwise it replies with the newest.
- Methods: `initialize`, `ping`, `tools/list`, `tools/call`. Anything else gets JSON-RPC error `-32601`.
- Responses are plain `application/json`. There are no SSE streams, sessions or server-initiated messages. `GET` returns `405`.
- Notifications, and responses sent by the client, get `202 Accepted` with no body.
- JSON-RPC batches (arrays) are accepted.
- A tool that throws becomes a normal result with `isError: true` and the exception text, not a protocol error.

### Threading

Game memory wrappers aren't built for concurrent access, so each tool queues its work and the plugin runs the queue in `Tick()` and `Render()`, spending up to 100 ms per call. If the HUD doesn't pick the work up within 2 seconds (loading screen, plugin not ticking), the request thread runs it itself so the call doesn't hang.

Anything slow you do in `eval_csharp` blocks the HUD's frame while it runs.

### Object dumps

`ObjectDumper` turns results into JSON:

- Public instance properties and fields, to the requested depth. At depth 0, an object becomes a one-line summary such as `Positioned @0x1A2B3C` or `List<Entity> (count 12)`.
- Scalars, enums and strings are written as-is. Vectors, colors, rectangles and dates use their `ToString()`.
- `long` members whose name contains `Address`, and every `IntPtr`, are shown in hex.
- Collections show the first `maxItems` entries plus a "…N more" marker. Dictionaries are keyed by `key.ToString()`.
- A getter that throws shows as `"!ExceptionType: message"` instead of failing the whole dump.
- Back-references to global state (`GameController`, `TheGame`, `Game`, `M`, `pM`, `Memory`, `Cache`, `pCache`, `CoreSettings`) are skipped, so a dump doesn't turn into a copy of the whole game.
- Each dump stops after 20,000 nodes, and the tool output is capped by **Max Response Kilo Chars**.

## Security

HudMcp gives whoever holds the token read access to your game's memory, and with `eval_csharp` it can run any code inside the HUD process. It guards that as follows:

- **Local only:** it listens on `http://localhost:<port>/`, not on your network.
- **Bearer token:** every request needs the token, compared in constant time. Missing or wrong tokens get `401`.
- **Origin check:** requests with an `Origin` header that isn't `http://localhost…` or `http://127.0.0.1…` get `403`. That stops web pages from reaching the server through your browser (DNS rebinding, CSRF).
- **Eval switch:** turn off **Allow Eval** if you only need read-only inspection.
- **Token rotation:** **Regenerate Token** invalidates every registered client.

Don't commit `connection.json` or share the token (the included `.gitignore` excludes it). Don't forward the port.

## Recipes

These are useful approaches found while building the plugin. Offsets were checked on the ExileCore2 and PoE2 builds from September 2026 and can change with patches.

**Identify a new mechanic's objects.** Group everything loaded by path, then filter:

```csharp
GameController.EntityListWrapper.Entities
    .Where(e => e?.Path != null)
    .GroupBy(e => e.Path.Split('@')[0])
    .Select(g => $"{g.Key} x{g.Count()} [{g.First().Type}] d={(int)g.Min(e => e.DistancePlayer)}")
    .OrderBy(x => x).ToList()
```

**Look at `StateMachine` states.** Many mechanics keep their gameplay values there. For example, expedition explodables expose `inherent_explosion_radius` and `expedition_detonated`, and rune encounters expose `sockets` and `activated`.

**Read the tooltip text on world objects.** Walk the `WorldDescription` component's element tree and collect every `Text`.

**Read an object template (`.ot`) as source text.** Loaded templates stay in memory as UTF-16 text:

```csharp
var all = (System.Collections.IDictionary)GameController.Files.GetType().GetProperty("AllFiles").GetValue(GameController.Files);
var info = all["Metadata/Some/Object.ot.tok"];                       // key as listed in AllFiles
var ptr = (long)info.GetType().GetProperty("Ptr").GetValue(info);
var textPtr = GameController.Memory.Read<long>(GameController.Memory.Read<long>(ptr + 0x28) + 0x20);
var text = System.Text.Encoding.Unicode.GetString(GameController.Memory.ReadBytes(textPtr, 0x4000));
var end = text.IndexOf('\0');
return end >= 0 ? text[..end] : text;
```

**Map stats in logbooks.** Logbook areas leave `IngameState.Data.MapStats` null and only fill `MapStatsVisible`, so check both.

## Gotchas and limitations

- **Ids and addresses don't last.** They change on every area change. Search again after zoning.
- **`includeInvalid` may find nothing.** On current ExileCore2 builds `NotOnlyValidEntities` is empty. `EntityListWrapper.Entities` in `eval_csharp` gives you everything that's loaded.
- **Use `localhost`, not `127.0.0.1`.** The listener is registered for `localhost`, and Windows' HTTP stack rejects requests whose `Host` header is `127.0.0.1` with `400`.
- **Only what's loaded.** HudMcp only sees what the client has loaded: entities outside the network bubble aren't listed, and server-side logic isn't visible at all.
- **Eval grows memory.** Each `eval_csharp` call compiles a new in-memory assembly that .NET never unloads. That's fine for a debugging session; restart the HUD after thousands of calls.
- **Eval holds up the frame.** Everything runs on the HUD's thread.
- **Stateless server.** There are no sessions or server-initiated messages, so tool-list change notifications and progress streaming aren't supported.

## Development

### Building outside the HUD

The project references the HUD's assemblies through the `exileCore2Package` property. Point it at your HUD folder:

```powershell
$env:exileCore2Package = 'C:\path\to\HUD'
dotnet build HudMcp.csproj -o out
```

(The HUD sets this itself when it compiles from `Plugins/Source`.)

### Adding a tool

1. Add an entry to `HudTools.All()`:
   ```csharp
   yield return new McpTool("my_tool",
       "What it does and when to use it.",
       Schema(["requiredArg"],
           ("requiredArg", Prop("string", "Description")),
           ("optionalArg", Prop("integer", "Description (default 10)"))),
       MyTool);
   ```
2. Implement it as `string MyTool(JObject args)`. Parse the arguments on the request thread, then do any game access inside `OnMain(() => ...)` so it runs on the HUD thread.
3. Return text. Use `new ObjectDumper(maxItems).Dump(value, depth).ToString(Formatting.None)` for object output.

Throwing an exception is fine: the server turns it into a result with `isError: true` and the message.

### Testing without the game

`McpServer` doesn't depend on the game. You can construct `HudTools` with a `null` `GameController` in a console app and exercise the protocol, authentication and eval compilation over HTTP. Only the actual game calls need the HUD running.
