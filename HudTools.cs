using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using GameOffsets2.Native;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HudMcp;

public sealed class HudTools
{
    private static readonly TimeSpan MainThreadWait = TimeSpan.FromSeconds(2);

    private static readonly Lazy<ScriptOptions> EvalOptions = new(() =>
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Where(a => a.GetName().Name is { } name && (name.StartsWith("System") || name is "netstandard" or "mscorlib"))
            .Append(typeof(GameController).Assembly)
            .Append(typeof(Vector2i).Assembly)
            .GroupBy(a => a.GetName().Name)
            .Select(g => g.First());
        return ScriptOptions.Default
            .AddReferences(references)
            .AddImports("System", "System.Linq", "System.Collections.Generic", "System.Numerics",
                "ExileCore2", "ExileCore2.PoEMemory", "ExileCore2.PoEMemory.Components", "ExileCore2.PoEMemory.MemoryObjects",
                "ExileCore2.PoEMemory.Elements", "ExileCore2.PoEMemory.FilesInMemory", "ExileCore2.Shared.Enums",
                "ExileCore2.Shared.Helpers", "GameOffsets2.Native");
    });

    private readonly GameController _gameController;
    private readonly HudMcpSettings _settings;
    private readonly MainThreadDispatcher _dispatcher;

    public HudTools(GameController gameController, HudMcpSettings settings, MainThreadDispatcher dispatcher)
    {
        _gameController = gameController;
        _settings = settings;
        _dispatcher = dispatcher;
    }

    public IEnumerable<McpTool> All()
    {
        yield return new McpTool("hud_status",
            "Current game state: whether you're in game, the area (name, level, etc.), the player entity and entity counts per type.",
            Schema(), HudStatus);

        yield return new McpTool("list_entities",
            "Search live entities, nearest to the player first. Filter by metadata path text, entity type, distance and animation metadata. " +
            "Returns id (for inspect_entity), type, path, grid position, distance and flags.",
            Schema(
                ("pathContains", Prop("string", "Comma-separated text; keeps entities whose metadata path contains any of it (case-insensitive)")),
                ("animatedContains", Prop("string", "Comma-separated text matched against the Animated component's base object metadata (slower; implies includeAnimated)")),
                ("type", Prop("string", "EntityType name, e.g. IngameIcon, Terrain, Monster, MiscellaneousObjects, Chest")),
                ("maxDistance", Prop("number", "Maximum grid distance from the player")),
                ("limit", Prop("integer", "Maximum rows returned (default 50)")),
                ("includeAnimated", Prop("boolean", "Also report the Animated base object metadata for each row")),
                ("includeInvalid", Prop("boolean", "Search entities the HUD considers invalid/out of range too"))),
            ListEntities);

        yield return new McpTool("inspect_entity",
            "Everything about one entity: path, positions, flags, component names with addresses, hash components, stats, " +
            "and a reflection dump of the requested components (ExileCore2 wrapper properties).",
            Schema(["id"],
                ("id", Prop("integer", "Entity id from list_entities")),
                ("components", Prop("string", "Comma-separated component names to expand (e.g. 'Animated,StateMachine,Render'), or '*' for all")),
                ("depth", Prop("integer", "How deep to expand component objects (default 1)")),
                ("maxItems", Prop("integer", "Maximum items shown per collection (default 30)"))),
            InspectEntity);

        yield return new McpTool("inspect_object",
            "Reflection dump of any object reachable from GameController, like DevTree. " +
            "Path uses members and indexers, e.g. 'IngameState.IngameUi.ExpeditionDetonatorElement.Info', 'Files.Expedition2Recipes.EntriesList[0]', " +
            "'EntityListWrapper.ValidEntitiesByType[\"IngameIcon\"][0]'. Empty path = GameController itself.",
            Schema(
                ("path", Prop("string", "Member path starting from GameController")),
                ("depth", Prop("integer", "How deep to expand (default 1)")),
                ("maxItems", Prop("integer", "Maximum items shown per collection (default 30)"))),
            InspectObject);

        yield return new McpTool("eval_csharp",
            "Run C# inside the HUD and dump the result. In scope: GameController, Player (the player entity), Entities (valid entity list), " +
            "plus usings for System.Linq, ExileCore2 memory objects/components/enums and GameOffsets2.Native. " +
            "Pass an expression (e.g. Entities.Where(e => e.Path.Contains(\"Expedition\")).Select(e => e.Path).Distinct()) " +
            "or statements that end with 'return ...;'. Runs on the HUD thread, so keep it quick.",
            Schema(["code"],
                ("code", Prop("string", "C# expression or statements with a return")),
                ("depth", Prop("integer", "How deep to expand the result (default 2)")),
                ("maxItems", Prop("integer", "Maximum items shown per collection (default 30)"))),
            EvalCSharp);

        yield return new McpTool("read_memory",
            "Read raw game process memory. Formats: hex (hexdump with ASCII), qwords (8-byte values, good for spotting pointers), " +
            "dwords, floats, utf16 (PoE strings), ascii.",
            Schema(["address"],
                ("address", Prop("string", "Address, hex with 0x prefix or decimal")),
                ("size", Prop("integer", "Bytes to read (default 256, max 16384)")),
                ("format", Prop("string", "hex | qwords | dwords | floats | utf16 | ascii (default hex)"))),
            ReadMemory);
    }

    private T OnMain<T>(Func<T> work) => _dispatcher.Invoke(work, MainThreadWait);

    private string HudStatus(JObject args)
    {
        return OnMain(() =>
        {
            var player = _gameController.Player;
            var counts = new JObject();
            foreach (var (type, entities) in _gameController.EntityListWrapper.ValidEntitiesByType.OrderBy(x => x.Key.ToString()))
            {
                if (entities.Count > 0)
                {
                    counts[type.ToString()] = entities.Count;
                }
            }

            return new JObject
            {
                ["inGame"] = _gameController.InGame,
                ["isLoading"] = _gameController.IsLoading,
                ["area"] = new ObjectDumper(20).Dump(_gameController.Area.CurrentArea, 1),
                ["player"] = player == null ? null : EntityRow(player, 0, null),
                ["validEntities"] = _gameController.EntityListWrapper.OnlyValidEntities.Count,
                ["validEntitiesByType"] = counts,
                ["evalEnabled"] = _settings.AllowEval.Value,
            }.ToString(Formatting.None);
        });
    }

    private string ListEntities(JObject args)
    {
        var pathTerms = Terms((string)args["pathContains"]);
        var animatedTerms = Terms((string)args["animatedContains"]);
        var maxDistance = (float?)args["maxDistance"];
        var limit = (int?)args["limit"] ?? 50;
        var includeInvalid = (bool?)args["includeInvalid"] ?? false;
        var includeAnimated = ((bool?)args["includeAnimated"] ?? false) || animatedTerms.Length > 0;
        EntityType? type = null;
        if ((string)args["type"] is { Length: > 0 } typeName)
        {
            if (!Enum.TryParse<EntityType>(typeName, true, out var parsed))
            {
                throw new ArgumentException($"Unknown type '{typeName}'. Valid types: {string.Join(", ", Enum.GetNames<EntityType>())}");
            }

            type = parsed;
        }

        return OnMain(() =>
        {
            var playerPos = _gameController.Player?.GridPos ?? Vector2.Zero;
            var source = includeInvalid ? _gameController.EntityListWrapper.NotOnlyValidEntities : _gameController.EntityListWrapper.OnlyValidEntities;
            var matches = new List<(Entity Entity, float Distance, string Animated)>();
            foreach (var entity in source.ToList())
            {
                if (entity == null || type != null && entity.Type != type)
                {
                    continue;
                }

                var path = entity.Path ?? "";
                if (pathTerms.Length > 0 && !pathTerms.Any(t => path.Contains(t, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var distance = Vector2.Distance(entity.GridPos, playerPos);
                if (distance > maxDistance)
                {
                    continue;
                }

                string animated = null;
                if (includeAnimated)
                {
                    animated = entity.GetComponent<Animated>()?.BaseAnimatedObjectEntity?.Metadata;
                    if (animatedTerms.Length > 0 && (animated == null || !animatedTerms.Any(t => animated.Contains(t, StringComparison.OrdinalIgnoreCase))))
                    {
                        continue;
                    }
                }

                matches.Add((entity, distance, animated));
            }

            var rows = matches.OrderBy(x => x.Distance).Take(limit).Select(x => EntityRow(x.Entity, x.Distance, includeAnimated ? x.Animated : null)).ToList();
            return new JObject
            {
                ["playerGridPos"] = playerPos.ToString(),
                ["matched"] = matches.Count,
                ["shown"] = rows.Count,
                ["entities"] = new JArray(rows),
            }.ToString(Formatting.None);
        });
    }

    private string InspectEntity(JObject args)
    {
        var id = (uint?)args["id"] ?? throw new ArgumentException("id is required");
        var componentArg = (string)args["components"] ?? "";
        var depth = (int?)args["depth"] ?? 1;
        var maxItems = (int?)args["maxItems"] ?? 30;
        return OnMain(() =>
        {
            var entity = EntityListWrapper.GetEntityById(id) ??
                         _gameController.EntityListWrapper.NotOnlyValidEntities.FirstOrDefault(x => x?.Id == id) ??
                         throw new ArgumentException($"No entity with id {id}. Ids change on area change; search again with list_entities.");
            var dumper = new ObjectDumper(maxItems);
            var playerPos = _gameController.Player?.GridPos ?? Vector2.Zero;
            var result = EntityRow(entity, Vector2.Distance(entity.GridPos, playerPos), null);
            result["metadata"] = entity.Metadata;
            result["worldPos"] = entity.Pos.ToString();
            result["isAlive"] = SafeValue(() => entity.IsAlive);
            result["rarity"] = SafeValue(() => entity.Rarity.ToString());

            var componentNames = new List<string>();
            var components = new JObject();
            foreach (DictionaryEntry entry in (IDictionary)entity.CacheComp ?? new Hashtable())
            {
                componentNames.Add(entry.Key.ToString());
                components[entry.Key.ToString()] = entry.Value is long address ? ObjectDumper.Hex(address) : ObjectDumper.Summary(entry.Value);
            }

            result["components"] = components;
            var hashComponents = new JObject();
            foreach (DictionaryEntry entry in (IDictionary)entity.HashComponents ?? new Hashtable())
            {
                var key = entry.Key is ushort hash ? $"0x{hash:X4}" : entry.Key.ToString();
                hashComponents[key] = entry.Value is long address ? ObjectDumper.Hex(address) : ObjectDumper.Summary(entry.Value);
            }

            result["hashComponents"] = hashComponents;
            result["stats"] = SafeToken(() => dumper.Dump(entity.Stats, 1));

            var requested = componentArg.Trim() == "*" ? componentNames : Terms(componentArg).ToList();
            if (requested.Count > 0)
            {
                var expanded = new JObject();
                foreach (var name in requested)
                {
                    expanded[name] = SafeToken(() =>
                    {
                        var componentType = typeof(Positioned).Assembly.GetType($"ExileCore2.PoEMemory.Components.{name}", false, true);
                        if (componentType == null)
                        {
                            return "No ExileCore2 wrapper type with this name; use read_memory on its address from 'components'";
                        }

                        var component = typeof(Entity).GetMethod(nameof(Entity.GetComponent))!.MakeGenericMethod(componentType).Invoke(entity, null);
                        return component == null ? "Entity has no such component" : dumper.Dump(component, depth);
                    });
                }

                result["expanded"] = expanded;
            }

            return result.ToString(Formatting.None);
        });
    }

    private string InspectObject(JObject args)
    {
        var path = ((string)args["path"] ?? "").Trim();
        var depth = (int?)args["depth"] ?? 1;
        var maxItems = (int?)args["maxItems"] ?? 30;
        return OnMain(() =>
        {
            object current = _gameController;
            var walked = "GameController";
            foreach (Match token in Regex.Matches(path, @"(?<member>[A-Za-z_][A-Za-z0-9_]*)|\[(?<index>-?\d+)\]|\[""(?<key>[^""]*)""\]"))
            {
                if (walked == "GameController" && token.Groups["member"].Value == "GameController")
                {
                    continue;
                }

                if (current == null)
                {
                    throw new ArgumentException($"{walked} is null");
                }

                current = token.Groups["member"].Success ? GetMember(current, token.Groups["member"].Value, walked)
                    : token.Groups["index"].Success ? GetIndex(current, int.Parse(token.Groups["index"].Value), walked)
                    : GetKey(current, token.Groups["key"].Value, walked);
                walked += token.Groups["member"].Success ? $".{token.Value}" : token.Value;
            }

            return new JObject
            {
                ["path"] = walked,
                ["type"] = current?.GetType().FullName,
                ["value"] = new ObjectDumper(maxItems).Dump(current, depth),
            }.ToString(Formatting.None);
        });
    }

    private string EvalCSharp(JObject args)
    {
        if (!_settings.AllowEval.Value)
        {
            throw new InvalidOperationException("eval_csharp is disabled in the HudMcp plugin settings");
        }

        var code = ((string)args["code"])?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            throw new ArgumentException("code is required");
        }

        var depth = (int?)args["depth"] ?? 2;
        var maxItems = (int?)args["maxItems"] ?? 30;
        var body = Regex.IsMatch(code, @"\breturn\b") ? code : $"return (object)({code.TrimEnd(';', ' ', '\t', '\r', '\n')});";
        var wrapped = "(System.Func<ExileCore2.GameController, object>)(GameController => {\n" +
                      "var Player = GameController.Player;\n" +
                      "var Entities = GameController.EntityListWrapper.OnlyValidEntities;\n" +
                      "#line 1\n" +
                      body + "\n})";

        //Compile off the HUD thread; only the call itself runs there
        var script = CSharpScript.Create<Func<GameController, object>>(wrapped, EvalOptions.Value);
        var errors = script.Compile().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new ArgumentException("Compilation failed:\n" + string.Join("\n", errors.Select(e => e.ToString())));
        }

        var function = script.RunAsync().GetAwaiter().GetResult().ReturnValue;
        return OnMain(() =>
        {
            var value = function(_gameController);
            return new JObject
            {
                ["type"] = value?.GetType().FullName,
                ["value"] = new ObjectDumper(maxItems).Dump(value, depth),
            }.ToString(Formatting.None);
        });
    }

    private string ReadMemory(JObject args)
    {
        var addressText = ((string)args["address"])?.Trim() ?? throw new ArgumentException("address is required");
        var address = addressText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt64(addressText[2..], 16)
            : long.Parse(addressText);
        var size = Math.Clamp((int?)args["size"] ?? 256, 1, 16384);
        var format = ((string)args["format"] ?? "hex").ToLowerInvariant();
        var bytes = _gameController.Memory.ReadBytes(address, size);
        if (bytes == null || bytes.Length == 0)
        {
            throw new InvalidOperationException($"Could not read {size} bytes at {ObjectDumper.Hex(address)}");
        }

        var output = new StringBuilder();
        switch (format)
        {
            case "qwords":
            case "dwords":
            {
                var width = format == "qwords" ? 8 : 4;
                for (var offset = 0; offset + width <= bytes.Length; offset += width)
                {
                    var value = width == 8 ? BitConverter.ToInt64(bytes, offset) : BitConverter.ToUInt32(bytes, offset);
                    output.AppendLine($"+0x{offset:X3} [{address + offset:X}] = 0x{value:X}{(width == 4 ? $" ({value})" : "")}");
                }

                break;
            }
            case "floats":
                for (var offset = 0; offset + 4 <= bytes.Length; offset += 4)
                {
                    output.AppendLine($"+0x{offset:X3} = {BitConverter.ToSingle(bytes, offset)}");
                }

                break;
            case "utf16":
                output.Append(Encoding.Unicode.GetString(bytes).Split('\0')[0]);
                break;
            case "ascii":
                output.Append(Encoding.ASCII.GetString(bytes).Split('\0')[0]);
                break;
            default:
                for (var offset = 0; offset < bytes.Length; offset += 16)
                {
                    var chunk = bytes.Skip(offset).Take(16).ToArray();
                    var hex = string.Join(" ", chunk.Select(b => b.ToString("x2")));
                    var ascii = new string(chunk.Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray());
                    output.AppendLine($"{address + offset:X13}  {hex,-47}  {ascii}");
                }

                break;
        }

        return output.ToString();
    }

    private static JObject EntityRow(Entity entity, float distance, string animated)
    {
        var row = new JObject
        {
            ["id"] = entity.Id,
            ["type"] = entity.Type.ToString(),
            ["path"] = entity.Path,
            ["renderName"] = SafeValue(() => entity.RenderName),
            ["gridPos"] = entity.GridPos.ToString(),
            ["distance"] = Math.Round(distance, 1),
            ["address"] = ObjectDumper.Hex(entity.Address),
            ["isValid"] = entity.IsValid,
            ["isTargetable"] = SafeValue(() => entity.IsTargetable),
            ["isHostile"] = SafeValue(() => entity.IsHostile),
        };
        if (animated != null)
        {
            row["animated"] = animated;
        }

        return row;
    }

    private static object GetMember(object target, string name, string walked)
    {
        var type = target.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
        if (type.GetProperties(flags).FirstOrDefault(p => p.GetIndexParameters().Length == 0 && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } property)
        {
            return property.GetValue(target);
        }

        if (type.GetField(name, flags) is { } field)
        {
            return field.GetValue(target);
        }

        var available = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).Select(p => p.Name)
            .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name)).OrderBy(x => x);
        throw new ArgumentException($"{walked} ({type.Name}) has no member '{name}'. Members: {string.Join(", ", available)}");
    }

    private static object GetIndex(object target, int index, string walked)
    {
        switch (target)
        {
            case IList list:
                if (index < 0)
                {
                    index += list.Count;
                }

                return index >= 0 && index < list.Count ? list[index] : throw new ArgumentException($"{walked} has {list.Count} items; index {index} is out of range");
            case IDictionary dictionary:
                return GetKey(dictionary, index.ToString(), walked);
            case IEnumerable enumerable:
                return enumerable.Cast<object>().ElementAtOrDefault(index);
            default:
                throw new ArgumentException($"{walked} ({target.GetType().Name}) can't be indexed");
        }
    }

    private static object GetKey(object target, string key, string walked)
    {
        if (target is not IDictionary dictionary)
        {
            throw new ArgumentException($"{walked} ({target.GetType().Name}) isn't a dictionary");
        }

        foreach (DictionaryEntry entry in dictionary)
        {
            if (string.Equals(entry.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }
        }

        throw new ArgumentException($"{walked} has no key '{key}'");
    }

    private static JToken SafeValue<T>(Func<T> getter)
    {
        try
        {
            return getter() is { } value ? JToken.FromObject(value) : JValue.CreateNull();
        }
        catch (Exception ex)
        {
            return $"!{ex.GetType().Name}";
        }
    }

    private static JToken SafeToken(Func<JToken> getter)
    {
        try
        {
            return getter();
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } e } ? e : ex;
            return $"!{inner.GetType().Name}: {inner.Message}";
        }
    }

    private static string[] Terms(string text) =>
        string.IsNullOrWhiteSpace(text) ? [] : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static JObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    private static JObject Schema(params (string Name, JObject Property)[] properties) => Schema([], properties);

    private static JObject Schema(string[] required, params (string Name, JObject Property)[] properties)
    {
        var schema = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(properties.Select(p => new JProperty(p.Name, p.Property))),
        };
        if (required.Length > 0)
        {
            schema["required"] = new JArray(required);
        }

        return schema;
    }
}
