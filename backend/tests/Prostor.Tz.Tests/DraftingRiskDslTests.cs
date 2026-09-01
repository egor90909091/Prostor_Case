using System.Text.Json.Nodes;
using FluentAssertions;
using Prostor.Tz;
using Xunit;

namespace Prostor.Tz.Tests;

/// <summary>
/// Тесты интерпретатора мини-DSL из risk_rules.when.
/// Покрывает каждый оператор: empty, empty_list, flag, missing_stage,
/// duration_below_typical, stages_without_docs, and/or/not, плюс
/// граничные случаи: пустой when, неизвестный op, вложенные условия.
///
/// DSL — чистая функция от (condition, state, typicalDays), поэтому
/// каждый тест сводится к шаблону с одним risk_rule и проверке, что
/// риск либо сработал (попал в Draft.Risks), либо нет.
/// </summary>
public class DraftingRiskDslTests
{
    // ------------------------------------------------------------ empty
    [Fact]
    public void empty_string_slot_fires_when_absent()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"empty","arg":"object"}}
            """);

        var draft = Drafting.Build(template, Fixtures.EmptyState(), typicalDays: null);

        draft.Risks.Should().ContainSingle(r => r.Code == "r");
    }

    [Fact]
    public void empty_string_slot_does_not_fire_when_filled()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"empty","arg":"object"}}
            """);

        var draft = Drafting.Build(template, Fixtures.J("""{"object":"Месторождение"}"""), null);

        draft.Risks.Should().BeEmpty();
    }

    [Fact]
    public void empty_period_fires_when_only_from_set()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"empty","arg":"period"}}
            """);

        var draft = Drafting.Build(template, Fixtures.J("""{"period":{"from":"2025-01-01"}}"""), null);

        draft.Risks.Should().ContainSingle(r => r.Code == "r");
    }

    [Fact]
    public void empty_period_does_not_fire_when_both_ends_set()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"empty","arg":"period"}}
            """);

        var draft = Drafting.Build(template,
            Fixtures.J("""{"period":{"from":"2025-01-01","to":"2025-03-31"}}"""), null);

        draft.Risks.Should().BeEmpty();
    }

    [Fact]
    public void empty_source_data_uses_sourceData_key()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"empty","arg":"source_data"}}
            """);

        var draftEmpty = Drafting.Build(template, Fixtures.EmptyState(), null);
        var draftFilled = Drafting.Build(template, Fixtures.J("""{"sourceData":"геологические отчёты"}"""), null);

        draftEmpty.Risks.Should().ContainSingle(r => r.Code == "r");
        draftFilled.Risks.Should().BeEmpty();
    }

    // ------------------------------------------------------------ empty_list
    [Theory]
    [InlineData("stages", """{"stages":[]}""", true)]
    [InlineData("stages", """{"stages":[{"name":"Этап 1"}]}""", false)]
    [InlineData("executors", """{}""", true)]
    [InlineData("executors", """{"executors":[{"id":"c-1","name":"ООО"}]}""", false)]
    [InlineData("operations", """{"operationIds":[]}""", true)]
    [InlineData("operations", """{"operationIds":["op-1"]}""", false)]
    [InlineData("unknown", """{}""", true)]
    public void empty_list_checks_array_slots(string slot, string stateJson, bool expectedFires)
    {
        var template = Fixtures.TemplateWithRisks(
            // $$$ / {{{...}}}: строка заканчивается тремя закрывающими скобками
            // подряд, и при $$ компилятор принял бы их за конец интерполяции.
            $$$"""{"code":"r","severity":"warning","title":"t","recommendation":"rec","when":{"op":"empty_list","arg":"{{{slot}}}"}}""");

        var draft = Drafting.Build(template, Fixtures.J(stateJson), null);

        if (expectedFires)
            draft.Risks.Should().ContainSingle(r => r.Code == "r");
        else
            draft.Risks.Should().BeEmpty();
    }

    // ------------------------------------------------------------ flag
    [Fact]
    public void flag_fires_when_true()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"flag","arg":"model3d"}}
            """);

        var draft = Drafting.Build(template, Fixtures.J("""{"flags":{"model3d":true}}"""), null);

        draft.Risks.Should().ContainSingle(r => r.Code == "r");
    }

    [Fact]
    public void flag_does_not_fire_when_false_or_absent()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"flag","arg":"model3d"}}
            """);

        Drafting.Build(template, Fixtures.J("""{"flags":{"model3d":false}}"""), null)
            .Risks.Should().BeEmpty();
        Drafting.Build(template, Fixtures.J("""{"flags":{}}"""), null)
            .Risks.Should().BeEmpty();
        Drafting.Build(template, Fixtures.EmptyState(), null)
            .Risks.Should().BeEmpty();
    }

    // ------------------------------------------------------------ missing_stage
    [Fact]
    public void missing_stage_fires_when_no_stages()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"missing_stage","arg":"исходн"}}
            """);

        var draft = Drafting.Build(template, Fixtures.EmptyState(), null);

        draft.Risks.Should().ContainSingle(r => r.Code == "r");
    }

    [Fact]
    public void missing_stage_fires_when_stage_name_not_found()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"missing_stage","arg":"исходн"}}
            """);

        var draft = Drafting.Build(template,
            Fixtures.J("""{"stages":[{"name":"Моделирование"}]}"""), null);

        draft.Risks.Should().ContainSingle(r => r.Code == "r");
    }

    [Fact]
    public void missing_stage_does_not_fire_when_stage_present_case_insensitive()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"missing_stage","arg":"исходн"}}
            """);

        var draft = Drafting.Build(template,
            Fixtures.J("""{"stages":[{"name":"Подготовка ИСХОДНЫХ данных"}]}"""), null);

        draft.Risks.Should().BeEmpty();
    }

    // ------------------------------------------------------------ duration_below_typical
    [Fact]
    public void duration_below_typical_fires_when_shorter_than_factor()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"duration_below_typical","arg":"0.8"}}
            """);
        // typical 100 дней, фактор 0.8 → порог 80 дней; период 50 дней → срабатывает
        var state = Fixtures.J("""{"period":{"from":"2025-01-01","to":"2025-02-19"}}"""); // 50 дней

        var draft = Drafting.Build(template, state, typicalDays: 100);

        draft.Risks.Should().ContainSingle(r => r.Code == "r");
    }

    [Fact]
    public void duration_below_typical_does_not_fire_when_long_enough()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"duration_below_typical","arg":"0.8"}}
            """);
        // 90 дней >= 80
        var state = Fixtures.J("""{"period":{"from":"2025-01-01","to":"2025-03-31"}}"""); // 90 дней

        var draft = Drafting.Build(template, state, typicalDays: 100);

        draft.Risks.Should().BeEmpty();
    }

    [Fact]
    public void duration_below_typical_does_not_fire_when_no_typical_days()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"duration_below_typical","arg":"0.8"}}
            """);

        var draft = Drafting.Build(template,
            Fixtures.J("""{"period":{"from":"2025-01-01","to":"2025-01-02"}}"""), typicalDays: null);

        draft.Risks.Should().BeEmpty();
    }

    [Fact]
    public void duration_below_typical_uses_default_factor_when_arg_invalid()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"duration_below_typical","arg":"not-a-number"}}
            """);
        // невалидный фактор → по умолчанию 0.8; 50 < 80
        var state = Fixtures.J("""{"period":{"from":"2025-01-01","to":"2025-02-19"}}""");

        var draft = Drafting.Build(template, state, typicalDays: 100);

        draft.Risks.Should().ContainSingle(r => r.Code == "r");
    }

    [Fact]
    public void duration_below_typical_does_not_fire_when_dates_unparseable()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"duration_below_typical","arg":"0.8"}}
            """);

        var draft = Drafting.Build(template,
            Fixtures.J("""{"period":{"from":"вчера","to":"завтра"}}"""), typicalDays: 100);

        draft.Risks.Should().BeEmpty();
    }

    // ------------------------------------------------------------ stages_without_docs
    [Fact]
    public void stages_without_docs_fires_when_any_stage_missing_documentation()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"stages_without_docs"}}
            """);

        var draft = Drafting.Build(template,
            Fixtures.J("""{"stages":[{"name":"А","documentation":"Отчёт"},{"name":"Б"}]}"""), null);

        draft.Risks.Should().ContainSingle(r => r.Code == "r");
    }

    [Fact]
    public void stages_without_docs_does_not_fire_when_all_documented()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"stages_without_docs"}}
            """);

        var draft = Drafting.Build(template,
            Fixtures.J("""{"stages":[{"name":"А","documentation":"Отчёт"}]}"""), null);

        draft.Risks.Should().BeEmpty();
    }

    [Fact]
    public void stages_without_docs_does_not_fire_when_no_stages()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"stages_without_docs"}}
            """);

        Drafting.Build(template, Fixtures.EmptyState(), null)
            .Risks.Should().BeEmpty();
    }

    // ------------------------------------------------------------ and / or / not
    [Fact]
    public void and_fires_only_when_all_args_true()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"and","args":[
               {"op":"flag","arg":"model3d"},
               {"op":"missing_stage","arg":"исходн"}
             ]}}
            """);

        var both = Fixtures.J("""{"flags":{"model3d":true}}"""); // нет этапов → missing true
        var onlyFlag = Fixtures.J("""
            {"flags":{"model3d":true},"stages":[{"name":"Подготовка исходных данных"}]}
            """);

        Drafting.Build(template, both, null).Risks.Should().ContainSingle(r => r.Code == "r");
        Drafting.Build(template, onlyFlag, null).Risks.Should().BeEmpty();
    }

    [Fact]
    public void or_fires_when_any_arg_true()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"or","args":[
               {"op":"empty","arg":"object"},
               {"op":"empty","arg":"source_data"}
             ]}}
            """);

        var neither = Fixtures.J("""{"object":"Объект","sourceData":"Данные"}""");
        var one = Fixtures.J("""{"object":"Объект"}"""); // source_data пуст

        Drafting.Build(template, neither, null).Risks.Should().BeEmpty();
        Drafting.Build(template, one, null).Risks.Should().ContainSingle(r => r.Code == "r");
    }

    [Fact]
    public void not_inverts_condition()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"not","args":[{"op":"empty","arg":"object"}]}}
            """);

        var empty = Fixtures.EmptyState();
        var filled = Fixtures.J("""{"object":"Объект"}""");

        Drafting.Build(template, empty, null).Risks.Should().BeEmpty();     // empty=true → not=false
        Drafting.Build(template, filled, null).Risks.Should().ContainSingle(r => r.Code == "r");
    }

    [Fact]
    public void nested_and_or_not_combine()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"and","args":[
               {"op":"not","args":[{"op":"empty","arg":"object"}]},
               {"op":"or","args":[
                 {"op":"flag","arg":"urgent"},
                 {"op":"missing_stage","arg":"исходн"}
               ]}
             ]}}
            """);

        // object заполнен, urgent=true → срабатывает
        var fires = Fixtures.J("""{"object":"Объект","flags":{"urgent":true}}""");
        // object пуст → не срабатывает (первый аргумент and = false)
        var noObject = Fixtures.J("""{"flags":{"urgent":true}}""");
        // object заполнен, urgent=false, этап есть → оба or-аргумента false
        var noFire = Fixtures.J("""
            {"object":"Объект","flags":{"urgent":false},"stages":[{"name":"Подготовка исходных данных"}]}
            """);

        Drafting.Build(template, fires, null).Risks.Should().ContainSingle(r => r.Code == "r");
        Drafting.Build(template, noObject, null).Risks.Should().BeEmpty();
        Drafting.Build(template, noFire, null).Risks.Should().BeEmpty();
    }

    // ------------------------------------------------------------ граничные случаи
    [Fact]
    public void missing_when_node_never_fires()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec"}
            """);

        var draft = Drafting.Build(template, Fixtures.EmptyState(), null);

        draft.Risks.Should().BeEmpty();
    }

    [Fact]
    public void unknown_op_never_fires()
    {
        var template = Fixtures.TemplateWithRisks("""
            {"code":"r","severity":"warning","title":"t","recommendation":"rec",
             "when":{"op":"nonexistent_op","arg":"object"}}
            """);

        var draft = Drafting.Build(template, Fixtures.EmptyState(), null);

        draft.Risks.Should().BeEmpty();
    }

    // ------------------------------------------------------------ сортировка рисков
    [Fact]
    public void risks_sorted_by_severity_blocking_first()
    {
        var template = Fixtures.TemplateWithRisks(
            """
            {"code":"warning-1","severity":"warning","title":"W","recommendation":"r",
             "when":{"op":"empty","arg":"object"}}
            """,
            """
            {"code":"blocking-1","severity":"blocking","title":"B","recommendation":"r",
             "when":{"op":"empty","arg":"object"}}
            """,
            """
            {"code":"info-1","severity":"info","title":"I","recommendation":"r",
             "when":{"op":"empty","arg":"object"}}
            """);

        var draft = Drafting.Build(template, Fixtures.EmptyState(), null);

        draft.Risks.Select(r => r.Code)
            .Should().Equal("blocking-1", "warning-1", "info-1");
    }
}
