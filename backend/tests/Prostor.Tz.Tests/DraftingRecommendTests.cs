using System.Text.Json.Nodes;
using FluentAssertions;
using Prostor.Tz;
using Xunit;

namespace Prostor.Tz.Tests;

/// <summary>
/// Текстовые рекомендации Drafting.Recommend — что показать пользователю
/// по итогам проверки рисков: «готово», «устраните критичное», «рекомендуется дополнительно».
/// </summary>
public class DraftingRecommendTests
{
    [Fact]
    public void no_risks_gives_ready_to_approve_message()
    {
        var draft = Drafting.Build(Fixtures.GenericTemplate(), Fixtures.FilledState(), typicalDays: 30);

        draft.Risks.Should().BeEmpty();
        draft.Recommendation.Should().Contain("готово к согласованию");
        draft.Recommendation.Should().Contain("критичных рисков нет");
    }

    [Fact]
    public void blocking_risk_produces_must_fix_message()
    {
        var draft = Drafting.Build(Fixtures.GenericTemplate(), Fixtures.EmptyState(), null);

        draft.Recommendation.Should().Contain("необходимо устранить");
        draft.Recommendation.Should().ContainEquivalentOf("не указан объект работ");
    }

    [Fact]
    public void warning_risk_produces_recommendation_message()
    {
        // Заполнено всё, но model3d без этапа исходных данных → warning
        var state = Fixtures.J("""
            {"productId":"p-1","object":"Объект",
             "period":{"from":"2025-01-01","to":"2025-03-31"},
             "stages":[{"name":"Моделирование"}],"executors":[{"id":"c-1","name":"ООО"}],
             "flags":{"model3d":true}}
            """);

        var draft = Drafting.Build(Fixtures.GenericTemplate(), state, typicalDays: 30);

        draft.Risks.Should().OnlyContain(r => r.Severity == "warning");
        draft.Recommendation.Should().Contain("Рекомендуется дополнительно");
        draft.Recommendation.Should().NotContain("необходимо устранить");
    }

    [Fact]
    public void both_blocking_and_warning_combine_in_one_message()
    {
        // object пуст (blocking) + model3d без исходных (warning)
        var state = Fixtures.J("""
            {"productId":"p-1",
             "period":{"from":"2025-01-01","to":"2025-03-31"},
             "stages":[{"name":"Моделирование"}],"executors":[{"id":"c-1","name":"ООО"}],
             "flags":{"model3d":true}}
            """);

        var draft = Drafting.Build(Fixtures.GenericTemplate(), state, typicalDays: 30);

        draft.Recommendation.Should().Contain("необходимо устранить");
        draft.Recommendation.Should().Contain("Рекомендуется дополнительно");
    }
}
