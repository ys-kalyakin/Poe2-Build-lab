using PBLApp.Core;
using PBLEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace PBLEngine.Tests;

/// <summary>
/// Pure BFS tests for TreePathFinder (no LuaHost needed).
/// </summary>
public class TreePathFinderTests
{
    private static TreeNodeDto N(int id, int[] links, string asc = "") =>
        new(id, 0, 0, "Normal", $"Node{id}", [], links, asc, "", "", "", "", 0, false);

    private static Dictionary<int, TreeNodeDto> ById(params TreeNodeDto[] nodes) =>
        nodes.ToDictionary(n => n.Id);

    [Fact]
    public void FindPath_StraightLine_ReturnsUnallocatedTailOnly()
    {
        var byId = ById(N(1, [2]), N(2, [1, 3]), N(3, [2]));

        var path = TreePathFinder.FindPath(byId, [1], 3);

        Assert.Equal(new List<int> { 2, 3 }, path);
    }

    [Fact]
    public void FindPath_AlreadyAllocated_ReturnsEmpty()
    {
        var byId = ById(N(1, [2]), N(2, [1]));

        var path = TreePathFinder.FindPath(byId, [1, 2], 2);

        Assert.Empty(path);
    }

    [Fact]
    public void FindPath_UnreachableAcrossAscendancy_ReturnsEmpty()
    {
        var byId = ById(N(1, [2]), N(2, [1]), N(100, [101], "Titan"), N(101, [100], "Titan"));

        var path = TreePathFinder.FindPath(byId, [1], 101);

        Assert.Empty(path);
    }

    [Fact]
    public void FindPath_WithinSameAscendancy_FindsPath()
    {
        var byId = ById(N(1, [2]), N(100, [101], "Titan"), N(101, [100], "Titan"));

        var path = TreePathFinder.FindPath(byId, [100], 101);

        Assert.Equal(new List<int> { 101 }, path);
    }

    [Fact]
    public void FindPath_MissingTarget_ReturnsEmpty()
    {
        var byId = ById(N(1, [2]), N(2, [1]));

        var path = TreePathFinder.FindPath(byId, [1], 999);

        Assert.Empty(path);
    }

    [Fact]
    public void FindPath_PrefersShortest()
    {
        // Diamond: 1 - (2, 3) - 4
        var byId = ById(N(1, [2, 3]), N(2, [1, 4]), N(3, [1, 4]), N(4, [2, 3]));

        var path = TreePathFinder.FindPath(byId, [1], 4);

        Assert.Equal(2, path.Count);
        Assert.Contains(4, path);
    }
}
