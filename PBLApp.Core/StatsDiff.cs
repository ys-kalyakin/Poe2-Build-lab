using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PBLApp.ViewModels;

/// <summary>Одно отличие двух снапшотов статов: значение <see cref="From"/> в
/// «до», <see cref="To"/> — в «после». Для Added From = null, для Removed
/// удаляется только ключ.</summary>
public sealed record StatChange(string Key, object? From, object? To);

public sealed record StatsDiffResult(
    List<StatChange> Changed,
    List<StatChange> Added,
    List<string> Removed);

/// <summary>Сравнение снапшотов build.AllStats. Значения — примитивы Lua
/// (double/long/string/bool/null), типы между снапшотами могут не совпадать:
/// сравниваем строковой формой (InvariantCulture), чтобы 4000.0 == 4000L, а
/// «12» (string) vs 13.0 было изменением, а не исключением.</summary>
public static class StatsDiff
{
    public static StatsDiffResult Compute(
        IReadOnlyDictionary<string, object?> before,
        IReadOnlyDictionary<string, object?> after)
    {
        var changed = new List<StatChange>();
        var added = new List<StatChange>();
        var removed = new List<string>();
        foreach (var (key, to) in after.OrderBy(kv => kv.Key, System.StringComparer.Ordinal))
        {
            if (!before.TryGetValue(key, out var from))
            {
                added.Add(new StatChange(key, null, to));
                continue;
            }
            if (!EqualsForm(from, to))
                changed.Add(new StatChange(key, from, to));
        }
        foreach (var key in before.Keys.Where(k => !after.ContainsKey(k)).OrderBy(k => k, System.StringComparer.Ordinal))
            removed.Add(key);
        return new StatsDiffResult(changed, added, removed);
    }

    private static bool EqualsForm(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a.GetType() == b.GetType()) return a.Equals(b);
        return ToForm(a) == ToForm(b);
    }

    private static string ToForm(object v) => v switch
    {
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };
}
