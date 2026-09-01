namespace Prostor.Tz;

/// <summary>
/// Кто сейчас работает с системой: заказчик (НТЦ) или компания-подрядчик.
///
/// ВАЖНО: это демо-контекст, а не авторизация. Аутентификации в прототипе
/// нет вовсе (см. docs/architecture.md), заголовок X-Prostor-Actor приходит
/// от фронта и принимается на веру — подделать его тривиально. Проверки
/// «подрядчик отвечает только за свою компанию» ниже нужны для
/// согласованности интерфейса (чтобы по прямой ссылке нельзя было случайно
/// вынести решение за чужую компанию), а не для разграничения доступа.
/// </summary>
public readonly record struct Actor(string Kind, string Id)
{
    public const string HeaderName = "X-Prostor-Actor";

    /// <summary>Заказчик по умолчанию: без заголовка система ведёт себя как раньше.</summary>
    public static readonly Actor Customer = new("customer", "ntc");

    public bool IsContractor => Kind == "contractor";

    /// <summary>Формат заголовка: "customer:ntc" либо "contractor:{company_id}".</summary>
    public static Actor From(HttpContext http)
    {
        var raw = http.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(raw)) return Customer;

        var parts = raw.Split(':', 2);
        if (parts.Length != 2) return Customer;

        var kind = parts[0].Trim();
        var id = parts[1].Trim();
        if (id.Length == 0) return Customer;

        return kind switch
        {
            "contractor" => new Actor("contractor", id),
            "customer" => new Actor("customer", id),
            _ => Customer
        };
    }
}
