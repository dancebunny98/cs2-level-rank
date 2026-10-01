namespace LevelsRanks;

/// <summary>
/// Набор "грязных" игроков, которых нужно сохранить в БД.
/// Раньше это была обычная очередь: игрок добавлялся в неё на КАЖДЫЙ выстрел/попадание/изменение
/// опыта, а таймер забирал только 10 записей раз в 5 секунд. Очередь росла быстрее, чем
/// разбиралась, и в БД попадали данные многоминутной давности.
/// Здесь один и тот же объект User хранится максимум один раз, а забирается всё сразу.
/// </summary>
internal sealed class DirtyUserSet
{
    private readonly ConcurrentDictionary<User, byte> _set = new(ReferenceEqualityComparer.Instance);

    public int Count => _set.Count;

    public void Enqueue(User user) => _set.TryAdd(user, 0);

    /// <summary>Забирает все накопленные записи. Изменения, сделанные после забора, снова попадут в набор.</summary>
    public List<User> Drain()
    {
        var result = new List<User>(_set.Count);
        foreach (var key in _set.Keys)
            if (_set.TryRemove(key, out _))
                result.Add(key);
        return result;
    }
}
