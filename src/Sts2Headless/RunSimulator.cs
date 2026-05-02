using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace Sts2Headless;

/// <summary>
/// Synchronization context that executes continuations inline immediately.
/// Task.Yield() posts to SynchronizationContext.Current — by executing inline,
/// the yield becomes a no-op and the entire async chain runs synchronously.
/// Uses a recursion guard to queue nested posts and drain them after.
/// </summary>
internal class InlineSynchronizationContext : SynchronizationContext
{
    private readonly Queue<(SendOrPostCallback, object?)> _queue = new();
    private bool _executing;

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (_executing)
        {
            _queue.Enqueue((d, state));
            return;
        }
        // removed debug log

        // Execute inline immediately, then drain any nested posts
        _executing = true;
        try
        {
            d(state);
            // Drain any callbacks that were queued during execution
            while (_queue.Count > 0)
            {
                var (cb, st) = _queue.Dequeue();
                cb(st);
            }
        }
        finally
        {
            _executing = false;
        }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        d(state);
    }

    public void Pump()
    {
        // Drain any remaining queued callbacks
        while (_queue.Count > 0)
        {
            var (cb, st) = _queue.Dequeue();
            _executing = true;
            try { cb(st); }
            finally { _executing = false; }
        }
    }
}

/// <summary>
/// Bilingual localization lookup — loads eng/zhs JSON files for display names.
/// </summary>
internal class LocLookup
{
    private readonly Dictionary<string, Dictionary<string, string>> _eng = new();
    private readonly Dictionary<string, Dictionary<string, string>> _zhs = new();

    public LocLookup()
    {
        var baseDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        Load(Path.Combine(baseDir, "localization_eng"), _eng);
        Load(Path.Combine(baseDir, "localization_zhs"), _zhs);
    }

    private static void Load(string dir, Dictionary<string, Dictionary<string, string>> target)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                if (data != null) target[name] = data;
            }
            catch { }
        }
    }

    /// <summary>Get bilingual name: "English / 中文" or just the key if not found.</summary>
    public string Name(string table, string key)
    {
        var en = _eng.GetValueOrDefault(table)?.GetValueOrDefault(key);
        var zh = _zhs.GetValueOrDefault(table)?.GetValueOrDefault(key);
        if (en != null && zh != null && en != zh) return $"{en} / {zh}";
        return en ?? zh ?? key;
    }

    public string? En(string table, string key) => _eng.GetValueOrDefault(table)?.GetValueOrDefault(key);
    public string? Zh(string table, string key) => _zhs.GetValueOrDefault(table)?.GetValueOrDefault(key);

    /// <summary>Strip BBCode tags like [gold], [/blue], [b], [sine], etc.</summary>
    private static string StripBBCode(string text)
    {
        return System.Text.RegularExpressions.Regex.Replace(text, @"\[/?[a-zA-Z_][a-zA-Z0-9_=]*\]", "");
    }

    /// <summary>Language for JSON output: "en" or "zh". Default: "en".</summary>
    public string Lang { get; set; } = "en";

    /// <summary>Return localized string for JSON output based on Lang setting.</summary>
    public string Bilingual(string table, string key)
    {
        if (Lang == "zh")
        {
            var zh = _zhs.GetValueOrDefault(table)?.GetValueOrDefault(key);
            if (zh != null) return StripBBCode(zh);
        }
        var en = _eng.GetValueOrDefault(table)?.GetValueOrDefault(key) ?? key;
        return StripBBCode(en);
    }

    // Convenience helpers using ModelId
    public string Card(string entry) => Bilingual("cards", entry + ".title");
    public string Monster(string entry) => Bilingual("monsters", entry + ".name");
    public string Relic(string entry) => Bilingual("relics", entry + ".title");
    public string Potion(string entry) => Bilingual("potions", entry + ".title");
    public string Power(string entry) => Bilingual("powers", entry + ".title");
    public string Event(string entry) => Bilingual("events", entry + ".title");
    public string Act(string entry) => Bilingual("acts", entry + ".title");

    /// <summary>Resolve a full loc key like "TABLE.KEY.SUB" by searching all tables.</summary>
    public string BilingualFromKey(string locKey)
    {
        if (Lang == "zh")
        {
            foreach (var tableName in _zhs.Keys)
            {
                var zh = _zhs.GetValueOrDefault(tableName)?.GetValueOrDefault(locKey);
                if (zh != null) return zh;
            }
        }
        foreach (var tableName in _eng.Keys)
        {
            var en = _eng.GetValueOrDefault(tableName)?.GetValueOrDefault(locKey);
            if (en != null) return en;
        }
        return locKey;
    }

    public bool IsLoaded => _eng.Count > 0;
}

/// <summary>
/// Full run simulator — manages the game lifecycle from character selection
/// through map navigation, combat, events, rest sites, shops, and act transitions.
/// Drives the engine forward until it hits a "decision point" requiring external input.
/// </summary>
public class RunSimulator
{
    private const int CurrentSaveSchemaVersion = 14;
    private RunState? _runState;
    private static bool _modelDbInitialized;
    private static readonly InlineSynchronizationContext _syncCtx = new();
    private readonly ManualResetEventSlim _turnStarted = new(false);
    private readonly ManualResetEventSlim _combatEnded = new(false);
    private static readonly LocLookup _loc = new();
    private bool _eventOptionChosen;
    private int _lastEventOptionCount;

    // Pending rewards for card selection (populated after combat, before proceeding)
    private List<Reward>? _pendingRewards;
    private CardReward? _pendingCardReward;
    private bool _rewardsProcessed;
    private int _goldBeforeCombat;
    private int _lastKnownHp;
    private readonly HeadlessCardSelector _cardSelector = new();
    // Pending bundle selection (Scroll Boxes: pick 1 of N packs)
    private IReadOnlyList<IReadOnlyList<CardModel>>? _pendingBundles;
    private TaskCompletionSource<IEnumerable<CardModel>>? _pendingBundleTcs;
    private Task? _pendingChoiceTask;

    public Dictionary<string, object?> StartRun(string character, int ascension = 0, string? seed = null, string lang = "en")
    {
        try
        {
            _loc.Lang = lang;
            EnsureModelDbInitialized();

            var player = CreatePlayer(character);
            if (player == null)
                return Error($"Unknown character: {character}");

            var seedStr = seed ?? "headless_" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Log($"Creating RunState with seed={seedStr}");

            // Use CreateForTest which properly handles mutable copies internally
            _runState = RunState.CreateForTest(
                players: new[] { player },
                ascensionLevel: ascension,
                seed: seedStr
            );

            // Set up RunManager with test mode
            var netService = new NetSingleplayerGameService();
            RunManager.Instance.SetUpTest(_runState, netService);
            LocalContext.NetId = netService.NetId;

            // Force Neow event (blessing selection at start)
            _runState.ExtraFields.StartedWithNeow = true;

            // Generate rooms for all acts
            RunManager.Instance.GenerateRooms();
            Log("Rooms generated");

            // Launch the run
            RunManager.Instance.Launch();
            Log("Run launched");

            // Register event handlers for combat turn transitions
            CombatManager.Instance.TurnStarted += _ => _turnStarted.Set();
            CombatManager.Instance.CombatEnded += _ => _combatEnded.Set();

            // Enter first act (generates map)
            RunManager.Instance.EnterAct(0, doTransition: true).GetAwaiter().GetResult();
            _syncCtx.Pump();
            Log("Entered Act 0 with transition");

            // BUGFIX: On some systems, EnterAct doesn't automatically trigger the Neow event
            // node. If we are still on map/null room, explicitly enter the starting node.
            if ((_runState.CurrentRoom is MapRoom || _runState.CurrentRoom == null) && _runState.Map?.StartingMapPoint != null)
            {
                Log("Explicitly entering Neow starting node");
                RunManager.Instance.EnterMapCoord(_runState.Map.StartingMapPoint.coord).GetAwaiter().GetResult();
                _syncCtx.Pump();
            }

            // Register card selector for cards that need player choice
            CardSelectCmd.UseSelector(_cardSelector);
            LocPatches._bundleSimRef = this;

            // Give the engine a moment to trigger start-of-run events/choices
            _syncCtx.Pump();
            Thread.Sleep(20);
            _syncCtx.Pump();

            // Now detect decision point
            return DetectDecisionPoint();
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("StartRun failed", ex);
        }
    }

    // ─── Test/Debug commands ───

    private static readonly System.Reflection.BindingFlags NonPublic =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    /// <summary>Get the backing List&lt;T&gt; behind an IReadOnlyList property via reflection.</summary>
    private static List<T>? GetBackingList<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName, NonPublic);
        return field?.GetValue(obj) as List<T>;
    }

    private static void SetField(object obj, string fieldName, object? value)
    {
        var field = obj.GetType().GetField(fieldName, NonPublic);
        field?.SetValue(obj, value);
    }

    private static object? GetField(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName, NonPublic);
        return field?.GetValue(obj);
    }


    private static object? GetProperty(object obj, string propName)
    {
        var prop = obj.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return prop?.GetValue(obj);
    }

    public Dictionary<string, object?> SetPlayer(Dictionary<string, System.Text.Json.JsonElement> args)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var player = _runState.Players[0];

            if (args.TryGetValue("hp", out var hpEl) && player.Creature != null)
                SetField(player.Creature, "_currentHp", hpEl.GetInt32());
            if (args.TryGetValue("max_hp", out var mhpEl) && player.Creature != null)
                SetField(player.Creature, "_maxHp", mhpEl.GetInt32());
            if (args.TryGetValue("gold", out var goldEl))
                player.Gold = goldEl.GetInt32();

            if (args.TryGetValue("relics", out var relicsEl))
            {
                var list = GetBackingList<RelicModel>(player, "_relics");
                if (list != null)
                {
                    list.Clear();
                    foreach (var rEl in relicsEl.EnumerateArray())
                    {
                        var id = rEl.GetString();
                        if (id == null) continue;
                        var model = ModelDb.GetById<RelicModel>(new ModelId("RELIC", id));
                        if (model != null) list.Add(model.ToMutable());
                    }
                }
            }
            if (args.TryGetValue("deck", out var deckEl))
            {
                // Remove existing cards from RunState tracking
                foreach (var c in player.Deck.Cards.ToList())
                    _runState.RemoveCard(c);
                player.Deck.Clear(silent: true);
                // Add new cards via RunState.CreateCard (sets Owner + registers)
                foreach (var cEl in deckEl.EnumerateArray())
                {
                    var id = cEl.GetString();
                    if (id == null) continue;
                    var canonical = ModelDb.GetById<CardModel>(new ModelId("CARD", id));
                    if (canonical != null)
                    {
                        var card = _runState.CreateCard(canonical, player);
                        player.Deck.AddInternal(card, silent: true);
                    }
                }
            }
            if (args.TryGetValue("potions", out var potionsEl))
            {
                var slots = GetBackingList<PotionModel>(player, "_potionSlots")
                         ?? GetBackingList<PotionModel?>(player, "_potionSlots") as System.Collections.IList;
                if (slots != null)
                {
                    for (int i = 0; i < slots.Count; i++) slots[i] = null;
                    int idx = 0;
                    foreach (var pEl in potionsEl.EnumerateArray())
                    {
                        if (idx >= slots.Count) break;
                        var id = pEl.GetString();
                        if (id != null)
                        {
                            var model = ModelDb.GetById<PotionModel>(new ModelId("POTION", id));
                            if (model != null) slots[idx] = model;
                        }
                        idx++;
                    }
                }
            }

            Log($"SetPlayer: hp={player.Creature?.CurrentHp} gold={player.Gold} relics={player.Relics.Count} deck={player.Deck?.Cards?.Count}");
            return new Dictionary<string, object?>
            {
                ["type"] = "ok",
                ["player"] = PlayerSummary(player),
            };
        }
        catch (Exception ex) { return ErrorWithTrace("SetPlayer failed", ex); }
    }

    public Dictionary<string, object?> EnterRoom(string roomType, string? encounter, string? eventId)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var runState = _runState;
            Log($"EnterRoom: type={roomType} encounter={encounter} event={eventId}");
            _rewardsProcessed = false;
            _pendingRewards = null;

            AbstractRoom room;
            switch (roomType.ToLowerInvariant())
            {
                case "combat":
                case "monster":
                case "elite":
                {
                    if (string.IsNullOrEmpty(encounter))
                        encounter = "SHRINKER_BEETLE_WEAK"; // default encounter
                    var encModel = ModelDb.GetById<EncounterModel>(new ModelId("ENCOUNTER", encounter));
                    if (encModel == null) return Error($"Unknown encounter: {encounter}");
                    room = new CombatRoom(encModel.ToMutable(), runState);
                    break;
                }
                case "shop":
                    room = new MerchantRoom();
                    break;
                case "rest":
                case "rest_site":
                    room = new RestSiteRoom();
                    break;
                case "event":
                {
                    if (string.IsNullOrEmpty(eventId))
                        return Error("event requires 'event' parameter (e.g. CHANGELING_GROVE)");
                    var evModel = ModelDb.GetById<EventModel>(new ModelId("EVENT", eventId));
                    if (evModel == null) return Error($"Unknown event: {eventId}");
                    room = new EventRoom(evModel);
                    break;
                }
                case "treasure":
                    room = new TreasureRoom(_runState.CurrentActIndex);
                    break;
                default:
                    return Error($"Unknown room type: {roomType}");
            }

            RunManager.Instance.EnterRoom(room).GetAwaiter().GetResult();
            _syncCtx.Pump();
            WaitForActionExecutor();
            return DetectDecisionPoint();
        }
        catch (Exception ex) { return ErrorWithTrace("EnterRoom failed", ex); }
    }

    public Dictionary<string, object?> SetDrawOrder(List<string> cardIds)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var player = _runState.Players[0];
            var pcs = player.PlayerCombatState;
            if (pcs?.DrawPile == null) return Error("Not in combat");

            var drawList = GetBackingList<CardModel>(pcs.DrawPile, "_cards");
            if (drawList == null) return Error("Cannot access draw pile");

            var newOrder = new List<CardModel>();
            var available = new List<CardModel>(drawList);
            foreach (var cardId in cardIds)
            {
                var match = available.FirstOrDefault(c =>
                    c.Id.Entry.Equals(cardId, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    newOrder.Add(match);
                    available.Remove(match);
                }
            }
            newOrder.AddRange(available);

            drawList.Clear();
            drawList.AddRange(newOrder);

            Log($"SetDrawOrder: {newOrder.Count} cards, top={newOrder.FirstOrDefault()?.Id.Entry}");
            return new Dictionary<string, object?>
            {
                ["type"] = "ok",
                ["draw_pile_count"] = drawList.Count,
                ["top_cards"] = newOrder.Take(5).Select(c => _loc.Card(c.Id.Entry)).ToList(),
            };
        }
        catch (Exception ex) { return ErrorWithTrace("SetDrawOrder failed", ex); }
    }

    // ─── Game actions ───
    public Dictionary<string, object?> LoadSave(string saveJson)
    {
        try
        {
            EnsureModelDbInitialized();

            Log("Loading save file...");

            if (!ValidateSaveSchemaVersion(saveJson, out var schemaError))
                return Error($"Save schema mismatch: {schemaError}");

            var readResult = SaveManager.FromJson<SerializableRun>(saveJson);
            if (!readResult.Success || readResult.SaveData == null)
                return Error($"Failed to parse save file: {readResult.Status} {readResult.ErrorMessage}");

            var save = readResult.SaveData;
            Log($"Save loaded: seed={save.SerializableRng?.Seed}, act={save.CurrentActIndex}, ascension={save.Ascension}");

            _runState = RunState.FromSerializable(save);
            if (_runState == null)
                return Error("Failed to create RunState from save");

            Log($"RunState created, players={_runState.Players?.Count}");

            var netService = new NetSingleplayerGameService();
            RunManager.Instance.SetUpSavedSinglePlayer(_runState, save);
            LocalContext.NetId = netService.NetId;

            CombatManager.Instance.TurnStarted += _ => _turnStarted.Set();
            CombatManager.Instance.CombatEnded += _ => _combatEnded.Set();
            CardSelectCmd.UseSelector(_cardSelector);
            LocPatches._bundleSimRef = this;

            var savedRoom = _runState.CurrentRoom;

            // Save visited coords before Launch (EnterAct will clear them)
            var savedVisitedCoords = _runState.VisitedMapCoords?.ToList() ?? new List<MapCoord>();
            var shouldResumeInitialNeow = IsInitialNeowSave(saveJson);
            Log($"Save has {savedVisitedCoords.Count} visited coords");

            RunManager.Instance.Launch();
            Log("Run launched");

            if (savedRoom is MapRoom || savedRoom == null)
            {
                // Preserve Neow for saves created before the first blessing choice.
                // Once the run has visited at least one map node, re-entering Act 1
                // should not send the player back through the Ancient start node.
                if (_runState.CurrentActIndex == 0 && savedVisitedCoords.Count > 0)
                    _runState.ExtraFields.StartedWithNeow = false;
                RunManager.Instance.EnterAct(_runState.CurrentActIndex, doTransition: false).GetAwaiter().GetResult();
                _syncCtx.Pump();
                Log($"Entered Act {_runState.CurrentActIndex}");

                if (shouldResumeInitialNeow && _runState.Map?.StartingMapPoint != null)
                {
                    Log("Restoring initial Neow event");
                    RunManager.Instance.EnterMapCoord(_runState.Map.StartingMapPoint.coord).GetAwaiter().GetResult();
                    _syncCtx.Pump();
                }

                // EnterAct clears visited coords and ActFloor — restore them from save
                if (savedVisitedCoords.Count > 0)
                {
                    if (_runState.VisitedMapCoords == null || _runState.VisitedMapCoords.Count == 0)
                    {
                        foreach (var coord in savedVisitedCoords)
                            _runState.AddVisitedMapCoord(coord);
                    }
                    _runState.ActFloor = savedVisitedCoords.Count;
                    var last = savedVisitedCoords[^1];
                    Log($"Restored map position: floor={_runState.ActFloor}, coord=({last.col},{last.row})");
                }
            }
            else
            {
                Log($"Preserving saved room: {savedRoom.GetType().Name}");
            }

            return DetectDecisionPoint();
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("LoadSave failed", ex);
        }
    }

    private static bool ValidateSaveSchemaVersion(string saveJson, out string error)
    {
        error = "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(saveJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("schema_version", out var versionElem))
            {
                error = "missing schema_version";
                return false;
            }

            if (versionElem.ValueKind != System.Text.Json.JsonValueKind.Number ||
                !versionElem.TryGetInt32(out var schemaVersion))
            {
                error = "schema_version is not a valid integer";
                return false;
            }

            if (schemaVersion != CurrentSaveSchemaVersion)
            {
                error = $"expected v{CurrentSaveSchemaVersion}, got v{schemaVersion}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"could not inspect save: {ex.Message}";
            return false;
        }
    }

    private static bool TrySetPropertyValue(object target, string propertyName, object? value)
    {
        var prop = target.GetType().GetProperty(propertyName);
        if (prop?.CanWrite != true)
            return false;
        prop.SetValue(target, value);
        return true;
    }

    private static bool IsInitialNeowSave(string saveJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(saveJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("current_act_index", out var actIndexElem) || actIndexElem.GetInt32() != 0)
                return false;

            var hasVisitedCoords = root.TryGetProperty("visited_map_coords", out var visitedElem)
                                && visitedElem.ValueKind == System.Text.Json.JsonValueKind.Array
                                && visitedElem.GetArrayLength() > 0;
            if (hasVisitedCoords)
                return false;

            return root.TryGetProperty("extra_fields", out var extraFieldsElem)
                && extraFieldsElem.ValueKind == System.Text.Json.JsonValueKind.Object
                && extraFieldsElem.TryGetProperty("started_with_neow", out var startedElem)
                && startedElem.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRollbackSerializedSaveToPreRoom(SerializableRun serializableRun, out string error)
    {
        error = "";

        var saveType = serializableRun.GetType();
        var visitedProp = saveType.GetProperty("VisitedMapCoords");
        if (visitedProp == null)
        {
            error = "Save data is missing VisitedMapCoords";
            return false;
        }

        var visitedValue = visitedProp.GetValue(serializableRun);
        var visitedItems = new List<object?>();
        if (visitedValue is System.Collections.IEnumerable visitedEnumerable)
        {
            foreach (var item in visitedEnumerable)
                visitedItems.Add(item);
        }

        if (visitedItems.Count == 0)
        {
            error = "Cannot roll back save before the first room";
            return false;
        }

        visitedItems.RemoveAt(visitedItems.Count - 1);

        var visitedType = visitedProp.PropertyType;
        if (visitedType.IsArray)
        {
            var elementType = visitedType.GetElementType()!;
            var array = Array.CreateInstance(elementType, visitedItems.Count);
            for (int i = 0; i < visitedItems.Count; i++)
                array.SetValue(visitedItems[i], i);
            visitedProp.SetValue(serializableRun, array);
        }
        else if (visitedType.IsGenericType)
        {
            var elementType = visitedType.GetGenericArguments()[0];
            var listType = typeof(List<>).MakeGenericType(elementType);
            var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
            foreach (var item in visitedItems)
                list.Add(item);
            visitedProp.SetValue(serializableRun, list);
        }
        else
        {
            error = $"Unsupported VisitedMapCoords type: {visitedType.Name}";
            return false;
        }

        TrySetPropertyValue(serializableRun, "ActFloor", visitedItems.Count);
        TrySetPropertyValue(serializableRun, "CurrentMapCoord", visitedItems.Count > 0 ? visitedItems[^1] : null);
        TrySetPropertyValue(serializableRun, "PreFinishedRoom", null);
        TrySetPropertyValue(serializableRun, "CurrentRoom", null);
        return true;
    }

    public Dictionary<string, object?> SaveCheckpoint(string? outputPath)
    {
        try
        {
            if (_runState == null)
                return Error("No active run to save");

            if (string.IsNullOrEmpty(outputPath))
                return Error("No output path specified for quit save");

            var currentRoom = _runState.CurrentRoom;
            SerializableRun serializableRun;

            if (currentRoom is MapRoom || currentRoom == null)
            {
                Log($"Saving map checkpoint (room={currentRoom?.GetType().Name ?? "null"}, outputPath={outputPath})...");
                serializableRun = RunManager.Instance.ToSave(currentRoom);
            }
            else
            {
                Log($"Saving pre-room checkpoint from {currentRoom.GetType().Name} (outputPath={outputPath})...");
                serializableRun = RunManager.Instance.ToSave(new MapRoom());
                if (!TryRollbackSerializedSaveToPreRoom(serializableRun, out var rollbackError))
                    return Error($"Cannot save checkpoint: {rollbackError}");
            }

            var saveJson = SaveManager.ToJson(serializableRun);
            Log($"Serialized save: {saveJson.Length} chars");

            var dir = System.IO.Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(outputPath, saveJson);
            Log($"Save written to: {outputPath}");

            return new Dictionary<string, object?>
            {
                ["type"] = "save_result",
                ["success"] = true,
                ["path"] = outputPath,
                ["size"] = saveJson.Length,
                ["room_type"] = currentRoom?.GetType().Name,
            };
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("SaveCheckpoint failed", ex);
        }
    }
    public Dictionary<string, object?> ExecuteAction(string action, Dictionary<string, object?>? args)
    {
        try
        {
            if (_runState == null)
                return Error("No run in progress");

            var player = _runState.Players[0];

            switch (action)
            {
                case "select_map_node":
                    return DoMapSelect(player, args);
                case "play_card":
                    return DoPlayCard(player, args);
                case "end_turn":
                    return DoEndTurn(player);
                case "choose_option":
                    return DoChooseOption(player, args);
                case "select_card_reward":
                    return DoSelectCardReward(player, args);
                case "skip_card_reward":
                    return DoSkipCardReward(player);
                case "buy_card":
                    return DoBuyCard(player, args);
                case "buy_relic":
                    return DoBuyRelic(player, args);
                case "buy_potion":
                    return DoBuyPotion(player, args);
                case "remove_card":
                    return DoRemoveCard(player);
                case "select_bundle":
                    return DoSelectBundle(player, args);
                case "select_cards":
                    return DoSelectCards(player, args);
                case "skip_select":
                    return DoSkipSelect(player);
                case "use_potion":
                    return DoUsePotion(player, args);
                case "discard_potion":
                    return DoDiscardPotion(player, args);
                case "leave_room":
                    return DoLeaveRoom(player);
                case "proceed":
                    return DoProceed(player);
                case "take_reward":
                    return DoTakeReward(player, args);
                case "swap_potion":
                    return DoSwapPotion(player, args);
                default:
                    return Error($"Unknown action: {action}");
            }
        }
        catch (Exception ex)
        {
            return ErrorWithTrace($"Action '{action}' failed", ex);
        }
    }

    #region Actions

    private Dictionary<string, object?> DoMapSelect(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("col") || !args.ContainsKey("row"))
            return Error("select_map_node requires 'col' and 'row'");

        // Reset tracking for new room
        _rewardsProcessed = false;
        _pendingCardReward = null;
        _eventOptionChosen = false;
        _lastEventOptionCount = 0;
        _pendingRewards = null;
        _lastKnownHp = player.Creature?.CurrentHp ?? 0;

        var col = Convert.ToInt32(args["col"]);
        var row = Convert.ToInt32(args["row"]);
        var coord = new MapCoord((byte)col, (byte)row);

        Log($"Moving to map coord ({col},{row})");

        // BUG-013: Wait for any pending actions (relic sessions, etc.) to complete before entering new room
        WaitForActionExecutor();
        _syncCtx.Pump();

        // Call EnterMapCoord directly (same as what MoveToMapCoordAction does in TestMode)
        // This avoids the action executor which can swallow errors silently.
        RunManager.Instance.EnterMapCoord(coord).GetAwaiter().GetResult();
        _syncCtx.Pump();
        WaitForActionExecutor();

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoPlayCard(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("card_index"))
            return Error("play_card requires 'card_index'");

        var cardIndex = Convert.ToInt32(args["card_index"]);
        var pcs = player.PlayerCombatState;
        if (pcs == null)
            return Error("Not in combat");

        var hand = pcs.Hand.Cards;
        if (cardIndex < 0 || cardIndex >= hand.Count)
            return Error($"Invalid card index {cardIndex}, hand has {hand.Count} cards");

        var card = hand[cardIndex];

        // Determine target based on card's TargetType first
        // Self/None/All cards: target = null (game handles internally)
        // AnyEnemy cards: use target_index or auto-pick first alive enemy
        Creature? target = null;
        var cardTargetType = card.TargetType;
        if (cardTargetType == TargetType.AnyEnemy)
        {
            // Use caller's target_index if provided
            if (args.TryGetValue("target_index", out var targetObj) && targetObj != null)
            {
                var targetIndex = Convert.ToInt32(targetObj);
                var state = CombatManager.Instance.DebugOnlyGetState();
                if (state != null)
                {
                    var enemies = state.Enemies.Where(e => e != null && e.IsAlive).ToList();
                    if (targetIndex >= 0 && targetIndex < enemies.Count)
                        target = enemies[targetIndex];
                }
            }
            // Fallback: auto-target first alive enemy
            if (target == null)
            {
                var state = CombatManager.Instance.DebugOnlyGetState();
                target = state?.Enemies?.FirstOrDefault(e => e != null && e.IsAlive);
            }
        }
        // All other target types (None, All, etc.) → leave target as null

        // Check if card can be played
        if (!card.CanPlay(out var reason, out var _))
        {
            return Error($"Cannot play card {card.GetType().Name}: {reason}");
        }

        Log($"Playing card {card.GetType().Name} (index {cardIndex}) targeting {(target != null ? target.Monster?.GetType().Name ?? "creature" : "none")}");

        var handCountBefore = hand.Count;

        var playAction = new PlayCardAction(card, target);
        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(playAction);
        WaitForActionExecutor();

        // Check if card play had no effect (hand unchanged, same card still at same index)
        var handAfter = pcs.Hand.Cards;
        if (handAfter.Count == handCountBefore && cardIndex < handAfter.Count && handAfter[cardIndex] == card)
        {
            return Error($"Card could not be played (still in hand after action): {card.GetType().Name} [{card.Id}]");
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoEndTurn(Player player)
    {
        if (!CombatManager.Instance.IsPartOfPlayerTurn(_runState!.Players[0]))
        {
            // Might be between phases — pump and check
            _syncCtx.Pump();
            if (!CombatManager.Instance.IsPartOfPlayerTurn(_runState!.Players[0]))
            {
                if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead)
                    return DetectDecisionPoint();
                // Brief wait for ThreadPool if sync context didn't catch it
                Thread.Sleep(20);
                _syncCtx.Pump();
                if (!CombatManager.Instance.IsPartOfPlayerTurn(_runState!.Players[0]))
                    return DetectDecisionPoint();
            }
        }

        // Ensure no actions are still running before ending turn
        WaitForActionExecutor();

        Log($"Ending turn (round={CombatManager.Instance.DebugOnlyGetState()?.RoundNumber ?? 0})");
        _turnStarted.Reset();
        _combatEnded.Reset();

        // Enable SuppressYield so Task.Yield() runs inline during enemy turn processing.
        // This prevents deadlocks during boss fights (e.g., Vantom) where continuations
        // would otherwise be posted to ThreadPool and never complete.
        YieldPatches.SuppressYield = true;
        try
        {
            PlayerCmd.EndTurn(player, canBackOut: false);
            _syncCtx.Pump();
        }
        finally
        {
            YieldPatches.SuppressYield = false;
        }

        // Fallback: if turn didn't complete synchronously, wait briefly then force retry
        if (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsPartOfPlayerTurn(_runState!.Players[0]) && !player.Creature.IsDead)
        {
            for (int i = 0; i < 50; i++)
            {
                _syncCtx.Pump();
                if (_turnStarted.IsSet || _combatEnded.IsSet) break;
                if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead) break;
                if (CombatManager.Instance.IsPartOfPlayerTurn(_runState!.Players[0])) break;
                Thread.Sleep(5);
            }

            // If STILL stuck, the WaitUntilQueue TCS is likely deadlocked.
            // Cancel the ActionExecutor to break out, then re-trigger EndTurn.
            if (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsPartOfPlayerTurn(_runState!.Players[0]) && !player.Creature.IsDead)
            {
                Log("EndTurn stuck, cancelling and retrying with SuppressYield...");
                try
                {
                    RunManager.Instance.ActionExecutor.Cancel();
                    _syncCtx.Pump();
                    Thread.Sleep(50);
                    _syncCtx.Pump();

                    // Reset the player ready state and try again with SuppressYield
                    CombatManager.Instance.UndoReadyToEndTurn(player);
                    _syncCtx.Pump();

                    YieldPatches.SuppressYield = true;
                    try
                    {
                        PlayerCmd.EndTurn(player, canBackOut: false);
                        _syncCtx.Pump();
                    }
                    finally
                    {
                        YieldPatches.SuppressYield = false;
                    }

                    for (int i = 0; i < 100; i++)
                    {
                        _syncCtx.Pump();
                        if (_turnStarted.IsSet || _combatEnded.IsSet) break;
                        if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead) break;
                        if (CombatManager.Instance.IsPartOfPlayerTurn(_runState!.Players[0])) break;
                        Thread.Sleep(10);
                    }
                }
                catch (Exception ex) { Log($"Cancel retry: {ex.Message}"); }
            }

            // NUCLEAR OPTION: If STILL stuck after 2 attempts, use ThreadPool to force
            // the enemy turn processing to complete with SuppressYield permanently on.
            if (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsPartOfPlayerTurn(_runState!.Players[0]) && !player.Creature.IsDead)
            {
                var stuckState = CombatManager.Instance.DebugOnlyGetState();
                var stuckEnemies = stuckState?.Enemies?.Where(e => e != null && e.IsAlive)
                    .Select(e => $"{e.Monster?.GetType().Name}(hp={e.CurrentHp})").ToList();
                Log($"EndTurn STILL stuck after retry — nuclear fallback. Round={stuckState?.RoundNumber}, " +
                    $"Enemies=[{string.Join(",", stuckEnemies ?? new())}], " +
                    $"IsPlayPhase={CombatManager.Instance.IsPartOfPlayerTurn(_runState!.Players[0])}, " +
                    $"IsInProgress={CombatManager.Instance.IsInProgress}, " +
                    $"ActionExecutor.IsRunning={RunManager.Instance.ActionExecutor.IsRunning}");
                try
                {
                    // Cancel again and undo
                    RunManager.Instance.ActionExecutor.Cancel();
                    _syncCtx.Pump();
                    CombatManager.Instance.UndoReadyToEndTurn(player);
                    _syncCtx.Pump();
                    Thread.Sleep(50);

                    // Run EndTurn on ThreadPool with SuppressYield permanently on
                    YieldPatches.SuppressYield = true;
                    var endTurnTask = Task.Run(() =>
                    {
                        PlayerCmd.EndTurn(player, canBackOut: false);
                    });

                    // Aggressively pump sync context while waiting (up to 5 seconds)
                    for (int i = 0; i < 500; i++)
                    {
                        _syncCtx.Pump();
                        if (endTurnTask.IsCompleted) break;
                        if (_turnStarted.IsSet || _combatEnded.IsSet) break;
                        if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead) break;
                        if (CombatManager.Instance.IsPartOfPlayerTurn(_runState!.Players[0])) break;
                        Thread.Sleep(10);
                    }
                    YieldPatches.SuppressYield = false;

                    // If still not play phase, try just waiting a bit more
                    if (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsPartOfPlayerTurn(_runState!.Players[0]) && !player.Creature.IsDead)
                    {
                        for (int i = 0; i < 200; i++)
                        {
                            _syncCtx.Pump();
                            Thread.Sleep(10);
                            if (CombatManager.Instance.IsPartOfPlayerTurn(_runState!.Players[0]) || !CombatManager.Instance.IsInProgress || player.Creature.IsDead)
                                break;
                        }
                    }

                    if (CombatManager.Instance.IsPartOfPlayerTurn(_runState!.Players[0]))
                        Log("Nuclear fallback SUCCEEDED — play phase resumed");
                    else
                        Log("Nuclear fallback FAILED — returning stuck state");
                }
                catch (Exception ex)
                {
                    Log($"Nuclear fallback error: {ex.Message}");
                    YieldPatches.SuppressYield = false;
                }
            }
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSelectCardReward(Player player, Dictionary<string, object?>? args)
    {
        // Handle event-triggered card reward (blocking GetSelectedCardReward)
        if (_cardSelector.HasPendingReward)
        {
            if (args == null || !args.ContainsKey("card_index"))
                return Error("select_card_reward requires 'card_index'");
            var idx = Convert.ToInt32(args["card_index"]);
            Log($"Resolving event card reward: index {idx}");
            _cardSelector.ResolveReward(idx);
            Thread.Sleep(10);
            _syncCtx.Pump();
            WaitForActionExecutor();
            return DetectDecisionPoint();
        }

        if (_pendingCardReward == null)
            return Error("No pending card reward");
        if (args == null || !args.ContainsKey("card_index"))
            return Error("select_card_reward requires 'card_index'");

        var cardIndex = Convert.ToInt32(args["card_index"]);
        var cards = _pendingCardReward.Cards.ToList();
        if (cardIndex < 0 || cardIndex >= cards.Count)
            return Error($"Invalid card index {cardIndex}, {cards.Count} cards available");

        var card = cards[cardIndex];
        Log($"Selected card reward: {card.GetType().Name}");

        // Add card to deck
        try
        {
            MegaCrit.Sts2.Core.Commands.CardPileCmd
                .Add(card, MegaCrit.Sts2.Core.Entities.Cards.PileType.Deck)
                .GetAwaiter().GetResult();
            _syncCtx.Pump();
            RunManager.Instance.RewardSynchronizer.SyncLocalObtainedCard(card);
        }
        catch (Exception ex) { Log($"Add card to deck: {ex.Message}"); }

        // Check if more rewards pending
        if (_pendingRewards != null)
        {
            _pendingRewards.Remove(_pendingCardReward);
        }
        _pendingCardReward = null;

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSkipCardReward(Player player)
    {
        if (_cardSelector.HasPendingReward)
        {
            Log("Skipping event card reward");
            _cardSelector.SkipReward();
            Thread.Sleep(10);
            _syncCtx.Pump();
            WaitForActionExecutor();
            return DetectDecisionPoint();
        }
        if (_pendingCardReward != null)
        {
            Log("Skipping card reward");
            _pendingCardReward.OnSkipped();
            if (_pendingRewards != null)
            {
                _pendingRewards.Remove(_pendingCardReward);
            }
            _pendingCardReward = null;
        }
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoBuyCard(Player player, Dictionary<string, object?>? args)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");
        if (args == null || !args.ContainsKey("card_index"))
            return Error("buy_card requires 'card_index'");

        var idx = Convert.ToInt32(args["card_index"]);
        var allEntries = merchantRoom.Inventory.CharacterCardEntries
            .Concat(merchantRoom.Inventory.ColorlessCardEntries).ToList();
        if (idx < 0 || idx >= allEntries.Count)
            return Error($"Invalid card index {idx}");

        var entry = allEntries[idx];
        if (!entry.IsStocked) return Error("Card already purchased");
        if (player.Gold < entry.Cost) return Error("Not enough gold");

        try
        {
            entry.OnTryPurchaseWrapper(merchantRoom.Inventory).GetAwaiter().GetResult();
            _syncCtx.Pump();
            Log($"Bought card: {entry.CreationResult?.Card?.GetType().Name ?? "?"} for {entry.Cost}g");
        }
        catch (Exception ex) { return Error($"Buy card failed: {ex.Message}"); }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoBuyRelic(Player player, Dictionary<string, object?>? args)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");
        if (args == null || !args.ContainsKey("relic_index"))
            return Error("buy_relic requires 'relic_index'");

        var idx = Convert.ToInt32(args["relic_index"]);
        var entries = merchantRoom.Inventory.RelicEntries;
        if (idx < 0 || idx >= entries.Count) return Error($"Invalid relic index {idx}");

        var entry = entries[idx];
        if (!entry.IsStocked) return Error("Relic already purchased");
        if (player.Gold < entry.Cost) return Error("Not enough gold");

        try
        {
            entry.OnTryPurchaseWrapper(merchantRoom.Inventory).GetAwaiter().GetResult();
            _syncCtx.Pump();
            Log($"Bought relic: {entry.Model.GetType().Name} for {entry.Cost}g");
        }
        catch (Exception ex) { return Error($"Buy relic failed: {ex.Message}"); }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoBuyPotion(Player player, Dictionary<string, object?>? args)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");
        if (args == null || !args.ContainsKey("potion_index"))
            return Error("buy_potion requires 'potion_index'");

        var idx = Convert.ToInt32(args["potion_index"]);
        var entries = merchantRoom.Inventory.PotionEntries;
        if (idx < 0 || idx >= entries.Count) return Error($"Invalid potion index {idx}");

        var entry = entries[idx];
        if (!entry.IsStocked) return Error("Potion already purchased");
        if (player.Gold < entry.Cost) return Error("Not enough gold");

        try
        {
            entry.OnTryPurchaseWrapper(merchantRoom.Inventory).GetAwaiter().GetResult();
            _syncCtx.Pump();
            Log($"Bought potion: {entry.Model.GetType().Name} for {entry.Cost}g");
        }
        catch (Exception ex)
        {
            // Potion purchase sometimes NullRefs in headless (missing potion slot UI)
            Log($"Buy potion failed: {ex.Message}");
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoRemoveCard(Player player)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");

        var removal = merchantRoom.Inventory.CardRemovalEntry;
        if (removal == null) return Error("No card removal available");
        if (player.Gold < removal.Cost) return Error("Not enough gold");

        try
        {
            // Run on background thread so card selection can pause (same pattern as event options)
            _pendingChoiceTask = Task.Run(() => removal.OnTryPurchaseWrapper(merchantRoom.Inventory));
            WaitForChoiceTask(_pendingChoiceTask);

            if (_cardSelector.HasPending)
            {
                WaitForActionExecutor();
                return DetectDecisionPoint();
            }
            _syncCtx.Pump();
            Log($"Removed card for {removal.Cost}g");
        }
        catch (Exception ex) { return Error($"Remove card failed: {ex.Message}"); }
        finally { _pendingChoiceTask = null; }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSelectBundle(Player player, Dictionary<string, object?>? args)
    {
        if (_pendingBundleTcs == null || _pendingBundles == null)
            return Error("No pending bundle selection");
        if (args == null || !args.ContainsKey("bundle_index"))
            return Error("select_bundle requires 'bundle_index'");

        var idx = Convert.ToInt32(args["bundle_index"]);
        Log($"Bundle selection: pack {idx}");
        var bundles = _pendingBundles;
        var tcs = _pendingBundleTcs;
        _pendingBundles = null;
        _pendingBundleTcs = null;

        // Set result directly (no ContinueWith/ThreadPool)
        var selected = (idx >= 0 && idx < bundles.Count) ? bundles[idx] : bundles[0];
        tcs.TrySetResult(selected);

        _syncCtx.Pump();
        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSelectCards(Player player, Dictionary<string, object?>? args)
    {
        if (!_cardSelector.HasPending)
            return Error("No pending card selection");
        if (args == null || !args.ContainsKey("indices"))
            return Error("select_cards requires 'indices' (comma-separated card indices)");

        var indicesStr = args["indices"]?.ToString() ?? "";
        var indices = indicesStr.Split(',')
            .Select(s => int.TryParse(s.Trim(), out var v) ? v : -1)
            .Where(i => i >= 0)
            .ToArray();

        Log($"Card selection: indices [{string.Join(",", indices)}]");
        _cardSelector.ResolvePendingByIndices(indices);
        _syncCtx.Pump();
        WaitForActionExecutor();

        // If this selection was triggered by a background task (event, shop, rest site),
        // wait for that task to settle before detecting the next state.
        if (_pendingChoiceTask != null && !_pendingChoiceTask.IsCompleted)
        {
            Log("SelectCards: waiting for parent choice task to resume...");
            WaitForChoiceTask(_pendingChoiceTask);
        }

        // Extra wait for rest-site SMITH and events: the background choice tasks
        // need time to complete transitions after card selection resolves.
        if (_runState?.CurrentRoom is RestSiteRoom || _runState?.CurrentRoom is EventRoom)
        {
            Thread.Sleep(20);
            _syncCtx.Pump();
            WaitForActionExecutor();
            
            if (_runState?.CurrentRoom is RestSiteRoom)
            {
                // Force to map after SMITH completes (same pattern as HEAL)
                Log("Card selection in rest site (SMITH), forcing to map");
                ForceToMap();
                return MapSelectState();
            }
        }

        // Extra wait for shop card removal: the purchase task needs to finish
        if (_runState?.CurrentRoom is MerchantRoom)
        {
            Thread.Sleep(20);
            _syncCtx.Pump();
            WaitForActionExecutor();
            Log("Card selection in shop (card removal), refreshing shop state");
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSkipSelect(Player player)
    {
        if (_cardSelector.HasPending)
        {
            Log("Skipping card selection");
            _cardSelector.CancelPending();
            _syncCtx.Pump();
            WaitForActionExecutor();
        }
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoUsePotion(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("potion_index"))
            return Error("use_potion requires 'potion_index'");

        var idx = Convert.ToInt32(args["potion_index"]);
        var potionsList = player.Potions?.ToList() ?? new();
        if (idx < 0 || idx >= potionsList.Count) return Error($"Invalid potion index {idx}");
        var potion = potionsList[idx];
        if (potion == null) return Error($"No potion at index {idx}");

        // Determine target based on potion's TargetType
        Creature? target = null;
        var ptt = potion.TargetType;

        // If target_index is explicitly provided, try to use it
        if (args.TryGetValue("target_index", out var tObj) && tObj != null)
        {
            var targetIdx = Convert.ToInt32(tObj);
            var combatState = CombatManager.Instance.DebugOnlyGetState();
            if (combatState != null)
            {
                var enemies = combatState.Enemies.Where(e => e != null && e.IsAlive).ToList();
                if (targetIdx >= 0 && targetIdx < enemies.Count)
                    target = enemies[targetIdx];
            }
        }

        // Fallback or default logic
        if (target == null)
        {
            if (ptt == TargetType.AnyEnemy || ptt == TargetType.RandomEnemy)
            {
                // Auto-pick first alive enemy for targeted potions if no target provided
                var combatState = CombatManager.Instance.DebugOnlyGetState();
                target = combatState?.Enemies?.FirstOrDefault(e => e != null && e.IsAlive);
            }
            else
            {
                // For Self, AnyPlayer, AnyAlly, AllAllies, etc. default to player
                target = player.Creature;
            }
        }

        Log($"Using potion: {potion.GetType().Name} at slot {idx} target={target?.GetType().Name ?? "none"}");
        try
        {
            var action = new MegaCrit.Sts2.Core.GameActions.UsePotionAction(potion, target, CombatManager.Instance.IsInProgress);
            RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(action);
            WaitForActionExecutor();
            _syncCtx.Pump();
            // Verify potion was consumed
            var afterPotions = player.Potions?.ToList() ?? new();
            if (afterPotions.Contains(potion))
            {
                // Potion wasn't consumed — manually discard it
                Log("Potion not consumed by action, manually discarding");
                MegaCrit.Sts2.Core.Commands.PotionCmd.Discard(potion).GetAwaiter().GetResult();
                _syncCtx.Pump();
            }
        }
        catch (Exception ex)
        {
            Log($"Use potion failed: {ex.Message}");
            // Try manual discard as fallback
            try { MegaCrit.Sts2.Core.Commands.PotionCmd.Discard(potion).GetAwaiter().GetResult(); } catch { }
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoDiscardPotion(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("potion_index"))
            return Error("discard_potion requires 'potion_index'");

        var idx = Convert.ToInt32(args["potion_index"]);
        var potionsList = player.Potions?.ToList() ?? new();
        if (idx < 0 || idx >= potionsList.Count) return Error($"Invalid potion index {idx}");
        var potion = potionsList[idx];
        if (potion == null) return Error($"No potion at index {idx}");

        MegaCrit.Sts2.Core.Commands.PotionCmd.Discard(potion).GetAwaiter().GetResult();
        _syncCtx.Pump();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoChooseOption(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("option_index"))
            return Error("choose_option requires 'option_index'");

        var optionIndex = Convert.ToInt32(args["option_index"]);
        Log($"Choosing option {optionIndex}");

        // Dispatch based on ROOM TYPE (not event state) to avoid cross-contamination
        if (_runState?.CurrentRoom is RestSiteRoom restSiteRoom)
        {
            Log($"Rest site: choosing option {optionIndex}");
            try
            {
                // Run on background thread so Smith card selection can pause
                _pendingChoiceTask = Task.Run(() => RunManager.Instance.RestSiteSynchronizer.ChooseLocalOption(optionIndex));
                WaitForChoiceTask(_pendingChoiceTask);

                if (_cardSelector.HasPending)
                {
                    WaitForActionExecutor();
                    return DetectDecisionPoint();
                }
                _syncCtx.Pump();
            }
            catch (Exception ex)
            {
                Log($"Rest site ChooseLocalOption failed: {ex.Message}");
            }
            finally { _pendingChoiceTask = null; }

            // After non-Smith rest site options (HEAL, etc.), the options may not clear.
            // Wait for the action to complete (heal/dig), then force transition to map.
            if (!_cardSelector.HasPending)
            {
                Log("Rest site: option chosen (non-Smith), waiting for action then detecting decision");
                WaitForActionExecutor();
                _syncCtx.Pump();
                Thread.Sleep(20);
                _syncCtx.Pump();
                WaitForActionExecutor();
                return DetectDecisionPoint();
            }
        }
        // For events — use EventSynchronizer
        // Run Chosen() on a background thread so card selections can pause
        else if (_runState?.CurrentRoom is EventRoom)
        {
            var eventSync = RunManager.Instance.EventSynchronizer;
            var localEvent = eventSync?.GetLocalEvent();
            if (localEvent != null && !localEvent.IsFinished)
            {
                var options = localEvent.CurrentOptions?.ToList();
                if (options != null && optionIndex >= 0 && optionIndex < options.Count)
                {
                    try
                    {
                        _eventOptionChosen = true;
                        _lastEventOptionCount = options.Count;
                        // Use EventSynchronizer.ChooseLocalOption for robust state transitions.
                        // Run on thread pool so GetSelectedCards/GetSelectedCardReward can block.
                        _pendingChoiceTask = Task.Run(() => eventSync.ChooseLocalOption(optionIndex));
                        WaitForChoiceTask(_pendingChoiceTask);

                        if (_cardSelector.HasPending || _cardSelector.HasPendingReward || _pendingBundles != null)
                        {
                            WaitForActionExecutor();
                            return DetectDecisionPoint();
                        }
                        _syncCtx.Pump();
                    }
                    catch (Exception ex) 
                    { 
                        Log($"Event choose: {ex.Message}"); 
                        Console.Error.WriteLine($"[ERROR] Event choice {optionIndex} failed: {ex}");
                    }
                    finally { _pendingChoiceTask = null; }
                }
            }
        }

        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoLeaveRoom(Player player)
    {
        Log("Leaving room");
        try { RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult(); }
        catch { }
        _syncCtx.Pump();
        WaitForActionExecutor();

        // If still in a non-combat room, force to map
        var room = _runState?.CurrentRoom;
        if (room is RestSiteRoom || room is MerchantRoom || room is EventRoom || room is TreasureRoom)
        {
            Log("Force leaving non-combat room to map");
            try
            {
                RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult();
                _syncCtx.Pump();
                WaitForActionExecutor();
            }
            catch (Exception ex) { Log($"Force leave: {ex.Message}"); }
        }
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoProceed(Player player)
    {
        Log("Proceeding");
        _pendingRewards = null;
        _pendingCardReward = null;
        _rewardsProcessed = true;

        // Check if we need to move to next act (boss defeated)
        var room = _runState?.CurrentRoom;
        if (room is CombatRoom combatRoom && combatRoom.RoomType == RoomType.Boss)
        {
            if (combatRoom.IsPreFinished || !CombatManager.Instance.IsInProgress)
            {
                RunManager.Instance.EnterNextAct().GetAwaiter().GetResult();
                _syncCtx.Pump();
                if (_runState?.Map?.StartingMapPoint != null)
                {
                    RunManager.Instance.EnterMapCoord(_runState.Map.StartingMapPoint.coord).GetAwaiter().GetResult();
                    _syncCtx.Pump();
                }
                WaitForActionExecutor();
                return DetectDecisionPoint();
            }
        }

        RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    #endregion

    #region Decision Point Detection

    private Dictionary<string, object?> DetectDecisionPoint()
    {
        if (_runState == null)
            return Error("No run in progress");

        // If a background choice task is still running, wait for it before detecting next state.
        // But only if we aren't currently waiting for a card/bundle selection from that task!
        if (_pendingChoiceTask != null && !_pendingChoiceTask.IsCompleted && !_cardSelector.HasPending && !_cardSelector.HasPendingReward && _pendingBundles == null)
        {
            Log("DetectDecisionPoint: waiting for background choice task...");
            WaitForChoiceTask(_pendingChoiceTask);
        }

        var room = _runState.CurrentRoom;
        Log($"DetectDecisionPoint: room={room?.GetType().Name ?? "null"} bundles={(_pendingBundles != null)} cardSelect={_cardSelector.HasPending}");

        var player = _runState.Players[0];

        // Check game over (death)
        if (player.Creature != null && player.Creature.IsDead)
        {
            return GameOverState(false);
        }

        // Check if there's a pending bundle selection (Scroll Boxes: pick 1 of N packs)
        if (_pendingBundles != null && _pendingBundleTcs != null && !_pendingBundleTcs.Task.IsCompleted)
        {
            var bundles = _pendingBundles.Select((bundle, i) => new Dictionary<string, object?>
            {
                ["index"] = i,
                ["cards"] = bundle.Select(card =>
                {
                    var stats = new Dictionary<string, object?>();
                    try { foreach (var dv in card.DynamicVars.Values) stats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue; } catch { }
                    return new Dictionary<string, object?>
                    {
                        ["name"] = _loc.Card(card.Id.Entry),
                        ["cost"] = card.EnergyCost?.GetResolved() ?? 0,
                        ["type"] = card.Type.ToString(),
                        ["description"] = _loc.Bilingual("cards", card.Id.Entry + ".description"),
                        ["stats"] = stats.Count > 0 ? stats : null,
                    };
                }).ToList(),
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "bundle_select",
                ["context"] = RunContext(),
                ["bundles"] = bundles,
                ["player"] = PlayerSummary(player),
            };
        }

        // Check if there's a pending card reward from event (GetSelectedCardReward blocking)
        if (_cardSelector.HasPendingReward)
        {
            var rewardCards = _cardSelector.PendingRewardCards!;
            var cards = rewardCards.Select((cr, i) =>
            {
                var stats = new Dictionary<string, object?>();
                try { foreach (var dv in cr.Card.DynamicVars.Values) stats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue; } catch { }
                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["id"] = cr.Card.Id.ToString(),
                    ["name"] = _loc.Card(cr.Card.Id.Entry),
                    ["cost"] = cr.Card.EnergyCost?.GetResolved() ?? 0,
                    ["type"] = cr.Card.Type.ToString(),
                    ["rarity"] = cr.Card.Rarity.ToString(),
                    ["description"] = _loc.Bilingual("cards", cr.Card.Id.Entry + ".description"),
                    ["stats"] = stats.Count > 0 ? stats : null,
                    ["after_upgrade"] = GetUpgradedInfo(cr.Card),
                };
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "card_reward",
                ["context"] = RunContext(),
                ["cards"] = cards,
                ["can_skip"] = true,
                ["from_event"] = true,
                ["player"] = PlayerSummary(_runState!.Players[0]),
            };
        }

        // Check if there's a pending card selection (upgrade, remove, transform, start-of-turn powers)
        checkCardSelect:
        if (_cardSelector.HasPending && _cardSelector.PendingOptions != null)
        {
            var opts = _cardSelector.PendingOptions.Select((card, i) =>
            {
                var stats = new Dictionary<string, object?>();
                try { foreach (var dv in card.DynamicVars.Values) stats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue; } catch { }
                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["id"] = card.Id.ToString(),
                    ["name"] = _loc.Card(card.Id.Entry),
                    ["cost"] = card.EnergyCost?.GetResolved() ?? 0,
                    ["type"] = card.Type.ToString(),
                    ["upgraded"] = card.IsUpgraded,
                    ["stats"] = stats.Count > 0 ? stats : null,
                    ["description"] = _loc.Bilingual("cards", card.Id.Entry + ".description"),
                    ["after_upgrade"] = GetUpgradedInfo(card),
                };
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "card_select",
                ["context"] = RunContext(),
                ["cards"] = opts,
                ["min_select"] = _cardSelector.PendingMinSelect,
                ["max_select"] = _cardSelector.PendingMaxSelect,
                ["player"] = PlayerSummary(player),
            };
        }

        // Check if there's a pending card reward
        if (_pendingCardReward != null)
        {
            return CardRewardState(player, _runState.CurrentRoom);
        }

        // Check for pending reward menu
        if (_pendingRewards != null && _pendingRewards.Count > 0)
        {
            return RewardMenuState(player);
        }

        // Check if RunManager reports game over (victory)
        if (RunManager.Instance.IsGameOver)
        {
            return GameOverState(true);
        }

        // Map room — need to select a node
        if (room is MapRoom || room == null)
        {
            return MapSelectState();
        }

        // Combat room
        if (room is CombatRoom combatRoom)
        {
            YieldPatches.SuppressYield = true;
            try
            {
                // With Task.Yield() patched, combat init should be synchronous
                _syncCtx.Pump();
                WaitForActionExecutor();

                // Re-check for pending card selections AFTER pump (BUG-024: start-of-turn effects
                // like Tools of Trade create card selections during Pump, AFTER the initial HasPending check)
                if (_cardSelector.HasPending && _cardSelector.PendingOptions != null)
                {
                    goto checkCardSelect;  // Jump back to card_select handling
                }

                if (CombatManager.Instance.IsInProgress && CombatManager.Instance.IsPartOfPlayerTurn(_runState!.Players[0]))
                {
                    return CombatPlayState(player);
                }
                if (!CombatManager.Instance.IsInProgress || (player.Creature != null && player.Creature.IsDead))
                {
                    return DetectRoomRewardsState(player, combatRoom);
                }

                // Fallback: wait longer for enemy turn to finish if it's still in progress
                for (int i = 0; i < 100; i++)
                {
                    _syncCtx.Pump();
                    Thread.Sleep(5);
                    if (CombatManager.Instance.IsPartOfPlayerTurn(_runState!.Players[0])) return CombatPlayState(player);
                    if (!CombatManager.Instance.IsInProgress || (player.Creature != null && player.Creature.IsDead))
                        return DetectRoomRewardsState(player, combatRoom);
                }

                // If still not player turn, return a 'waiting' state instead of falling back to play
                Log("Combat stuck: not player turn and enemy turn not finishing.");
                return new Dictionary<string, object?>
                {
                    ["type"] = "decision",
                    ["decision"] = "combat_waiting",
                    ["context"] = RunContext(),
                    ["player"] = PlayerSummary(player),
                    ["message"] = "Waiting for enemy turn..."
                };
            }
            finally
            {
                YieldPatches.SuppressYield = false;
            }
        }

        // Event room
        if (room is EventRoom eventRoom)
        {
            return EventChoiceState(eventRoom);
        }

        // Rest site
        if (room is RestSiteRoom restRoom)
        {
            if (restRoom.Options == null || restRoom.Options.Count == 0)
            {
                return DetectRoomRewardsState(player, restRoom);
            }
            return RestSiteState(restRoom);
        }

        // Merchant/Shop
        if (room is MerchantRoom merchantRoom)
        {
            return ShopState(merchantRoom, player);
        }

        // Treasure room
        if (room is TreasureRoom treasureRoom)
        {
            return TreasureState(treasureRoom);
        }

        // Fallback
        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "unknown",
            ["context"] = RunContext(),
            ["room_type"] = room?.GetType().Name,
            ["message"] = "Unknown room type or state",
        };
    }

    private Dictionary<string, object?> MapSelectState()
    {
        var map = _runState?.Map;
        if (map == null)
        {
            Log("Map is null, generating...");
            try
            {
                RunManager.Instance.GenerateMap().GetAwaiter().GetResult();
                _syncCtx.Pump();
                map = _runState?.Map;
            }
            catch (Exception ex)
            {
                Log($"GenerateMap failed: {ex.Message}");
            }
            if (map == null)
                return Error("No map available");
        }
        var currentCoord = _runState!.CurrentMapCoord;

        List<Dictionary<string, object?>> choices;
        if (currentCoord.HasValue)
        {
            var currentPoint = map.GetPoint(currentCoord.Value);
            if (currentPoint == null)
            {
                Log($"GetPoint returned null for coord ({currentCoord.Value.col},{currentCoord.Value.row}), falling back to start");
                // Current coord is invalid (stale after forced room transition); treat as no position
                choices = new List<Dictionary<string, object?>>();
                var sp = map.StartingMapPoint;
                if (sp?.Children != null)
                {
                    foreach (var child in sp.Children)
                    {
                        choices.Add(new Dictionary<string, object?>
                        {
                            ["col"] = (int)child.coord.col,
                            ["row"] = (int)child.coord.row,
                            ["type"] = child.PointType.ToString(),
                        });
                    }
                }
            }
            else
            {
                choices = (currentPoint.Children ?? Enumerable.Empty<MapPoint>())
                    .Select(child => new Dictionary<string, object?>
                    {
                        ["col"] = (int)child.coord.col,
                        ["row"] = (int)child.coord.row,
                        ["type"] = child.PointType.ToString(),
                    })
                    .ToList();

                // Winged Boots interaction: allow jumping to any node in the next row if charges exist
                var player = _runState.Players[0];
                var wingedRelic = player.Relics?.FirstOrDefault(r => r.Id.Entry == "WINGED_BOOTS");
                if (wingedRelic != null)
                {
                    var vars = GetDynamicVars(wingedRelic.DynamicVars);
                    if (vars.TryGetValue("rooms", out var charges) && Convert.ToInt32(charges) > 0)
                    {
                        foreach (var node in map.GetPointsInRow(currentPoint.coord.row + 1))
                        {
                            if (node == null) continue;
                            if (!choices.Any(c => (int)c["col"] == node.coord.col && (int)c["row"] == node.coord.row))
                            {
                                choices.Add(new Dictionary<string, object?>
                                {
                                    ["col"] = (int)node.coord.col,
                                    ["row"] = (int)node.coord.row,
                                    ["type"] = node.PointType.ToString(),
                                    ["is_winged"] = true
                                });
                            }
                        }
                    }
                }
            }
        }
        else
        {
            // Starting point — pick from starting row
            var startPoint = map.StartingMapPoint;
            choices = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["col"] = (int)startPoint.coord.col,
                    ["row"] = (int)startPoint.coord.row,
                    ["type"] = startPoint.PointType.ToString(),
                }
            };
            // Add all children of start point as well since we can travel to them
            if (startPoint.Children != null)
            {
                foreach (var child in startPoint.Children)
                {
                    choices.Add(new Dictionary<string, object?>
                    {
                        ["col"] = (int)child.coord.col,
                        ["row"] = (int)child.coord.row,
                        ["type"] = child.PointType.ToString(),
                    });
                }
            }
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "map_select",
            ["context"] = RunContext(),
            ["choices"] = choices,
            ["player"] = PlayerSummary(_runState!.Players[0]),
            ["act"] = _runState.CurrentActIndex + 1,
            ["act_name"] = _loc.Act(_runState.Act?.Id.Entry ?? "OVERGROWTH"),
            ["floor"] = _runState.ActFloor,
        };
    }

    private Dictionary<string, object?> CombatPlayState(Player player)
    {
        var pcs = player.PlayerCombatState;
        var combatState = CombatManager.Instance.DebugOnlyGetState();

        // Track last known HP for accurate game_over reporting (BUG-005)
        if (player.Creature != null && player.Creature.CurrentHp > 0)
            _lastKnownHp = player.Creature.CurrentHp;

        var hand = pcs?.Hand?.Cards?.Select((c, i) =>
        {
            // Extract actual stat values from DynamicVars
            var stats = new Dictionary<string, object?>();
            try
            {
                foreach (var dv in c.DynamicVars.Values)
                {
                    stats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue;
                }
            }
            catch { }

            var starCost = c.BaseStarCost;
            var cardInfo = new Dictionary<string, object?>
            {
                ["index"] = i,
                ["id"] = c.Id.ToString(),
                ["name"] = _loc.Card(c.Id.Entry),
                ["cost"] = c.EnergyCost?.GetResolved() ?? 0,
                ["type"] = c.Type.ToString(),
                ["can_play"] = c.CanPlay(out _, out _),
                ["target_type"] = c.TargetType.ToString(),
                ["stats"] = stats.Count > 0 ? stats : null,
                ["description"] = _loc.Bilingual("cards", c.Id.Entry + ".description"),
            };
            if (starCost > 0)
            {
                cardInfo["star_cost"] = starCost;
                // BUG-007: Override can_play for star-cost cards when player lacks stars
                if (pcs != null && pcs.Stars < starCost)
                    cardInfo["can_play"] = false;
            }
            var kws = c.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
            if (kws?.Count > 0) cardInfo["keywords"] = kws;
            if (c.Enchantment != null)
            {
                cardInfo["enchantment"] = _loc.Bilingual("enchantments", c.Enchantment.Id.Entry + ".title");
                try { if (c.Enchantment.Amount != 0) cardInfo["enchantment_amount"] = c.Enchantment.Amount; } catch { }
            }
            if (c.Affliction != null)
            {
                cardInfo["affliction"] = _loc.Bilingual("afflictions", c.Affliction.Id.Entry + ".title");
                try { if (c.Affliction.Amount != 0) cardInfo["affliction_amount"] = c.Affliction.Amount; } catch { }
            }
            return cardInfo;
        }).ToList() ?? new();

        var playerCreatures = combatState?.PlayerCreatures?.ToList();

        var enemies = combatState?.Enemies?
            .Where(e => e != null && e.IsAlive)
            .Select((e, i) =>
            {
                // Extract detailed intent info
                var intents = new List<Dictionary<string, object?>>();
                try
                {
                    if (e.Monster?.NextMove?.Intents != null)
                    {
                        foreach (var intent in e.Monster.NextMove.Intents)
                        {
                            var intentInfo = new Dictionary<string, object?>
                            {
                                ["type"] = intent.IntentType.ToString(),
                            };
                            // Get damage for attack intents
                            if (intent is MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent atk && playerCreatures != null)
                            {
                                try
                                {
                                    intentInfo["damage"] = atk.GetTotalDamage(playerCreatures, e);
                                    if (atk.Repeats > 1) intentInfo["hits"] = atk.Repeats;
                                }
                                catch { }
                            }
                            intents.Add(intentInfo);
                        }
                    }
                }
                catch { }

                // Enemy powers
                var ePowers = e.Powers?.Select(pw => new Dictionary<string, object?>
                {
                    ["name"] = _loc.Power(pw.Id.Entry),
                    ["description"] = _loc.Bilingual("powers", pw.Id.Entry + ".description"),
                    ["amount"] = pw.Amount,
                }).ToList();

                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["name"] = _loc.Monster(e.Monster?.Id.Entry ?? "UNKNOWN"),
                    ["hp"] = e.CurrentHp,
                    ["max_hp"] = e.MaxHp,
                    ["block"] = e.Block,
                    ["intents"] = intents.Count > 0 ? intents : null,
                    ["intends_attack"] = e.Monster?.IntendsToAttack ?? false,
                    ["powers"] = ePowers?.Count > 0 ? ePowers : null,
                };
            }).ToList() ?? new();

        // Player powers/buffs
        var playerPowers = player.Creature?.Powers?.Select(pw => new Dictionary<string, object?>
        {
            ["name"] = _loc.Power(pw.Id.Entry),
            ["description"] = _loc.Bilingual("powers", pw.Id.Entry + ".description"),
            ["amount"] = pw.Amount,
        }).ToList();

        var result = new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "combat_play",
            ["context"] = RunContext(),
            ["round"] = combatState?.RoundNumber ?? 0,
            ["energy"] = pcs?.Energy ?? 0,
            ["max_energy"] = pcs?.MaxEnergy ?? 0,
            ["hand"] = hand,
            ["enemies"] = enemies,
            ["player"] = PlayerSummary(player),
            ["player_powers"] = playerPowers?.Count > 0 ? playerPowers : null,
            ["draw_pile_count"] = pcs?.DrawPile?.Cards?.Count ?? 0,
            ["discard_pile_count"] = pcs?.DiscardPile?.Cards?.Count ?? 0,
        };

        // Character-specific mechanics
        try
        {
            // Defect: Orbs
            var orbQueue = pcs?.OrbQueue;
            if (orbQueue?.Orbs?.Count > 0)
            {
                result["orbs"] = orbQueue.Orbs.Select((orb, i) => new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["name"] = _loc.Bilingual("orbs", orb.Id.Entry + ".title"),
                    ["type"] = orb.GetType().Name.Replace("Orb", ""),
                    ["passive"] = (int)orb.PassiveVal,
                    ["evoke"] = (int)orb.EvokeVal,
                }).ToList();
                result["orb_slots"] = orbQueue.Capacity;
            }

            // Regent: Stars
            if (pcs != null && pcs.Stars >= 0 && player.Character?.Id.Entry == "REGENT")
            {
                result["stars"] = pcs.Stars;
            }

            // Necrobinder: Osty (minion)
            var osty = player.Osty;
            if (osty != null)
            {
                result["osty"] = new Dictionary<string, object?>
                {
                    ["name"] = _loc.Monster(osty.Monster?.Id.Entry ?? "OSTY"),
                    ["hp"] = osty.CurrentHp,
                    ["max_hp"] = osty.MaxHp,
                    ["block"] = osty.Block,
                    ["alive"] = osty.IsAlive,
                };
            }
            else if (player.Character?.Id.Entry == "NECROBINDER")
            {
                result["osty"] = new Dictionary<string, object?> { ["alive"] = false };
            }
        }
        catch (Exception ex)
        {
            Log($"Character-specific data: {ex.Message}");
        }

        return result;
    }

    private Dictionary<string, object?> DetectRoomRewardsState(Player player, AbstractRoom room)
    {
        if (room == null)
        {
            Log("DetectRoomRewardsState: room is null, forcing to map");
            ForceToMap();
            return MapSelectState();
        }
        Log($"DetectRoomRewardsState: RoomType={room.GetType().Name}, Processed={_rewardsProcessed}");
        _syncCtx.Pump();

        // Generate rewards manually
        if (_pendingRewards == null && !_rewardsProcessed)
        {
            _goldBeforeCombat = player.Gold;
            try
            {
                var rewardsSet = new RewardsSet(player).WithRewardsFromRoom(room);
                var rewards = rewardsSet.GenerateWithoutOffering().GetAwaiter().GetResult();
                _syncCtx.Pump();

                if (rewards != null && rewards.Count > 0)
                {
                    _pendingRewards = rewards.ToList();
                    return RewardMenuState(player);
                }

                _pendingRewards = null;
            }
            catch (Exception ex) { Log($"Generate rewards: {ex.Message}"); }
        }

        if (_pendingRewards != null && _pendingRewards.Count > 0)
        {
            return RewardMenuState(player);
        }

        // No more pending rewards — proceed
        _pendingCardReward = null;
        _pendingRewards = null;
        _rewardsProcessed = true;

        if (room is CombatRoom combatRoom)
        {
            // Boss → next act
            if (combatRoom.RoomType == RoomType.Boss)
            {
                Log("Boss defeated, entering next act");
                try
                {
                    RunManager.Instance.EnterNextAct().GetAwaiter().GetResult();
                    _syncCtx.Pump();
                    if (_runState?.Map?.StartingMapPoint != null)
                    {
                        RunManager.Instance.EnterMapCoord(_runState.Map.StartingMapPoint.coord).GetAwaiter().GetResult();
                        _syncCtx.Pump();
                    }
                    WaitForActionExecutor();
                }
                catch (Exception ex) { Log($"EnterNextAct: {ex.Message}"); }
            }
        }

        ForceToMap();
        return MapSelectState();
    }

    private Dictionary<string, object?> CardRewardState(Player player, AbstractRoom? room)
    {
        if (_pendingCardReward == null)
            return DetectRoomRewardsState(player, room ?? _runState?.CurrentRoom!);

        if (_pendingCardReward.Cards == null)
        {
            Log("CardRewardState: Cards is NULL!");
            return Error("CardReward has no cards");
        }

        var cards = _pendingCardReward.Cards.Select((c, i) =>
        {
            var stats = GetDynamicVars(c.DynamicVars);
            return new Dictionary<string, object?>
            {
                ["index"] = i,
                ["id"] = c.Id.ToString(),
                ["name"] = _loc.Card(c.Id.Entry),
                ["cost"] = c.EnergyCost?.GetResolved() ?? 0,
                ["type"] = c.Type.ToString(),
                ["rarity"] = c.Rarity.ToString(),
                ["description"] = _loc.Bilingual("cards", c.Id.Entry + ".description"),
                ["stats"] = stats.Count > 0 ? stats : null,
                ["after_upgrade"] = GetUpgradedInfo(c),
            };
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "card_reward",
            ["context"] = RunContext(),
            ["cards"] = cards,
            ["can_skip"] = _pendingCardReward.CanSkip,
            ["gold_earned"] = _runState!.Players[0].Gold - _goldBeforeCombat,
            ["player"] = PlayerSummary(_runState!.Players[0]),
        };
    }

    private Dictionary<string, object?> RewardMenuState(Player player)
    {
        if (_pendingRewards == null || _pendingRewards.Count == 0)
            return DetectRoomRewardsState(player, _runState!.CurrentRoom!);

        var rewards = _pendingRewards.Select((r, i) =>
        {
            var summary = new Dictionary<string, object?>
            {
                ["index"] = i,
                ["type"] = r.GetType().Name
            };

            if (r is GoldReward gold)
            {
                summary["name"] = "Gold";
                summary["amount"] = GetProperty(gold, "Amount") ?? 0;
            }
            else if (r is MegaCrit.Sts2.Core.Rewards.RelicReward rel)
            {
                var model = GetProperty(rel, "ClaimedRelic") as RelicModel ?? GetField(rel, "_relic") as RelicModel;
                summary["name"] = model != null ? _loc.Relic(model.Id.Entry) : "Relic";
            }
            else if (r is MegaCrit.Sts2.Core.Rewards.PotionReward pot)
            {
                var model = GetProperty(pot, "Potion") as PotionModel;
                summary["name"] = model != null ? _loc.Potion(model.Id.Entry) : "Potion";
            }
            else if (r is CardReward)
            {
                summary["name"] = "Card Reward";
            }
            else
            {
                summary["name"] = r.GetType().Name;
            }

            return summary;
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "combat_rewards",
            ["context"] = RunContext(),
            ["rewards"] = rewards,
            ["player"] = PlayerSummary(player),
        };
    }

    private Dictionary<string, object?> DoTakeReward(Player player, Dictionary<string, object?>? args)
    {
        if (_pendingRewards == null || _pendingRewards.Count == 0)
            return Error("No rewards pending");
        if (args == null || !args.ContainsKey("index"))
            return Error("take_reward requires 'index'");

        int idx = Convert.ToInt32(args["index"]);
        if (idx < 0 || idx >= _pendingRewards.Count)
            return Error("Invalid reward index");

        var reward = _pendingRewards[idx];
        if (reward is CardReward cr)
        {
            _pendingCardReward = cr;
            return DetectDecisionPoint();
        }

        // Potion Reward Swap Logic
        if (reward is MegaCrit.Sts2.Core.Rewards.PotionReward pr && !player.HasOpenPotionSlots)
        {
            var model = GetProperty(pr, "Potion") as PotionModel;
            Log($"Potion slots full, prompting for swap for reward {idx} ({model?.Id.Entry ?? "Potion"})");
            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "potion_swap",
                ["reward_index"] = idx,
                ["potion_name"] = model != null ? _loc.Potion(model.Id.Entry) : "Potion",
                ["player"] = PlayerSummary(player)
            };
        }

        try
        {
            reward.OnSelectWrapper().GetAwaiter().GetResult();
            _syncCtx.Pump();
            _pendingRewards.RemoveAt(idx);
        }
        catch (Exception ex)
        {
            return Error($"Failed to collect reward: {ex.Message}");
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSwapPotion(Player player, Dictionary<string, object?>? args)
    {
        if (_pendingRewards == null || _pendingRewards.Count == 0)
            return Error("No rewards pending");
        if (args == null || !args.ContainsKey("reward_index") || !args.ContainsKey("potion_index"))
            return Error("swap_potion requires 'reward_index' and 'potion_index'");

        int rewardIdx = Convert.ToInt32(args["reward_index"]);
        int potionIdx = Convert.ToInt32(args["potion_index"]);

        if (rewardIdx < 0 || rewardIdx >= _pendingRewards.Count)
            return Error("Invalid reward index");

        // Cancel/Back
        if (potionIdx < 0)
        {
            Log("Swap cancelled, returning to rewards");
            return DetectDecisionPoint();
        }

        var reward = _pendingRewards[rewardIdx];
        if (reward is not MegaCrit.Sts2.Core.Rewards.PotionReward)
            return Error("Selected reward is not a potion reward");

        var potionSlots = player.PotionSlots;
        if (potionIdx >= potionSlots.Count)
            return Error("Invalid potion slot index");

        var potionToDiscard = potionSlots[potionIdx];
        if (potionToDiscard == null)
        {
            // Slot is actually empty, just take the reward
            Log($"Slot {potionIdx} is empty, collecting reward {rewardIdx} directly");
        }
        else
        {
            Log($"Swapping reward {rewardIdx} with potion in slot {potionIdx} ({potionToDiscard.Id.Entry})");
            try
            {
                MegaCrit.Sts2.Core.Commands.PotionCmd.Discard(potionToDiscard).GetAwaiter().GetResult();
                _syncCtx.Pump();
            }
            catch (Exception ex)
            {
                return Error($"Failed to discard potion: {ex.Message}");
            }
        }

        // Now take the reward
        try
        {
            reward.OnSelectWrapper().GetAwaiter().GetResult();
            _syncCtx.Pump();
            _pendingRewards.RemoveAt(rewardIdx);
        }
        catch (Exception ex)
        {
            return Error($"Failed to collect reward: {ex.Message}");
        }

        return DetectDecisionPoint();
    }

    private void ForceToMap()
    {
        try
        {
            RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
            _syncCtx.Pump();
        }
        catch { }

        if (_runState?.CurrentRoom is not MapRoom)
        {
            try { RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult(); _syncCtx.Pump(); }
            catch (Exception ex) { Log($"ForceToMap: {ex.Message}"); }
        }
    }

    private Dictionary<string, object?> EventChoiceState(EventRoom eventRoom)
    {
        var localEvent = RunManager.Instance.EventSynchronizer?.GetLocalEvent();
        _syncCtx.Pump();

        // If we already chose an event option, wait for it to settle (transitions, sub-actions)
        if (_eventOptionChosen)
        {
            Log("EventChoiceState: waiting for previous choice to settle...");
            for (int i = 0; i < 40; i++) // up to 2 seconds
            {
                _syncCtx.Pump();
                WaitForActionExecutor();
                localEvent = RunManager.Instance.EventSynchronizer?.GetLocalEvent();
                if (localEvent == null || localEvent.IsFinished) break;
                // If options count changed, it's a sign the state advanced
                if (localEvent.CurrentOptions?.Count != _lastEventOptionCount) break;
                // If we hit another card selection, stop waiting
                if (_cardSelector.HasPending || _cardSelector.HasPendingReward || _pendingBundles != null) break;
                
                Thread.Sleep(10);
            }
            _eventOptionChosen = false;
        }

        // If event is finished, check for rewards then proceed to map
        if (localEvent == null || localEvent.IsFinished)
        {
            Log($"Event {localEvent?.GetType().Name ?? "null"} finished, detecting rewards");
            return DetectRoomRewardsState(_runState!.Players[0], eventRoom);
        }

        var currentOptions = localEvent.CurrentOptions;
        if (currentOptions == null || currentOptions.Count == 0)
        {
            // Wait and retry in case options are still being generated
            for (int i = 0; i < 5; i++)
            {
                _syncCtx.Pump();
                Thread.Sleep(10);
                _syncCtx.Pump();
                currentOptions = localEvent.CurrentOptions;
                if (currentOptions != null && currentOptions.Count > 0) break;
            }
        }

        if (currentOptions == null || currentOptions.Count == 0)
        {
            Log($"Event {localEvent.GetType().Name} has no options, auto-skipping");
            try { RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult(); _syncCtx.Pump(); }
            catch { }
            return MapSelectState();
        }

        var options = currentOptions
            .Select((opt, i) =>
            {
                // Try to resolve title via loc tables
                string? title = null;
                if (opt.Title != null)
                {
                    var t = _loc.Bilingual(opt.Title.LocTable, opt.Title.LocEntryKey);
                    // Check if we actually found a translation (not just the key echoed back)
                    if (t != opt.Title.LocEntryKey)
                        title = t;
                }
                // Fallback: try to extract option ID from the key and look up as relic/card/potion
                if (title == null && opt.TextKey != null)
                {
                    // TextKey like "NEOW.pages.INITIAL.options.STONE_HUMIDIFIER" → extract "STONE_HUMIDIFIER"
                    var parts = opt.TextKey.Split('.');
                    var optionId = parts.Length > 0 ? parts[^1] : opt.TextKey;
                    // Try relic, then card, then just use the optionId
                    var relic = _loc.Relic(optionId);
                    if (relic != optionId + ".title")
                        title = relic;
                    else
                    {
                        var card = _loc.Card(optionId);
                        if (card != optionId + ".title")
                            title = card;
                        else
                            title = optionId.Replace("_", " ");
                    }
                }
                title ??= $"option_{i}";

                // Description: try loc table first
                string? optDesc = null;
                if (opt.Description != null && !string.IsNullOrEmpty(opt.Description.LocEntryKey))
                {
                    var d = _loc.Bilingual(opt.Description.LocTable, opt.Description.LocEntryKey);
                    if (d != opt.Description.LocEntryKey)
                        optDesc = d;
                }
                // Fallback: try relic/card description
                if (optDesc == null && opt.TextKey != null)
                {
                    var parts = opt.TextKey.Split('.');
                    var optionId = parts.Length > 0 ? parts[^1] : opt.TextKey;
                    var rd = _loc.Bilingual("relics", optionId + ".description");
                    if (rd != optionId + ".description")
                        optDesc = rd;
                }

                // Extract vars: try event's own DynamicVars first, then relic
                Dictionary<string, object?>? optVars = null;
                try
                {
                    // Event's DynamicVars (covers Gold, HpLoss, Heal, etc.)
                    if (localEvent.DynamicVars?.Values != null)
                    {
                        optVars = new Dictionary<string, object?>();
                        foreach (var dv in localEvent.DynamicVars.Values)
                            optVars[dv.Name] = (int)dv.BaseValue;
                    }
                }
                catch { }
                // Also try relic vars (for Neow options)
                if (opt.TextKey != null)
                {
                    try
                    {
                        var parts = opt.TextKey.Split('.');
                        var optionId = parts.Length > 0 ? parts[^1] : opt.TextKey;
                        var relicModel = ModelDb.GetById<RelicModel>(new ModelId("RELIC", optionId));
                        if (relicModel != null)
                        {
                            optVars ??= new Dictionary<string, object?>();
                            var mutable = relicModel.ToMutable();
                            foreach (var dv in mutable.DynamicVars.Values)
                                optVars[dv.Name] = (int)dv.BaseValue;
                        }
                    }
                    catch { }
                }

                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["title"] = title,
                    ["description"] = optDesc,
                    ["text_key"] = opt.TextKey,
                    ["is_locked"] = opt.IsLocked,
                    ["vars"] = optVars?.Count > 0 ? optVars : null,
                };
            }).ToList();

        // Resolve event name — try ancients table first (for Neow), then events
        var eventEntry = localEvent.Id?.Entry ?? localEvent.GetType().Name.ToUpperInvariant();
        var eventName = _loc.Bilingual("ancients", eventEntry + ".title");
        if (eventName == eventEntry + ".title")
            eventName = _loc.Event(eventEntry);

        // Resolve event description, suppress if key not found
        string? eventDesc = null;
        if (localEvent.Description != null)
        {
            var d = _loc.Bilingual(localEvent.Description.LocTable, localEvent.Description.LocEntryKey);
            if (d != localEvent.Description.LocEntryKey)
                eventDesc = d;
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "event_choice",
            ["context"] = RunContext(),
            ["event_name"] = eventName,
            ["description"] = eventDesc,
            ["options"] = options,
            ["player"] = PlayerSummary(_runState!.Players[0]),
        };
    }

    private Dictionary<string, object?> RestSiteState(RestSiteRoom restRoom)
    {
        var options = restRoom.Options;
        var player = _runState!.Players[0];

        if (options == null || options.Count == 0)
        {
            // Options empty = choice already made (synchronizer cleared them)
            // Fall through to rewards detection
            return DetectRoomRewardsState(player, restRoom);
        }

        var optionList = options.Select((opt, i) => new Dictionary<string, object?>
        {
            ["index"] = i,
            ["option_id"] = opt.OptionId,
            ["name"] = opt.GetType().Name,
            ["is_enabled"] = opt.IsEnabled,
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "rest_site",
            ["context"] = RunContext(),
            ["options"] = optionList,
            ["player"] = PlayerSummary(player),
        };
    }

    private Dictionary<string, object?> ShopState(MerchantRoom merchantRoom, Player player)
    {
        var inv = merchantRoom.Inventory;
        if (inv == null) { ForceToMap(); return MapSelectState(); }

        var cards = inv.CharacterCardEntries.Concat(inv.ColorlessCardEntries)
            .Select((e, i) =>
            {
                var card = e.CreationResult?.Card;
                var entry = card?.Id.Entry ?? "?";
                var stats = GetDynamicVars(card?.DynamicVars);
                int cardCost = 0;
                try { if (card != null) cardCost = card.EnergyCost?.GetResolved() ?? 0; } catch { }

                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["name"] = _loc.Card(entry),
                    ["type"] = card?.Type.ToString() ?? "?",
                    ["card_cost"] = cardCost,
                    ["description"] = _loc.Bilingual("cards", entry + ".description"),
                    ["stats"] = stats.Count > 0 ? stats : null,
                    ["after_upgrade"] = card != null ? GetUpgradedInfo(card) : null,
                    ["cost"] = e.Cost,
                    ["is_stocked"] = e.IsStocked,
                    ["on_sale"] = e.IsOnSale,
                };
            }).ToList();

        var relics = inv.RelicEntries.Select((e, i) =>
        {
            var r = e.Model;
            var rvars = GetDynamicVars(r?.DynamicVars);
            return new Dictionary<string, object?>
            {
                ["index"] = i,
                ["name"] = _loc.Relic(r?.Id.Entry ?? "?"),
                ["description"] = _loc.Bilingual("relics", (r?.Id.Entry ?? "?") + ".description"),
                ["vars"] = rvars.Count > 0 ? rvars : null,
                ["cost"] = e.Cost,
                ["is_stocked"] = e.IsStocked,
            };
        }).ToList();

        var potions = inv.PotionEntries.Select((e, i) =>
        {
            var p = e.Model;
            var pvars = GetDynamicVars(p?.DynamicVars);
            return new Dictionary<string, object?>
            {
                ["index"] = i,
                ["name"] = _loc.Potion(p?.Id.Entry ?? "?"),
                ["description"] = _loc.Bilingual("potions", (p?.Id.Entry ?? "?") + ".description"),
                ["vars"] = pvars.Count > 0 ? pvars : null,
                ["cost"] = e.Cost,
                ["is_stocked"] = e.IsStocked,
            };
        }).ToList();

        var removal = merchantRoom.Inventory.CardRemovalEntry;

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "shop",
            ["context"] = RunContext(),
            ["cards"] = cards,
            ["relics"] = relics,
            ["potions"] = potions,
            ["card_removal_cost"] = removal?.Cost,
            ["player"] = PlayerSummary(player),
        };
    }

    private Dictionary<string, object?> TreasureState(TreasureRoom treasureRoom)
    {
        Log("Treasure room — collecting rewards");
        var player = _runState!.Players[0];

        // Ensure we wait for any entry actions
        WaitForActionExecutor();
        _syncCtx.Pump();

        try
        {
            Log($"Treasure room logic: RoomType={treasureRoom.RoomType}");
            
            // Try standard RewardsSet BEFORE DoNormalRewards
            var rewardsSet = new MegaCrit.Sts2.Core.Rewards.RewardsSet(player).WithRewardsFromRoom(treasureRoom);
            var rewards = rewardsSet.GenerateWithoutOffering().GetAwaiter().GetResult();
            _syncCtx.Pump();

            Log($"RewardsSet generated {rewards.Count} rewards");

            if (rewards.Count == 0)
            {
                // Fallback: trigger the room's normal reward logic
                treasureRoom.DoNormalRewards().GetAwaiter().GetResult();
                _syncCtx.Pump();
                Thread.Sleep(10);
                _syncCtx.Pump();

                // Try again
                rewardsSet = new MegaCrit.Sts2.Core.Rewards.RewardsSet(player).WithRewardsFromRoom(treasureRoom);
                rewards = rewardsSet.GenerateWithoutOffering().GetAwaiter().GetResult();
                _syncCtx.Pump();
                Log($"RewardsSet (retry) generated {rewards.Count} rewards");
            }

            foreach (var reward in rewards)
            {
                Log($"Collecting treasure reward: {reward.GetType().Name}");
                try
                {
                    reward.OnSelectWrapper().GetAwaiter().GetResult();
                    _syncCtx.Pump();
                }
                catch (Exception ex) { Log($"Failed to collect reward: {ex.Message}"); }
            }

            // Final safety check: synchronizer relics
            var synchronizer = RunManager.Instance.TreasureRoomRelicSynchronizer;
            if (synchronizer != null && synchronizer.CurrentRelics != null && synchronizer.CurrentRelics.Count > 0)
            {
                foreach (var r in synchronizer.CurrentRelics.ToList())
                {
                    if (!player.Relics.Any(pr => pr.Id == r.Id))
                    {
                        Log($"Granting missing relic from synchronizer: {r.Id}");
                        try
                        {
                            var newModel = MegaCrit.Sts2.Core.Models.ModelDb.GetById<MegaCrit.Sts2.Core.Models.RelicModel>(r.Id).ToMutable();
                            var relReward = new MegaCrit.Sts2.Core.Rewards.RelicReward(newModel, player);
                            relReward.OnSelectWrapper().GetAwaiter().GetResult();
                            _syncCtx.Pump();
                        }
                        catch (Exception ex)
                        {
                            Log($"Failed to grant relic from synchronizer: {ex.Message}");
                            // Last ditch effort: manual sync
                            RunManager.Instance.RewardSynchronizer.SyncLocalObtainedRelic(r);
                            _syncCtx.Pump();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Treasure rewards error: {ex.Message}");
        }

        ForceToMap();
        return MapSelectState();
    }

    private Dictionary<string, object?> GameOverState(bool isVictory)
    {
        var player = _runState!.Players[0];
        var summary = PlayerSummary(player);
        // BUG-005: When player died, the engine resets HP to max. Use last known HP instead.
        if (!isVictory)
            summary["hp"] = _lastKnownHp > 0 ? 0 : (player.Creature?.CurrentHp ?? 0);
        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "game_over",
            ["context"] = RunContext(),
            ["victory"] = isVictory,
            ["player"] = summary,
            ["act"] = _runState.CurrentActIndex + 1,
            ["floor"] = _runState.ActFloor,
        };
    }

    #endregion

    #region Helpers

    private void WaitForActionExecutor()
    {
        YieldPatches.SuppressYield = true;
        try
        {
            // Ensure sync context is set for this thread
            SynchronizationContext.SetSynchronizationContext(_syncCtx);

            // Pump the synchronization context to execute any pending continuations
            _syncCtx.Pump();

            var executor = RunManager.Instance.ActionExecutor;
            if (executor.IsRunning)
            {
                // Pump while waiting for executor (up to 5 seconds)
                int maxPumps = 1000;
                for (int i = 0; i < maxPumps; i++)
                {
                    _syncCtx.Pump();
                    if (!executor.IsRunning) break;
                    if (_cardSelector.HasPending || _cardSelector.HasPendingReward || _pendingBundles != null) break;
                    Thread.Sleep(5);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"WaitForActionExecutor exception: {ex.Message}");
        }
        finally
        {
            YieldPatches.SuppressYield = false;
        }
    }

    private void SpinWaitForCombatStable()
    {
        int maxIterations = 200;
        for (int i = 0; i < maxIterations; i++)
        {
            _syncCtx.Pump();
            if (!CombatManager.Instance.IsInProgress) return;
            if (CombatManager.Instance.IsPartOfPlayerTurn(_runState!.Players[0])) return;
            WaitForActionExecutor();
            if (CombatManager.Instance.IsPartOfPlayerTurn(_runState!.Players[0]) || !CombatManager.Instance.IsInProgress) return;
            Thread.Sleep(5);
        }
    }

    /// <summary>Compute what a card would look like after upgrading (stats + cost + description).</summary>
    private Dictionary<string, object?> GetUpgradedInfo(CardModel card)
    {
        if (card == null || !card.IsUpgradable) return null;
        try
        {
            var clone = ModelDb.GetById<CardModel>(card.Id).ToMutable();
            // Apply existing upgrades
            for (int i = 0; i < card.CurrentUpgradeLevel; i++)
            {
                clone.UpgradeInternal();
                clone.FinalizeUpgradeInternal();
            }
            // Apply the next upgrade
            clone.UpgradeInternal();
            clone.FinalizeUpgradeInternal();

            var stats = GetDynamicVars(clone.DynamicVars);
            return new Dictionary<string, object?>
            {
                ["card_cost"] = clone.EnergyCost?.GetResolved() ?? 0,
                ["description"] = _loc.Bilingual("cards", card.Id.Entry + ".description"),
                ["stats"] = stats.Count > 0 ? stats : null,
            };
        }
        catch { return null; }
    }

    private Dictionary<string, object?> GetDynamicVars(DynamicVarSet? set)
    {
        var dict = new Dictionary<string, object?>();
        if (set == null) return dict;

        try
        {
            // 1. Core dictionary values
            foreach (var dv in set.Values)
            {
                if (dv == null) continue;
                dict[dv.Name.ToLowerInvariant()] = dv.IntValue;
            }

            // 2. Ensure hardcoded properties are included (important for CalculatedDamage etc.)
            if (set.CalculatedDamage != null) dict["calculateddamage"] = set.CalculatedDamage.IntValue;
            if (set.CalculatedBlock != null) dict["calculatedblock"] = set.CalculatedBlock.IntValue;
            if (set.Damage != null) dict["damage"] = set.Damage.IntValue;
            if (set.Block != null) dict["block"] = set.Block.IntValue;
            if (set.ExtraDamage != null) dict["extradamage"] = set.ExtraDamage.IntValue;
            if (set.Heal != null) dict["heal"] = set.Heal.IntValue;
            if (set.HpLoss != null) dict["hploss"] = set.HpLoss.IntValue;
            if (set.Cards != null) dict["cards"] = set.Cards.IntValue;
            if (set.Repeat != null) dict["repeat"] = set.Repeat.IntValue;
            if (set.Poison != null) dict["poison"] = set.Poison.IntValue;
            if (set.Strength != null) dict["strength"] = set.Strength.IntValue;
            if (set.Dexterity != null) dict["dexterity"] = set.Dexterity.IntValue;
            if (set.Vulnerable != null) dict["vulnerable"] = set.Vulnerable.IntValue;
            if (set.Weak != null) dict["weak"] = set.Weak.IntValue;
            if (set.Stars != null) dict["stars"] = set.Stars.IntValue;
            if (set.Gold != null) dict["gold"] = set.Gold.IntValue;
        }
        catch { }

        return dict;
    }

    private Dictionary<string, object?> PlayerSummary(Player player)
    {
        return new Dictionary<string, object?>
        {
            ["name"] = _loc.Bilingual("characters", (player.Character?.Id.Entry ?? "IRONCLAD") + ".title"),
            ["hp"] = player.Creature?.CurrentHp ?? 0,
            ["max_hp"] = player.Creature?.MaxHp ?? 0,
            ["block"] = player.Creature?.Block ?? 0,
            ["gold"] = player.Gold,
            ["relics"] = player.Relics?.Select(r =>
            {
                var vars = GetDynamicVars(r?.DynamicVars);
                // Extract optional counter/amount (some relics use 'Amount', some 'Counter', some 'Value')
                var counter = GetProperty(r, "Amount") ?? GetProperty(r, "Counter") ?? GetProperty(r, "Value");

                return new Dictionary<string, object?>
                {
                    ["name"] = _loc.Relic(r.Id.Entry),
                    ["description"] = _loc.Bilingual("relics", r.Id.Entry + ".description"),
                    ["vars"] = vars.Count > 0 ? vars : null,
                    ["counter"] = counter,
                };
            }).ToList(),
            ["potions"] = player.PotionSlots?.Select((p, i) =>
            {
                if (p == null) return new Dictionary<string, object?> { ["index"] = i, ["name"] = null };
                var pvars = GetDynamicVars(p?.DynamicVars);
                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["name"] = _loc.Potion(p.Id.Entry),
                    ["description"] = _loc.Bilingual("potions", p.Id.Entry + ".description"),
                    ["vars"] = pvars.Count > 0 ? pvars : null,
                    ["target_type"] = p.TargetType.ToString(),
                };
            }).ToList(),
            ["deck_size"] = player.Deck?.Cards?.Count(c => c != null) ?? 0,
            ["deck"] = player.Deck?.Cards?.Where(c => c != null).Select(c =>
            {
                var dstats = GetDynamicVars(c?.DynamicVars);
                var dkws = c.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
                return new Dictionary<string, object?>
                {
                    ["id"] = c.Id.ToString(),
                    ["name"] = _loc.Card(c.Id.Entry),
                    ["cost"] = c.EnergyCost?.GetResolved() ?? 0,
                    ["type"] = c.Type.ToString(),
                    ["upgraded"] = c.IsUpgraded,
                    ["description"] = _loc.Bilingual("cards", c.Id.Entry + ".description"),
                    ["stats"] = dstats.Count > 0 ? dstats : null,
                    ["keywords"] = dkws?.Count > 0 ? dkws : null,
                    ["after_upgrade"] = GetUpgradedInfo(c),
                };
            }).ToList(),
        };
    }

    /// <summary>Common context added to every decision point.</summary>
    private Dictionary<string, object?> RunContext()
    {
        if (_runState == null) return new();
        var ctx = new Dictionary<string, object?>
        {
            ["act"] = _runState.CurrentActIndex + 1,
            ["act_name"] = _loc.Act(_runState.Act?.Id.Entry ?? "OVERGROWTH"),
            ["floor"] = _runState.ActFloor,
            ["room_type"] = _runState.CurrentRoom?.RoomType.ToString(),
        };

        // Boss encounter info — use BossEncounter?.Id?.Entry
        try
        {
            var bossIdEntry = _runState.Act?.BossEncounter?.Id?.Entry;
            if (!string.IsNullOrEmpty(bossIdEntry))
            {
                var monsterKey = bossIdEntry.EndsWith("_BOSS") ? bossIdEntry[..^5] : bossIdEntry;
                // Handle special mappings
                if (monsterKey == "THE_KIN") monsterKey = "KIN_PRIEST";
                ctx["boss"] = new Dictionary<string, object?>
                {
                    ["id"] = bossIdEntry,
                    ["name"] = _loc.Monster(monsterKey),
                };
            }
        }
        catch { }

        return ctx;
    }

    private static void EnsureModelDbInitialized()
    {
        if (_modelDbInitialized) return;
        _modelDbInitialized = true;

        TestMode.IsOn = true;

        // Install inline sync context on main thread
        SynchronizationContext.SetSynchronizationContext(_syncCtx);

        // Initialize PlatformServices before anything touches PlatformUtil
        try
        {
            // Try to access PlatformUtil to trigger its static init
            // If it fails, it won't be available but most code checks SteamInitializer.Initialized
            var _ = MegaCrit.Sts2.Core.Platform.PlatformUtil.PrimaryPlatform;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] PlatformUtil init: {ex.Message}");
        }

        // Initialize SaveManager with a dummy profile for save/load support
        try { SaveManager.Instance.InitProfileId(0); }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] SaveManager.InitProfileId: {ex.Message}"); }

        // Initialize progress data for epoch/timeline tracking
        try { SaveManager.Instance.InitProgressData(); }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] InitProgressData: {ex.Message}"); }

        // Install the Task.Yield patch but keep SuppressYield=false by default.
        // SuppressYield is toggled to true only during EndTurn to prevent boss fight deadlocks.
        PatchTaskYield();

        // Patch Cmd.Wait to be a no-op in headless mode.
        // Cmd.Wait(duration) is used for UI animations (e.g., PreviewCardPileAdd during
        // Vantom's Dismember move adding Wounds). In headless mode, these never complete
        // because there's no Godot scene tree, causing the ActionExecutor to deadlock.
        PatchCmdWait();

        // Patch UI-only commands that crash in headless mode (e.g. Bygone Effigy speech bubbles)
        PatchUICommands();

        // Initialize localization system (needed for events, cards, etc.)
        InitLocManager();

        var subtypes = MegaCrit.Sts2.Core.Models.AbstractModelSubtypes.All;
        int registered = 0, failed = 0;
        for (int i = 0; i < subtypes.Count; i++)
        {
            try
            {
                ModelDb.Inject(subtypes[i]);
                registered++;
            }
            catch (Exception ex)
            {
                failed++;
                // Only log first few failures to reduce noise
                if (failed <= 5)
                    Console.Error.WriteLine($"[WARN] Failed to register {subtypes[i].Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        // Console.Error.WriteLine($"[INFO] ModelDb: {registered} registered, {failed} failed out of {subtypes.Count}");

        // Initialize net ID serialization cache (needed for combat actions)
        try
        {
            ModelIdSerializationCache.Init();
            // Console.Error.WriteLine("[INFO] ModelIdSerializationCache initialized");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] ModelIdSerializationCache.Init: {ex.Message}");
        }
    }

    private Player? CreatePlayer(string characterName)
    {
        return characterName.ToLowerInvariant() switch
        {
            "ironclad" => Player.CreateForNewRun<Ironclad>(UnlockState.all, 1uL),
            "silent" => Player.CreateForNewRun<Silent>(UnlockState.all, 1uL),
            "defect" => Player.CreateForNewRun<Defect>(UnlockState.all, 1uL),
            "regent" => Player.CreateForNewRun<Regent>(UnlockState.all, 1uL),
            "necrobinder" => Player.CreateForNewRun<Necrobinder>(UnlockState.all, 1uL),
            _ => null
        };
    }

    private static void PatchCmdWait()
    {
        try
        {
            var harmony = new Harmony("sts2headless.cmdwait");
            // Find Cmd.Wait(float) — it's in MegaCrit.Sts2.Core.Commands namespace
            // Find Cmd type via CardPileCmd's assembly (both are in same namespace)
            var cmdPileType = typeof(MegaCrit.Sts2.Core.Commands.CardPileCmd);
            var cmdAsm = cmdPileType.Assembly;
            Type? cmdType = cmdAsm.GetType("MegaCrit.Sts2.Core.Commands.Cmd");
            // If not found by exact name, search by namespace + "Wait" method
            if (cmdType == null)
            {
                foreach (var t in cmdAsm.GetTypes())
                {
                    if (t.Namespace == "MegaCrit.Sts2.Core.Commands")
                    {
                        var waitM = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
                            .Where(m => m.Name == "Wait").ToList();
                        if (waitM.Count > 0)
                        {
                            cmdType = t;
                            Console.Error.WriteLine($"[INFO] Found Wait() in {t.FullName}");
                            break;
                        }
                    }
                }
            }
            if (cmdType != null)
            {
                var waitMethod = cmdType.GetMethod("Wait",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(float) }, null);
                if (waitMethod != null)
                {
                    var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.CmdWaitPrefix),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    if (prefix != null)
                    {
                        harmony.Patch(waitMethod, new HarmonyMethod(prefix));
                        Console.Error.WriteLine("[INFO] Patched Cmd.Wait() to no-op (prevents boss fight deadlocks)");
                    }
                }
                else
                {
                    // Try to find any Wait method
                    var methods = cmdType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                        .Where(m => m.Name == "Wait").ToList();
                    foreach (var m in methods)
                    {
                        Console.Error.WriteLine($"[INFO] Found Cmd.Wait({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");
                        var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.CmdWaitPrefix),
                            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                        if (prefix != null)
                        {
                            harmony.Patch(m, new HarmonyMethod(prefix));
                            // Console.Error.WriteLine($"[INFO] Patched Cmd.Wait variant");
                        }
                    }
                }
            }
            else
            {
                Console.Error.WriteLine("[WARN] Could not find MegaCrit.Sts2.Core.Commands.Cmd type");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch Cmd.Wait: {ex.Message}");
        }
    }

    private static void PatchUICommands()
    {
        try
        {
            var harmony = new Harmony("sts2headless.uicommands");
            var cmdAsm = typeof(MegaCrit.Sts2.Core.Commands.CardPileCmd).Assembly;
            
            var taskPrefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.CmdWaitPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            var voidPrefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.VoidPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            var nullPrefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.NullPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            
            if (taskPrefix == null || voidPrefix == null || nullPrefix == null) return;

            string[] cmdClasses = { "TalkCmd", "ScreenShakeCmd", "VfxCmd", "SoundCmd", "MusicCmd" };

            foreach (var className in cmdClasses)
            {
                var type = cmdAsm.GetType($"MegaCrit.Sts2.Core.Commands.{className}");
                if (type == null) continue;

                var playMethods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    .Where(m => m.Name == "Play").ToList();
                
                int count = 0;
                foreach (var m in playMethods)
                {
                    try
                    {
                        if (m.ReturnType == typeof(void))
                        {
                            harmony.Patch(m, new HarmonyMethod(voidPrefix));
                        }
                        else if (m.ReturnType == typeof(Task))
                        {
                            harmony.Patch(m, new HarmonyMethod(taskPrefix));
                        }
                        else if (!m.ReturnType.IsValueType)
                        {
                            harmony.Patch(m, new HarmonyMethod(nullPrefix));
                        }
                        else
                        {
                            // For value types, we'd need a specific prefix or just let it crash if it's rare.
                            // But most UI commands return Task or Vfx object.
                            continue;
                        }
                        count++;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[WARN] Failed to patch {className}.{m.Name}: {ex.Message}");
                    }
                }
                if (count > 0)
                    Console.Error.WriteLine($"[INFO] Patched {count} {className}.Play methods");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch UI commands: {ex.Message}");
        }
    }

    private static void PatchTaskYield()
    {
        try
        {
            var harmony = new Harmony("sts2headless.yieldpatch");

            // Patch YieldAwaitable.YieldAwaiter.IsCompleted to return true
            // This makes `await Task.Yield()` execute synchronously (continuation runs inline)
            var yieldAwaiterType = typeof(System.Runtime.CompilerServices.YieldAwaitable)
                .GetNestedType("YieldAwaiter");
            if (yieldAwaiterType != null)
            {
                var isCompletedProp = yieldAwaiterType.GetProperty("IsCompleted");
                if (isCompletedProp != null)
                {
                    var getter = isCompletedProp.GetGetMethod();
                    var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.IsCompletedPrefix),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    if (getter != null && prefix != null)
                    {
                        harmony.Patch(getter, new HarmonyMethod(prefix));
                        // Console.Error.WriteLine("[INFO] Patched Task.Yield() to be synchronous");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch Task.Yield: {ex.Message}");
        }
    }

    /// <summary>
    /// Card selector for headless mode — picks first available card for any selection prompt.
    /// Used by cards like Headbutt, Armaments, etc. that need player to choose a card.
    /// </summary>
    /// <summary>
    /// Card selector that creates a pending selection decision point.
    /// When the game needs the player to choose cards (upgrade, remove, transform, bundle pick),
    /// this stores the options and waits for the main loop to provide the answer.
    /// </summary>
    internal class HeadlessCardSelector : MegaCrit.Sts2.Core.TestSupport.ICardSelector
    {
        // Pending card selection — set by game engine, read by main loop
        public List<CardModel>? PendingOptions { get; private set; }
        public int PendingMinSelect { get; private set; }
        public int PendingMaxSelect { get; private set; }
        public string PendingPrompt { get; private set; } = "";
        private TaskCompletionSource<IEnumerable<CardModel>>? _pendingTcs;

        public bool HasPending => _pendingTcs != null && !_pendingTcs.Task.IsCompleted;

        public Task<IEnumerable<CardModel>> GetSelectedCards(
            IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            var optList = options.ToList();
            if (optList.Count == 0)
                return Task.FromResult<IEnumerable<CardModel>>(Array.Empty<CardModel>());

            // If only one option and minSelect requires it, auto-select
            if (optList.Count == 1 && minSelect >= 1)
                return Task.FromResult<IEnumerable<CardModel>>(optList);

            // Store pending selection and wait
            PendingOptions = optList;
            PendingMinSelect = minSelect;
            PendingMaxSelect = maxSelect;
            _pendingTcs = new TaskCompletionSource<IEnumerable<CardModel>>();

            // Console.Error.WriteLine($"[SIM] Card selection pending: {optList.Count} options, select {minSelect}-{maxSelect}");

            // Return the task — the main loop will complete it
            return _pendingTcs.Task;
        }

        public void ResolvePending(IEnumerable<CardModel> selected)
        {
            _pendingTcs?.TrySetResult(selected);
            PendingOptions = null;
            _pendingTcs = null;
        }

        public void ResolvePendingByIndices(int[] indices)
        {
            if (PendingOptions == null) return;
            var selected = indices
                .Where(i => i >= 0 && i < PendingOptions.Count)
                .Select(i => PendingOptions[i])
                .ToList();
            ResolvePending(selected);
        }

        public void CancelPending()
        {
            _pendingTcs?.TrySetResult(Array.Empty<CardModel>());
            PendingOptions = null;
            _pendingTcs = null;
        }

        // Pending card reward from events (GetSelectedCardReward blocks until resolved)
        public List<MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult>? PendingRewardCards { get; private set; }
        private ManualResetEventSlim? _rewardWait;
        private int _rewardChoice = -1;

        public CardModel? GetSelectedCardReward(
            IReadOnlyList<MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives)
        {
            if (options.Count == 0) return null;

            // Store pending and block until main loop resolves
            PendingRewardCards = options.ToList();
            _rewardChoice = -1;
            _rewardWait = new ManualResetEventSlim(false);

            // Console.Error.WriteLine($"[SIM] Card reward pending: {options.Count} cards (blocking)");
            _rewardWait.Wait(TimeSpan.FromSeconds(300)); // Wait up to 5 min

            var choice = _rewardChoice;
            PendingRewardCards = null;
            _rewardWait = null;

            if (choice >= 0 && choice < options.Count)
                return options[choice].Card;
            return null;  // Skip
        }

        public bool HasPendingReward => PendingRewardCards != null && _rewardWait != null;

        public void ResolveReward(int index)
        {
            _rewardChoice = index;
            _rewardWait?.Set();
        }

        public void SkipReward()
        {
            _rewardChoice = -1;
            _rewardWait?.Set();
        }
    }

    internal static class YieldPatches
    {
        // Only suppress Task.Yield() when this flag is set (during end_turn processing)
        public static volatile bool SuppressYield;

        public static bool IsCompletedPrefix(ref bool __result)
        {
            if (SuppressYield)
            {
                __result = true;
                return false;
            }
            return true; // Let normal Yield behavior run
        }

        /// <summary>Harmony prefix: make Cmd.Wait() return completed task immediately (no-op in headless).</summary>
        public static bool CmdWaitPrefix(ref Task __result)
        {
            __result = Task.CompletedTask;
            return false; // Skip original method
        }

        /// <summary>Harmony prefix for methods that return void.</summary>
        public static bool VoidPrefix()
        {
            return false; // Skip original method
        }

        /// <summary>Harmony prefix for methods that return a reference type (returns null).</summary>
        public static bool NullPrefix(ref object __result)
        {
            __result = null;
            return false;
        }
    }

    private static void InitLocManager()
    {
        // Create a LocManager instance with stub tables via reflection.
        // LocManager.Initialize() fails because PlatformUtil isn't available,
        // and Harmony can't patch some LocString methods due to JIT issues.
        // Solution: create an uninitialized LocManager, set its _tables, and
        // use Harmony only for the simple LocTable.GetRawText fallback.
        try
        {
            // Create uninitialized LocManager and set Instance
            var instanceProp = typeof(LocManager).GetProperty("Instance",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LocManager));
            instanceProp!.SetValue(null, instance);

            // Load REAL localization data from localization_eng/ JSON files
            var tablesField = typeof(LocManager).GetField("_tables",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var tables = new Dictionary<string, LocTable>();

            var locDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "localization_eng");
            if (Directory.Exists(locDir))
            {
                foreach (var file in Directory.GetFiles(locDir, "*.json"))
                {
                    try
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                            File.ReadAllText(file));
                        if (data != null)
                            tables[name] = new LocTable(name, data);
                    }
                    catch { }
                }
                Console.Error.WriteLine($"[INFO] Loaded {tables.Count} localization tables from {locDir}");
            }
            else
            {
                Console.Error.WriteLine($"[WARN] Localization dir not found: {locDir}");
                // Fallback: empty tables
                var tableNames = new[] {
                    "achievements","acts","afflictions","ancients","ascension",
                    "bestiary","card_keywords","card_library","card_reward_ui",
                    "card_selection","cards","characters","combat_messages",
                    "credits","enchantments","encounters","epochs","eras",
                    "events","ftues","game_over_screen","gameplay_ui",
                    "inspect_relic_screen","intents","main_menu_ui","map",
                    "merchant_room","modifiers","monsters","orbs","potion_lab",
                    "potions","powers","relic_collection","relics","rest_site_ui",
                    "run_history","settings_ui","static_hover_tips","stats_screen",
                    "timeline","vfx"
                };
                foreach (var name in tableNames)
                    tables[name] = new LocTable(name, new Dictionary<string, string>());
            }
            tablesField!.SetValue(instance, tables);

            // Set Language
            var langProp = typeof(LocManager).GetProperty("Language",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            try { langProp?.SetValue(instance, "eng"); } catch { }

            // Set CultureInfo
            var cultureProp = typeof(LocManager).GetProperty("CultureInfo",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            try { cultureProp?.SetValue(instance, System.Globalization.CultureInfo.InvariantCulture); } catch { }

            // Initialize _smartFormatter — the game uses `new SmartFormatter()`
            try
            {
                var sfField = typeof(LocManager).GetField("_smartFormatter",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                // Dump ALL fields (instance + static)
                foreach (var f in typeof(LocManager).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                    // Console.Error.WriteLine($"[DEBUG] LocManager {(f.IsStatic?"static":"inst")} field: {f.Name} ({f.FieldType.Name})");
                // Console.Error.WriteLine($"[DEBUG] sfField: {sfField?.Name ?? "null"} type: {sfField?.FieldType?.Name ?? "null"}");
                if (sfField != null)
                {
                    try
                    {
                        // List constructors to find the right one
                        var ctors = sfField.FieldType.GetConstructors(
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        // Console.Error.WriteLine($"[DEBUG] SmartFormatter has {ctors.Length} constructors:");
                        foreach (var ctor in ctors)
                        {
                            var ps = ctor.GetParameters();
                                // Console.Error.WriteLine($"  ({string.Join(", ", ps.Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
                        }
                        // Try the one with fewest params
                        var bestCtor = ctors.OrderBy(c => c.GetParameters().Length).First();
                        var args2 = bestCtor.GetParameters().Select(p =>
                            p.HasDefaultValue ? p.DefaultValue :
                            p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null
                        ).ToArray();
                        var sf = bestCtor.Invoke(args2);
                        // Register extensions using the game's own LoadLocFormatters logic
                        // Call it via reflection on LocManager instance
                        try
                        {
                            var loadMethod = typeof(LocManager).GetMethod("LoadLocFormatters",
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                            if (loadMethod != null)
                            {
                                loadMethod.Invoke(instance, null);
                                // Console.Error.WriteLine("[INFO] SmartFormatter initialized via LoadLocFormatters");
                            }
                            else
                            {
                                sfField.SetValue(null, sf);
                                Console.Error.WriteLine("[INFO] SmartFormatter set (no LoadLocFormatters found)");
                            }
                        }
                        catch (Exception lfEx)
                        {
                            sfField.SetValue(null, sf);
                            Console.Error.WriteLine($"[WARN] LoadLocFormatters failed: {lfEx.InnerException?.Message ?? lfEx.Message}");
                        }
                    }
                    catch (Exception sfEx)
                    {
                        Console.Error.WriteLine($"[WARN] SmartFormatter create failed: {sfEx.GetType().Name}: {sfEx.Message}");
                        if (sfEx.InnerException != null)
                            Console.Error.WriteLine($"  Inner: {sfEx.InnerException.GetType().Name}: {sfEx.InnerException.Message}");
                    }
                }
                else
                {
                    Console.Error.WriteLine("[WARN] _smartFormatter field not found in LocManager");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] _smartFormatter init: {ex.GetType().Name}: {ex.Message}\n{ex.InnerException?.Message}"); }

            // Initialize _engTables to point to _tables (avoid null ref in fallback)
            try
            {
                var engTablesField = typeof(LocManager).GetField("_engTables",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                engTablesField?.SetValue(instance, tables);
            }
            catch { }

            // Console.Error.WriteLine("[INFO] LocManager initialized with stub tables");

            // Use Harmony to patch methods that need fallback behavior
            var harmony = new Harmony("sts2headless.locpatch");

            // With real loc data loaded, we only need fallback patches for:
            // 1. LocTable.GetRawText — return key for missing entries instead of throwing
            // 2. LocManager.SmartFormat — _smartFormatter is null, return raw text instead
            // We do NOT patch GetFormattedText/GetRawText on LocString anymore
            // so the real localization pipeline works (needed for Neow event etc.)

            var getRawText = typeof(LocTable).GetMethod("GetRawText",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                null, new[] { typeof(string) }, null);
            var prefix = typeof(LocPatches).GetMethod(nameof(LocPatches.GetRawTextPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (getRawText != null && prefix != null)
            {
                harmony.Patch(getRawText, new HarmonyMethod(prefix));
                // Console.Error.WriteLine("[INFO] Patched LocTable.GetRawText");
            }

            // Patch GetLocString to not throw
            var getLocString = typeof(LocTable).GetMethod("GetLocString");
            var glsPrefix = typeof(LocPatches).GetMethod(nameof(LocPatches.GetLocStringPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (getLocString != null && glsPrefix != null)
            {
                try { harmony.Patch(getLocString, new HarmonyMethod(glsPrefix)); }
                catch (Exception ex4) { Console.Error.WriteLine($"[WARN] Failed to patch GetLocString: {ex4.Message}"); }
            }

            // Patch FromChooseABundleScreen to use our card selector
            try
            {
                var bundleMethod = typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd).GetMethod("FromChooseABundleScreen",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                var bundlePrefix = typeof(LocPatches).GetMethod(nameof(LocPatches.BundleScreenPrefix),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                if (bundleMethod != null && bundlePrefix != null)
                {
                    harmony.Patch(bundleMethod, new HarmonyMethod(bundlePrefix));
                    // Console.Error.WriteLine("[INFO] Patched FromChooseABundleScreen");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] Bundle patch: {ex.Message}"); }

            // Patch Neutralize.OnPlay to avoid NullRef in DamageCmd.Attack().Execute()
            try
            {
                var neutralizeType = typeof(MegaCrit.Sts2.Core.Models.Cards.Neutralize);
                var neutralizeOnPlay = neutralizeType.GetMethod("OnPlay",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (neutralizeOnPlay != null)
                {
                    var neutPrefix = typeof(LocPatches).GetMethod(nameof(LocPatches.NeutralizePrefix),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    if (neutPrefix != null)
                    {
                        harmony.Patch(neutralizeOnPlay, new HarmonyMethod(neutPrefix));
                        // Console.Error.WriteLine("[INFO] Patched Neutralize.OnPlay");
                    }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] Neutralize patch: {ex.Message}"); }

            // Patch HasEntry to always return true
            PatchMethod(harmony, typeof(LocTable), "HasEntry", nameof(LocPatches.HasEntryPrefix));

            // Patch IsLocalKey to always return true
            PatchMethod(harmony, typeof(LocTable), "IsLocalKey", nameof(LocPatches.HasEntryPrefix));

            // Patch LocString.Exists (static) to always return true
            var locStringExists = typeof(LocString).GetMethod("Exists",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (locStringExists != null)
            {
                PatchMethod(harmony, locStringExists, nameof(LocPatches.HasEntryPrefix));
            }

            // Patch LocTable.GetLocStringsWithPrefix to return empty list
            PatchMethod(harmony, typeof(LocTable), "GetLocStringsWithPrefix", nameof(LocPatches.GetLocStringsWithPrefixPrefix));

            // Patch LocManager.GetTable to return stub instead of throwing
            PatchMethod(harmony, typeof(LocManager), "GetTable", nameof(LocPatches.GetTablePrefix));

            // Patch LocManager.SmartFormat to return raw text if formatter missing
            PatchMethod(harmony, typeof(LocManager), "SmartFormat", nameof(LocPatches.SmartFormatPrefix));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] InitLocManager failed: {ex.Message}");
        }
    }

    private static void PatchMethod(Harmony harmony, Type type, string methodName, string patchName)
    {
        try
        {
            var method = type.GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            PatchMethod(harmony, method, patchName);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] Failed to patch {type.Name}.{methodName}: {ex.Message}"); }
    }

    private static void PatchMethod(Harmony harmony, System.Reflection.MethodInfo? method, string patchName)
    {
        if (method == null) return;
        try
        {
            var prefix = typeof(LocPatches).GetMethod(patchName, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (prefix != null) harmony.Patch(method, new HarmonyMethod(prefix));
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] Failed to patch {method.Name}: {ex.Message}"); }
    }

    internal static class LocPatches
    {
        public static bool GetRawTextPrefix(LocTable __instance, string key, ref string __result)
        {
            // Return key as fallback "translation"
            __result = key;
            return false;
        }

        public static bool GetFormattedTextPrefix(LocString __instance, ref string __result)
        {
            __result = __instance?.LocEntryKey ?? "";
            return false;
        }

        public static bool GetRawTextInstancePrefix(LocString __instance, ref string __result)
        {
            __result = __instance?.LocEntryKey ?? "";
            return false;
        }


        /// <summary>Harmony prefix: replace Neutralize.OnPlay with safe damage+weak.</summary>
        public static bool NeutralizePrefix(CardModel __instance, ref Task __result,
            PlayerChoiceContext choiceContext, CardPlay cardPlay)
        {
            if (cardPlay.Target == null) { __result = Task.CompletedTask; return false; }
            __result = NeutralizeSafe(__instance, choiceContext, cardPlay);
            return false;
        }

    private static async Task NeutralizeSafe(CardModel card, PlayerChoiceContext ctx, CardPlay play)
    {
        try
        {
            // 1. Apply Damage (stable signature)
            await CreatureCmd.Damage(ctx, play.Target!, (decimal)card.DynamicVars.Damage.BaseValue,
                MegaCrit.Sts2.Core.ValueProps.ValueProp.Move, card);

            // 2. Apply Weak Power using reflection to handle beta vs non-beta differences safely
            var amount = (decimal)card.DynamicVars["WeakPower"].BaseValue;
            var source = (Creature)card.Owner.Creature;
            var target = (Creature)play.Target!;

            var applyMethod = typeof(PowerCmd).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Apply" && m.IsGenericMethod && (m.GetParameters().Length == 5 || m.GetParameters().Length == 6));

            if (applyMethod != null)
            {
                var genericApply = applyMethod.MakeGenericMethod(typeof(WeakPower));
                var parameters = genericApply.GetParameters();

                object?[] args = parameters.Length == 6
                    ? [ctx, target, amount, source, card, false]
                    : [target, amount, source, card, false];

                if (genericApply.Invoke(null, args) is Task task)
                {
                    await task;
                }
            }
            else
            {
                Console.Error.WriteLine("[WARN] Neutralize safe: Could not find PowerCmd.Apply method");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Neutralize safe: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.Error.WriteLine($"  Inner: {ex.InnerException.Message}");
            }
        }
    }

        public static bool HasEntryPrefix(ref bool __result)
        {
            __result = true;
            return false;
        }

        public static bool GetLocStringPrefix(LocTable __instance, string key, ref LocString __result)
        {
            var nameField = typeof(LocTable).GetField("_name",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var tableName = nameField?.GetValue(__instance) as string ?? "_unknown";
            __result = new LocString(tableName, key);
            return false;
        }

        public static bool GetTablePrefix(string name, ref LocTable __result)
        {
            try {
                // Use uninitialized object to avoid constructor logic that might check for real files
                __result = (LocTable)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(LocTable));
                var nameField = typeof(LocTable).GetField("_name", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                nameField?.SetValue(__result, name);
            } catch { }
            return false;
        }

        public static bool SmartFormatPrefix(LocString locString, ref string __result)
        {
            // Fallback to raw text
            __result = locString.LocEntryKey ?? "";
            return false;
        }

        /// <summary>
        /// Intercept bundle selection — store bundles and wait for player to pick a pack index.
        /// </summary>
        public static bool BundleScreenPrefix(
            MegaCrit.Sts2.Core.Entities.Players.Player player,
            IReadOnlyList<IReadOnlyList<CardModel>> bundles,
            ref Task<IEnumerable<CardModel>> __result)
        {
            if (bundles.Count == 0)
            {
                __result = Task.FromResult<IEnumerable<CardModel>>(Array.Empty<CardModel>());
                return false;
            }

            // Store pending bundles for the main loop to present
            var sim = _bundleSimRef;
            if (sim != null)
            {
                sim._pendingBundles = bundles;
                sim._pendingBundleTcs = new TaskCompletionSource<IEnumerable<CardModel>>();
                // Console.Error.WriteLine($"[SIM] Bundle selection pending: {bundles.Count} packs");

                __result = sim._pendingBundleTcs.Task;
                return false;
            }

            __result = Task.FromResult<IEnumerable<CardModel>>(bundles[0]);
            return false;
        }

        // Static reference so Harmony patch can access the simulator instance
        internal static RunSimulator? _bundleSimRef;

        public static bool GetLocStringsWithPrefixPrefix(ref IReadOnlyList<LocString> __result)
        {
            __result = new List<LocString>();
            return false;
        }
    }

    private static void Log(string message)
    {
        // Console.Error.WriteLine($"[SIM] {message}");
    }

    private static Dictionary<string, object?> Error(string message) =>
        new() { ["type"] = "error", ["message"] = message };

    private static Dictionary<string, object?> ErrorWithTrace(string context, Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        return new Dictionary<string, object?>
        {
            ["type"] = "error",
            ["message"] = $"{context}: {inner.GetType().Name}: {inner.Message}",
            ["stack_trace"] = inner.StackTrace,
        };
    }

    public Dictionary<string, object?> GetFullMap()
    {
        if (_runState?.Map == null)
            return Error("No map available");

        var map = _runState.Map;
        var rows = new List<List<Dictionary<string, object?>>>();
        var currentCoord = _runState.CurrentMapCoord;
        var visited = _runState.VisitedMapCoords;

        for (int row = 0; row < map.GetRowCount(); row++)
        {
            var rowNodes = new List<Dictionary<string, object?>>();
            foreach (var point in map.GetPointsInRow(row))
            {
                if (point == null) continue;
                var children = point.Children?.Select(ch => new Dictionary<string, object?>
                {
                    ["col"] = (int)ch.coord.col,
                    ["row"] = (int)ch.coord.row,
                }).ToList();

                var isVisited = visited?.Any(v => v.col == point.coord.col && v.row == point.coord.row) ?? false;
                var isCurrent = currentCoord.HasValue &&
                    currentCoord.Value.col == point.coord.col && currentCoord.Value.row == point.coord.row;

                rowNodes.Add(new Dictionary<string, object?>
                {
                    ["col"] = (int)point.coord.col,
                    ["row"] = (int)point.coord.row,
                    ["type"] = point.PointType.ToString(),
                    ["children"] = children,
                    ["visited"] = isVisited,
                    ["current"] = isCurrent,
                });
            }
            if (rowNodes.Count > 0)
                rows.Add(rowNodes);
        }

        // Boss node
        var bossNode = new Dictionary<string, object?>
        {
            ["col"] = (int)map.BossMapPoint.coord.col,
            ["row"] = (int)map.BossMapPoint.coord.row,
            ["type"] = map.BossMapPoint.PointType.ToString(),
        };

        // Add boss name/id — use BossEncounter?.Id?.Entry
        try
        {
            var bossIdEntry = _runState.Act?.BossEncounter?.Id?.Entry;
            if (!string.IsNullOrEmpty(bossIdEntry))
            {
                var monsterKey = bossIdEntry.EndsWith("_BOSS") ? bossIdEntry[..^5] : bossIdEntry;
                if (monsterKey == "THE_KIN") monsterKey = "KIN_PRIEST";
                bossNode["id"] = bossIdEntry;
                bossNode["name"] = _loc.Monster(monsterKey);
            }
        }
        catch { }

        return new Dictionary<string, object?>
        {
            ["type"] = "map",
            ["context"] = RunContext(),
            ["rows"] = rows,
            ["boss"] = bossNode,
            ["current_coord"] = currentCoord.HasValue ? new Dictionary<string, object?>
            {
                ["col"] = (int)currentCoord.Value.col,
                ["row"] = (int)currentCoord.Value.row,
            } : null,
            ["player"] = PlayerSummary(_runState!.Players[0]),
        };
    }

    public void CleanUp()
    {
        try
        {
            if (RunManager.Instance.IsInProgress)
                RunManager.Instance.CleanUp(graceful: true);
            _runState = null;
        }
        catch (Exception ex)
        {
            Log($"CleanUp exception: {ex.Message}");
        }
    }

    private void WaitForChoiceTask(Task? task)
    {
        if (task == null) return;
        for (int i = 0; i < 200; i++) // max 2s
        {
            _syncCtx.Pump();
            if (task.IsCompleted) break;
            // If the task triggered another selection, stop waiting so we can return the decision
            if (_cardSelector.HasPending || _cardSelector.HasPendingReward || _pendingBundles != null) break;
            Thread.Sleep(10);
        }
    }

    #endregion
}
