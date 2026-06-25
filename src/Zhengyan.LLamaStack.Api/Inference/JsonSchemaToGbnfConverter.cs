using System.Text;
using System.Text.Json;

namespace Zhengyan.LLamaStack.Api.Inference;

public static class JsonSchemaToGbnfConverter
{
    public static string Convert(JsonElement schema, string rootRuleName = "root")
    {
        var rules = new Dictionary<string, string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var counter = 0;

        var root = ConvertSchema(schema, rootRuleName, rules, visited, ref counter);

        var sb = new StringBuilder();
        sb.AppendLine($"root ::= {root}");
        sb.AppendLine();

        foreach (var rule in rules)
        {
            sb.AppendLine($"{rule.Key} ::= {rule.Value}");
        }

        return sb.ToString();
    }

    private static string ConvertSchema(JsonElement schema, string ruleName, Dictionary<string, string> rules, HashSet<string> visited, ref int counter, bool isNested = false)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return "value";
        }

        if (schema.TryGetProperty("$ref", out var refValue) && refValue.ValueKind == JsonValueKind.String)
        {
            var refPath = refValue.GetString() ?? "";
            var refName = refPath.StartsWith("#/$defs/") ? refPath[8..] : refPath.StartsWith("#/definitions/") ? refPath[14..] : refPath;
            refName = SanitizeRuleName(refName);
            return refName;
        }

        if (schema.TryGetProperty("enum", out var enumValue) && enumValue.ValueKind == JsonValueKind.Array)
        {
            return ConvertEnum(enumValue, ruleName, rules, ref counter);
        }

        if (schema.TryGetProperty("const", out var constValue))
        {
            return ConvertConst(constValue);
        }

        if (schema.TryGetProperty("anyOf", out var anyOf) && anyOf.ValueKind == JsonValueKind.Array)
        {
            return ConvertAnyOf(anyOf, ruleName, rules, visited, ref counter);
        }

        var type = schema.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
            ? typeProp.GetString()
            : null;

        if (type is null && schema.TryGetProperty("type", out var typeArr) && typeArr.ValueKind == JsonValueKind.Array)
        {
            var types = typeArr.EnumerateArray().Select(t => t.GetString()).Where(t => t is not null).ToArray();
            if (types.Length == 0) return "value";
            if (types.Length == 1) type = types[0];
            else
            {
                var alternatives = new List<string>();
                foreach (var t in types)
                {
                    alternatives.Add(t switch
                    {
                        "object" => ConvertSchema(schema, ruleName + "_obj", rules, visited, ref counter, true),
                        "array" => ConvertArrayRule(schema, ruleName + "_arr", rules, visited, ref counter),
                        "string" => "string",
                        "number" => "number",
                        "integer" => "integer",
                        "boolean" => "boolean",
                        "null" => "null",
                        _ => "value"
                    });
                }
                return $"({string.Join(" | ", alternatives)})";
            }
        }

        return type switch
        {
            "object" => ConvertObject(schema, ruleName, rules, visited, ref counter, isNested),
            "array" => ConvertArrayRule(schema, ruleName, rules, visited, ref counter),
            "string" => "string",
            "number" => "number",
            "integer" => "integer",
            "boolean" => "boolean",
            "null" => "null",
            _ => "value"
        };
    }

    private static string ConvertObject(JsonElement schema, string ruleName, Dictionary<string, string> rules, HashSet<string> visited, ref int counter, bool isNested)
    {
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return "\"\\{\"" + " ws " + "\"\\}\"";
        }

        var requiredSet = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in required.EnumerateArray())
            {
                if (r.ValueKind == JsonValueKind.String)
                {
                    requiredSet.Add(r.GetString()!);
                }
            }
        }

        var propRules = new List<string>();
        var addlRule = ruleName + "_additional";
        var hasAddl = schema.TryGetProperty("additionalProperties", out var addl) && addl.ValueKind != JsonValueKind.False;

        foreach (var prop in properties.EnumerateObject())
        {
            var propName = prop.Name;
            var propRuleName = SanitizeRuleName(ruleName + "_" + propName);
            var propValueRule = ConvertSchema(prop.Value, propRuleName + "_val", rules, visited, ref counter, true);

            if (requiredSet.Contains(propName))
            {
                propRules.Add($"  \"\\\"{EscapeString(propName)}\\\"\" ws \":\" ws {propValueRule}");
            }
            else
            {
                propRules.Add($"  (\"\\\"{EscapeString(propName)}\\\"\" ws \":\" ws {propValueRule})?");
            }
        }

        var sep = " \",\" ws ";
        var propsJoined = string.Join(sep, propRules);

        var ruleValue = $"\"\\{{\" ws {propsJoined} ws \"\\}}\"";

        if (isNested && !visited.Contains(ruleName))
        {
            visited.Add(ruleName);
            rules[ruleName] = ruleValue;
            return ruleName;
        }

        if (visited.Contains(ruleName))
        {
            return "\"\\{\" ws ... \"\\}\"";
        }

        return ruleValue;
    }

    private static string ConvertArrayRule(JsonElement schema, string ruleName, Dictionary<string, string> rules, HashSet<string> visited, ref int counter)
    {
        var itemsRule = schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object
            ? ConvertSchema(items, ruleName + "_item", rules, visited, ref counter, true)
            : "value";

        var minItems = schema.TryGetProperty("minItems", out var min) && min.ValueKind == JsonValueKind.Number
            ? min.GetInt32() : 0;
        var maxItems = schema.TryGetProperty("maxItems", out var max) && max.ValueKind == JsonValueKind.Number
            ? max.GetInt32() : -1;

        if (maxItems >= 0)
        {
            var itemsList = string.Join(" ", Enumerable.Repeat($"({itemsRule})", maxItems));
            if (minItems == 0)
                return $"\"\\[\" ws ({string.Join(" | ", Enumerable.Range(minItems, maxItems - minItems + 1).Select(i => $"({string.Join(" ", Enumerable.Repeat(itemsRule, i))})"))}) ws \"\\]\"";
            return $"\"\\[\" ws {string.Join(" ", Enumerable.Repeat(itemsRule, minItems))} ws \"\\]\"";
        }

        if (minItems > 0)
        {
            var required = string.Join(" ", Enumerable.Repeat(itemsRule, minItems));
            return $"\"\\[\" ws {required} ({sep()} {itemsRule})* ws \"\\]\"";
        }

        return $"\"\\[\" ws ({itemsRule} ({sep()} {itemsRule})*)? ws \"\\]\"";

        static string sep() => " \",\" ws ";
    }

    private static string ConvertEnum(JsonElement enumValues, string ruleName, Dictionary<string, string> rules, ref int counter)
    {
        var alternatives = new List<string>();
        foreach (var val in enumValues.EnumerateArray())
        {
            alternatives.Add(val.ValueKind switch
            {
                JsonValueKind.String => $"\"\\\"{EscapeString(val.GetString()!)}\\\"\"",
                JsonValueKind.Number => val.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "null",
                _ => val.GetRawText()
            });
        }

        if (alternatives.Count == 1) return alternatives[0];
        return $"({string.Join(" | ", alternatives)})";
    }

    private static string ConvertConst(JsonElement constValue)
    {
        return constValue.ValueKind switch
        {
            JsonValueKind.String => $"\"\\\"{EscapeString(constValue.GetString()!)}\\\"\"",
            JsonValueKind.Number => constValue.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => constValue.GetRawText()
        };
    }

    private static string ConvertAnyOf(JsonElement anyOf, string ruleName, Dictionary<string, string> rules, HashSet<string> visited, ref int counter)
    {
        var alternatives = new List<string>();
        foreach (var item in anyOf.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                alternatives.Add(ConvertSchema(item, ruleName + "_alt" + counter++, rules, visited, ref counter, true));
            }
        }

        if (alternatives.Count == 0) return "value";
        if (alternatives.Count == 1) return alternatives[0];
        return $"({string.Join(" | ", alternatives)})";
    }

    private static string SanitizeRuleName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
            else if (c == ' ' || c == '.') sb.Append('_');
            else sb.Append('_');
        }

        if (sb.Length == 0) sb.Append('r');
        if (char.IsDigit(sb[0])) sb.Insert(0, 'r');
        return sb.ToString();
    }

    private static string EscapeString(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }
}
