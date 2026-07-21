using System.Text.Json;

namespace FreeAgent.Kernel;

/// <summary>Outcome of validating tool-call arguments against a tool's input schema.</summary>
public readonly record struct SchemaValidationResult(bool IsValid, string? Error)
{
    public static SchemaValidationResult Valid => new(true, null);

    public static SchemaValidationResult Invalid(string error) => new(false, error);
}

/// <summary>
/// A small, deterministic, dependency-free validator for the JSON Schema subset the kernel needs
/// for tool-input safety. It is intentionally NOT a full JSON Schema implementation. Supported:
/// root object <c>type</c>, <c>properties</c>, <c>required</c>, primitive property <c>type</c>s
/// (string, number, integer, boolean, object, array), and <c>additionalProperties: false</c> at
/// the root. Unknown/unsupported keywords are ignored; a malformed schema yields an invalid result
/// rather than throwing. See the "Tool Execution Pipeline" architecture section, step 2.
/// </summary>
public static class ToolInputSchemaValidator
{
    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
    {
        "string", "number", "integer", "boolean", "object", "array"
    };

    public static SchemaValidationResult Validate(JsonDocument schema, JsonDocument arguments)
    {
        var schemaRoot = schema.RootElement;
        var args = arguments.RootElement;

        if (schemaRoot.ValueKind != JsonValueKind.Object)
        {
            return SchemaValidationResult.Invalid("invalid tool schema: schema must be a JSON object");
        }

        string? rootType = null;
        if (schemaRoot.TryGetProperty("type", out var typeElement))
        {
            if (typeElement.ValueKind != JsonValueKind.String)
            {
                return SchemaValidationResult.Invalid("invalid tool schema: 'type' must be a string");
            }

            rootType = typeElement.GetString();
            if (rootType is not null && KnownTypes.Contains(rootType) && !MatchesType(args, rootType))
            {
                return SchemaValidationResult.Invalid($"arguments: expected {rootType} at root but got {Kind(args)}");
            }
        }

        var hasProperties = schemaRoot.TryGetProperty("properties", out var propertiesElement);
        var hasRequired = schemaRoot.TryGetProperty("required", out var requiredElement);
        var hasAdditional = schemaRoot.TryGetProperty("additionalProperties", out var additionalElement);

        // Only an object schema constrains anything in this subset; an empty schema accepts anything.
        if (rootType != "object" && !hasProperties && !hasRequired && !hasAdditional)
        {
            return SchemaValidationResult.Valid;
        }

        if (args.ValueKind != JsonValueKind.Object)
        {
            return SchemaValidationResult.Invalid($"arguments: expected object but got {Kind(args)}");
        }

        if (hasProperties && propertiesElement.ValueKind != JsonValueKind.Object)
        {
            return SchemaValidationResult.Invalid("invalid tool schema: 'properties' must be an object");
        }

        if (hasRequired && requiredElement.ValueKind != JsonValueKind.Array)
        {
            return SchemaValidationResult.Invalid("invalid tool schema: 'required' must be an array");
        }

        if (hasRequired)
        {
            foreach (var requiredName in requiredElement.EnumerateArray())
            {
                if (requiredName.ValueKind != JsonValueKind.String)
                {
                    return SchemaValidationResult.Invalid("invalid tool schema: 'required' entries must be strings");
                }

                if (!args.TryGetProperty(requiredName.GetString()!, out _))
                {
                    return SchemaValidationResult.Invalid($"missing required property '{requiredName.GetString()}'");
                }
            }
        }

        if (hasProperties)
        {
            foreach (var member in args.EnumerateObject())
            {
                if (!propertiesElement.TryGetProperty(member.Name, out var propertySchema)
                    || propertySchema.ValueKind != JsonValueKind.Object
                    || !propertySchema.TryGetProperty("type", out var propertyTypeElement))
                {
                    continue;
                }

                if (propertyTypeElement.ValueKind != JsonValueKind.String)
                {
                    return SchemaValidationResult.Invalid(
                        $"invalid tool schema: type of property '{member.Name}' must be a string");
                }

                var propertyType = propertyTypeElement.GetString();
                if (propertyType is not null && KnownTypes.Contains(propertyType) && !MatchesType(member.Value, propertyType))
                {
                    return SchemaValidationResult.Invalid(
                        $"property '{member.Name}' expected {propertyType} but got {Kind(member.Value)}");
                }
            }
        }

        if (hasAdditional && additionalElement.ValueKind == JsonValueKind.False)
        {
            foreach (var member in args.EnumerateObject())
            {
                var declared = hasProperties && propertiesElement.TryGetProperty(member.Name, out _);
                if (!declared)
                {
                    return SchemaValidationResult.Invalid($"additional property '{member.Name}' is not allowed");
                }
            }
        }

        return SchemaValidationResult.Valid;
    }

    private static bool MatchesType(JsonElement value, string type) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && IsIntegerNumber(value),
        _ => true
    };

    private static bool IsIntegerNumber(JsonElement value)
    {
        if (value.TryGetInt64(out _))
        {
            return true;
        }

        return value.TryGetDecimal(out var asDecimal) && asDecimal == decimal.Truncate(asDecimal);
    }

    private static string Kind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "undefined"
    };
}
