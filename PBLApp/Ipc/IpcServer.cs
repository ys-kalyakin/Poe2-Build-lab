using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PBLApp.Core;
using PBLApp.Core.Localization;
using PBLApp.ViewModels;
using PBLEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PBLApp.Ipc;

/// <summary>
/// HTTP-based IPC server for PBLMcp to control the running PBLApp instance.
/// Binds 127.0.0.1 on a random port, writes a discovery file with port+token.
///
/// All endpoints dispatch their work to the Avalonia UI thread so they
/// operate on the live MainWindowViewModel and trigger normal binding updates.
/// </summary>
public sealed class IpcServer
{
    private readonly HttpListener _listener = new();
    private readonly string _token;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();

    public static IpcServer? Current { get; private set; }

    public int Port  => _port;
    public string Token => _token;

    private IpcServer(int port, string token)
    {
        _port  = port;
        _token = token;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    /// <summary>Start IPC server: bind a random localhost port, write discovery file, begin accept loop.</summary>
    public static IpcServer Start()
    {
        if (Current is not null) return Current;

        // Pick a free port by binding to :0 and reading back
        var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();

        var token = GenerateToken();
        var srv = new IpcServer(port, token);
        srv._listener.Start();
        WriteDiscoveryFile(port, token);
        _ = Task.Run(srv.AcceptLoopAsync);
        Current = srv;
        return srv;
    }

    private static string GenerateToken()
    {
        var buf = new byte[16];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToHexString(buf);
    }

    private static string DiscoveryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PathOfBuilding", "mcp-ipc.json");

    private static void WriteDiscoveryFile(int port, string token)
    {
        var dir = Path.GetDirectoryName(DiscoveryPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new
        {
            port,
            token,
            pid = Environment.ProcessId,
            startedAt = DateTime.UtcNow.ToString("o"),
        });
        File.WriteAllText(DiscoveryPath, json);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            // Auth via X-PoB-Token header
            var header = ctx.Request.Headers["X-PoB-Token"];
            if (header != _token)
            {
                ctx.Response.StatusCode = 401;
                await WriteJson(ctx, new { error = "Invalid or missing X-PoB-Token." });
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath ?? "";
            string body = "";
            if (ctx.Request.HasEntityBody)
                using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = await sr.ReadToEndAsync();

            object result = path switch
            {
                "/ping"          => new { ok = true, pid = Environment.ProcessId },
                "/state"         => await OnUi(GetState),
                "/screenshot"    => await OnUi(() => TakeScreenshot(body)),
                "/set-language"  => await OnUi(() => SetLanguage(body)),
                "/select-tab"    => await OnUi(() => SelectTab(body)),
                "/list-builds"   => await OnUi(ListBuilds),
                "/open-build"    => await OnUi(() => OpenBuild(body)),
                "/go-back"       => await OnUi(GoBack),
                "/items/state"        => await OnUi(ItemsState),
                "/items/select-weapon-set" => await OnUi(() => ItemsSelectWeaponSet(body)),
                "/items/select-slot"  => await OnUi(() => ItemsSelectSlot(body)),
                "/items/select-pool"  => await OnUi(() => ItemsSelectPool(body)),
                "/items/equip"        => await OnUi(() => ItemsEquip(body)),
                "/items/unequip"      => await OnUi(() => ItemsUnequip(body)),
                "/items/get-editor"   => await OnUi(GetEditorState),
                "/items/get-tooltip"  => await OnUi(GetTooltipState),
                "/items/all-tooltips" => await OnUi(AllTooltips),
                "/items/hover-text"   => await OnUi(HoverText),
                "/items/compatible-slots" => await OnUi(() => CompatibleSlots(body)),
                "/items/tattoo-state" => await OnUi(TattooState),
                "/items/set-tattoo"   => await OnUi(() => SetTattoo(body)),
                "/items/phylactery-state" => await OnUi(PhylacteryStateInfo),
                "/items/edit-current" => await OnUi(EditCurrentItem),
                "/items/cancel-edit"  => await OnUi(CancelEdit),
                "/items/editor-save"             => await OnUi(EditorSave),
                "/items/editor-create-new"       => await OnUi(() => EditorCreateNew(body)),
                "/items/editor-set-quality"      => await OnUi(() => EditorSetQuality(body)),
                "/items/editor-set-item-level"   => await OnUi(() => EditorSetItemLevel(body)),
                "/items/editor-set-corrupted"    => await OnUi(() => EditorSetCorrupted(body)),
                "/items/editor-set-rarity"       => await OnUi(() => EditorSetRarity(body)),
                "/items/editor-set-base"         => await OnUi(() => EditorSetBase(body)),
                "/items/editor-set-unique"       => await OnUi(() => EditorSetUnique(body)),
                "/items/editor-set-base-stat"    => await OnUi(() => EditorSetBaseStat(body)),
                "/items/editor-set-mod-slider"   => await OnUi(() => EditorSetModSlider(body)),
                "/items/editor-add-affix"        => await OnUi(() => EditorAddAffix(body)),
                "/items/editor-remove-mod"       => await OnUi(() => EditorRemoveMod(body)),
                "/items/editor-add-rune-socket"  => await OnUi(EditorAddRuneSocket),
                "/items/editor-set-rune"         => await OnUi(() => EditorSetRune(body)),
                "/items/editor-remove-rune-socket" => await OnUi(() => EditorRemoveRuneSocket(body)),
                "/items/bases"       => await OnUi(() => ItemBases(body)),
                "/items/affixes"     => await OnUi(() => ItemAffixes(body)),
                "/items/uniques"     => await OnUi(() => ItemUniques(body)),
                "/tree/state"             => await OnUi(TreeState),
                "/tree/set-search"        => await OnUi(() => TreeSetSearch(body)),
                "/tree/hover-node"        => await OnUi(() => TreeHoverNode(body)),
                "/tree/node-text"         => await OnUi(() => TreeNodeText(body)),
                "/tree/toggle-node"       => await OnUiAsync(() => TreeToggleNode(body)),
                "/tree/select-class"      => await OnUi(() => TreeSelectClass(body)),
                "/tree/select-ascendancy" => await OnUi(() => TreeSelectAscendancy(body)),
                "/tree/view"              => await OnUi(TreeGetView),
                "/tree/set-view"          => await OnUi(() => TreeSetView(body)),
                "/tree/focus-node"        => await OnUi(() => TreeFocusNode(body)),
                "/tree/zoom"              => await OnUi(() => TreeZoom(body)),
                "/tree/pan"               => await OnUi(() => TreePan(body)),
                "/tree/open-jewel-picker" => await OnUi(() => TreeOpenJewelPicker(body)),
                "/tree/pick-jewel"        => await OnUi(() => TreePickJewel(body)),
                "/tree/power-build"       => await OnUiAsync(() => TreePowerBuild(body)),
                "/tree/power-report"      => await OnUi(TreePowerReport),
                "/tree/allocated"         => await OnUi(TreeAllocated),
                "/tree/allocate-path"     => await OnUiAsync(() => TreeAllocatePath(body)),
                "/tree/deallocate-node"   => await OnUiAsync(() => TreeDeallocateNode(body)),
                "/tree/nodes"             => await OnUi(() => TreeNodes(body)),
                "/skills/tooltips"        => await OnUi(SkillsTooltips),
                "/skills/list"            => await OnUi(SkillsList),
                "/skills/dps"             => await OnUi(SkillsDps),
                "/skills/gem-catalog"     => await OnUi(() => GemCatalog(body)),
                "/skills/gem-tooltip"     => await OnUi(() => GemTooltip(body)),
                "/skills/add-group"       => await OnUi(() => SkillsAddGroup(body)),
                "/skills/remove-group"    => await OnUi(() => SkillsRemoveGroup(body)),
                "/skills/add-gem"         => await OnUi(() => SkillsAddGem(body)),
                "/skills/remove-gem"      => await OnUi(() => SkillsRemoveGem(body)),
                "/skills/set-gem"         => await OnUi(() => SkillsSetGem(body)),
                "/skills/set-group"       => await OnUi(() => SkillsSetGroup(body)),
                "/skills/set-main"        => await OnUi(() => SkillsSetMain(body)),
                "/skills/support-picker"          => await OnUi(() => SupportPickerState(body)),
                "/skills/support-picker/set-tab"  => await OnUi(() => SupportPickerSetTab(body)),
                "/skills/support-picker/set-mode" => await OnUi(() => SupportPickerSetMode(body)),
                "/skills/support-picker/select"   => await OnUi(() => SupportPickerSelect(body)),
                "/ui/text"                => await OnUi(() => DumpUiText(body)),
                "/app/open-settings"      => await OnUi(OpenAppSettings),
                "/app/set-theme"          => await OnUi(() => SetAppTheme(body)),
                "/character-import/open" => await OnUi(() => CharacterImportOpen(body)),
                "/export/open"           => await OnUi(() => ImportExportOpen(body)),
                "/build/update-from-game" => await OnUi(UpdateFromGame),
                "/stats"         => await OnUi(() => GetStatsFiltered(ctx.Request.Url?.Query ?? "")),
                "/stats/snapshot" => await OnUi(() => StatsSnapshot(body)),
                "/stats/diff"     => await OnUi(() => StatsDiffHandler(ctx.Request.Url?.Query ?? "")),
                "/build/save"    => await OnUiAsync(BuildSave),
                "/build/xml"     => await OnUi(BuildXml),
                "/build/import"  => await OnUiAsync(() => BuildImport(body)),
                "/build/validate" => await OnUi(BuildValidate),
                "/notes"         => ctx.Request.HttpMethod == "POST"
                                        ? await OnUi(() => NotesSet(body))
                                        : await OnUi(NotesGet),
                "/config/state"  => await OnUi(ConfigState),
                "/config/set"    => await OnUi(() => ConfigSet(body)),
                "/app/screens"           => await OnUi(ListScreens),
                "/app/move-to-screen"    => await OnUi(() => MoveToScreen(body)),
                "/trader/state"      => await OnUi(TraderState),
                "/trader/open"       => await OnUi(() => TraderOpen(body)),
                "/trader/search"     => await OnUi(() => TraderSearch(body)),
                "/prices/currency"   => await OnUiAsync(() => PricesCurrency(body)),
                "/prices/uniques"    => await OnUiAsync(() => PricesUniques(body)),
                "/trader/set-league" => await OnUi(() => TraderSetLeague(body)),
                _ => new { error = $"Unknown endpoint: {path}" }
            };

            ctx.Response.StatusCode = 200;
            await WriteJson(ctx, result);
        }
        catch (Exception ex)
        {
            try
            {
                ctx.Response.StatusCode = 500;
                await WriteJson(ctx, new { error = ex.GetType().Name + ": " + ex.Message });
            }
            catch { }
        }
    }

    private static async Task WriteJson(HttpListenerContext ctx, object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        var buf = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = buf.Length;
        await ctx.Response.OutputStream.WriteAsync(buf);
        ctx.Response.OutputStream.Close();
    }

    // ── UI-thread dispatch ────────────────────────────────────────────────

    private static async Task<object> OnUi(Func<object> fn)
        => await Dispatcher.UIThread.InvokeAsync(fn);

    private static async Task<object> OnUiAsync(Func<Task<object>> fn)
        => await Dispatcher.UIThread.InvokeAsync(fn);

    // ── Endpoint handlers (all run on UI thread) ──────────────────────────

    private static Window? GetMainWindow()
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private static MainWindowViewModel? GetMainVm()
        => GetMainWindow()?.DataContext as MainWindowViewModel;

    /// <summary>Test hook: open the app settings window (same as the gear button).</summary>
    private static object OpenAppSettings()
    {
        var owner = GetMainWindow();
        if (owner is null) return new { error = "MainWindow not available." };
        var window = new Views.AppSettingsWindow();
        _ = window.ShowDialog(owner);
        return new { ok = true };
    }

    /// <summary>Test hook: apply a theme mode ("System"|"Dark"|"Light") through
    /// ThemeService — the same path the settings ComboBox uses.</summary>
    private static object SetAppTheme(string body)
    {
        var req = string.IsNullOrWhiteSpace(body)
            ? new() : JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        var mode = PBLApp.Core.AppThemeModes.Parse(req.GetValueOrDefault("mode"));
        Services.ThemeService.Instance.Apply(mode);
        return new { ok = true, applied = mode.ToString() };
    }

    private static object GetState()
    {
        var vm = GetMainVm();
        if (vm is null) return new { error = "MainWindowViewModel not available." };

        var lang = LocalizationService.Instance.CurrentLanguage;
        var w = GetMainWindow();
        var size = w is null ? (0, 0) : ((int)w.ClientSize.Width, (int)w.ClientSize.Height);

        return vm.CurrentPage switch
        {
            BuildListViewModel bl => new
            {
                language = lang,
                page     = "BuildList",
                buildCount = EnumerateAllBuildFiles().Count(),
                selectedBuild = (string?)null,
                window = new { width = size.Item1, height = size.Item2 },
            },
            BuildPageViewModel bp => new
            {
                language = lang,
                page     = bp.IsLoading ? "BuildPageLoading" : bp.LoadError.Length > 0 ? "BuildPageError" : "BuildPageReady",
                buildName  = bp.BuildName,
                loadError  = bp.LoadError.Length > 0 ? bp.LoadError : null,
                selectedTabIndex = bp.SelectedTabIndex,
                selectedTabKey   = bp.SelectedTabKey,
                window = new { width = size.Item1, height = size.Item2 },
            },
            _ => new { language = lang, page = vm.CurrentPage?.GetType().Name, window = new { width = size.Item1, height = size.Item2 } }
        };
    }

    private static object SetLanguage(string body)
    {
        var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        if (!req.TryGetValue("lang", out var lang) || string.IsNullOrWhiteSpace(lang))
            return new { error = "Missing 'lang' field." };
        LocalizationService.Instance.SetLanguage(lang);
        // Force any open Items tab to refresh (translated item names rebuild)
        if (GetMainVm()?.CurrentPage is BuildPageViewModel bp && bp.ItemsTab is { } items)
            items.Refresh();
        return new { ok = true, language = LocalizationService.Instance.CurrentLanguage };
    }

    private static object SelectTab(string body)
    {
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        if (GetMainVm()?.CurrentPage is not BuildPageViewModel bp)
            return new { error = "Not on BuildPage." };

        int idx;
        if (req.TryGetValue("index", out var ie) && ie.ValueKind == JsonValueKind.Number)
            idx = ie.GetInt32();
        else if (req.TryGetValue("name", out var ne) && ne.ValueKind == JsonValueKind.String)
        {
            idx = Array.FindIndex(BuildPageViewModel.TabKeys,
                k => k.Equals(ne.GetString(), StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return new { error = $"Unknown tab '{ne.GetString()}'." };
        }
        else return new { error = "Provide 'name' or 'index'." };

        if (idx < 0 || idx >= BuildPageViewModel.TabKeys.Length)
            return new { error = $"Index {idx} out of range." };

        bp.SelectedTabIndex = idx;
        return new { ok = true, index = idx, key = bp.SelectedTabKey };
    }

    private static object ListBuilds()
    {
        if (GetMainVm()?.CurrentPage is not BuildListViewModel)
            return new { error = "Not on BuildList page." };
        var builds = EnumerateAllBuildFiles()
            .Select(f => new { name = System.IO.Path.GetFileNameWithoutExtension(f), isFolder = false })
            .ToArray();
        return new { builds };
    }

    /// <summary>Recursively list all *.xml build files under the Builds root.</summary>
    private static IEnumerable<string> EnumerateAllBuildFiles()
    {
        var root = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PathOfBuilding2", "Builds");
        if (!System.IO.Directory.Exists(root)) yield break;
        foreach (var f in System.IO.Directory.EnumerateFiles(root, "*.xml", System.IO.SearchOption.AllDirectories))
            yield return f;
    }

    private static object OpenBuild(string body)
    {
        var req  = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        if (GetMainVm()?.CurrentPage is not BuildListViewModel bl)
            return new { error = "Not on BuildList page." };
        ClearStatSnapshots();   // снапшоты статов принадлежат конкретному билду
        if (!req.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
            return new { error = "Missing 'name'." };

        var match = EnumerateAllBuildFiles()
            .FirstOrDefault(f =>
            {
                var n = System.IO.Path.GetFileNameWithoutExtension(f);
                return n.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || n.Contains(name, StringComparison.OrdinalIgnoreCase);
            });
        if (match is null) return new { error = $"Build '{name}' not found." };

        var entry = new BuildEntryViewModel(
            System.IO.Path.GetFileNameWithoutExtension(match)!, match, BuildEntryKind.Build);
        bl.OpenItemCommand.Execute(entry);
        return new { ok = true, opened = entry.Name };
    }

    private static object GoBack()
    {
        if (GetMainVm()?.CurrentPage is BuildPageViewModel bp)
        {
            bp.BackCommand.Execute(null);
            return new { ok = true };
        }
        return new { error = "Not on BuildPage." };
    }

    private static object TakeScreenshot(string body)
    {
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();

        // "window": какое окно снимать — главное (по умолчанию) или отдельное:
        // "export" (импорт/экспорт), "character-import", "settings", "notes".
        var which = req.TryGetValue("window", out var wsel) && wsel.ValueKind == JsonValueKind.String
            ? wsel.GetString() ?? "main"
            : "main";
        var window = which.ToLowerInvariant() switch
        {
            "export" or "import-export" =>
                AppWindows().OfType<Views.ImportExportWindow>().LastOrDefault() as Window,
            "character-import" =>
                AppWindows().OfType<Views.CharacterImportWindow>().LastOrDefault(),
            "settings" => AppWindows().OfType<Views.SettingsWindow>().LastOrDefault(),
            "notes"    => AppWindows().OfType<Views.NotesWindow>().LastOrDefault(),
            "confirm"  => AppWindows().OfType<Views.ConfirmDialog>().LastOrDefault(),
            _          => GetMainWindow(),
        };
        if (window is null) return new { error = $"Window '{which}' is not open." };

        var size = window.ClientSize;
        if (size.Width <= 0 || size.Height <= 0)
            return new { error = $"Window has zero size: {size}." };

        var px = new PixelSize(Math.Max(1, (int)size.Width), Math.Max(1, (int)size.Height));
        var dpi = new Vector(96, 96);

        // Render the whole window into a bitmap.
        var rtb = new RenderTargetBitmap(px, dpi);
        rtb.Render(window);

        // Resolve output path
        string outPath;
        if (req.TryGetValue("path", out var p) && p.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(p.GetString()))
        {
            outPath = p.GetString()!;
        }
        else
        {
            var dir = Path.Combine(Path.GetTempPath(), "pob-screenshots");
            Directory.CreateDirectory(dir);
            outPath = Path.Combine(dir, $"pob_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.png");
        }

        rtb.Save(outPath);
        return new
        {
            ok    = true,
            path  = outPath,
            width = px.Width,
            height = px.Height,
        };
    }

    // ── Items tab handlers ─────────────────────────────────────────────────

    private static ItemsTabViewModel? GetItemsVm()
        => (GetMainVm()?.CurrentPage as BuildPageViewModel)?.ItemsTab;

    private static ItemSlotViewModel[] AllSlots(ItemsTabViewModel v) => [
        v.Helmet, v.BodyArmour, v.Weapon1, v.Weapon2, v.Weapon1Swap, v.Weapon2Swap,
        v.Ring1, v.Ring2, v.Ring3, v.Gloves, v.Boots, v.Belt, v.Amulet,
        v.Flask1, v.Flask2, v.Charm1, v.Charm2, v.Charm3,
        v.Arm1, v.Arm2, v.Leg1, v.Leg2 ];

    /// <summary>Переключение активного weapon set (Weapon Set I / II) — влияет на
    /// то, какие скил-группы считаются (slot.weaponSet) и какие цифры показывает
    /// расчёт. set: 1 или 2.</summary>
    private static object ItemsSelectWeaponSet(string body)
    {
        if (GetItemsVm() is not { } v) return new { error = "ItemsTab not ready." };
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        if (!req.TryGetValue("set", out var s) || s.ValueKind != JsonValueKind.Number)
            return new { error = "Missing 'set' (1 or 2)." };
        var set = s.GetInt32();
        if (set is not (1 or 2))
            return new { error = $"Invalid weapon set {set}. Allowed: 1, 2." };
        if (set == 1) v.SelectWeaponSet1Command.Execute(null);
        else v.SelectWeaponSet2Command.Execute(null);
        return new { ok = true, weaponSet = set, isWeaponSwapActive = set == 2 };
    }

    private static object ItemsState()
    {
        if (GetItemsVm() is not { } v) return new { error = "ItemsTab not ready." };
        return new
        {
            selectedSlot       = v.SelectedSlotName,
            selectedPoolItemId = v.SelectedPoolItemId,
            isEditorOpen       = v.IsEditorOpen,
            isTooltipMode      = v.IsTooltipMode,
            isEditMode         = v.IsEditMode,
            isRightPaneEmpty   = v.IsRightPaneEmpty,
            isWeaponSwapActive = v.IsWeaponSwapActive,
            slots = AllSlots(v).Select(s => new
            {
                slot               = s.SlotName,
                isEmpty            = s.IsEmpty,
                displayName        = s.DisplayName,
                translatedBaseName = s.TranslatedBaseName,
                rarity             = s.Rarity,
            }).ToArray(),
            jewelSlots = v.JewelSlots.Select(j => new
            {
                slot = j.SlotName, nodeId = j.NodeId, nodeName = j.NodeName,
                isEmpty = j.IsEmpty, displayName = j.DisplayName,
            }).ToArray(),
            pool = v.ItemPool.Select(p => new
            {
                id = p.ItemId, name = p.Name, baseName = p.BaseName,
                rarity = p.Rarity, equippedSlot = p.EquippedSlot,
            }).ToArray(),
        };
    }

    private static object ItemsSelectSlot(string body)
    {
        if (GetItemsVm() is not { } v) return new { error = "ItemsTab not ready." };
        var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        if (!req.TryGetValue("slot", out var slot) || string.IsNullOrEmpty(slot))
            return new { error = "Missing 'slot'." };

        var all = AllSlots(v);
        var match = all.FirstOrDefault(s => s.SlotName.Equals(slot, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            var jewel = v.JewelSlots.FirstOrDefault(j => j.SlotName.Equals(slot, StringComparison.OrdinalIgnoreCase));
            if (jewel is null) return new { error = $"Slot '{slot}' not found." };
            v.SelectSlot(jewel.SlotName);
            return new { ok = true, selected = jewel.SlotName, isEmpty = jewel.IsEmpty };
        }
        v.SelectSlot(match.SlotName);
        return new { ok = true, selected = match.SlotName, isEmpty = match.IsEmpty, displayName = match.DisplayName };
    }

    private static object ItemsSelectPool(string body)
    {
        if (GetItemsVm() is not { } v) return new { error = "ItemsTab not ready." };
        var req = JsonSerializer.Deserialize<Dictionary<string, int>>(body) ?? new();
        if (!req.TryGetValue("id", out var id))
            return new { error = "Missing 'id'." };
        var entry = v.ItemPool.FirstOrDefault(e => e.ItemId == id);
        if (entry is null) return new { error = $"Pool item id={id} not found." };
        v.SelectPoolItem(id);
        return new { ok = true, name = entry.Name, baseName = entry.BaseName };
    }

    private static object ItemsEquip(string body)
    {
        if (GetItemsVm() is not { } v) return new { error = "ItemsTab not ready." };
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        if (!req.TryGetValue("id", out var ie) || ie.ValueKind != JsonValueKind.Number)
            return new { error = "Missing 'id' (number)." };
        if (!req.TryGetValue("slot", out var se) || se.ValueKind != JsonValueKind.String)
            return new { error = "Missing 'slot' (string)." };

        v.SelectPoolItem(ie.GetInt32());
        v.SelectedEquipSlot = se.GetString() ?? "";
        if (!v.CanEquipPoolItem) return new { error = "CanEquipPoolItem=false (incompatible slot?)." };
        v.EquipPoolItemCommand.Execute(null);
        return new { ok = true };
    }

    private static object ItemsUnequip(string body)
    {
        if (GetItemsVm() is not { } v) return new { error = "ItemsTab not ready." };
        var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        if (!req.TryGetValue("slot", out var slot) || string.IsNullOrEmpty(slot))
            return new { error = "Missing 'slot'." };

        var entry = v.ItemPool.FirstOrDefault(e => e.EquippedSlot.Equals(slot, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return new { error = $"No item equipped in '{slot}'." };
        v.SelectPoolItem(entry.ItemId);
        v.UnequipPoolItemCommand.Execute(null);
        return new { ok = true, unequipped = entry.Name };
    }

    private static object GetEditorState()
    {
        if (GetItemsVm() is not { } v) return new { error = "ItemsTab not ready." };
        if (v.ItemEditor is not { } ed) return new { isOpen = false };
        return new
        {
            isOpen            = true,
            title             = ed.EditorTitle,
            displayName       = ed.DisplayName,
            displayBaseName   = ed.DisplayBaseName,
            isEditingExisting = ed.IsEditingExisting,
            rarity            = ed.Rarity.ToString(),
            baseName          = ed.SelectedBase?.Name,
            uniqueName        = ed.SelectedUnique?.Name,
            itemLevel         = ed.ItemLevel,
            quality           = ed.Quality,
            isCorrupted       = ed.IsCorrupted,
            canEditMods       = ed.CanEditMods,
            prefixCount       = ed.PrefixCount,
            suffixCount       = ed.SuffixCount,
            maxRunes          = ed.MaxRunes,
            runeSockets       = ed.RuneSockets.Select(r => new
            {
                index    = r.Index,
                runeName = r.SelectedRune?.Name,
                modSummary = r.ModSummary,
            }).ToArray(),
            baseStats = new
            {
                armour        = new { value = ed.BaseArmour,       @default = ed.DefaultArmour,       overridden = ed.IsArmourOverridden },
                evasion       = new { value = ed.BaseEvasion,      @default = ed.DefaultEvasion,      overridden = ed.IsEvasionOverridden },
                energyShield  = new { value = ed.BaseEnergyShield, @default = ed.DefaultEnergyShield, overridden = ed.IsEnergyShieldOverridden },
                ward          = new { value = ed.BaseWard,         @default = ed.DefaultWard,         overridden = ed.IsWardOverridden },
                spirit        = new { value = ed.BaseSpirit,       @default = ed.DefaultSpirit,       overridden = ed.IsSpiritOverridden },
                charmSlots    = new { value = ed.BaseCharmSlots,   @default = ed.DefaultCharmSlots,   overridden = ed.IsCharmSlotsOverridden },
            },
            implicits         = ed.ImplicitMods.ToArray(),
            explicits         = ed.ExplicitMods.Select((m, i) => new
            {
                index         = i,
                text          = m.Text,
                translatedText = m.TranslatedText,
                affixType     = m.AffixType,
                affixLabel    = m.AffixLabel,
                isImplicit    = m.IsImplicit,
                isRemovable   = m.IsRemovable,
                hasSlider     = m.HasSlider,
                sliderValue   = m.SliderValue,
                sliderMin     = m.SliderMin,
                sliderMax     = m.SliderMax,
            }).ToArray(),
        };
    }

    private static object GetTooltipState()
    {
        if (GetItemsVm() is not { } v) return new { error = "ItemsTab not ready." };
        if (v.ItemTooltip is not { } tt) return new { isOpen = false };
        return new
        {
            isOpen = true,
            lineCount = tt.Lines.Count,
            lines = SerializeTooltipLines(tt),
        };
    }

    private static object[] SerializeTooltipLines(PBLApp.ViewModels.ItemTooltipViewModel tt)
        => tt.Lines.Select(l => new
        {
            kind     = l.Kind,
            size     = l.Size,
            centered = l.Centered,
            block    = l.Block,
            plain    = l.PlainText,
            segments = l.Segments.Select(s => new { text = s.Text, color = s.ColorHex }).ToArray(),
        }).ToArray();

    /// <summary>Builds the display tooltip for every equipped slot and every pool item,
    /// without disturbing the current selection. Lets inspection tooling read all
    /// tooltips at once for verification.</summary>
    private static object AllTooltips()
    {
        if (GetItemsVm() is not { } v) return new { error = "ItemsTab not ready." };

        var slotTooltips = new List<object>();
        foreach (var s in AllSlots(v))
        {
            if (s.IsEmpty) continue;
            var tt = v.BuildTooltipFor(null, s.SlotName);
            if (tt is null) continue;
            slotTooltips.Add(new
            {
                slot = s.SlotName, displayName = s.DisplayName,
                lineCount = tt.Lines.Count, lines = SerializeTooltipLines(tt),
            });
        }
        foreach (var j in v.JewelSlots)
        {
            if (j.IsEmpty) continue;
            var tt = v.BuildTooltipFor(null, j.SlotName);
            if (tt is null) continue;
            slotTooltips.Add(new
            {
                slot = j.SlotName, displayName = j.DisplayName,
                lineCount = tt.Lines.Count, lines = SerializeTooltipLines(tt),
            });
        }

        var poolTooltips = new List<object>();
        foreach (var p in v.ItemPool)
        {
            var tt = v.BuildTooltipFor(p.ItemId, null);
            if (tt is null) continue;
            poolTooltips.Add(new
            {
                id = p.ItemId, name = p.Name, equippedSlot = p.EquippedSlot,
                lineCount = tt.Lines.Count, lines = SerializeTooltipLines(tt),
            });
        }

        return new { ok = true, slots = slotTooltips.ToArray(), pool = poolTooltips.ToArray() };
    }

    // Returns the plain-text hover tooltip (the StyledHoverTooltip popup shown over
    // each slot/pool cell on pointer-hover) — a path separate from the right-pane
    // ItemTooltipViewModel, so it needs its own translation audit.
    private static object HoverText()
    {
        if (GetItemsVm() is not { } v) return new { error = "ItemsTab not ready." };
        return new
        {
            ok = true,
            slots = AllSlots(v).Where(s => !s.IsEmpty)
                .Select(s => new { slot = s.SlotName, text = s.HoverTooltipText }).ToArray(),
            pool = v.ItemPool.Select(p => new { id = p.ItemId, name = p.Name, text = p.HoverTooltipText }).ToArray(),
        };
    }

    // ── Skills tooltips ────────────────────────────────────────────────────
    // Returns the EXACT text rendered in each gem's hover tooltip. The tooltip
    // is a hover-only popup, so we read the same GemTooltipEntry list the view
    // binds to (built by SkillsTabViewModel.ProcessTooltipLines) and concatenate
    // each entry's rendered segments — identical to what the View draws.

    private static SkillsTabViewModel? GetSkillsVm()
        => (GetMainVm()?.CurrentPage as BuildPageViewModel)?.SkillsTab;

    private static object SkillsTooltips()
    {
        if (GetSkillsVm() is not { } v) return new { error = "SkillsTab not ready." };

        var groups = new List<object>();
        foreach (var g in v.Groups)
        {
            var gems = new List<object>();
            void AddGem(GemViewModel? gem)
            {
                if (gem is null || gem.GemIndex <= 0) return;
                gem.RefreshTooltip();   // same code path the view uses on hover
                var lines = SerializeGemTooltip(gem.TooltipEntries);
                gems.Add(new
                {
                    name        = gem.CommittedName,
                    displayName = GameTranslationService.TGem(gem.CommittedName),
                    gemIndex    = gem.GemIndex,
                    lineCount   = lines.Length,
                    lines,
                });
            }
            AddGem(g.ActiveGem);
            foreach (var s in g.SupportSlots) AddGem(s);

            groups.Add(new
            {
                index     = g.Index,
                isMain    = g.IsMain,
                activeGem = g.ActiveGemName,
                gems      = gems.ToArray(),
            });
        }
        return new { ok = true, groups = groups.ToArray() };
    }

    // ── Skills CRUD (agent-facing) ─────────────────────────────────────────
    // Addressing contract: engine 1-based group/gem indexes (group == the
    // 'index' field of the listing, gem == 'gemIndex'). The engine re-indexes
    // densely after every removal, so EVERY mutation response echoes a fresh
    // listing — that echo is the only valid addressing for the next call.

    private static object[] SkillGemList(SkillGroupViewModel g)
    {
        var gems = new List<object>();
        void Add(GemViewModel? gem)
        {
            if (gem is null || gem.GemIndex <= 0) return;   // VM-side empty slots are not addressable
            gems.Add(new
            {
                gemIndex  = gem.GemIndex,
                name      = gem.CommittedName,
                level     = (int)gem.Level,
                quality   = (int)gem.Quality,
                enabled   = gem.IsEnabled,
                isSupport = gem.IsSupport,
            });
        }
        Add(g.ActiveGem);
        foreach (var s in g.SupportSlots) Add(s);
        return gems.ToArray();
    }

    private static object SkillsListing(SkillsTabViewModel v) => new
    {
        ok = true,
        isWeaponSwapActive = ItemDataHost()?.IsWeaponSwapActive() ?? false,
        groups = v.Groups.Select(g =>
        {
            // slot из движка (weapon set привязка через group.slot)
            var entry = ItemDataHost()?.GetSkillGroups().FirstOrDefault(e => e.Index == g.Index);
            return new
            {
                index            = g.Index,
                name             = g.ActiveGemName,
                isMain           = g.IsMain,
                enabled          = g.IsEnabled,
                includeInFullDps = g.IncludeInFullDps,
                isGranted        = g.IsGranted,
                sourceLabel      = g.SourceLabel,
                slot             = entry?.Slot,
                gems             = SkillGemList(g),
            };
        }).ToArray(),
    };

    private static SkillsTabViewModel? SkillsVmOrError(out object error)
    {
        error = null!;
        if (GetSkillsVm() is not { } v)
        {
            error = new { error = "SkillsTab not ready." };
            return null;
        }
        return v;
    }

    private static SkillGroupViewModel? ResolveGroup(int gi, SkillsTabViewModel v, out object error)
    {
        error = null!;
        var grp = v.Groups.FirstOrDefault(g => g.Index == gi);
        if (grp is null)
        {
            var avail = string.Join(", ", v.Groups.Select(g => $"[{g.Index}] {g.ActiveGemName}"));
            error = new { error = $"group {gi} not found. Available: {avail}" };
        }
        return grp;
    }

    private static object SkillsList()
    {
        if (SkillsVmOrError(out var err) is not { } v) return err;
        return SkillsListing(v);
    }

    /// <summary>DPS-разрез: разбивка Full DPS по скилам (только группы с
    /// includeInFullDPS) + итоги + метаданные групп. Чистый read: группу не
    /// видно — включи ей includeInFullDps (/skills/set-group) и перечитай.</summary>
    private static object SkillsDps()
    {
        var host = ItemDataHost();
        if (host is null) return new { error = "No build page open or engine not ready." };
        if (BuildPageOrError(out var err) is not { } bp) return err;
        var stats = bp.Build?.AllStats;
        if (stats is null) return new { error = "Build not loaded yet." };
        var mainIdx = host.GetMainSkillGroupIndex();
        return new
        {
            ok       = true,
            totalDps = stats.TryGetValue("TotalDPS", out var td) ? td : null,
            fullDps  = stats.TryGetValue("FullDPS", out var fd) ? fd : null,
            skills   = host.GetFullDpsBreakdown().Select(s => new
            {
                name  = s.Name,
                dps   = s.Dps,
                count = s.Count,
                total = s.Total,
            }).ToArray(),
            groups = host.GetSkillGroups().Select(g => new
            {
                index            = g.Index,
                name             = g.Name,
                isMain           = g.Index == mainIdx,
                includeInFullDps = g.IncludeInFullDPS,
            }).ToArray(),
        };
    }

    // Каталог гемов статичен в рамках версии данных — кэшируем на процесс.
    private static List<GemCatalogEntry>? _gemCatalogCache;

    private static object GemCatalog(string body)
    {
        var host = ItemDataHost();
        if (host is null) return new { error = "No build page open or engine not ready." };
        _gemCatalogCache ??= host.GetGemCatalog();
        var catalog = _gemCatalogCache;
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        string? Param(string key) =>
            req.TryGetValue(key, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        var query = Param("query");
        var type  = Param("type");
        var tag   = Param("tag");
        var weapon = Param("weapon");
        IEnumerable<GemCatalogEntry> items = catalog;
        if (!string.IsNullOrEmpty(query))
            items = items.Where(g =>
                g.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                g.TagString.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(type))
        {
            if (type.Equals("active", StringComparison.OrdinalIgnoreCase))
                items = items.Where(g => !g.Support);
            else if (type.Equals("support", StringComparison.OrdinalIgnoreCase))
                items = items.Where(g => g.Support);
            else
                return new { error = $"Unknown type '{type}'. Allowed: active, support." };
        }
        if (!string.IsNullOrEmpty(tag))
            items = items.Where(g => g.Tags.Any(t => t.Contains(tag, StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrEmpty(weapon))
            items = items.Where(g =>
                g.WeaponRequirements?.Contains(weapon, StringComparison.OrdinalIgnoreCase) == true);
        var matched = items.ToList();
        return new
        {
            ok        = true,
            count     = Math.Min(matched.Count, 400),
            truncated = matched.Count > 400,
            gems      = matched.Take(400).Select(g => new
            {
                name          = g.Name,
                baseTypeName  = g.BaseTypeName,
                support       = g.Support,
                tags          = g.Tags,
                tagString     = g.TagString,
                gemType       = g.GemType,
                reqStr        = g.ReqStr,
                reqDex        = g.ReqDex,
                reqInt        = g.ReqInt,
                naturalMaxLevel = g.NaturalMaxLevel,
                tier          = g.Tier,
                weaponRequirements = g.WeaponRequirements,
                manaCost      = g.ManaCost,
                description   = g.Description,
            }).ToArray(),
        };
    }

    private static object GemTooltip(string body)
    {
        var host = ItemDataHost();
        if (host is null) return new { error = "No build page open or engine not ready." };
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        if (!req.TryGetValue("gemName", out var gn) || gn.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(gn.GetString()))
            return new { error = "Missing 'gemName'." };
        var level = req.TryGetValue("level", out var lv) && lv.ValueKind == JsonValueKind.Number
            ? lv.GetInt32() : 20;
        var lines = host.GetGemTooltipByName(gn.GetString()!, level);
        if (lines.Count == 0)
            return new { error = $"Unknown gem '{gn.GetString()}' (check spelling via /skills/gem-catalog)." };
        return new
        {
            ok    = true,
            gem   = gn.GetString(),
            lines = lines.Select(l => new { text = l.Text, kind = l.Kind }).ToArray(),
        };
    }

    private static object SkillsAddGroup(string body)
    {
        if (SkillsVmOrError(out var err) is not { } v) return err;
        var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        req.TryGetValue("gemName", out var gemName);
        var (nameErr, resolved) = ResolveGemName(gemName);
        if (nameErr is not null) return nameErr;
        v.AddGroupFromPicker(resolved ?? "");
        return SkillsListing(v);
    }

    private static object SkillsRemoveGroup(string body)
    {
        if (SkillsVmOrError(out var err) is not { } v) return err;
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        if (!req.TryGetValue("group", out var ge) || ge.ValueKind != JsonValueKind.Number)
            return new { error = "Missing 'group' (1-based engine index)." };
        if (ResolveGroup(ge.GetInt32(), v, out var gerr) is not { } grp) return gerr;
        if (grp.IsGranted)
            return new { error = $"group {grp.Index} is granted by '{grp.SourceLabel}' and cannot be removed" };
        v.RemoveGroup(grp.Index);
        return SkillsListing(v);
    }

    /// <summary>Резолв имени гема по каталогу: (null, resolved) при успехе,
    /// (error, null) при промахе — с подсказками. Общий для add-group/add-gem/
    /// set-gem: иначе Lua молча создаст битый гем ("Unrecognised gem name"
    /// до IPC не доходит).</summary>
    private static (object? Error, string? Resolved) ResolveGemName(string? gemName)
    {
        var host = ItemDataHost();
        if (host is null) return (null, gemName);   // нет движка — пусть VM отработает как раньше
        var (active, support) = host.GetAvailableGemNames();
        var all = active.Concat(support).ToList();
        var resolved = GemNameMatcher.FindExact(gemName ?? "", all);
        return resolved is null
            ? (new
               {
                   error = $"Unknown gem name '{gemName}'. Resolve names via /skills/gem-catalog.",
                   suggestions = GemNameMatcher.Suggest(gemName ?? "", all),
               }, null)
            : (null, resolved);
    }

    private static object SkillsAddGem(string body)
    {
        if (SkillsVmOrError(out var err) is not { } v) return err;
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        if (!req.TryGetValue("group", out var ge) || ge.ValueKind != JsonValueKind.Number)
            return new { error = "Missing 'group'." };
        if (ResolveGroup(ge.GetInt32(), v, out var gerr) is not { } grp) return gerr;
        if (!req.TryGetValue("gemName", out var ne) || ne.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(ne.GetString()))
            return new { error = "Missing 'gemName'." };

        var (nameErr, resolved) = ResolveGemName(ne.GetString());
        if (nameErr is not null) return nameErr;

        v.AddGem(grp.Index);
        var fresh = v.Groups.First(g => g.Index == grp.Index);
        int newIdx = new[] { fresh.ActiveGem }.Concat(fresh.SupportSlots)
            .Where(x => x is { GemIndex: > 0 })
            .Max(x => x!.GemIndex);
        v.CommitGemName(grp.Index, newIdx, resolved!);
        return SkillsListing(v);
    }

    private static object SkillsRemoveGem(string body)
    {
        if (SkillsVmOrError(out var err) is not { } v) return err;
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        if (!req.TryGetValue("group", out var ge) || ge.ValueKind != JsonValueKind.Number)
            return new { error = "Missing 'group'." };
        if (ResolveGroup(ge.GetInt32(), v, out var gerr) is not { } grp) return gerr;
        if (!req.TryGetValue("gem", out var gme) || gme.ValueKind != JsonValueKind.Number)
            return new { error = "Missing 'gem' (1-based gem index)." };

        v.RemoveGem(grp.Index, gme.GetInt32());
        return SkillsListing(v);
    }

    private static object SkillsSetGem(string body)
    {
        if (SkillsVmOrError(out var err) is not { } v) return err;
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        if (!req.TryGetValue("group", out var ge) || ge.ValueKind != JsonValueKind.Number)
            return new { error = "Missing 'group'." };
        if (ResolveGroup(ge.GetInt32(), v, out var gerr) is not { } grp) return gerr;
        if (!req.TryGetValue("gem", out var gme) || gme.ValueKind != JsonValueKind.Number)
            return new { error = "Missing 'gem' (1-based gem index)." };
        int gem = gme.GetInt32();

        if (req.TryGetValue("name", out var ne) && ne.ValueKind == JsonValueKind.String)
        {
            var (nameErr, resolved) = ResolveGemName(ne.GetString());
            if (nameErr is not null) return nameErr;
            v.CommitGemName(grp.Index, gem, resolved ?? "");
        }
        if (req.TryGetValue("level", out var le) && le.ValueKind == JsonValueKind.Number)
            v.SyncGemLevel(grp.Index, gem, le.GetInt32());
        if (req.TryGetValue("quality", out var qe) && qe.ValueKind == JsonValueKind.Number)
            v.SyncGemQuality(grp.Index, gem, qe.GetInt32());
        if (req.TryGetValue("enabled", out var ee) &&
            (ee.ValueKind == JsonValueKind.True || ee.ValueKind == JsonValueKind.False))
            v.SyncGemEnabled(grp.Index, gem, ee.GetBoolean());

        // Sync* calls push to the engine but don't rebuild gem VMs (the UI
        // slider does that first); the IPC path has no UI, so re-read.
        v.Refresh();
        return SkillsListing(v);
    }

    private static object SkillsSetGroup(string body)
    {
        if (SkillsVmOrError(out var err) is not { } v) return err;
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        if (!req.TryGetValue("group", out var ge) || ge.ValueKind != JsonValueKind.Number)
            return new { error = "Missing 'group'." };
        if (ResolveGroup(ge.GetInt32(), v, out var gerr) is not { } grp) return gerr;

        if (req.TryGetValue("enabled", out var ee) &&
            (ee.ValueKind == JsonValueKind.True || ee.ValueKind == JsonValueKind.False))
            v.SyncGroupEnabled(grp.Index, ee.GetBoolean());
        if (req.TryGetValue("includeInFullDps", out var fe) &&
            (fe.ValueKind == JsonValueKind.True || fe.ValueKind == JsonValueKind.False))
            v.SyncGroupIncludeInFullDps(grp.Index, fe.GetBoolean());
        if (req.TryGetValue("label", out var lb) && lb.ValueKind == JsonValueKind.String)
            v.SetGroupLabel(grp.Index, lb.GetString() ?? "");
        // Привязка группы к weapon set: 1/2 → slot "Weapon 1"/"Weapon 1 Swap",
        // 0 — снять привязку (CalcSetup считает группу по slot.weaponSet).
        if (req.TryGetValue("weaponSet", out var wse))
        {
            var ws = wse.ValueKind == JsonValueKind.Number ? wse.GetInt32()
                   : wse.ValueKind == JsonValueKind.True ? 1 : 0;
            if (ws is < 0 or > 2)
                return new { error = "weaponSet is 0 (none), 1 or 2 (weapon set)." };
            var host = ItemDataHost();
            host?.SetGroupSlot(grp.Index, ws switch { 1 => "Weapon 1", 2 => "Weapon 1 Swap", _ => null });
        }

        v.Refresh();
        return SkillsListing(v);
    }

    private static object SkillsSetMain(string body)
    {
        if (SkillsVmOrError(out var err) is not { } v) return err;
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        if (!req.TryGetValue("group", out var ge) || ge.ValueKind != JsonValueKind.Number)
            return new { error = "Missing 'group'." };
        if (ResolveGroup(ge.GetInt32(), v, out var gerr) is not { } grp) return gerr;

        v.SetMainGroup(grp.Index);
        return SkillsListing(v);
    }

    // ── Support-gem picker (selector) test hooks ──────────────────────────────
    // Address a support slot by group Index + 0-based slot ordinal, so the picker's
    // attribute tabs/filter/selection can be driven and inspected without the popup.

    private static GemViewModel? ResolvePickerSlot(IDictionary<string, string> req)
    {
        if (GetSkillsVm() is not { } v) return null;
        int gi = req.TryGetValue("group", out var gs) && int.TryParse(gs, out var g) ? g : -1;
        int so = req.TryGetValue("slot",  out var ss) && int.TryParse(ss, out var s) ? s : -1;
        var grp = v.Groups.FirstOrDefault(x => x.Index == gi);
        return grp?.SupportSlots.ElementAtOrDefault(so);
    }

    private static object PickerStateOf(GemViewModel slot) => new
    {
        ok          = true,
        isTrigger   = slot.IsTriggerSlot,
        supportMode = slot.SupportMode,
        skillsMode  = slot.SkillsMode,
        tab         = slot.Tab.ToString(),
        committed   = slot.CommittedName,
        tabs = new[]
        {
            new { key = "All", have = slot.TabAllHave, max = slot.TabAllMax },
            new { key = "Str", have = slot.TabStrHave, max = slot.TabStrMax },
            new { key = "Dex", have = slot.TabDexHave, max = slot.TabDexMax },
            new { key = "Int", have = slot.TabIntHave, max = slot.TabIntMax },
            new { key = "Special", have = slot.TabSpecialHave, max = 0 },
        },
        filtered = slot.FilteredGemNames.Select(g => new
        {
            name    = g.Name,
            display = g.DisplayName,
            color   = g.Color,
            attr    = GemAttrUtil.FromColor(g.Color).ToString(),
        }).ToArray(),
    };

    private static object SupportPickerState(string body)
    {
        var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        if (GetSkillsVm() is null) return new { error = "SkillsTab not ready." };
        return ResolvePickerSlot(req) is { } slot ? PickerStateOf(slot)
                                                   : new { error = "Slot not found." };
    }

    private static object SupportPickerSetTab(string body)
    {
        var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        if (ResolvePickerSlot(req) is not { } slot) return new { error = "Slot not found." };
        if (req.TryGetValue("tab", out var t) && Enum.TryParse<GemTab>(t, true, out var tab))
            slot.Tab = tab;
        return PickerStateOf(slot);
    }

    private static object SupportPickerSetMode(string body)
    {
        var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        if (ResolvePickerSlot(req) is not { } slot) return new { error = "Slot not found." };
        if (req.TryGetValue("support", out var s))
            slot.SupportMode = s is "true" or "1";
        return PickerStateOf(slot);
    }

    private static object SupportPickerSelect(string body)
    {
        var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        if (ResolvePickerSlot(req) is not { } slot) return new { error = "Slot not found." };
        if (!req.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
            return new { error = "Missing 'name'." };
        slot.SearchText = name;
        slot.CommitName();
        return new { ok = true, committed = slot.CommittedName };
    }

    private static object[] SerializeGemTooltip(System.Collections.Generic.IReadOnlyList<GemTooltipEntry>? entries)
    {
        if (entries is null) return Array.Empty<object>();
        return entries.Select(e => new
        {
            isSep = e.IsSep,
            text  = e.IsSep ? "" : RenderedTextOf(e),
        }).ToArray();
    }

    // The view renders the per-entry Segments (via InlinesHelper); concatenating
    // their text reproduces exactly what reaches the screen. Falls back to the
    // whole-line Text when no segments were built.
    private static string RenderedTextOf(GemTooltipEntry e)
        => e.Segments is { Count: > 0 } segs
            ? string.Concat(segs.Select(s => s.Text))
            : e.Text;

    // ── Generic rendered-text dump ─────────────────────────────────────────
    // Walks the live visual tree(s) and returns the actual text of every visible
    // TextBlock / TextBox — the real strings going to render (translated where the
    // app translates them), for any on-screen field. Note: tree-canvas node labels
    // are immediate-mode drawn (no TextBlock) and are NOT included here — use
    // /tree/hover-node or /skills/tooltips for those.
    private static object DumpUiText(string body)
    {
        var win = GetMainWindow();
        if (win is null) return new { error = "No main window." };

        var items = new List<object>();
        CollectText(win, items, "MainWindow");

        // Any other open windows (modal dialogs: confirm / choose-attribute, etc.).
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desk)
            foreach (var w in desk.Windows)
                if (!ReferenceEquals(w, win))
                    CollectText(w, items, w.Title ?? w.GetType().Name);

        return new { ok = true, count = items.Count, items = items.ToArray() };
    }

    private static void CollectText(Visual root, List<object> items, string scope)
    {
        foreach (var v in root.GetVisualDescendants())
        {
            if (v is not Control c || !c.IsEffectivelyVisible) continue;

            string? text = v switch
            {
                TextBlock tb => InlineOrText(tb),
                TextBox  txt => txt.Text,
                _            => null,
            };
            if (string.IsNullOrWhiteSpace(text)) continue;

            items.Add(new { scope, type = v.GetType().Name, name = c.Name, text });
        }
    }

    private static string? InlineOrText(TextBlock tb)
    {
        if (tb.Inlines is { Count: > 0 } inlines)
        {
            var sb = new StringBuilder();
            AppendInlines(sb, inlines);
            if (sb.Length > 0) return sb.ToString();
        }
        return tb.Text;
    }

    private static void AppendInlines(StringBuilder sb, InlineCollection inlines)
    {
        foreach (var inl in inlines)
        {
            switch (inl)
            {
                case Run r:        sb.Append(r.Text); break;
                case LineBreak:    sb.Append('\n');   break;
                case Span sp:      AppendInlines(sb, sp.Inlines); break;
            }
        }
    }

    /// <summary>Reports the equip-compatible (drag-highlight) target slots PoB would
    /// accept for an item — honouring keystone/ascendancy flags such as Instruments of
    /// Power (Focus into Weapon 2 with a Staff) and Giant's Blood. Body: optional
    /// {"id": poolItemId} or {"slot": equippedSlotName}; with neither, returns the map
    /// for every pool item.</summary>
    private static object TattooState()
    {
        if (GetItemsVm() is not { } v) return new { error = "ItemsTab not ready." };
        var t = v.Tattoos;
        return new
        {
            available = t.Available,
            sockets = t.Sockets.Select(s => new
            {
                index    = s.Index,
                slotType = s.SlotType,
                label    = s.SlotTypeLabel,
                rune     = s.SelectedRune?.Name ?? "",
                options  = s.AvailableRunes.Count,
            }).ToArray(),
        };
    }

    private static object SetTattoo(string body)
    {
        if (GetItemsVm() is not { } v) return new { error = "ItemsTab not ready." };
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        if (!req.TryGetValue("index", out var ie) || ie.ValueKind != JsonValueKind.Number)
            return new { error = "Missing 'index' (1-based number)." };
        var rune = req.TryGetValue("rune", out var re) && re.ValueKind == JsonValueKind.String
            ? re.GetString() ?? "" : "";
        if (!v.Tattoos.Available) return new { error = "Tattoos not available (Runic Meridians not allocated)." };
        v.Tattoos.SetSocketRune(ie.GetInt32(), rune);
        return new { ok = true };
    }

    private static object PhylacteryStateInfo()
    {
        if (GetItemsVm() is not { } v) return new { error = "ItemsTab not ready." };
        var p = v.Phylactery;
        return new { available = p.Available, jewel = p.JewelName };
    }

    private static object CompatibleSlots(string body)
    {
        if (GetItemsVm() is not { } v) return new { error = "ItemsTab not ready." };
        var req = string.IsNullOrWhiteSpace(body)
            ? new Dictionary<string, JsonElement>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();

        if (req.TryGetValue("id", out var ie) && ie.ValueKind == JsonValueKind.Number)
        {
            int id = ie.GetInt32();
            var entry = v.ItemPool.FirstOrDefault(e => e.ItemId == id);
            if (entry is null) return new { error = $"Pool item id={id} not found." };
            return new { ok = true, id, name = entry.Name, equippedSlot = entry.EquippedSlot,
                         slots = v.CompatibleSlotsForPool(id).OrderBy(s => s).ToArray() };
        }
        if (req.TryGetValue("slot", out var se) && se.ValueKind == JsonValueKind.String)
        {
            var slot = se.GetString() ?? "";
            return new { ok = true, slot,
                         slots = v.CompatibleSlotsForSlot(slot).OrderBy(s => s).ToArray() };
        }

        var all = v.ItemPool.Select(p => new
        {
            id = p.ItemId, name = p.Name, equippedSlot = p.EquippedSlot,
            slots = v.CompatibleSlotsForPool(p.ItemId).OrderBy(s => s).ToArray(),
        }).ToArray();
        return new { ok = true, pool = all };
    }

    private static object EditCurrentItem()
    {
        if (GetItemsVm() is not { } v) return new { error = "ItemsTab not ready." };
        if (!v.EditCurrentItemCommand.CanExecute(null))
            return new { error = "EditCurrentItemCommand not executable (no item selected?)." };
        v.EditCurrentItemCommand.Execute(null);
        return new { ok = true, isEditMode = v.IsEditMode };
    }

    private static object CancelEdit()
    {
        if (GetItemsVm() is not { } v) return new { error = "ItemsTab not ready." };
        if (!v.CancelEditCommand.CanExecute(null))
            return new { error = "CancelEditCommand not executable (no editor open?)." };
        v.CancelEditCommand.Execute(null);
        return new { ok = true, isTooltipMode = v.IsTooltipMode };
    }

    // ── Editor mutation handlers (used by MCP for crafting workflows) ────

    private static ItemEditorViewModel? GetEditor()
        => GetItemsVm()?.ItemEditor;

    private static object EditorSave()
    {
        if (GetItemsVm() is not { } v) return new { error = "ItemsTab not ready." };
        if (v.ItemEditor is not { } ed) return new { error = "No editor open." };
        ed.SaveCommand.Execute(null);
        if (!string.IsNullOrEmpty(ed.SaveError)) return new { error = ed.SaveError };
        return new { ok = true };
    }

    private static object EditorCreateNew(string body)
    {
        if (GetItemsVm() is not { } v) return new { error = "ItemsTab not ready." };
        v.OpenNewItemEditorCommand.Execute(null);
        if (v.ItemEditor is null) return new { error = "Editor failed to open." };
        // Optional initial selection
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        if (req.TryGetValue("rarity", out var rJson) && rJson.ValueKind == JsonValueKind.String)
            ApplyRarity(v.ItemEditor, rJson.GetString());
        if (req.TryGetValue("category", out var catJson) && catJson.ValueKind == JsonValueKind.String)
            v.ItemEditor.SelectedCategory = catJson.GetString() ?? "";
        if (req.TryGetValue("base", out var bJson) && bJson.ValueKind == JsonValueKind.String)
        {
            var match = v.ItemEditor.Bases.FirstOrDefault(b =>
                b.Name.Contains(bJson.GetString() ?? "", StringComparison.OrdinalIgnoreCase));
            if (match is not null) v.ItemEditor.SelectedBase = match;
        }
        if (req.TryGetValue("unique", out var uJson) && uJson.ValueKind == JsonValueKind.String)
        {
            ApplyRarity(v.ItemEditor, "Unique");
            var match = v.ItemEditor.FilteredUniques.FirstOrDefault(u =>
                u.Name.Contains(uJson.GetString() ?? "", StringComparison.OrdinalIgnoreCase));
            if (match is not null) v.ItemEditor.SelectedUnique = match;
        }
        return new { ok = true };
    }

    private static void ApplyRarity(ItemEditorViewModel ed, string? rarity)
    {
        if (string.IsNullOrEmpty(rarity)) return;
        ed.Rarity = rarity.ToLowerInvariant() switch
        {
            "normal" => ItemEditorViewModel.ItemRarity.Normal,
            "magic"  => ItemEditorViewModel.ItemRarity.Magic,
            "rare"   => ItemEditorViewModel.ItemRarity.Rare,
            "unique" => ItemEditorViewModel.ItemRarity.Unique,
            _        => ed.Rarity,
        };
    }

    private static object EditorSetQuality(string body)
    {
        if (GetEditor() is not { } ed) return new { error = "No editor open." };
        var req = JsonSerializer.Deserialize<Dictionary<string, int>>(body) ?? new();
        if (!req.TryGetValue("quality", out var q)) return new { error = "Missing 'quality'." };
        ed.Quality = Math.Clamp(q, 0, 30);
        return new { ok = true, quality = ed.Quality };
    }

    /// <summary>Уровень предмета (item level) в редакторе: определяет, какие тиры
    /// афиксов доступны (CanAddAffix гейтит e.Level > ItemLevel). Кламп 1..100.</summary>
    private static object EditorSetItemLevel(string body)
    {
        if (GetEditor() is not { } ed) return new { error = "No editor open." };
        var req = JsonSerializer.Deserialize<Dictionary<string, int>>(body) ?? new();
        if (!req.TryGetValue("level", out var l)) return new { error = "Missing 'level'." };
        ed.ItemLevel = Math.Clamp(l, 1, 100);
        return new { ok = true, itemLevel = ed.ItemLevel };
    }

    private static object EditorSetCorrupted(string body)
    {
        if (GetEditor() is not { } ed) return new { error = "No editor open." };
        var req = JsonSerializer.Deserialize<Dictionary<string, bool>>(body) ?? new();
        if (!req.TryGetValue("value", out var v2)) return new { error = "Missing 'value'." };
        ed.IsCorrupted = v2;
        return new { ok = true, isCorrupted = ed.IsCorrupted };
    }

    private static object EditorSetRarity(string body)
    {
        if (GetEditor() is not { } ed) return new { error = "No editor open." };
        var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        if (!req.TryGetValue("rarity", out var r) || string.IsNullOrEmpty(r))
            return new { error = "Missing 'rarity'." };
        ApplyRarity(ed, r);
        return new { ok = true, rarity = ed.Rarity.ToString() };
    }

    private static object EditorSetBase(string body)
    {
        if (GetEditor() is not { } ed) return new { error = "No editor open." };
        var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        if (!req.TryGetValue("name", out var name) || string.IsNullOrEmpty(name))
            return new { error = "Missing 'name'." };
        var match = ed.Bases.FirstOrDefault(b =>
            b.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? ed.Bases.FirstOrDefault(b => b.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (match is null) return new { error = $"Base '{name}' not found in current category." };
        ed.SelectedBase = match;
        return new { ok = true, baseName = match.Name };
    }

    private static object EditorSetUnique(string body)
    {
        if (GetEditor() is not { } ed) return new { error = "No editor open." };
        var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        if (!req.TryGetValue("name", out var name) || string.IsNullOrEmpty(name))
            return new { error = "Missing 'name'." };
        ApplyRarity(ed, "Unique");
        var match = ed.FilteredUniques.FirstOrDefault(u =>
            u.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? ed.FilteredUniques.FirstOrDefault(u => u.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (match is null) return new { error = $"Unique '{name}' not found." };
        ed.SelectedUnique = match;
        return new { ok = true, uniqueName = match.Name };
    }

    private static object EditorSetBaseStat(string body)
    {
        if (GetEditor() is not { } ed) return new { error = "No editor open." };
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        if (!req.TryGetValue("stat", out var sJson) || sJson.ValueKind != JsonValueKind.String)
            return new { error = "Missing 'stat'." };
        if (!req.TryGetValue("value", out var vJson) || !vJson.TryGetInt32(out int val))
            return new { error = "Missing 'value' (int)." };
        var stat = sJson.GetString()?.ToLowerInvariant() ?? "";
        switch (stat)
        {
            case "armour":       ed.BaseArmour       = val; break;
            case "evasion":      ed.BaseEvasion      = val; break;
            case "energyshield":
            case "es":           ed.BaseEnergyShield = val; break;
            case "ward":         ed.BaseWard         = val; break;
            case "spirit":       ed.BaseSpirit       = val; break;
            case "charmslots":   ed.BaseCharmSlots   = val; break;
            default: return new { error = $"Unknown stat '{stat}'." };
        }
        return new { ok = true, stat, value = val };
    }

    private static object EditorSetModSlider(string body)
    {
        if (GetEditor() is not { } ed) return new { error = "No editor open." };
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        if (!req.TryGetValue("index", out var iJson) || !iJson.TryGetInt32(out int idx))
            return new { error = "Missing 'index' (int)." };
        if (!req.TryGetValue("value", out var vJson) || !vJson.TryGetDouble(out double val))
            return new { error = "Missing 'value' (number)." };
        if (idx < 0 || idx >= ed.ExplicitMods.Count) return new { error = "Index out of range." };
        var mod = ed.ExplicitMods[idx];
        if (!mod.HasSlider) return new { error = "Mod has no slider." };
        mod.SliderValue = Math.Clamp(val, mod.SliderMin, mod.SliderMax);
        return new { ok = true, index = idx, sliderValue = mod.SliderValue, text = mod.Text };
    }

    private static object EditorAddAffix(string body)
    {
        if (GetEditor() is not { } ed) return new { error = "No editor open." };
        var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        if (!req.TryGetValue("query", out var q) || string.IsNullOrEmpty(q))
            return new { error = "Missing 'query'." };
        // Try affixName first, then stat text contains.
        var match = ed.FilteredAffixes.FirstOrDefault(a =>
            a.Entry.AffixName.Contains(q, StringComparison.OrdinalIgnoreCase))
            ?? ed.FilteredAffixes.FirstOrDefault(a =>
            a.Entry.StatText.Contains(q, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            // Афикс мог быть отфильтрован гейтом ilvl — дадим внятную причину.
            var hidden = ed.AllAffixEntries.FirstOrDefault(a =>
                    a.AffixName.Contains(q, StringComparison.OrdinalIgnoreCase))
                ?? ed.AllAffixEntries.FirstOrDefault(a =>
                    a.StatText.Contains(q, StringComparison.OrdinalIgnoreCase));
            if (hidden is not null && hidden.Level > ed.ItemLevel)
                return new
                {
                    error = $"Affix '{hidden.AffixName}' (tier level {hidden.Level}) requires " +
                            $"item level ≥ {hidden.Level} (current {ed.ItemLevel}) — " +
                            "call visual_editor_set_item_level.",
                };
            return new { error = $"No matching affix for '{q}'." };
        }
        if (!ed.CanAddAffix(match.Entry))
        {
            if (match.Entry.Level > ed.ItemLevel)
                return new
                {
                    error = $"Affix '{match.Entry.AffixName}' (tier level {match.Entry.Level}) requires " +
                            $"item level ≥ {match.Entry.Level} (current {ed.ItemLevel}) — " +
                            "call visual_editor_set_item_level.",
                };
            return new { error = "Cannot add this affix (capped/duplicated)." };
        }
        ed.AddAffix(match.Entry);
        return new { ok = true, added = match.Entry.AffixName, statText = match.Entry.StatText };
    }

    private static object EditorRemoveMod(string body)
    {
        if (GetEditor() is not { } ed) return new { error = "No editor open." };
        var req = JsonSerializer.Deserialize<Dictionary<string, int>>(body) ?? new();
        if (!req.TryGetValue("index", out var idx) || idx < 0 || idx >= ed.ExplicitMods.Count)
            return new { error = "Invalid 'index'." };
        var mod = ed.ExplicitMods[idx];
        if (!mod.IsRemovable) return new { error = "Mod is not user-removable." };
        ed.RemoveExplicitMod(mod);
        return new { ok = true };
    }

    private static object EditorAddRuneSocket()
    {
        if (GetEditor() is not { } ed) return new { error = "No editor open." };
        if (!ed.CanAddRune) return new { error = "Cannot add another rune socket (cap reached / corrupted)." };
        ed.AddRuneSocketCommand.Execute(null);
        return new { ok = true, sockets = ed.RuneSockets.Count };
    }

    private static object EditorSetRune(string body)
    {
        if (GetEditor() is not { } ed) return new { error = "No editor open." };
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        if (!req.TryGetValue("socket", out var sJson) || !sJson.TryGetInt32(out int sock))
            return new { error = "Missing 'socket' (1-based int)." };
        if (sock < 1 || sock > ed.RuneSockets.Count) return new { error = "Socket index out of range." };
        var slot = ed.RuneSockets[sock - 1];
        if (req.TryGetValue("rune", out var rJson) && rJson.ValueKind == JsonValueKind.String)
        {
            var name = rJson.GetString() ?? "";
            if (string.IsNullOrEmpty(name) || string.Equals(name, "None", StringComparison.OrdinalIgnoreCase))
            { slot.SelectedRune = null; return new { ok = true, cleared = true }; }
            var rune = ed.CompatibleRunes.FirstOrDefault(r =>
                r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? ed.CompatibleRunes.FirstOrDefault(r => r.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (rune is null) return new { error = $"Rune '{name}' not compatible with this base." };
            slot.SelectedRune = rune;
            return new { ok = true, runeName = rune.Name, modSummary = slot.ModSummary };
        }
        slot.SelectedRune = null;
        return new { ok = true, cleared = true };
    }

    private static object EditorRemoveRuneSocket(string body)
    {
        if (GetEditor() is not { } ed) return new { error = "No editor open." };
        var req = JsonSerializer.Deserialize<Dictionary<string, int>>(body) ?? new();
        if (!req.TryGetValue("socket", out var sock) || sock < 1 || sock > ed.RuneSockets.Count)
            return new { error = "Invalid 'socket' (1-based)." };
        var slot = ed.RuneSockets[sock - 1];
        ed.RemoveRuneSocketCommand.Execute(slot);
        return new { ok = true, sockets = ed.RuneSockets.Count };
    }

    // ── Tree tab handlers ─────────────────────────────────────────────────

    private static TreeTabViewModel? GetTreeVm()
        => (GetMainVm()?.CurrentPage as BuildPageViewModel)?.TreeTab;

    private static object TreeState()
    {
        if (GetTreeVm() is not { } t) return new { error = "TreeTab not ready." };
        return new
        {
            selectedClass      = t.SelectedClass?.Name,
            selectedAscendancy = t.SelectedAscend?.Name,
            classes = t.Classes.Select(c => new
            {
                id   = c.Id,
                name = c.Name,
                ascendancies = c.Ascendancies.Select(a => new { id = a.Id, name = a.Name }).ToArray(),
            }).ToArray(),
            availableAscendancies = t.AvailableAscendancies.Select(a => new { id = a.Id, name = a.Name }).ToArray(),
            searchText      = t.SearchText,
            nodeCount       = t.NodeCount,
            allocatedCount  = t.AllocatedCount,
            isToggleBusy    = t.IsToggleBusy,
            heatmapEnabled     = t.HeatmapEnabled,
            powerStat          = t.SelectedPowerStat?.Option.StatKey,
            isPowerStale       = t.IsPowerStale,
            powerBuildProgress = t.PowerBuildProgress,
            workersReady       = TreePowerService.Current?.Ready ?? 0,
            workersTotal       = TreePowerService.Current?.Size ?? 0,
            powerSortIndex     = t.PowerSortIndex,
            powerFilter        = t.PowerFilter,
        };
    }

    private static object TreeSetSearch(string body)
    {
        if (GetTreeVm() is not { } t) return new { error = "TreeTab not ready." };
        var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        t.SearchText = req.TryGetValue("text", out var text) ? text ?? "" : "";
        return new { ok = true, searchText = t.SearchText };
    }

    private static async Task<object> TreeToggleNode(string body)
    {
        if (GetTreeVm() is not { } t) return new { error = "TreeTab not ready." };
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        int? id = req.TryGetValue("nodeId", out var n) && n.ValueKind == JsonValueKind.Number
            ? n.GetInt32() : null;
        var res = await t.ToggleNodeTimed(id);
        return res is { } r
            ? new { ok = true, id = r.Id, allocated = r.Allocated, ms = Math.Round(r.Ms, 2) }
            : new { error = "No toggle candidate." };
    }

    private static object TreeHoverNode(string body)
    {
        if (GetTreeVm() is not { } t) return new { error = "TreeTab not ready." };
        if (t.HoverCanvasNode is null) return new { error = "Canvas view not bound." };
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        int? id = req.TryGetValue("nodeId", out var n) && n.ValueKind == JsonValueKind.Number
            ? n.GetInt32() : null;
        var res = t.HoverCanvasNode(id);
        return res is { } r
            ? new { ok = true, id = r.Id, name = r.Name, pathLen = r.PathLen }
            : new { error = "No hover candidate (no allocated nodes?)." };
    }

    // Returns the REAL translated text of a passive-node tooltip — the same
    // strings TreeCanvas draws (node name via TPassiveName, each stat line via
    // TPassiveStat). Canvas labels are immediate-mode drawn so /ui/text can't
    // see them; this is the faithful-text route for tree-node translation audits.
    // Body: {"nodeId": N} for one node, or omit to dump every allocated node.
    // Each line carries raw + translated so callers can spot still-English text.
    private static object TreeNodeText(string body)
    {
        if (GetTreeVm() is not { } t) return new { error = "TreeTab not ready." };
        var req = string.IsNullOrWhiteSpace(body)
            ? new Dictionary<string, JsonElement>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();

        IEnumerable<int> ids = req.TryGetValue("nodeId", out var n) && n.ValueKind == JsonValueKind.Number
            ? new[] { n.GetInt32() }
            : t.AllocatedIds.OrderBy(x => x);

        static bool LooksUntranslated(string raw, string tr)
            => string.Equals(raw, tr, StringComparison.Ordinal)
               && System.Text.RegularExpressions.Regex.IsMatch(tr, "[A-Za-z]");

        var nodes = new List<object>();
        int modTotal = 0, modUntr = 0, nameUntr = 0;
        foreach (var id in ids)
        {
            var info = t.GetNodeHoverInfo(id);
            if (info is null) continue;

            var nameTr = GameTranslationService.TPassiveName(info.Name);
            bool nameBad = LooksUntranslated(info.Name, nameTr);
            if (nameBad) nameUntr++;

            var mods = (info.Mods ?? Array.Empty<string>()).Select(raw =>
            {
                var tr = GameTranslationService.TPassiveStat(raw);
                bool bad = LooksUntranslated(raw, tr);
                modTotal++;
                if (bad) modUntr++;
                return new { raw, translated = tr, untranslated = bad };
            }).ToArray();

            nodes.Add(new
            {
                id = info.NodeId,
                nameRaw          = info.Name,
                name             = nameTr,
                nameUntranslated = nameBad,
                type             = info.Type,
                ascendancy       = info.AscendancyName,
                allocated        = info.IsAllocated,
                mods,
            });
        }

        return new
        {
            ok = true,
            count = nodes.Count,
            namesUntranslated = nameUntr,
            modsTotal = modTotal,
            modsUntranslated = modUntr,
            nodes = nodes.ToArray(),
        };
    }

    // ── Tree building (agent-driven allocation) ────────────────────────────
    // Backed by TreeTabViewModel.AllocatePathTo / DeallocateNode — the same
    // code the VM tests in PBLEngine.Tests cover; these handlers are thin.

    private static object PointsPayload(PBLEngine.PointUsage? u) => u is null
        ? (object)null!
        : new
        {
            passivesUsed   = u.PassivesUsed,
            passivesMax    = u.PassivesMax,
            ascendancyUsed = u.AscendancyUsed,
            ascendancyMax  = u.AscendancyMax,
            weaponSet1Used = u.WeaponSet1Used,
            weaponSet2Used = u.WeaponSet2Used,
            weaponSetMax   = u.WeaponSetMax,
        };

    private static bool OverBudget(PBLEngine.PointUsage? u) =>
        u is { } v && (v.PassivesUsed > v.PassivesMax || v.AscendancyUsed > v.AscendancyMax);

    private static object TreeAllocated()
    {
        if (GetTreeVm() is not { } t) return new { error = "TreeTab not ready." };
        var alloc = t.AllocatedIds.OrderBy(x => x).ToArray();
        var byId  = t.Nodes.ToDictionary(n => n.Id);
        // Принадлежность узла к weapon set: 0 — общий, 1/2 — weapon set.
        var modes = ItemDataHost()?.GetAllocatedNodeModes() ?? new Dictionary<int, int>();
        var nodes = alloc.Where(byId.ContainsKey).Select(id => new
        {
            id,
            name       = byId[id].Name,
            type       = byId[id].Type,
            ascendancy = byId[id].AscendancyName,
            allocMode  = modes.TryGetValue(id, out var m) ? m : 0,
        }).ToArray();
        var usage = t.PointUsage;
        return new
        {
            ok         = true,
            ids        = alloc,
            nodes,
            points     = PointsPayload(usage),
            overBudget = OverBudget(usage),
        };
    }

    private static async Task<object> TreeAllocatePath(string body)
    {
        if (GetTreeVm() is not { } t) return new { error = "TreeTab not ready." };
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        if (!req.TryGetValue("nodeId", out var n) || n.ValueKind != JsonValueKind.Number)
            return new { error = "Missing 'nodeId'." };
        string? attribute = req.TryGetValue("attribute", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString()
            : null;
        int weaponSet = req.TryGetValue("weaponSet", out var w) && w.ValueKind == JsonValueKind.Number
            ? w.GetInt32() : 0;

        var r = await t.AllocatePathTo(n.GetInt32(), attribute, weaponSet);
        await t.WaitForEngineSettledAsync();   // idempotent; AllocatePathTo settles internally
        var usage = t.PointUsage;
        return new
        {
            ok             = r.Ok,
            error          = r.Error,
            path           = r.Path,
            failed         = r.Failed,
            attributeNodes = r.AttributeNodes,
            pointsSpent    = r.PointsSpent,
            points         = PointsPayload(usage),
            overBudget     = OverBudget(usage),
        };
    }

    private static async Task<object> TreeDeallocateNode(string body)
    {
        if (GetTreeVm() is not { } t) return new { error = "TreeTab not ready." };
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        if (!req.TryGetValue("nodeId", out var n) || n.ValueKind != JsonValueKind.Number)
            return new { error = "Missing 'nodeId'." };
        int weaponSet = req.TryGetValue("weaponSet", out var w) && w.ValueKind == JsonValueKind.Number
            ? w.GetInt32() : 0;

        var (ok, error, dependents) = await t.DeallocateNode(n.GetInt32(), weaponSet);
        await t.WaitForEngineSettledAsync();
        return new { ok, error, dependents };
    }

    private static object TreeNodes(string body)
    {
        if (GetTreeVm() is not { } t) return new { error = "TreeTab not ready." };
        var req = string.IsNullOrWhiteSpace(body)
            ? new Dictionary<string, JsonElement>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        string? query = req.TryGetValue("query", out var q) && q.ValueKind == JsonValueKind.String
            ? q.GetString() : null;
        int? nearId = req.TryGetValue("nearId", out var ni) && ni.ValueKind == JsonValueKind.Number
            ? ni.GetInt32() : null;
        int limit = req.TryGetValue("limit", out var l) && l.ValueKind == JsonValueKind.Number
            ? l.GetInt32() : 50;
        limit = Math.Clamp(limit, 1, 200);

        var byId = t.Nodes.ToDictionary(n => n.Id);
        IEnumerable<TreeNodeDto> source = t.Nodes;
        if (!string.IsNullOrEmpty(query))
        {
            source = source.Where(nd =>
                nd.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                nd.Stats.Any(s => s.Contains(query, StringComparison.OrdinalIgnoreCase)));
        }

        TreeNodeDto? near = nearId is { } id && byId.TryGetValue(id, out var nd2) ? nd2 : null;
        if (near is not null)
            source = source.OrderBy(nd =>
            {
                double dx = nd.X - near.X, dy = nd.Y - near.Y;
                return dx * dx + dy * dy;
            });

        var selected = source.Take(limit).ToArray();
        return new
        {
            ok    = true,
            count = selected.Length,
            nodes = selected.Select(nd => new
            {
                id         = nd.Id,
                x          = nd.X,
                y          = nd.Y,
                name       = nd.Name,
                type       = nd.Type,
                ascendancy = nd.AscendancyName,
                allocated  = t.AllocatedIds.Contains(nd.Id),
                stats      = nd.Stats,
                linkedIds  = nd.LinkedIds,
            }).ToArray(),
        };
    }

    private static object TreeSelectClass(string body)
    {
        if (GetTreeVm() is not { } t) return new { error = "TreeTab not ready." };
        var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        if (!req.TryGetValue("name", out var name) || string.IsNullOrEmpty(name))
            return new { error = "Missing 'name'." };

        var match = t.Classes.FirstOrDefault(c =>
            c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            c.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return new { error = $"Class '{name}' not found.", available = t.Classes.Select(c => c.Name).ToArray() };

        // Auto-confirm dialog in IPC mode
        t.ConfirmClassChange = () => Task.FromResult(true);
        t.SelectedClass = match;
        return new
        {
            ok                = true,
            selectedClass     = t.SelectedClass?.Name,
            ascendancies      = t.AvailableAscendancies.Select(a => a.Name).ToArray(),
        };
    }

    private static object TreeSelectAscendancy(string body)
    {
        if (GetTreeVm() is not { } t) return new { error = "TreeTab not ready." };
        var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        if (!req.TryGetValue("name", out var name) || string.IsNullOrEmpty(name))
            return new { error = "Missing 'name'." };

        var match = t.AvailableAscendancies.FirstOrDefault(a =>
            a.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            a.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return new { error = $"Ascendancy '{name}' not found.",
                         available = t.AvailableAscendancies.Select(a => a.Name).ToArray() };

        t.SelectedAscend = match;
        return new { ok = true, selectedAscendancy = match.Name };
    }

    // ── Tree view control (zoom / pan / focus) ────────────────────────────

    private static object TreeViewResult(TreeTabViewModel t)
    {
        if (t.GetCanvasView is null) return new { error = "Canvas view not bound." };
        var (scale, cx, cy) = t.GetCanvasView();
        return new { ok = true, scale, centerX = cx, centerY = cy };
    }

    private static object TreeGetView()
    {
        if (GetTreeVm() is not { } t) return new { error = "TreeTab not ready." };
        return TreeViewResult(t);
    }

    private static object TreeSetView(string body)
    {
        if (GetTreeVm() is not { } t) return new { error = "TreeTab not ready." };
        if (t.SetCanvasView is null) return new { error = "Canvas view not bound." };
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        double? scale = req.TryGetValue("scale",   out var s)  && s.ValueKind  == JsonValueKind.Number ? s.GetDouble()  : null;
        double? cx    = req.TryGetValue("centerX", out var x)  && x.ValueKind  == JsonValueKind.Number ? x.GetDouble()  : null;
        double? cy    = req.TryGetValue("centerY", out var y)  && y.ValueKind  == JsonValueKind.Number ? y.GetDouble()  : null;
        t.SetCanvasView(scale, cx, cy);
        return TreeViewResult(t);
    }

    private static object TreeFocusNode(string body)
    {
        if (GetTreeVm() is not { } t) return new { error = "TreeTab not ready." };
        if (t.FocusCanvasNode is null) return new { error = "Canvas view not bound." };
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        if (!req.TryGetValue("nodeId", out var n) || n.ValueKind != JsonValueKind.Number)
            return new { error = "Missing numeric 'nodeId'." };
        int nodeId = n.GetInt32();
        double? scale = req.TryGetValue("scale", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : null;
        bool found = t.FocusCanvasNode(nodeId, scale);
        if (!found) return new { error = $"Node {nodeId} is not present in the current tree." };
        return TreeViewResult(t);
    }

    private static object TreeZoom(string body)
    {
        if (GetTreeVm() is not { } t) return new { error = "TreeTab not ready." };
        if (t.ZoomCanvas is null) return new { error = "Canvas view not bound." };
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        if (!req.TryGetValue("factor", out var f) || f.ValueKind != JsonValueKind.Number)
            return new { error = "Missing numeric 'factor'." };
        t.ZoomCanvas(f.GetDouble());
        return TreeViewResult(t);
    }

    private static object TreePan(string body)
    {
        if (GetTreeVm() is not { } t) return new { error = "TreeTab not ready." };
        if (t.PanCanvas is null) return new { error = "Canvas view not bound." };
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        double dx = req.TryGetValue("dx", out var x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0;
        double dy = req.TryGetValue("dy", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetDouble() : 0;
        t.PanCanvas(dx, dy);
        return TreeViewResult(t);
    }

    private static object TreeOpenJewelPicker(string body)
    {
        if (GetTreeVm() is not { } t) return new { error = "TreeTab not ready." };
        if (t.TriggerSocketPicker is null) return new { error = "Canvas not bound." };
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        if (!req.TryGetValue("nodeId", out var n) || n.ValueKind != JsonValueKind.Number)
            return new { error = "Missing numeric 'nodeId'." };
        t.TriggerSocketPicker(n.GetInt32());
        return new { ok = true, isOpen = t.IsJewelPickerOpen,
                     options = t.JewelPickerOptions.Select(o => new { o.ItemId, o.DisplayName, o.StatusText, o.IsCurrent }).ToArray() };
    }

    private static object TreePickJewel(string body)
    {
        if (GetTreeVm() is not { } t) return new { error = "TreeTab not ready." };
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        if (!req.TryGetValue("itemId", out var i) || i.ValueKind != JsonValueKind.Number)
            return new { error = "Missing numeric 'itemId'." };
        t.PickJewelById(i.GetInt32());
        return new { ok = true };
    }

    private static async Task<object> TreePowerBuild(string body)
    {
        if (GetTreeVm() is not { } t) return new { error = "TreeTab not ready." };
        var req = string.IsNullOrWhiteSpace(body)
            ? new Dictionary<string, JsonElement>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();

        string? stat = req.TryGetValue("stat", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() : null;

        // Generation is now manual (Generate button) — toggling the heat map / changing
        // the stat no longer auto-builds. Set the stat, show the panel, then fire the
        // Generate command explicitly so automation can drive a build.
        if (stat != null)
            t.SelectedPowerStat = t.PowerStatOptions
                .FirstOrDefault(o => o.Option.StatKey == stat) ?? t.SelectedPowerStat;
        t.HeatmapEnabled = true;
        t.GeneratePowerCommand.Execute(null);

        // Wait for the background build to drain so the screenshot reflects the result.
        // Real builds can take ~85s; cap at 3600 iterations × 50ms = 180s. Exit early on
        // PowerError too — otherwise a failed build (e.g. "all workers failed" with no
        // fallback) spins the full 180s since IsPowerBuilding/PowerOverlay never settle
        // into the "done" shape the loop condition is waiting for.
        for (int i = 0; i < 3600 && t.PowerError is null && (t.IsPowerBuilding || t.PowerOverlay is null); i++)
            await Task.Delay(50);

        return new
        {
            ok       = t.PowerError is null,
            building = t.IsPowerBuilding,
            rows     = t.PowerReport.Count,
            error    = t.PowerError,
        };
    }

    private static object TreePowerReport()
    {
        if (GetTreeVm() is not { } t) return new { error = "TreeTab not ready." };
        return new
        {
            enabled  = t.HeatmapEnabled,
            stat     = t.SelectedPowerStat?.Option.StatKey,
            isStale  = t.IsPowerStale,
            progress = t.PowerBuildProgress,
            rows = t.PowerReport.Take(30).Select(r => new
            {
                id        = r.NodeId,
                name      = r.Name,
                type      = r.Type,
                typeBadge = r.TypeBadge,
                power     = r.PowerStr,
                perPoint  = r.PerPointStr,
                steps     = r.StepsStr,
                alloc     = r.IsAllocated,
            }).ToArray(),
        };
    }

    // ── Данные вещей для агента (существование, пулы афиксов) ────────────────

    private static LuaHost? ItemDataHost()
        => (GetMainVm()?.CurrentPage as BuildPageViewModel)?.Host;

    private static object ItemBases(string body)
    {
        var host = ItemDataHost();
        if (host is null) return new { error = "No build page open or engine not ready." };
        var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        var categories = host.GetItemBaseCategories();
        if (!req.TryGetValue("category", out var category) || string.IsNullOrEmpty(category))
            return new { categories };

        var match = categories.FirstOrDefault(c => c.Equals(category, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return new { error = $"Unknown category '{category}'.", categories };
        return new
        {
            category = match,
            bases = host.GetItemBasesForCategory(match).Select(b => new
            {
                name = b.Name,
                category = b.Category,
                implicitMod = b.Implicit,
                levelReq = b.LevelReq,
                type = b.Type,
                socketCount = b.SocketCount,
            }).ToArray(),
        };
    }

    private static object ItemAffixEntry(AffixEntry a) => new
    {
        id = a.Id,
        name = a.AffixName,
        statText = a.StatText,
        type = a.AffixType,
        level = a.Level,
        group = a.Group,
        tier = a.Tier,
        tierCount = a.TierCount,
    };

    private static object ItemAffixes(string body)
    {
        var host = ItemDataHost();
        if (host is null) return new { error = "No build page open or engine not ready." };
        var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        if (!req.TryGetValue("base", out var baseName) || string.IsNullOrEmpty(baseName))
            return new { error = "Missing 'base'." };

        var affixes = host.GetItemAffixes(baseName);
        var corruption = host.GetItemCorruptedAffixes(baseName);
        if (affixes.Count == 0 && corruption.Count == 0)
        {
            // подсказка агенту: базы с похожим именем по всем категориям
            var guesses = host.GetItemBaseCategories()
                .SelectMany(c => host.GetItemBasesForCategory(c))
                .Where(b => b.Name.Contains(baseName, StringComparison.OrdinalIgnoreCase))
                .Select(b => b.Name).Distinct().Take(10).ToArray();
            return new { error = $"No affix pool for base '{baseName}'.", guesses };
        }
        return new
        {
            @base = baseName,
            affixes = affixes.Select(ItemAffixEntry).ToArray(),
            corruption = corruption.Select(ItemAffixEntry).ToArray(),
            runes = host.GetAllRunes().Select(r => new
            {
                name = r.Name,
                slotTypes = r.SlotTypes,
                modsByType = r.ModsByType,
                augType = r.AugType,
            }).ToArray(),
        };
    }

    private static object ItemUniques(string body)
    {
        var host = ItemDataHost();
        if (host is null) return new { error = "No build page open or engine not ready." };
        var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        req.TryGetValue("query", out var query);
        var uniques = host.GetUniqueItems();
        if (!string.IsNullOrEmpty(query))
            uniques = uniques.Where(u =>
                u.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                u.BaseName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                u.Category.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return new
        {
            count = uniques.Count,
            uniques = uniques.Select(u => new
            {
                name = u.Name,
                @base = u.BaseName,
                category = u.Category,
                lookupKey = u.LookupKey,
            }).ToArray(),
        };
    }

    // ── Цены для агента (ninja через сессию трейдера) ────────────────────────

    private static (TraderSession?, object?) PriceSession()
    {
        var s = GetTraderSession();
        return s is null
            ? (null, (object)new { error = "Trader session not created. It appears when the trader window is opened once — call /trader/open (MCP: visual_trader_open) — then price tools work." })
            : (s, null);
    }

    private static async Task<object> PricesCurrency(string body)
    {
        var (s, err) = PriceSession();
        if (s is null) return err!;
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        var type = req.TryGetValue("type", out var t) && t.GetString() is { Length: > 0 } ty ? ty : "Currency";
        if (!PBLApp.Core.Trader.TraderWebApi.ExchangeTypes.Contains(type))
            return new { error = $"Unknown exchange type '{type}'. Allowed: {string.Join(", ", PBLApp.Core.Trader.TraderWebApi.ExchangeTypes)}." };
        var query = req.TryGetValue("query", out var qe) && qe.ValueKind == JsonValueKind.String ? qe.GetString() : null;

        var rates = await s.GetExchangeRatesAsync(type);
        var list = rates.Select(kv => new { name = kv.Key, value = kv.Value });
        if (!string.IsNullOrEmpty(query))
            list = list.Where(r => r.name.Contains(query, StringComparison.OrdinalIgnoreCase));
        return new { league = s.SelectedLeague, type, rates = list.ToArray() };
    }

    private static async Task<object> PricesUniques(string body)
    {
        var (s, err) = PriceSession();
        if (s is null) return err!;
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        var type = req.TryGetValue("type", out var t) && t.GetString() is { Length: > 0 } ty ? ty : "UniqueArmours";
        if (!PBLApp.Core.Trader.TraderWebApi.StashItemTypes.Contains(type))
            return new { error = $"Unknown stash type '{type}'. Allowed: {string.Join(", ", PBLApp.Core.Trader.TraderWebApi.StashItemTypes)}." };
        var query = req.TryGetValue("query", out var qe) && qe.ValueKind == JsonValueKind.String ? qe.GetString() : null;

        var items = await s.GetUniquePricesAsync(type, query);
        return new
        {
            league = s.SelectedLeague,
            type,
            items = items.Select(i => new
            {
                name = i.Name,
                @base = i.BaseName,
                price = i.DivineValue,
                listingCount = i.ListingCount,
                corrupted = i.Corrupted,
                mods = i.Mods,
            }).ToArray(),
        };
    }

    // ── Trader (session + active window) ─────────────────────────────────────

    private static TraderSession? GetTraderSession()
        => (GetMainVm()?.CurrentPage as BuildPageViewModel)?.TraderSession;

    private static TraderWindowViewModel? GetTraderWindow()
        => (GetMainVm()?.CurrentPage as BuildPageViewModel)?.ActiveTraderWindow;

    private static object TraderState()
    {
        var s = GetTraderSession();
        if (s is null) return new { open = false, error = "Trader session not created." };
        var w = GetTraderWindow();
        return new
        {
            ok = true,
            windowOpen = w is not null,
            slot = w?.SlotName,
            league = s.SelectedLeague,
            leagues = s.Leagues.ToArray(),
            leagueLoadError = s.LeagueLoadError.Length > 0 ? s.LeagueLoadError : null,
            loggedIn = s.IsLoggedIn,
            account = s.AccountName,
            activeWeightCount = s.ActiveWeightCount,
            statWeightsJson = s.StatWeightsJson,
            status = w?.Status,
            busy = w?.IsBusy ?? false,
            hasQuery = w?.LastQueryJson is not null,
            results = w?.Results.Select(r => new
            {
                price = r.Listing.Amount,
                currency = r.Listing.Currency,
                seller = r.Listing.Seller,
                dps = r.DpsDiff,
                ehp = r.EhpDiff,
                value = r.StatValue,
                valuePerDiv = r.ValuePerDiv,
                triedOn = r.IsTriedOn,
            }).ToArray(),
        };
    }

    /// <summary>Открывает окно импорта персонажа (#47) с экрана выбора билда —
    /// оно дочернее, скриншот MainWindow его не покажет, поэтому агенту нужен явный
    /// способ его вызвать. Необязательный "screen" (индекс из /app/screens) уводит
    /// окно на другой монитор, чтобы проверка не залезала поверх работы человека.</summary>
    private static object CharacterImportOpen(string body)
    {
        // На списке билдов это импорт нового билда, на странице билда — «Обновить
        // из игры» (то же окно в режиме перезаписи открытого билда).
        switch (GetMainVm()?.CurrentPage)
        {
            case BuildListViewModel bl:
                bl.OpenCharacterImportCommand.Execute(null);
                break;
            case BuildPageViewModel bp:
                // Именно окно: сама кнопка «Из игры» обновляет билд без него.
                _ = bp.OpenCharacterImportWindowAsync();
                break;
            default:
                return new { error = "Not on the build list or build page." };
        }

        var window = AppWindows().OfType<Views.CharacterImportWindow>().LastOrDefault();
        var placed = PlaceOnScreen(window, ScreenIndexFrom(body));
        return new { ok = true, placedOnScreen = placed };
    }

    /// <summary>Жмёт «Из игры» на странице билда — как пользователь: сперва диалог
    /// подтверждения, потом обновление из привязанного персонажа.</summary>
    private static object UpdateFromGame()
    {
        if (GetMainVm()?.CurrentPage is not BuildPageViewModel bp)
            return new { error = "Not on the build page." };
        bp.UpdateFromGameCommand.Execute(null);
        return new { ok = true };
    }

    // ── Stats / build persistence / notes (agent-facing) ───────────────────

    private static BuildPageViewModel? BuildPageOrError(out object error)
    {
        error = null!;
        var bp = GetMainVm()?.CurrentPage as BuildPageViewModel;
        if (bp is null) error = new { error = "Not on BuildPage." };
        return bp;
    }

    /// <summary>Full stat dump of the open build, optionally filtered:
    /// GET /stats?keys=TotalDPS,Life — empty keys returns all (~600) stats.</summary>
    // ── Снапшоты статов (diff до/после примерки) ─────────────────────────────
    // Одна сессия IPC = один клиент (токен), хранилище на процесс.
    // Стираются при открытии/импорте другого билда — diff между билдами не имеет смысла.
    private static readonly Dictionary<string, Dictionary<string, object?>> _statSnapshots = new();

    private static void ClearStatSnapshots() => _statSnapshots.Clear();

    private static object StatsSnapshot(string body)
    {
        if (BuildPageOrError(out var err) is not { } bp) return err;
        if (bp.Build is not { } build) return new { error = "Build not loaded yet." };
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body ?? "{}") ?? new();
        var name = req.TryGetValue("name", out var n) && n.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(n.GetString())
            ? n.GetString()! : "last";
        _statSnapshots[name] = new Dictionary<string, object?>(build.AllStats);
        return new { ok = true, name, keyCount = build.AllStats.Count };
    }

    private static object StatsDiffHandler(string query)
    {
        if (BuildPageOrError(out var err) is not { } bp) return err;
        if (bp.Build is not { } build) return new { error = "Build not loaded yet." };
        string? Param(string key)
        {
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0] == key)
                    return Uri.UnescapeDataString(kv[1]);
            }
            return null;
        }
        var fromName = Param("from") ?? "last";
        if (!_statSnapshots.TryGetValue(fromName, out var before))
            return new
            {
                error = $"No snapshot '{fromName}'. Call POST /stats/snapshot first.",
                available = _statSnapshots.Keys.OrderBy(k => k).ToArray(),
            };
        var toName = Param("to");
        IReadOnlyDictionary<string, object?> after = toName is null
            ? build.AllStats
            : _statSnapshots.TryGetValue(toName, out var snap)
                ? snap
                : new Dictionary<string, object?>();   // неизвестное имя to → пусто → всё removed
        if (toName is not null && after.Count == 0)
            return new
            {
                error = $"No snapshot '{toName}'.",
                available = _statSnapshots.Keys.OrderBy(k => k).ToArray(),
            };
        var diff = StatsDiff.Compute(before, after);
        return new
        {
            ok   = true,
            from = fromName,
            to   = toName ?? "current",
            changed = diff.Changed.Select(c => new { key = c.Key, from = c.From, to = c.To }).ToArray(),
            added   = diff.Added.Select(c => new { key = c.Key, to = c.To }).ToArray(),
            removed = diff.Removed.ToArray(),
        };
    }

    private static object GetStatsFiltered(string query)
    {
        if (BuildPageOrError(out var err) is not { } bp) return err;
        if (bp.Build is not { } build) return new { error = "Build not loaded yet." };

        var keys = new List<string>();
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "keys")
                keys.AddRange(Uri.UnescapeDataString(kv[1])
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var all = build.AllStats;
        if (keys.Count == 0)
            return all
                .Where(kv => kv.Value is not null)
                .OrderBy(kv => kv.Key)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

        var result = new Dictionary<string, object?>();
        foreach (var k in keys)
        {
            all.TryGetValue(k, out var v);
            result[k] = v;
        }
        return result;
    }

    private static async Task<object> BuildSave()
    {
        if (BuildPageOrError(out var err) is not { } bp) return err;
        await bp.SaveNow();
        return new { ok = true, path = bp.XmlPath };
    }

    private static object BuildXml()
    {
        if (BuildPageOrError(out var err) is not { } bp) return err;
        if (bp.Build is not { } build) return new { error = "Build not loaded yet." };
        var xml = build.SaveBuildToXml();
        return xml is null ? new { error = "SaveBuildToXml returned null." } : new { ok = true, xml };
    }

    /// <summary>Импорт XML или PoB share code в живое приложение: LoadBuildFromXml +
    /// рефреш вкладок. save:false (по умолчанию) — билд только в памяти, файл
    /// пользователя не перезаписывается; снапшоты статов стираются.</summary>
    private static async Task<object> BuildImport(string body)
    {
        if (BuildPageOrError(out var err) is not { } bp) return err;
        if (bp.Build is not { } build) return new { error = "Build not loaded yet." };
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        string? xml = null;
        string? name = null;
        var save = false;
        if (req.TryGetValue("xml", out var x) && x.ValueKind == JsonValueKind.String)
            xml = x.GetString();
        else if (req.TryGetValue("code", out var c) && c.ValueKind == JsonValueKind.String)
            xml = PBLEngine.BuildCodec.Decode(c.GetString() ?? "");
        if (req.TryGetValue("name", out var n) && n.ValueKind == JsonValueKind.String)
            name = n.GetString();
        if (req.TryGetValue("save", out var s))
            save = s.ValueKind == JsonValueKind.True ||
                   (s.ValueKind == JsonValueKind.String && s.GetString() == "true");
        if (string.IsNullOrWhiteSpace(xml))
            return new { error = "Provide 'xml' (build XML text) or 'code' (PoB share code)." };
        if (!xml.Contains("<PathOfBuilding"))
            return new { error = "Payload does not look like a PoB build XML (<PathOfBuilding missing). " +
                                 "For share codes pass them as 'code'." };

        build.LoadBuildFromXml(xml, string.IsNullOrWhiteSpace(name) ? "Imported Build" : name);
        bp.RefreshAfterImport();   // включая TreeTab — иначе /tree/allocated вернёт прежнее дерево
        ClearStatSnapshots();   // снапшоты принадлежали прежнему билду
        if (save)
            await bp.SaveNow();
        return new { ok = true, name = string.IsNullOrWhiteSpace(name) ? "Imported Build" : name, saved = save };
    }

    /// <summary>Свод предупреждений билда: несходящиеся атрибуты, отрицательные
    /// пулы, cost-предупреждения, itemWarnings, отключённые скилы, бюджет очков.
    /// Запускать после мутаций шмота/дерева — иначе агент не узнает, что сломал.</summary>
    private static object BuildValidate()
    {
        var host = ItemDataHost();
        if (host is null) return new { error = "No build page open or engine not ready." };
        var v = host.GetBuildValidation();
        return new
        {
            ok             = true,
            points         = PointsPayload(v.Points),
            warnings       = v.Issues.Select(i => new { code = i.Code, message = i.Message, source = i.Source }).ToArray(),
            disabledSkills = v.DisabledSkills.Select(d => new { group = d.GroupIndex, gem = d.GemName, reason = d.Reason }).ToArray(),
        };
    }

    private static object NotesGet()
    {
        if (BuildPageOrError(out var err) is not { } bp) return err;
        if (bp.NotesTab is not { } t) return new { error = "NotesTab not ready." };
        return new { ok = true, text = t.Notes };
    }

    private static object NotesSet(string body)
    {
        if (BuildPageOrError(out var err) is not { } bp) return err;
        if (bp.NotesTab is not { } t) return new { error = "NotesTab not ready." };
        var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        if (!req.TryGetValue("text", out var text))
            return new { error = "Missing 'text'." };
        t.Notes = text;
        t.ApplyToBuild();   // so /build/save persists the agent-written notes
        return new { ok = true };
    }

    // ── Config tab (agent-facing) ──────────────────────────────────────────

    private static object ConfigOptionState(ConfigOptionViewModel o) => new
    {
        var     = o.Var,
        label   = o.Label,
        type    = o.Type,
        value   = o.Type == "check" ? (object)o.IsChecked
                : o.Type == "list"  ? o.SelectedVal
                :                     o.TextValue,
        listOptions = o.Type == "list"
            ? o.ListOptions.Select(li => new { label = li.Label, val = li.Val }).ToArray()
            : null,
    };

    private static object ConfigState()
    {
        if (BuildPageOrError(out var err) is not { } bp) return err;
        if (bp.ConfigTab is not { } cfg) return new { error = "ConfigTab not ready." };
        return new
        {
            ok = true,
            sections = cfg.Sections.Select(s => new
            {
                name    = s.Name,
                options = s.Options.Select(ConfigOptionState).ToArray(),
            }).ToArray(),
        };
    }

    private static object ConfigSet(string body)
    {
        if (BuildPageOrError(out var err) is not { } bp) return err;
        if (bp.ConfigTab is not { } cfg) return new { error = "ConfigTab not ready." };
        var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        if (!req.TryGetValue("var", out var varName) || string.IsNullOrWhiteSpace(varName))
            return new { error = "Missing 'var'." };
        if (!req.TryGetValue("value", out var value))
            return new { error = "Missing 'value'." };

        var opt = cfg.Sections.SelectMany(s => s.Options)
            .FirstOrDefault(o => o.Var.Equals(varName, StringComparison.Ordinal));
        if (opt is null)
        {
            var avail = string.Join(", ", cfg.Sections.SelectMany(s => s.Options).Select(o => o.Var));
            return new { error = $"config var '{varName}' not found. Available: {avail}" };
        }

        switch (opt.Type)
        {
            case "check":
                if (value is not "true" and not "false")
                    return new { error = $"check option '{varName}' expects 'true' or 'false'." };
                opt.IsChecked = value == "true";   // setter applies via SetConfigValue
                break;
            case "list":
                var item = opt.ListOptions.FirstOrDefault(li => li.Val == value)
                        ?? opt.ListOptions.FirstOrDefault(li => li.Label == value);
                if (item is null)
                    return new
                    {
                        error = $"unknown value '{value}' for '{varName}'. Options: " +
                                string.Join(", ", opt.ListOptions.Select(li => li.Val)),
                    };
                opt.SelectedItem = item;           // setter applies via SetConfigValue
                break;
            default:
                // Numeric option types gate on the invariant culture (Lua
                // tonumber expects a dot) — reject up front so {ok:true} never
                // lies about a value the VM would silently drop.
                if (opt.Type != "text" && !Core.ConfigValues.IsNumericInvariant(value))
                    return new { error = $"option '{varName}' expects a number (dot decimal separator), got '{value}'." };
                opt.TextValue = value;             // OnTextValueChanged applies (text as-is, numbers validated)
                break;
        }

        return new { ok = true, var = varName, applied = value };
    }

    /// <summary>Открывает окно «Импорт/Экспорт» открытого билда — там же живёт
    /// экспорт гайда в игру (.build).</summary>
    private static object ImportExportOpen(string body)
    {
        if (GetMainVm()?.CurrentPage is not BuildPageViewModel bp)
            return new { error = "Not on the build page." };
        if (bp.RequestOpenImportExport is null)
            return new { error = "Build page view is not ready." };
        bp.RequestOpenImportExport();

        var window = AppWindows().OfType<Views.ImportExportWindow>().LastOrDefault();
        var placed = PlaceOnScreen(window, ScreenIndexFrom(body));
        return new { ok = true, placedOnScreen = placed };
    }

    /// <summary>Мониторы, как их видит приложение: индекс для "screen" в других
    /// вызовах + рабочая область в физических пикселях.</summary>
    private static object ListScreens()
    {
        var w = GetMainWindow();
        if (w is null) return new { error = "MainWindow not available." };
        var screens = w.Screens.All;
        return new
        {
            ok = true,
            screens = screens.Select((s, i) => new
            {
                index = i,
                primary = s.IsPrimary,
                x = s.WorkingArea.X,
                y = s.WorkingArea.Y,
                width = s.WorkingArea.Width,
                height = s.WorkingArea.Height,
                scaling = s.Scaling,
            }).ToArray(),
        };
    }

    /// <summary>Служебное: увести окна приложения на указанный монитор, чтобы
    /// агентская проверка не перекрывала основной экран человека. "window": "main"
    /// (по умолчанию) | "character-import" | "all".</summary>
    private static object MoveToScreen(string body)
    {
        var index = ScreenIndexFrom(body);
        if (index is null) return new { error = "Provide 'screen' (index from /app/screens)." };

        var which = "main";
        try
        {
            var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
            if (req is not null && req.TryGetValue("window", out var w) &&
                w.ValueKind == JsonValueKind.String && w.GetString() is { Length: > 0 } name)
                which = name;
        }
        catch (JsonException) { /* без "window" — двигаем главное окно */ }

        var moved = new List<string>();
        foreach (var window in AppWindows())
        {
            var isMain = ReferenceEquals(window, GetMainWindow());
            var match = which switch
            {
                "all" => true,
                "character-import" => window is Views.CharacterImportWindow,
                _ => isMain,
            };
            if (match && PlaceOnScreen(window, index) is not null)
                moved.Add(window.GetType().Name);
        }
        return new { ok = true, screen = index, moved = moved.ToArray() };
    }

    private static int? ScreenIndexFrom(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
            if (req is not null && req.TryGetValue("screen", out var s) &&
                s.ValueKind == JsonValueKind.Number)
                return s.GetInt32();
        }
        catch (JsonException) { /* тела нет или оно не наше — просто без переноса */ }
        return null;
    }

    /// <summary>Двигает окно на монитор с указанным индексом и подрезает под его
    /// рабочую область. Возвращает индекс монитора либо null, если переносить нечего.</summary>
    private static int? PlaceOnScreen(Window? window, int? index)
    {
        if (window is null || index is null) return null;
        var screens = window.Screens.All;
        if (index < 0 || index >= screens.Count) return null;

        var screen = screens[index.Value];
        var scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
        window.WindowState = WindowState.Normal;
        window.Width  = Math.Min(window.Width,  screen.WorkingArea.Width  / scale);
        window.Height = Math.Min(window.Height, screen.WorkingArea.Height / scale);
        window.Position = new PixelPoint(screen.WorkingArea.X + 40, screen.WorkingArea.Y + 40);
        return index;
    }

    private static IReadOnlyList<Window> AppWindows()
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
               ?.Windows ?? Array.Empty<Window>();

    private static object TraderOpen(string body)
    {
        if (GetMainVm()?.CurrentPage is not BuildPageViewModel bp)
            return new { error = "No build page." };
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        var slot = req.TryGetValue("slot", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() : null;
        if (string.IsNullOrEmpty(slot)) return new { error = "Provide 'slot'." };
        if (bp.RequestOpenTrader is null) return new { error = "Trader open route not wired." };
        bp.RequestOpenTrader(slot);
        return new { ok = true, opened = slot };
    }

    private static object TraderSearch(string body)
    {
        var w = GetTraderWindow();
        if (w is null) return new { error = "No trader window open. Call /trader/open first." };
        w.SearchCommand.Execute(null); // fire-and-forget; прогресс виден через /trader/state
        return new { ok = true, started = w.SlotName };
    }

    private static object TraderSetLeague(string body)
    {
        var s = GetTraderSession();
        if (s is null) return new { error = "Trader session not created." };
        var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        if (req.TryGetValue("league", out var l) && l.ValueKind == JsonValueKind.String &&
            l.GetString() is { Length: > 0 } league)
        {
            s.SelectedLeague = league;
            return new { ok = true, league };
        }
        return new { error = "Provide 'league'." };
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        try { if (File.Exists(DiscoveryPath)) File.Delete(DiscoveryPath); } catch { }
    }
}

