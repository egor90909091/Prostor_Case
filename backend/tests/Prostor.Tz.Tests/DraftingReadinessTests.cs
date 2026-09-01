using System.Text.Json.Nodes;
using FluentAssertions;
using Prostor.Tz;
using Xunit;

namespace Prostor.Tz.Tests;

/// <summary>
/// Расчёт процента готовности ТЗ.
/// Готовность — сумма весов заполненных полей; если сумма весов шаблона
/// не равна 100, значение нормируется к 100. CanGenerate = нет рисков
/// с severity=blocking.
/// </summary>
public class DraftingReadinessTests
{
    [Fact]
    public void empty_state_gives_zero_readiness()
    {
        var draft = Drafting.Build(Fixtures.GenericTemplate(), Fixtures.EmptyState(), null);

        draft.Readiness.Should().Be(0);
        draft.CanGenerate.Should().BeFalse(); // есть blocking-риск no_object
    }

    [Fact]
    public void fully_filled_state_gives_full_readiness()
    {
        var draft = Drafting.Build(Fixtures.GenericTemplate(), Fixtures.FilledState(), typicalDays: 30);

        draft.Readiness.Should().Be(100);
        draft.CanGenerate.Should().BeTrue();
    }

    [Fact]
    public void partial_fill_sums_only_filled_field_weights()
    {
        // Заполняем только productId (20) и object (15) → 35
        var state = Fixtures.J("""
            {"productId":"p-1","object":"Месторождение","productName":"Услуга"}
            """);

        var draft = Drafting.Build(Fixtures.GenericTemplate(), state, null);

        draft.Readiness.Should().Be(35);
    }

    [Fact]
    public void period_filled_counts_only_when_both_ends_set()
    {
        var onlyFrom = Fixtures.J("""
            {"productId":"p-1","object":"Объект","period":{"from":"2025-01-01"}}
            """);
        var both = Fixtures.J("""
            {"productId":"p-1","object":"Объект","period":{"from":"2025-01-01","to":"2025-03-31"}}
            """);

        var draftPartial = Drafting.Build(Fixtures.GenericTemplate(), onlyFrom, null);
        var draftFull = Drafting.Build(Fixtures.GenericTemplate(), both, null);

        draftPartial.Readiness.Should().Be(35);  // 20 + 15, period не засчитан
        draftFull.Readiness.Should().Be(55);     // + 20 за period
    }

    [Fact]
    public void stages_and_executors_count_as_lists()
    {
        var state = Fixtures.J("""
            {"stages":[{"name":"Этап 1"}],"executors":[{"id":"c-1","name":"ООО"}]}
            """);

        var draft = Drafting.Build(Fixtures.GenericTemplate(), state, null);

        draft.Readiness.Should().Be(45); // stages 30 + executors 15
    }

    [Fact]
    public void readiness_normalized_when_total_weight_not_100()
    {
        // Шаблон с суммарным весом 50 — готовность нормируется к 100
        var template = new TemplateDefinition("tpl-norm", "Тест", "WORKS",
            Sections: new JsonArray(),
            Fields: new JsonArray
            {
                new JsonObject { ["key"] = "object", ["section"] = "p", ["title"] = "Объект", ["weight"] = 50 }
            },
            Risks: new JsonArray());

        var draft = Drafting.Build(template, Fixtures.J("""{"object":"Объект"}"""), null);

        // 50/50 = 100% после нормировки
        draft.Readiness.Should().Be(100);
    }

    [Fact]
    public void readiness_clamped_to_0_100()
    {
        // Сумма весов 50, но веса могут дать переполнение только через нормировку;
        // проверяем нижнюю границу — пустое состояние даёт 0
        var template = new TemplateDefinition("tpl-clamp", "Тест", "WORKS",
            Sections: new JsonArray(),
            Fields: new JsonArray
            {
                new JsonObject { ["key"] = "object", ["section"] = "p", ["title"] = "Объект", ["weight"] = 50 }
            },
            Risks: new JsonArray());

        var draft = Drafting.Build(template, Fixtures.EmptyState(), null);

        draft.Readiness.Should().Be(0);
    }

    [Fact]
    public void can_generate_false_when_blocking_risk_present()
    {
        // no_object — blocking, сработает на пустом object
        var state = Fixtures.J("""
            {"productId":"p-1","period":{"from":"2025-01-01","to":"2025-03-31"},
             "stages":[{"name":"Этап"}],"executors":[{"id":"c-1","name":"ООО"}]}
            """);

        var draft = Drafting.Build(Fixtures.GenericTemplate(), state, null);

        // Все поля заполнены (readiness 100), но object пуст → blocking-риск
        draft.Readiness.Should().Be(85);
        draft.CanGenerate.Should().BeFalse();
        draft.Risks.Should().Contain(r => r.Severity == "blocking");
    }

    [Fact]
    public void can_generate_true_with_only_warning_risks()
    {
        // Заполнено всё, но включён model3d без этапа исходных данных → warning, не blocking
        var state = Fixtures.J("""
            {"productId":"p-1","object":"Объект",
             "period":{"from":"2025-01-01","to":"2025-03-31"},
             "stages":[{"name":"Моделирование"}],"executors":[{"id":"c-1","name":"ООО"}],
             "flags":{"model3d":true}}
            """);

        var draft = Drafting.Build(Fixtures.GenericTemplate(), state, typicalDays: 30);

        draft.CanGenerate.Should().BeTrue();
        draft.Risks.Should().OnlyContain(r => r.Severity == "warning");
    }

    [Fact]
    public void field_status_reports_filled_flag_per_field()
    {
        var state = Fixtures.J("""
            {"productId":"p-1","object":"Объект","period":{"from":"2025-01-01","to":"2025-03-31"}}
            """);

        var draft = Drafting.Build(Fixtures.GenericTemplate(), state, null);

        draft.Fields.Should().ContainEquivalentOf(new { Key = "productId", Filled = true });
        draft.Fields.Should().ContainEquivalentOf(new { Key = "period", Filled = true });
        draft.Fields.Should().ContainEquivalentOf(new { Key = "stages", Filled = false });
        draft.Fields.Should().ContainEquivalentOf(new { Key = "executors", Filled = false });
        draft.Fields.Should().ContainEquivalentOf(new { Key = "object", Filled = true });
    }

    [Fact]
    public void blocking_field_flag_propagates_from_definition()
    {
        var draft = Drafting.Build(Fixtures.GenericTemplate(), Fixtures.EmptyState(), null);

        draft.Fields.Single(f => f.Key == "period").Blocking.Should().BeTrue();
        draft.Fields.Single(f => f.Key == "productId").Blocking.Should().BeFalse();
    }
}
