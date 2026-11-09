using System;
using System.Collections.Generic;
using System.Linq;

namespace PBLApp.ViewModels;

/// <summary>Резолв имён гемов из каталога для IPC/MCP-путей: точное совпадение
/// без учёта регистра, при промахе — детерминированные подсказки (сначала
/// вхождения подстроки — короче лучше, затем совпадения по словам).</summary>
public static class GemNameMatcher
{
    public static string? FindExact(string name, IEnumerable<string> all)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return all.FirstOrDefault(g => g.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static string[] Suggest(string name, IEnumerable<string> all, int max = 5)
    {
        if (string.IsNullOrWhiteSpace(name) || max <= 0) return [];
        var query = name.Trim();
        var bySubstring = all
            .Where(g => g.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => g.Length).ThenBy(g => g, StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var g in bySubstring)
        {
            result.Add(g);
            if (result.Count >= max) return [.. result];
        }
        // слова запроса ("fire bal") могут быть разбросаны по имени ("Fireball")
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length > 1)
        {
            foreach (var g in all
                         .Where(g => words.All(w => g.Contains(w, StringComparison.OrdinalIgnoreCase)))
                         .OrderBy(g => g.Length).ThenBy(g => g, StringComparer.Ordinal))
            {
                if (result.Contains(g)) continue;
                result.Add(g);
                if (result.Count >= max) break;
            }
        }
        return [.. result];
    }
}
