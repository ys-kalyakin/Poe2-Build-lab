using PBLApp.ViewModels;
using PBLEngine;
using System;
using System.Linq;
using Xunit;

namespace PBLEngine.Tests;

/// <summary>
/// VM-level contract tests for the agent-facing skills API (the /skills/*
/// IPC endpoints are thin wrappers over these VM calls):
/// engine 1-based group/gem indexes, listing after every mutation,
/// granted groups not removable. Note: the engine re-indexes groups/gems
/// densely after every removal — the listing echo is the only valid addressing.
/// Lookups use CommittedName (raw gem name); ActiveGemName is localized.
/// </summary>
[Collection("LuaHost")]
public class SkillsIpcApiTests : IClassFixture<LuaHostFixture>
{
    private readonly LuaHost _host;
    private readonly BuildModel _build;
    private readonly SkillsTabViewModel _vm;

    public SkillsIpcApiTests(LuaHostFixture fixture)
    {
        _host = fixture.Host;
        _host.NewBuild();
        _build = new BuildModel(_host);
        _vm = new SkillsTabViewModel(_host, _build);
    }

    private void AddSkill(string gemLine)
    {
        _host.State["_testSkillText"] = gemLine.Replace("\n", "\r\n");
        _host.State.DoString(@"
            build.skillsTab:PasteSocketGroup(_testSkillText)
            runCallback('OnFrame')
            if build.calcsTab then build.calcsTab:BuildOutput() end
        ");
        _host.State["_testSkillText"] = null;
        _vm.Refresh();
    }

    private SkillGroupViewModel GroupByName(string name) =>
        _vm.Groups.First(g => g.ActiveGem?.CommittedName.Contains(name, StringComparison.OrdinalIgnoreCase) == true);

    // ── Add / remove groups ────────────────────────────────────────────────

    [Fact]
    public void AddGroupFromPicker_NewGroup_LastIndex()
    {
        int maxBefore = _vm.Groups.Count == 0 ? 0 : _vm.Groups.Max(g => g.Index);

        _vm.AddGroupFromPicker("Fireball");

        var added = _vm.Groups.OrderBy(g => g.Index).Last();
        Assert.True(added.Index > maxBefore);
        Assert.Contains("Fireball", added.ActiveGem!.CommittedName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoveGroup_Middle_IndicesShift()
    {
        AddSkill("Fireball 20/20  1");
        AddSkill("Lightning Warp 20/20  1");
        AddSkill("Frost Bomb 20/20  1");
        Assert.Equal(3, _vm.Groups.Count);
        int middle = _vm.Groups.OrderBy(g => g.Index).ElementAt(1).Index;

        _vm.RemoveGroup(middle);

        Assert.Equal(2, _vm.Groups.Count);
        Assert.DoesNotContain(_vm.Groups,
            g => g.ActiveGem?.CommittedName.Contains("Lightning Warp", StringComparison.OrdinalIgnoreCase) == true);
        // Engine re-indexes densely after removal — the listing echo is the
        // only valid addressing for subsequent calls.
        Assert.Equal(new[] { 1, 2 }, _vm.Groups.Select(g => g.Index).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void GrantedGroups_AreNotRemovable()
    {
        // Contract pin: granted groups (from tree nodes / items) are protected.
        // The IPC remove-group handler refuses exactly when IsGranted is true.
        Assert.All(_vm.Groups, g => Assert.Equal(!g.IsGranted, g.CanRemove));
    }

    // ── Gems ───────────────────────────────────────────────────────────────

    [Fact]
    public void SetGem_ChangesLevelQualityEnabled()
    {
        AddSkill("Fireball 20/20  1");
        var grp = GroupByName("Fireball");
        int gemIdx = grp.ActiveGem!.GemIndex;

        _vm.SyncGemLevel(grp.Index, gemIdx, 10);
        _vm.SyncGemQuality(grp.Index, gemIdx, 10);
        _vm.SyncGemEnabled(grp.Index, gemIdx, false);
        _vm.Refresh();   // the IPC listing always re-reads the engine after mutations

        var fresh = GroupByName("Fireball");
        var gem = fresh.ActiveGem!;
        Assert.Equal(10, (int)gem.Level);
        Assert.Equal(10, (int)gem.Quality);
        Assert.False(gem.IsEnabled);
    }

    [Fact]
    public void AddGem_SupportGem_Appears()
    {
        AddSkill("Fireball 20/20  1");
        var grp = GroupByName("Fireball");
        // A real PoE2 support-gem name from the engine's own list — hand-written
        // PoE1-style names ("... Support") may not resolve in PoE2 gem data.
        string supportName = _vm.SupportGemNames.First();

        _vm.AddGem(grp.Index);
        int newIdx = grp.SupportSlots.Where(s => s.GemIndex > 0).Max(s => s.GemIndex);
        _vm.CommitGemName(grp.Index, newIdx, supportName);

        var fresh = GroupByName("Fireball");
        var support = fresh.SupportSlots.First(s => s.GemIndex == newIdx);
        Assert.Equal(supportName, support.CommittedName);
        Assert.True(support.IsSupport);
    }

    [Fact]
    public void RemoveGem_ThenRelist_IndexesShift()
    {
        AddSkill("Fireball 20/20  1\nAdded Lightning Damage Support 20/20  1");
        var grp = GroupByName("Fireball");
        int gemCount = 1 + grp.SupportSlots.Count(s => s.GemIndex > 0);
        Assert.True(gemCount >= 2, "need 2 gems");

        _vm.RemoveGem(grp.Index, 1);   // engine 1-based: remove the first gem

        var fresh = _vm.Groups.First(g => g.Index == grp.Index);   // group index unchanged
        var remaining = new[] { fresh.ActiveGem }.Concat(fresh.SupportSlots)
            .Where(s => s is { GemIndex: > 0 }).ToList();
        Assert.Single(remaining);
        Assert.Equal(1, remaining[0]!.GemIndex);   // engine re-indexed after removal
        Assert.Contains("Added Lightning Damage", remaining[0]!.CommittedName, StringComparison.OrdinalIgnoreCase);
    }
}
