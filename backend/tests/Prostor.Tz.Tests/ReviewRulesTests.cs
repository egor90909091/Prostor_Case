using FluentAssertions;
using Prostor.Tz;
using Xunit;

namespace Prostor.Tz.Tests;

/// <summary>
/// Переходы статусов согласования ТЗ. Как и DSL рисков, правила живут в
/// чистой функции без БД и HTTP — их можно проверить целиком здесь.
/// </summary>
public class ReviewRulesTests
{
    // ------------------------------------------------------------ направление
    [Fact]
    public void draft_cannot_be_sent_to_contractor()
    {
        var error = ReviewRules.ValidateSend("draft", new[] { "c1" });

        error.Should().NotBeNull();
        error!.Code.Should().Be("draft_not_sendable");
    }

    [Fact]
    public void sending_requires_at_least_one_company()
    {
        ReviewRules.ValidateSend("final", Array.Empty<string>())!.Code.Should().Be("no_companies");
    }

    [Fact]
    public void final_document_is_sendable()
    {
        ReviewRules.ValidateSend("final", new[] { "c1", "c2" }).Should().BeNull();
    }

    // -------------------------------------------------------------- решение
    [Theory]
    [InlineData("revision")]
    [InlineData("rejected")]
    public void revision_and_rejection_require_a_reason(string decision)
    {
        var error = ReviewRules.ValidateDecision("viewed", decision, "   ");

        error.Should().NotBeNull();
        error!.Code.Should().Be("comment_required");
    }

    [Fact]
    public void approval_without_comment_is_allowed()
    {
        ReviewRules.ValidateDecision("viewed", "approved", null).Should().BeNull();
    }

    [Fact]
    public void unknown_decision_is_rejected()
    {
        ReviewRules.ValidateDecision("sent", "maybe", "текст")!.Code.Should().Be("bad_decision");
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("revision")]
    public void decided_assignment_cannot_be_decided_twice(string current)
    {
        // После любого исхода направление закрыто: повторное согласование
        // идёт уже по новой версии ТЗ, то есть по другому направлению.
        var error = ReviewRules.ValidateDecision(current, "approved", "текст");

        error.Should().NotBeNull();
        error!.Code.Should().Be("already_decided");
    }

    [Theory]
    [InlineData("sent")]
    [InlineData("viewed")]
    public void pending_assignment_accepts_a_decision(string current)
    {
        ReviewRules.ValidateDecision(current, "revision", "уточните объём выборки")
            .Should().BeNull();
    }

    // ------------------------------------------------------- сводный статус
    [Fact]
    public void document_without_assignments_has_no_review_status()
    {
        ReviewRules.Summarize(Array.Empty<string>()).Should().BeNull();
    }

    [Fact]
    public void revision_outranks_everything_else()
    {
        ReviewRules.Summarize(new[] { "approved", "sent", "revision" }).Should().Be("revision");
    }

    [Fact]
    public void pending_assignment_outranks_a_refusal_from_another_company()
    {
        // Один отказ не делает заявку отклонённой, пока кто-то ещё думает.
        ReviewRules.Summarize(new[] { "rejected", "sent" }).Should().Be("sent");
    }

    [Fact]
    public void refusal_shows_when_nobody_is_left_to_answer()
    {
        ReviewRules.Summarize(new[] { "rejected" }).Should().Be("rejected");
    }

    [Fact]
    public void approval_is_the_quietest_status()
    {
        ReviewRules.Summarize(new[] { "approved", "approved" }).Should().Be("approved");
    }
}
