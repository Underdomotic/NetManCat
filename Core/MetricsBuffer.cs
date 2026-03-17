using NetManCat.Models;

namespace NetManCat.Core;

/// <summary>
/// Buffer circolare in-memory per le metriche (modalità standard).
/// Thread-safe. Mantiene gli ultimi <see cref="Capacity"/> record.
/// </summary>
public class MetricsBuffer
{
    private readonly Queue<MetricRecord> _queue = new();

    /// <summary>Numero massimo di record conservati in memoria.</summary>
    public int Capacity { get; set; } = 10_000;

    /// <summary>Aggiunge un record; rimuove il più vecchio se si supera la capacità.</summary>
    public void Add(MetricRecord record)
    {
        lock (_queue)
        {
            if (_queue.Count >= Capacity)
                _queue.Dequeue();
            _queue.Enqueue(record);
        }
    }

    /// <summary>Restituisce uno snapshot immutabile dell'intero buffer.</summary>
    public MetricRecord[] Snapshot()
    {
        lock (_queue)
            return _queue.ToArray();
    }

    /// <summary>Svuota il buffer.</summary>
    public void Clear()
    {
        lock (_queue)
            _queue.Clear();
    }

    /// <summary>Numero di record attualmente in buffer.</summary>
    public int Count
    {
        get { lock (_queue) return _queue.Count; }
    }
}
