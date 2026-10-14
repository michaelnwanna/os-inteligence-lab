using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ail.Schemas;

/// <summary>JSON settings shared by storage and HTTP: snake_case, same wire format as the Python implementation.</summary>
public static class AilJson
{
    public static readonly JsonSerializerOptions Options = Create(strict: false);

    /// <summary>Rejects unknown properties, so personal data cannot slip into telemetry unnoticed.</summary>
    public static readonly JsonSerializerOptions Strict = Create(strict: true);

    private static JsonSerializerOptions Create(bool strict)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            UnmappedMemberHandling = strict ? JsonUnmappedMemberHandling.Disallow : JsonUnmappedMemberHandling.Skip,
        };
        return o;
    }
}
