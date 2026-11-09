using System.Linq;
using PBLEngine;
using Xunit;

namespace PBLEngine.Tests;

/// <summary>Weapon set'ы: аллокация узла в режим set 1/2 через spec.allocMode —
/// бюджет WeaponSet1Used растёт, деаллокация возвращает. SetAllocMode валидирует
/// 0..2 (0 — общий режим, 1/2 — weapon set).</summary>
[Collection("LuaHost")]
public class WeaponSetTests : IClassFixture<LuaHostFixture>
{
    private readonly LuaHost _host;
    public WeaponSetTests(LuaHostFixture fixture) => _host = fixture.Host;

    /// <summary>Сосед class start'а: короткий путь без jewel-socket'ов.
    /// Дальние узлы через сокет движок прунит в weapon-set режиме (CanPathThroughAllocMode:
    /// сокет форсится в allocMode=0 и "отрезает" сегмент) — та же семантика, что в UI.</summary>
    private int FirstAllocatableNodeId()
    {
        var (nodes, _) = _host.GetTreeData();
        var byId = nodes.ToDictionary(n => n.Id);
        var start = nodes.First(n => n.Type == "ClassStart");
        return start.LinkedIds.First(id => byId.TryGetValue(id, out var n) && n.Type == "Normal");
    }

    [Fact]
    public void AllocInSetMode_CountedInWeaponSetBudget()
    {
        var nodeId = FirstAllocatableNodeId();
        _host.SetAllocMode(1);
        try
        {
            _host.AllocNode(nodeId);
            var usage = _host.GetPointUsage();
            Assert.NotNull(usage);
            Assert.True(usage!.WeaponSet1Used >= 1);
            // узел помечен как принадлежащий set 1
            Assert.Equal(1, _host.GetAllocatedNodeModes()[nodeId]);
        }
        finally
        {
            _host.DeallocNode(nodeId);
            _host.SetAllocMode(0);
        }
        Assert.Equal(0, _host.GetPointUsage()!.WeaponSet1Used);
    }

    [Fact]
    public void SetAllocMode_ValidatesRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _host.SetAllocMode(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => _host.SetAllocMode(-1));
        _host.SetAllocMode(2);   // валидно
        _host.SetAllocMode(0);
    }
}
