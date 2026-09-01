using System.Text.Json.Nodes;
using FluentAssertions;
using Prostor.Tz;
using Xunit;

namespace Prostor.Tz.Tests;

/// <summary>
/// Сборка содержимого разделов документа из состояния диалога.
/// Drafting.BuildSections транслирует слоты в человекочитаемый текст:
/// сроки, состав работ, исполнителей, флаги, дефолты для обязательных разделов.
/// </summary>
public class DraftingSectionsTests
{
    private static JsonArray SectionsOf(JsonObject state)
    {
        var draft = Drafting.Build(Fixtures.GenericTemplate(), state, null);
        return draft.Sections;
    }

    private static string? BodyOf(JsonArray sections, string key) =>
        sections.FirstOrDefault(s => s?["key"]?.GetValue<string>() == key)?["body"]?.GetValue<string>();

    // ------------------------------------------------------------ schedule
    [Fact]
    public void schedule_renders_dates_and_duration()
    {
        var state = Fixtures.J("""
            {"period":{"from":"2025-01-15","to":"2025-04-15"}}
            """);

        var body = BodyOf(SectionsOf(state), "schedule");

        body.Should().NotBeNull();
        body.Should().Contain("15.01.2025");
        body.Should().Contain("15.04.2025");
        body.Should().Contain("91 календарных дней");
    }

    [Fact]
    public void schedule_null_when_period_incomplete()
    {
        var body = BodyOf(SectionsOf(Fixtures.J("""{"period":{"from":"2025-01-15"}}""")), "schedule");

        BodyOf(SectionsOf(Fixtures.J("""{"period":{"from":"2025-01-15"}}""")), "schedule")
            .Should().BeNull();
        BodyOf(SectionsOf(Fixtures.EmptyState()), "schedule").Should().BeNull();
    }

    // ------------------------------------------------------------ content (этапы)
    [Fact]
    public void content_enumerates_stages_with_index_and_days()
    {
        var state = Fixtures.J("""
            {"stages":[
              {"name":"Подготовка исходных данных","days":10,"documentation":"Отчёт"},
              {"name":"Моделирование","days":50}
            ]}
            """);

        var body = BodyOf(SectionsOf(state), "content");

        body.Should().NotBeNull();
        body.Should().Contain("Этап 1. Подготовка исходных данных");
        body.Should().Contain("10 дн.");
        body.Should().Contain("Отчётные материалы: Отчёт");
        body.Should().Contain("Этап 2. Моделирование");
        body.Should().Contain("50 дн.");
    }

    [Fact]
    public void content_null_when_no_stages()
    {
        BodyOf(SectionsOf(Fixtures.EmptyState()), "content").Should().BeNull();
    }

    // ------------------------------------------------------------ subcontract (исполнители)
    [Fact]
    public void subcontract_lists_executor_names()
    {
        var state = Fixtures.J("""
            {"executors":[
              {"id":"c-1","name":"ООО Гео","subcontract":false},
              {"id":"c-2","name":"АО Недра","subcontract":true}
            ]}
            """);

        var body = BodyOf(SectionsOf(state), "subcontract");

        body.Should().NotBeNull();
        body.Should().Contain("ООО Гео");
        body.Should().Contain("АО Недра");
        body.Should().Contain("субподряда");
    }

    [Fact]
    public void subcontract_without_subcontract_flag_has_no_subcontract_phrase()
    {
        var state = Fixtures.J("""
            {"executors":[{"id":"c-1","name":"ООО Гео","subcontract":false}]}
            """);

        var body = BodyOf(SectionsOf(state), "subcontract");

        body.Should().NotBeNull();
        body.Should().NotContain("субподряда");
    }

    [Fact]
    public void subcontract_null_when_no_executors()
    {
        BodyOf(SectionsOf(Fixtures.EmptyState()), "subcontract").Should().BeNull();
    }

    // ------------------------------------------------------------ purpose
    [Fact]
    public void purpose_uses_explicit_value_when_set()
    {
        var state = Fixtures.J("""{"purpose":"Доразведка запасов","productName":"Оценка"}""");

        var body = BodyOf(SectionsOf(state), "purpose");

        body.Should().Be("Доразведка запасов");
    }

    [Fact]
    public void purpose_falls_back_to_product_name_when_absent()
    {
        var state = Fixtures.J("""{"productName":"Оценка запасов"}""");

        var body = BodyOf(SectionsOf(state), "purpose");

        body.Should().Contain("Оценка запасов");
        body.Should().Contain("«");
    }

    // ------------------------------------------------------------ documentation / quality / abbreviations — дефолты
    [Fact]
    public void documentation_has_default_when_not_set()
    {
        var body = BodyOf(SectionsOf(Fixtures.EmptyState()), "documentation");

        body.Should().NotBeNull();
        body.Should().Contain("информационными отчётами");
    }

    [Fact]
    public void documentation_uses_explicit_value_when_set()
    {
        var state = Fixtures.J("""{"documentation":"Отчёт в PDF и SIG"}""");

        BodyOf(SectionsOf(state), "documentation").Should().Be("Отчёт в PDF и SIG");
    }

    [Fact]
    public void abbreviations_section_is_constant()
    {
        // Раздела abbreviations нет в типовом тестовом шаблоне — проверяем
        // на шаблоне, который его объявляет.
        var template = new TemplateDefinition("tpl", "Т", "WORKS",
            Sections: new JsonArray
            {
                new JsonObject { ["key"] = "abbreviations", ["title"] = "Сокращения", ["required"] = false }
            },
            Fields: new JsonArray(), Risks: new JsonArray());
        var draft = Drafting.Build(template, Fixtures.EmptyState(), null);
        var body2 = draft.Sections[0]?["body"]?.GetValue<string>();

        body2.Should().Contain("ТЗ — техническое задание");
        body2.Should().Contain("ГМ — геологическая модель");
    }

    // ------------------------------------------------------------ perimeter
    [Fact]
    public void perimeter_joins_object_and_perimeter()
    {
        var state = Fixtures.J("""{"object":"Месторождение Северное","perimeter":"Лицензионный участок 1"}""");

        var body = BodyOf(SectionsOf(state), "perimeter");

        body.Should().NotBeNull();
        body.Should().Contain("Объект работ: Месторождение Северное");
        body.Should().Contain("Лицензионный участок 1");
    }

    [Fact]
    public void perimeter_null_when_both_absent()
    {
        BodyOf(SectionsOf(Fixtures.EmptyState()), "perimeter").Should().BeNull();
    }

    // ------------------------------------------------------------ flags (в conditions)
    [Fact]
    public void conditions_renders_model3d_and_urgent_flags()
    {
        var state = Fixtures.J("""
            {"sourceData":"Геологические отчёты","flags":{"model3d":true,"urgent":true}}
            """);

        // Раздела conditions нет в GenericTemplate — собираем отдельный шаблон
        var template = new TemplateDefinition("tpl", "Т", "WORKS",
            Sections: new JsonArray
            {
                new JsonObject { ["key"] = "conditions", ["title"] = "Условия", ["required"] = false }
            },
            Fields: new JsonArray(), Risks: new JsonArray());

        var draft = Drafting.Build(template, state, null);
        var body = draft.Sections[0]?["body"]?.GetValue<string>();

        body.Should().NotBeNull();
        body.Should().Contain("трёхмерной геологической модели");
        body.Should().Contain("сокращённые сроки");
        body.Should().Contain("Требования к исходным данным: Геологические отчёты");
    }

    // ------------------------------------------------------------ filled-флаг секции
    [Fact]
    public void section_filled_flag_reflects_body_presence()
    {
        var draft = Drafting.Build(Fixtures.GenericTemplate(), Fixtures.FilledState(), null);

        var schedule = draft.Sections.First(s => s?["key"]?.GetValue<string>() == "schedule");
        schedule!["filled"]!.GetValue<bool>().Should().BeTrue();

        var emptyDraft = Drafting.Build(Fixtures.GenericTemplate(), Fixtures.EmptyState(), null);
        var emptySchedule = emptyDraft.Sections.First(s => s?["key"]?.GetValue<string>() == "schedule");
        emptySchedule!["filled"]!.GetValue<bool>().Should().BeFalse();
    }
}
