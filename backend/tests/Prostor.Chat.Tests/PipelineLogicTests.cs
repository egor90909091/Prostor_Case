using System.Text.Json.Nodes;
using FluentAssertions;
using Prostor.Chat;
using Xunit;

namespace Prostor.Chat.Tests;

/// <summary>
/// Детерминированная часть хода диалога: разбор решения «мозга», отбраковка
/// мусора в нём и поведение при недоступной модели. Без БД и без сети —
/// проверяем ровно то, что код решает сам, не спрашивая LLM.
/// </summary>
public class PipelineLogicTests
{
    private static ChatState Selected() => new()
    {
        ProductId = "pr-1",
        ProductName = "Подсчёт запасов",
        ProductCategory = "Геология",
        StepName = Step.ProductPicked,
    };

    [Theory]
    [InlineData("а какие этапы будут?")]
    [InlineData("какие этапы входят в услугу")]
    [InlineData("Сколько обычно длится?")]
    [InlineData("подскажи, что дальше")]
    [InlineData("а?")]
    [InlineData("Почему нельзя сократить срок")]
    public void LooksLikeQuestion_ОтличаетВопросОтОтвета(string text)
    {
        TurnPipeline.LooksLikeQuestion(text).Should().BeTrue();
    }

    [Theory]
    [InlineData("Приобское месторождение, куст 3")]
    [InlineData("скважина 452")]
    [InlineData("Западно-Сургутский лицензионный участок")]
    public void LooksLikeQuestion_ЗначениеПоляВопросомНеЯвляется(string text)
    {
        TurnPipeline.LooksLikeQuestion(text).Should().BeFalse();
    }

    // ------------------------------------------------------------- разбор решения
    [Fact]
    public void Parse_РазбираетПолноеРешениеМодели()
    {
        var raw = JsonNode.Parse("""
        {
          "reply": "Понял: считаем запасы по Приобскому.",
          "intent": "search_services",
          "query": "подсчёт запасов Приобское",
          "optionIndex": null,
          "facts": {
            "object": "Приобское месторождение",
            "purpose": null, "customer": null, "perimeter": null,
            "sourceData": null, "documentation": null, "acceptance": null, "other": null,
            "periodFrom": "2026-09-01", "periodTo": "2026-12-01",
            "flags": ["model3d", "выдуманный"]
          },
          "offer": "period",
          "suggestions": ["Какие этапы входят?", "Кто может выполнить?"]
        }
        """)!.AsObject();

        var decision = Brain.Parse(raw)!;

        decision.Intent.Should().Be(Intent.SearchServices);
        decision.Query.Should().Be("подсчёт запасов Приобское");
        decision.Offer.Should().Be(Offer.Period);
        decision.Facts.Object.Should().Be("Приобское месторождение");
        decision.Facts.PeriodFrom.Should().Be("2026-09-01");
        // Флаги — по белому списку: чего нет в схеме условий, того нет и в заявке.
        decision.Facts.Flags.Should().BeEquivalentTo(new[] { "model3d" });
        decision.Suggestions.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_ПустойОбъектЭтоОтсутствиеРешения()
    {
        // Так отвечает заглушка без ключа: это «модели нет», а не «нечего сказать».
        Brain.Parse(new JsonObject()).Should().BeNull();
        Brain.Parse(null).Should().BeNull();
    }

    [Fact]
    public void Parse_ОбрезаетПодсказкиДоЧетырёх()
    {
        var raw = JsonNode.Parse("""
        {
          "reply": "Ок.", "intent": "answer", "query": null, "optionIndex": null,
          "facts": {"object":null,"purpose":null,"customer":null,"perimeter":null,
                    "sourceData":null,"documentation":null,"acceptance":null,"other":null,
                    "periodFrom":null,"periodTo":null,"flags":[]},
          "offer": "none",
          "suggestions": ["раз","два","три","четыре","пять","  "]
        }
        """)!.AsObject();

        Brain.Parse(raw)!.Suggestions.Should().HaveCount(4);
    }

    // ------------------------------------------------------------- без модели
    [Fact]
    public void Fallback_БезУслугиЛюбойТекстЭтоПоиск()
    {
        var decision = Brain.Fallback(new ChatState(), "нужно оценить запасы");

        decision.Intent.Should().Be(Intent.SearchServices);
        decision.Query.Should().Be("нужно оценить запасы");
    }

    [Fact]
    public void Fallback_ВопросПриВыбраннойУслугеПолучаетОтветИзСостояния()
    {
        var state = Selected();
        state.Period = new Period { From = "2026-09-01", To = "2026-12-01" };

        var decision = Brain.Fallback(state, "а что дальше?");

        decision.Intent.Should().Be(Intent.Answer);
        decision.Reply.Should().Contain("Подсчёт запасов");
        decision.Reply.Should().Contain("2026-09-01");
        // Ответ собран из состояния, а не из одной заготовки: раз исполнителей
        // нет — так и сказано, и следующий шаг предложен именно тот.
        decision.Reply.Should().Contain("исполнители не выбраны");
        decision.Offer.Should().Be(Offer.Executors);
    }

    [Fact]
    public void Fallback_НеВопросЗаписываетсяВОбъектРаботПокаОнПустой()
    {
        var decision = Brain.Fallback(Selected(), "Приобское месторождение, куст 3");

        decision.Facts.Object.Should().Be("Приобское месторождение, куст 3");
        decision.Intent.Should().Be(Intent.Answer);
    }

    [Fact]
    public void Fallback_ЗаполненныйОбъектНеПерезаписывается()
    {
        var state = Selected();
        state.Object = "Приобское";

        Brain.Fallback(state, "скважина 452").Facts.Object.Should().BeNull();
    }

    [Fact]
    public void Schema_СобираетсяЦеликом()
    {
        // Схема — статическое поле: порядок инициализации внутри класса важен,
        // и его поломка проявляется только в рантайме (TypeInitializationException
        // на первом же ходе). Поэтому трогаем её отдельным тестом.
        var facts = Brain.Schema.Schema["properties"]!["facts"]!;

        Brain.Schema.Name.Should().Be("dialogue_turn");
        facts["required"]!.AsArray().Should().HaveCount(11);   // 10 полей + flags
        facts["properties"]!["periodFrom"].Should().NotBeNull();
    }

    // ------------------------------------------------------------- следующий шаг
    [Fact]
    public void NextOffer_ИдётПоНезаполненнымСлотам()
    {
        var state = new ChatState();
        Brain.NextOffer(state).Should().Be(Offer.None);

        state.ProductId = "pr-1";
        Brain.NextOffer(state).Should().Be(Offer.Period);

        state.Period = new Period { From = "2026-09-01", To = "2026-12-01" };
        Brain.NextOffer(state).Should().Be(Offer.Executors);

        state.Executors.Add(new ExecutorRef { Id = "c-1", Name = "СибГео" });
        Brain.NextOffer(state).Should().Be(Offer.Stages);

        state.Stages.Add(new StageRef { Key = "st-1", Name = "Сбор данных" });
        Brain.NextOffer(state).Should().Be(Offer.Tz);
    }
}
