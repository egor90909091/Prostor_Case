using FluentAssertions;
using Prostor.Chat;
using Xunit;

namespace Prostor.Chat.Tests;

/// <summary>
/// Сборка промпта хода диалога: проверяем, что контекст (услуга, состояние
/// заявки, уже собранные поля ТЗ, показанный список вариантов, хвост диалога
/// и сегодняшняя дата) реально доходит до модели. Без этого «мозг» отвечает
/// вслепую — а именно это и порождало заготовленные фразы.
/// </summary>
public class PromptBuilderTests
{
    private static ChatState SelectedState() => new()
    {
        ProductId = "pr-42",
        ProductName = "Подсчёт запасов",
        ProductCategory = "Геология",
        TemplateId = "tpl-1",
        TypicalDays = 120,
        Object = "Приобское месторождение",
        Period = new Period { From = "2026-09-01", To = "2026-12-01" },
        Executors =
        {
            new ExecutorRef { Id = "c-1", Name = "СибГео", Subcontract = false },
            new ExecutorRef { Id = "c-2", Name = "БурСервис", Subcontract = true },
        },
        Stages = { new StageRef { Key = "st-1", Name = "Сбор исходных данных" } },
    };

    private static string Build(ChatState state, string text, string? tail = null) =>
        Brain.BuildPrompt(
            state, text,
            card: new ProductCard("pr-42", "Подсчёт запасов", "Геология", 120, 7, 3, "tpl-1"),
            stages: new List<StageInfo> { new("st-1", "Сбор исходных данных", 5, 14, null) },
            similar: new List<SimilarCalc>(),
            transcriptTail: tail,
            today: new DateOnly(2026, 8, 27));

    [Fact]
    public void Prompt_СодержитСообщениеДатуИСостояниеЗаявки()
    {
        var prompt = Build(SelectedState(), "а какие этапы?");

        prompt.Should().Contain("2026-08-27");          // без сегодняшней даты «с сентября» не превратить в период
        prompt.Should().Contain("а какие этапы?");
        prompt.Should().Contain("«Подсчёт запасов»");
        prompt.Should().Contain("Геология");
        prompt.Should().Contain("2026-09-01 — 2026-12-01");
        prompt.Should().Contain("СибГео");
        prompt.Should().Contain("субподряд");
        prompt.Should().Contain("Сбор исходных данных");
    }

    [Fact]
    public void Prompt_ПеречисляетУжеЗаполненныеПоляТз()
    {
        var prompt = Build(SelectedState(), "и ещё нужен отчёт");

        prompt.Should().Contain("object: Приобское месторождение");
        prompt.Should().Contain("повторно в facts не присылай");
    }

    [Fact]
    public void Prompt_БезУслугиСообщаетОбЭтом()
    {
        var prompt = Brain.BuildPrompt(
            new ChatState(), "привет", card: null,
            stages: new List<StageInfo>(), similar: new List<SimilarCalc>(),
            transcriptTail: null, today: new DateOnly(2026, 8, 27));

        prompt.Should().Contain("услуга ещё не выбрана");
        prompt.Should().Contain("Поля ТЗ пока не заполнены");
    }

    [Fact]
    public void Prompt_НумеруетПоказанныеВариантыДляВыбораСловами()
    {
        var state = SelectedState();
        state.LastOptions = new List<OptionRef>
        {
            new() { Id = "pr-42", Title = "Подсчёт запасов" },
            new() { Id = "pr-43", Title = "Концепт обустройства" },
        };

        var prompt = Build(state, "давай второй");

        prompt.Should().Contain("1) Подсчёт запасов");
        prompt.Should().Contain("2) Концепт обустройства");
    }

    [Fact]
    public void Prompt_ПрикладываетХвостДиалога()
    {
        var prompt = Build(SelectedState(), "а подробнее?", "Заказчик: нужно оценить запасы\nАссистент: нашёл 3 услуги");

        prompt.Should().Contain("Ход диалога");
        prompt.Should().Contain("нужно оценить запасы");
    }
}
