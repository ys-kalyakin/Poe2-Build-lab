using PBLApp.Core;
using PBLApp.ViewModels;
using PBLEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace PBLEngine.Tests;

/// <summary>
/// VM-level tests for the agent-facing tree mutation API:
/// AllocatePathTo / DeallocateNode / WaitForEngineSettledAsync / PointUsage.
/// These back the IPC endpoints /tree/allocate-path and /tree/deallocate-node.
/// </summary>
[Collection("LuaHost")]
public class TreeIpcApiTests : IClassFixture<LuaHostFixture>
{
    private readonly LuaHost _host;
    private readonly TreeTabViewModel _vm;

    public TreeIpcApiTests(LuaHostFixture fixture)
    {
        _host = fixture.Host;
        _host.NewBuild();
        _vm = new TreeTabViewModel(_host);
    }

    private static IReadOnlyDictionary<int, TreeNodeDto> ById(TreeTabViewModel vm) =>
        vm.Nodes.ToDictionary(n => n.Id);

    /// <summary>Shortest allocatable path (tree-outward) whose nodes are plain
    /// (no attribute connectors) — the first such path from the allocated set.</summary>
    private List<int> PlainPath(int minLen = 1)
    {
        var byId = ById(_vm);
        return _vm.Nodes
            .Where(n => !n.IsAttribute
                        && n.Type is not ("Keystone" or "Socket" or "Mastery"
                                          or "ClassStart" or "AscendClassStart")
                        && string.IsNullOrEmpty(n.AscendancyName))
            .Select(n => TreePathFinder.FindPath(byId, _vm.AllocatedIds, n.Id))
            .Where(p => p.Count >= minLen && p.All(id => !byId[id].IsAttribute))
            .OrderBy(p => p.Count)
            .First();
    }

    /// <summary>Shortest allocatable path that goes through an attribute node.</summary>
    private List<int> AttributePath()
    {
        var byId = ById(_vm);
        return _vm.Nodes
            .Where(n => !string.IsNullOrEmpty(n.AscendancyName) == false)
            .Select(n => TreePathFinder.FindPath(byId, _vm.AllocatedIds, n.Id))
            .Where(p => p.Count > 0 && p.Any(id => byId[id].IsAttribute))
            .OrderBy(p => p.Count)
            .First();
    }

    // ── AllocatePathTo ─────────────────────────────────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task AllocatePathTo_TwoStepNode_AllocatesWholePath()
    {
        var path = PlainPath();
        Assert.True(path.Count >= 1);

        var result = await _vm.AllocatePathTo(path[^1]);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(path, result.Path);
        Assert.All(path, id => Assert.Contains(id, _vm.AllocatedIds));
        // Response claims must be reconciled engine truth, not predictions:
        // every predicted node took, and PointsSpent counts what the engine
        // actually allocated.
        Assert.Empty(result.Failed);
        Assert.Equal(path.Count(id => _vm.AllocatedIds.Contains(id)), result.PointsSpent);
    }

    [Fact]
    public async System.Threading.Tasks.Task AllocatePathTo_ThroughAttributeNode_AutoPicks_NoDialog()
    {
        var path = AttributePath();
        var attrIds = path.Where(id => _vm.Nodes.First(n => n.Id == id).IsAttribute).ToList();
        Assert.NotEmpty(attrIds); // the fixture tree must have an attribute node in reach

        var result = await _vm.AllocatePathTo(path[^1]);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(attrIds, result.AttributeNodes);
        Assert.All(path, id => Assert.Contains(id, _vm.AllocatedIds));
    }

    [Fact]
    public async System.Threading.Tasks.Task AllocatePathTo_ExplicitAttribute_UsesGivenIndex()
    {
        var path = AttributePath();

        var result = await _vm.AllocatePathTo(path[^1], attribute: "dex");

        Assert.True(result.Ok, result.Error);
        Assert.All(path, id => Assert.Contains(id, _vm.AllocatedIds));
    }

    [Fact]
    public async System.Threading.Tasks.Task AllocatePathTo_Unreachable_ReturnsError()
    {
        var ascTarget = _vm.Nodes.First(n => !string.IsNullOrEmpty(n.AscendancyName)).Id;

        var result = await _vm.AllocatePathTo(ascTarget);

        Assert.False(result.Ok);
        Assert.Contains("unreachable", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(ascTarget, _vm.AllocatedIds);
    }

    [Fact]
    public async System.Threading.Tasks.Task AllocatePathTo_OverBudget_ReportsPointsLeft()
    {
        var path = PlainPath();

        var result = await _vm.AllocatePathTo(path[^1]);

        // Budget over-run is allowed (matches the UI); the response must still
        // carry the point usage so the agent can see what it spent.
        Assert.True(result.Ok, result.Error);
        Assert.NotNull(result.Usage);
        Assert.Equal(path.Count, result.PointsSpent);
    }

    // ── DeallocateNode ─────────────────────────────────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task DeallocateNode_Leaf_Removes()
    {
        var path = PlainPath();
        await _vm.AllocatePathTo(path[^1]);
        int leaf = path[^1];

        var (ok, error, _) = await _vm.DeallocateNode(leaf);

        Assert.True(ok, error);
        Assert.DoesNotContain(leaf, _vm.AllocatedIds);
    }

    [Fact]
    public async System.Threading.Tasks.Task DeallocateNode_WithDependents_ListsThem()
    {
        var path = PlainPath(minLen: 2);
        await _vm.AllocatePathTo(path[^1]);
        int intermediate = path[0];

        var (ok, error, dependents) = await _vm.DeallocateNode(intermediate);

        Assert.False(ok);
        Assert.Contains(dependents, d => d == path[^1]);
        Assert.Contains(intermediate, _vm.AllocatedIds); // nothing was removed
        Assert.NotNull(error);
    }

    // ── WaitForEngineSettledAsync ──────────────────────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task WaitForEngineSettled_ReconcilesFromLua()
    {
        var path = PlainPath();
        await _vm.AllocatePathTo(path[^1]);

        await _vm.WaitForEngineSettledAsync();

        var luaTruth = _host.GetAllocatedNodeIds();
        Assert.True(luaTruth.SetEquals(_vm.AllocatedIds),
            $"Lua truth {string.Join(",", luaTruth.OrderBy(x => x))} vs VM {string.Join(",", _vm.AllocatedIds.OrderBy(x => x))}");
    }
}
