namespace Prostor.Tz;

public sealed record ReviewError(string Code, string Message);

/// <summary>
/// Правила согласования ТЗ подрядчиком — без БД и без HTTP, чтобы переходы
/// статусов проверялись тестами так же, как DSL рисков в Drafting.
///
/// Жизненный цикл направления (tz.assignment.status):
///   sent → viewed → approved | revision | rejected
/// Все три исхода терминальны для этой версии ТЗ: после «на доработку»
/// заказчик правит документ, и повторное направление создаётся уже на
/// новую версию (новый tz_id), то есть это другое направление.
/// </summary>
public static class ReviewRules
{
    public static readonly string[] Decisions = { "approved", "revision", "rejected" };

    /// <summary>Отказ и доработка без причины бессмысленны для заказчика.</summary>
    public static bool RequiresComment(string decision) =>
        decision is "revision" or "rejected";

    public static bool IsDecided(string status) =>
        status is "approved" or "revision" or "rejected";

    /// <summary>Можно ли направить документ подрядчику.</summary>
    public static ReviewError? ValidateSend(string documentStatus, IReadOnlyCollection<string> companyIds)
    {
        if (documentStatus == "draft")
            return new ReviewError("draft_not_sendable",
                "Черновик нельзя направить подрядчику: сформируйте документ.");

        if (companyIds.Count == 0)
            return new ReviewError("no_companies", "Не выбрано ни одной компании-подрядчика.");

        return null;
    }

    /// <summary>Можно ли вынести решение по направлению.</summary>
    public static ReviewError? ValidateDecision(string currentStatus, string? decision, string? text)
    {
        if (decision is null || !Decisions.Contains(decision))
            return new ReviewError("bad_decision", "Неизвестное решение по ТЗ.");

        if (IsDecided(currentStatus))
            return new ReviewError("already_decided",
                "Решение по этой версии ТЗ уже вынесено.");

        if (RequiresComment(decision) && string.IsNullOrWhiteSpace(text))
            return new ReviewError("comment_required",
                decision == "rejected"
                    ? "Укажите причину отклонения."
                    : "Опишите, что нужно доработать в ТЗ.");

        return null;
    }

    /// <summary>
    /// Сводный статус документа по всем его направлениям — то, что видит
    /// заказчик в списке «Мои заявки». Порядок приоритета — по срочности
    /// действия заказчика: сначала то, что требует правок, затем то, чего
    /// ещё ждём, затем отказ (нужно искать другого исполнителя) и только
    /// потом согласование. Пустой список — ТЗ никому не направляли.
    /// </summary>
    public static string? Summarize(IEnumerable<string> statuses)
    {
        var all = statuses.ToList();
        if (all.Count == 0) return null;
        if (all.Contains("revision")) return "revision";
        if (all.Any(s => s is "sent" or "viewed")) return "sent";
        if (all.Contains("rejected")) return "rejected";
        if (all.Contains("approved")) return "approved";
        return null;
    }
}
