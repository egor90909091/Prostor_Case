using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Prostor.Chat;

/// <summary>
/// Шаги сценария. Порядок фиксирован и известен заранее — поэтому решение
/// «что делать дальше» принимает код, а не модель.
/// </summary>
public static class Step
{
    public const string Idle = "Idle";
    public const string ProductSearch = "ProductSearch";
    public const string ProductPicked = "ProductPicked";
    public const string PeriodSet = "PeriodSet";
    public const string ExecutorsPicked = "ExecutorsPicked";
    public const string StagesPicked = "StagesPicked";
    public const string Review = "Review";
    public const string TzReady = "TzReady";
}

public sealed class Period
{
    [JsonPropertyName("from")] public string? From { get; set; }
    [JsonPropertyName("to")] public string? To { get; set; }

    public bool IsSet => !string.IsNullOrWhiteSpace(From) && !string.IsNullOrWhiteSpace(To);

    public int Days =>
        DateOnly.TryParse(From, out var f) && DateOnly.TryParse(To, out var t)
            ? Math.Max(t.DayNumber - f.DayNumber + 1, 0)
            : 0;
}

public sealed class StageRef
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("days")] public int? Days { get; set; }
    [JsonPropertyName("documentation")] public string? Documentation { get; set; }
}

public sealed class ExecutorRef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("subcontract")] public bool Subcontract { get; set; }
}

/// <summary>
/// Состояние диалога. Живёт в chat.session.state (jsonb), а не в памяти сервиса
/// и не в контексте модели: сервис остаётся stateless и переживает рестарт.
/// </summary>
public sealed class ChatState
{
    [JsonPropertyName("step")] public string StepName { get; set; } = Step.Idle;
    [JsonPropertyName("lastQuery")] public string? LastQuery { get; set; }

    [JsonPropertyName("customer")] public string? Customer { get; set; }
    [JsonPropertyName("object")] public string? Object { get; set; }
    [JsonPropertyName("purpose")] public string? Purpose { get; set; }
    [JsonPropertyName("perimeter")] public string? Perimeter { get; set; }
    [JsonPropertyName("sourceData")] public string? SourceData { get; set; }
    [JsonPropertyName("documentation")] public string? Documentation { get; set; }
    [JsonPropertyName("acceptance")] public string? Acceptance { get; set; }
    [JsonPropertyName("other")] public string? Other { get; set; }

    [JsonPropertyName("productId")] public string? ProductId { get; set; }
    [JsonPropertyName("productName")] public string? ProductName { get; set; }
    [JsonPropertyName("templateId")] public string? TemplateId { get; set; }
    [JsonPropertyName("typicalDays")] public int? TypicalDays { get; set; }

    [JsonPropertyName("period")] public Period Period { get; set; } = new();
    [JsonPropertyName("stages")] public List<StageRef> Stages { get; set; } = new();
    [JsonPropertyName("operationIds")] public List<string> OperationIds { get; set; } = new();
    [JsonPropertyName("executors")] public List<ExecutorRef> Executors { get; set; } = new();
    [JsonPropertyName("flags")] public Dictionary<string, bool> Flags { get; set; } = new();
    [JsonPropertyName("tzId")] public string? TzId { get; set; }

    public static ChatState Parse(string json) =>
        JsonSerializer.Deserialize<ChatState>(json, Json.Options) ?? new ChatState();

    public string ToJson() => JsonSerializer.Serialize(this, Json.Options);

    /// <summary>Незаполненные обязательные слоты — их показываем пользователю.</summary>
    public List<string> Missing()
    {
        var missing = new List<string>();
        if (ProductId is null) missing.Add("productId");
        if (!Period.IsSet) missing.Add("period");
        if (Executors.Count == 0) missing.Add("executors");
        if (Stages.Count == 0) missing.Add("stages");
        if (string.IsNullOrWhiteSpace(Object)) missing.Add("object");
        return missing;
    }

    /// <summary>
    /// Каскадный сброс: смена слота обнуляет всё, что от него зависит.
    /// Правило задано здесь один раз и не размазано по промптам.
    /// </summary>
    public void ResetFrom(string slot)
    {
        switch (slot)
        {
            case "product":
                Stages.Clear();
                OperationIds.Clear();
                Executors.Clear();
                TzId = null;
                goto case "period";
            case "period":
                Executors.Clear();
                TzId = null;
                break;
        }
    }
}

/// <summary>Действие с фронта: нажатие на карточку в ленте чата.</summary>
public sealed class TurnAction
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("ids")] public List<string>? Ids { get; set; }
    [JsonPropertyName("from")] public string? From { get; set; }
    [JsonPropertyName("to")] public string? To { get; set; }
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("flag")] public bool? Flag { get; set; }
    [JsonPropertyName("subcontract")] public List<string>? Subcontract { get; set; }
}

public sealed class TurnRequest
{
    [JsonPropertyName("clientMessageId")] public string? ClientMessageId { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("action")] public TurnAction? Action { get; set; }
}

/// <summary>Структурный блок сообщения. Фронт рендерит каждый тип своим компонентом.</summary>
public sealed class Block
{
    [JsonPropertyName("type")] public string Type { get; set; } = "text";
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("selectMode")] public string? SelectMode { get; set; }
    [JsonPropertyName("items")] public JsonArray? Items { get; set; }
    [JsonPropertyName("meta")] public JsonObject? Meta { get; set; }

    public static Block TextBlock(string text) => new() { Type = "text", Text = text };
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Write(object value) => JsonSerializer.Serialize(value, Options);
}
