namespace Prostor.Chat;

/// <summary>
/// Фоновая индексация каталога: считает эмбеддинги для чанков, у которых их ещё нет.
///
/// Живёт в том же процессе, что и Chat Service, — отдельный сервис и очередь
/// здесь не окупаются: каталог меняется десятками позиций в день. Батч
/// отправляется в модель одним запросом, при недоступности модели индексация
/// просто откладывается до следующего цикла, а поиск продолжает работать
/// на полнотекстовом канале.
/// </summary>
public sealed class Indexer : BackgroundService
{
    private readonly Db _db;
    private readonly IEmbedder _embedder;
    private readonly ILogger<Indexer> _log;

    public Indexer(Db db, IEmbedder embedder, ILogger<Indexer> log)
    {
        _db = db;
        _embedder = embedder;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // даём базе подняться
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var indexed = await IndexBatchAsync(ct);
                if (indexed == 0)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), ct);
                    continue;
                }
                _log.LogInformation("проиндексировано чанков: {Count}", indexed);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "цикл индексации завершился с ошибкой, повтор через минуту");
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
        }
    }

    private async Task<int> IndexBatchAsync(CancellationToken ct)
    {
        var total = 0;

        foreach (var table in new[] { "search.product_chunk", "search.company_chunk" })
        {
            var pending = await _db.GetChunksWithoutEmbeddingAsync(table, 50, ct);
            foreach (var (id, text) in pending)
            {
                var vector = await _embedder.EmbedAsync(text, ct);
                await _db.SetChunkEmbeddingAsync(table, id, vector, ct);
                total++;
            }
        }

        return total;
    }
}
