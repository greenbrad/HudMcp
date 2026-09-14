using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ExileCore2.PoEMemory;
using Newtonsoft.Json.Linq;

namespace HudMcp;

//Reflection-based JSON view of HUD objects, bounded by depth, collection size and a total node budget
public sealed class ObjectDumper
{
    //Back-references to global state that would just re-dump the whole game
    private static readonly HashSet<string> SkippedMembers = ["GameController", "TheGame", "Game", "M", "pM", "Memory", "Cache", "pCache", "CoreSettings"];

    private readonly int _maxItems;
    private int _budget;

    public ObjectDumper(int maxItems, int nodeBudget = 20000)
    {
        _maxItems = maxItems;
        _budget = nodeBudget;
    }

    public JToken Dump(object value, int depth)
    {
        if (--_budget < 0)
        {
            return "…(output budget exhausted)";
        }

        switch (value)
        {
            case null:
                return JValue.CreateNull();
            case JToken token:
                return token;
            case string s:
                return s;
            case bool or char or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                return new JValue(value);
            case IntPtr pointer:
                return Hex(pointer.ToInt64());
            case Enum e:
                return e.ToString();
            case Delegate:
                return $"<delegate {value.GetType().Name}>";
            case Type type:
                return type.FullName;
            case MemberInfo member:
                return member.ToString();
        }

        var valueType = value.GetType();
        if (IsScalarLike(valueType))
        {
            return value.ToString();
        }

        if (value is IDictionary dictionary)
        {
            var result = new JObject();
            var count = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (count++ >= _maxItems)
                {
                    result["…"] = $"{dictionary.Count - _maxItems} more entries";
                    break;
                }

                result[KeyText(entry.Key)] = depth > 0 ? SafeDump(() => entry.Value, depth - 1) : Summary(entry.Value);
            }

            return result;
        }

        if (value is IEnumerable enumerable)
        {
            var result = new JArray();
            var count = 0;
            foreach (var item in enumerable)
            {
                if (count++ >= _maxItems)
                {
                    result.Add(value is ICollection collection
                        ? $"…{collection.Count - _maxItems} more items"
                        : $"…more items (only the first {_maxItems} shown)");
                    break;
                }

                result.Add(depth > 0 ? SafeDump(() => item, depth - 1) : Summary(item));
            }

            return result;
        }

        if (depth <= 0)
        {
            return Summary(value);
        }

        var obj = new JObject { ["$type"] = valueType.Name };
        foreach (var property in valueType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || SkippedMembers.Contains(property.Name) || !property.CanRead)
            {
                continue;
            }

            obj[property.Name] = DumpMember(property.Name, () => property.GetValue(value), depth - 1);
        }

        foreach (var field in valueType.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!SkippedMembers.Contains(field.Name))
            {
                obj[field.Name] = DumpMember(field.Name, () => field.GetValue(value), depth - 1);
            }
        }

        return obj;
    }

    private JToken DumpMember(string name, Func<object> getter, int depth)
    {
        return SafeDump(() =>
        {
            var memberValue = getter();
            //Addresses are far easier to use in hex
            return memberValue is long address && name.Contains("Address", StringComparison.OrdinalIgnoreCase) ? Hex(address) : memberValue;
        }, depth);
    }

    private JToken SafeDump(Func<object> getter, int depth)
    {
        try
        {
            return Dump(getter(), depth);
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } e } ? e : ex;
            return $"!{inner.GetType().Name}: {inner.Message}";
        }
    }

    public static JToken Summary(object value)
    {
        switch (value)
        {
            case null:
                return JValue.CreateNull();
            case string or bool or char or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                return new JValue(value);
            case IntPtr pointer:
                return Hex(pointer.ToInt64());
            case Enum e:
                return e.ToString();
            case RemoteMemoryObject remote:
                return $"{remote.GetType().Name} @{Hex(remote.Address)}";
            case ICollection collection:
                return $"{FriendlyName(value.GetType())} (count {collection.Count})";
        }

        var type = value.GetType();
        if (IsScalarLike(type))
        {
            return value.ToString();
        }

        var text = value.ToString();
        return text == type.FullName || text == type.ToString() ? FriendlyName(type) : $"{FriendlyName(type)}: {text}";
    }

    public static string Hex(long value) => value < 0 ? $"-0x{-value:X}" : $"0x{value:X}";

    private static bool IsScalarLike(Type type)
    {
        //Vectors, colors, rectangles, dates... read best as their ToString
        return type.IsValueType && !type.IsEnum && (type.IsPrimitive || type.Namespace is "System" or "System.Numerics" or "System.Drawing" ||
                                                    type.Name.StartsWith("Vector") || type.Name.StartsWith("Rectangle") || type.Name == "Color");
    }

    private static string KeyText(object key) => key switch
    {
        null => "null",
        ushort or short or uint or int or ulong or long => key.ToString(),
        _ => key.ToString() ?? "?",
    };

    private static string FriendlyName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name[..type.Name.IndexOf('`')];
        return $"{name}<{string.Join(",", type.GetGenericArguments().Select(FriendlyName))}>";
    }
}
