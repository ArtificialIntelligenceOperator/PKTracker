using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKTracker.Engine.Stats;

public enum StatScope : byte { Decision = 0, Hand = 1, Expression = 2 }

/// <summary>Определение стата (§4.3). Хранится в stat_definition.definition как JSON.</summary>
public sealed class StatDefinition
{
    [JsonIgnore] public int StatId { get; set; }
    public required string Code { get; init; }
    public required string Title { get; init; }
    public StatScope Scope { get; init; }

    /// <summary>decision/hand: условие знаменателя.</summary>
    public string? Opportunity { get; init; }
    /// <summary>decision/hand: условие числителя; expression: формула.</summary>
    public string? Hit { get; init; }
    /// <summary>hand (sum): выражение значения, суммируемого в sum_value.</summary>
    public string? Value { get; init; }

    public StatFormat Format { get; init; } = new();
    /// <summary>Пороги цветов: [from, to, "#RRGGBB"], from включительно, to исключительно.</summary>
    public List<StatColor> Colors { get; init; } = new();
    /// <summary>Выборка, ниже которой стат визуально приглушается (§5.3).</summary>
    public int MinSample { get; init; } = 30;

    [JsonIgnore] public int Version { get; set; } = 1;
    [JsonIgnore] public bool IsBuiltin { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static StatDefinition FromJson(string json) => JsonSerializer.Deserialize<StatDefinition>(json, Json)!;
}

public sealed class StatFormat
{
    public int Decimals { get; init; }
    public string Suffix { get; init; } = "";
    /// <summary>true — значение hits/cases×100; false — значение как есть (expression / sum).</summary>
    public bool Percent { get; init; } = true;
}

public sealed record StatColor(double From, double To, string Color);
