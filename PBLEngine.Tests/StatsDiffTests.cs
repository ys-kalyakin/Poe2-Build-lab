using PBLApp.ViewModels;
using System.Collections.Generic;
using Xunit;

namespace PBLEngine.Tests;

/// <summary>Diff снапшотов статов для visual_stats_diff: сравнение примерок
/// до/после. Значения AllStats — примитивы (double/string/bool), типы могут
/// не совпадать между снапшотами — сравнение не должно падать и не должно
/// видеть изменение там, где значение равно.</summary>
public class StatsDiffTests
{
    private static Dictionary<string, object?> D(params (string K, object? V)[] items)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in items) d[k] = v;
        return d;
    }

    [Fact]
    public void NumberChanged_GoesToChanged()
    {
        var r = StatsDiff.Compute(
            D(("TotalDPS", 100.0), ("Life", 4000.0)),
            D(("TotalDPS", 150.0), ("Life", 4000.0)));
        var c = Assert.Single(r.Changed);
        Assert.Equal("TotalDPS", c.Key);
        Assert.Equal(100.0, c.From);
        Assert.Equal(150.0, c.To);
        Assert.Empty(r.Added);
        Assert.Empty(r.Removed);
    }

    [Fact]
    public void SameValueDifferentNumericType_IsEqual()
    {
        var r = StatsDiff.Compute(
            D(("Life", 4000.0)),          // double
            D(("Life", 4000L)));          // long из Lua-integer
        Assert.Empty(r.Changed);
        Assert.Empty(r.Added);
        Assert.Empty(r.Removed);
    }

    [Fact]
    public void StringVsNumber_ComparedAsStringForm_NoException()
    {
        var r = StatsDiff.Compute(
            D(("Speed", "12")),
            D(("Speed", 13.0)));
        var c = Assert.Single(r.Changed);
        Assert.Equal("Speed", c.Key);
    }

    [Fact]
    public void BoolChanged_GoesToChanged()
    {
        var r = StatsDiff.Compute(D(("ManaCostWarning", false)), D(("ManaCostWarning", true)));
        Assert.Single(r.Changed);
    }

    [Fact]
    public void AddedAndRemoved_Reported()
    {
        var r = StatsDiff.Compute(
            D(("Old", 1.0)),
            D(("New", 2.0)));
        Assert.Equal(["Old"], r.Removed);
        var a = Assert.Single(r.Added);
        Assert.Equal(("New", 2.0), (a.Key, a.To));
    }

    [Fact]
    public void IdenticalDictionaries_AllEmpty()
    {
        var d = D(("TotalDPS", 1.0), ("Name", "x"), ("Flag", true), ("Null", null));
        var r = StatsDiff.Compute(d, D(("TotalDPS", 1.0), ("Name", "x"), ("Flag", true), ("Null", null)));
        Assert.Empty(r.Changed);
        Assert.Empty(r.Added);
        Assert.Empty(r.Removed);
    }
}
