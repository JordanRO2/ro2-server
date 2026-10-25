using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RO2.GameData.Tables; // generated table models (PCBaseStatus, …)

namespace RO2.GameData;

/// <summary>
/// In-memory store for all game master data.
/// Loaded once at startup from typed-CSV (preferred) or JSON files, then immutable.
/// Uses FrozenDictionary for optimal read performance.
/// </summary>
public sealed partial class GameDataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public FrozenDictionary<uint, ItemData> Items { get; private set; } = FrozenDictionary<uint, ItemData>.Empty;

    /// <summary>
    /// The skill ids an item carries and the client casts through RMI 1561 ReqSkillStart_Item: the non-zero
    /// ItemInfo.Skill_ID_1 values at or above <see cref="ItemSkillMinId"/> (potions, warp/revival wings, town
    /// scrolls). Used to refuse a raw 1508 ReqSkillStart for one of them: a legitimate item skill starts
    /// ONLY through the 1561 path, which checks the caster owns the item that carries the skill and consumes it
    /// before recording the pending cast. Skill_ID_1 values below the threshold are learnable guild/class
    /// skills carried by skill books (learned, not cast through 1561), and the guild Emergency Call range
    /// is not an ItemInfo.Skill_ID_1, so neither is caught by this set.
    /// </summary>
    public FrozenSet<uint> ItemSkillIds { get; private set; } = FrozenSet<uint>.Empty;

    /// <summary>
    /// Lowest skill id treated as a 1561-cast item skill. Mirrors SkillStartHandler/SkillDamageHandler's
    /// FirstNonClassSkillId (2,000,000), the boundary above which skills are item/guild/monster and never appear in
    /// a player's learned skills.
    /// </summary>
    public const uint ItemSkillMinId = 2_000_000u;

    /// <summary>True when <paramref name="skillId"/> is a 1561-cast item skill (see <see cref="ItemSkillIds"/>).</summary>
    public bool IsItemSkill(uint skillId) => ItemSkillIds.Contains(skillId);

    /// <summary>
    /// Multiplier that packs a skill group and level into one SkillLevels key: <c>Skill_Group * 100 + Skill_Level</c>
    /// (the skill id the client sends, and the Skill_Table_SkillLevel ID column).
    /// </summary>
    private const uint SkillLevelKeyStride = 100u;

    /// <summary>Denominator of a drop-table <c>Drop_Rate</c>: the rate is per 10000 (5000 = 50%).</summary>
    private const int DropRateScale = 10000;

    /// <summary>
    /// Base_Exp_Next returned for a level with no ExpTable row (past the table's last level): a ceiling large
    /// enough that such a level never levels up.
    /// </summary>
    private const long FallbackExpForNextLevel = 999999;
    public FrozenDictionary<uint, MapData> Maps { get; private set; } = FrozenDictionary<uint, MapData>.Empty;
    public FrozenDictionary<uint, QuestData> Quests { get; private set; } = FrozenDictionary<uint, QuestData>.Empty;

    /// <summary>
    /// Quest_ID → EVERY Quest_Info row of that quest, in file order (<see cref="Quests"/> keeps only the
    /// first). A quest's objectives are its rows, and some quests have several kill-objective rows (Complete_Type 2)
    /// beyond the first. Each of them is tracked separately, e.g. a quest with three kill rows ticks each one
    /// independently in OnQuestMonsterOrGO 2425.
    /// </summary>
    public FrozenDictionary<uint, QuestData[]> QuestRows { get; private set; } = FrozenDictionary<uint, QuestData[]>.Empty;
    public FrozenDictionary<uint, SkillData> Skills { get; private set; } = FrozenDictionary<uint, SkillData>.Empty;

    /// <summary>Skill_Group * 100 + Skill_Level → per-level skill data</summary>
    public FrozenDictionary<uint, SkillLevelData> SkillLevels { get; private set; } = FrozenDictionary<uint, SkillLevelData>.Empty;
    public FrozenDictionary<uint, NpcData> Npcs { get; private set; } = FrozenDictionary<uint, NpcData>.Empty;
    public FrozenDictionary<byte, ExpTableEntry> ExpTable { get; private set; } = FrozenDictionary<byte, ExpTableEntry>.Empty;
    public FrozenDictionary<uint, PositionData> Positions { get; private set; } = FrozenDictionary<uint, PositionData>.Empty;
    public FrozenDictionary<uint, TeleportData> Teleports { get; private set; } = FrozenDictionary<uint, TeleportData>.Empty;

    /// <summary>NPC ID → list of item IDs they sell (raw Merchant table, keyed by the merchant's own id)</summary>
    public FrozenDictionary<uint, uint[]> MerchantItems { get; private set; } = FrozenDictionary<uint, uint[]>.Empty;

    /// <summary>
    /// NPC ID → every item its shop can offer, resolved through the NPC's Function slots. An NPC opens
    /// a shop via Function_Type 11 → Function_ID (a merchant id, possibly itself or another NPC), so
    /// the shown inventory is the UNION of that NPC's own Merchant items and the items of every merchant
    /// it references. This is what the client actually displays; validate buys against THIS, not the
    /// raw MerchantItems keyed by the NPC's own id.
    /// </summary>
    public FrozenDictionary<uint, uint[]> NpcShopItems { get; private set; } = FrozenDictionary<uint, uint[]>.Empty;

    /// <summary>Map ID → list of NPC/monster spawns on that map</summary>
    public FrozenDictionary<uint, SpawnEntry[]> MapSpawns { get; private set; } = FrozenDictionary<uint, SpawnEntry[]>.Empty;

    /// <summary>NPC ID → server-side computed stats (HP, ATK, DEF, EXP)</summary>
    public FrozenDictionary<uint, NpcServerStats> NpcStats { get; private set; } = FrozenDictionary<uint, NpcServerStats>.Empty;

    /// <summary>Drop_ID → list of possible drops</summary>
    public FrozenDictionary<uint, DropTableEntry[]> DropTables { get; private set; } = FrozenDictionary<uint, DropTableEntry[]>.Empty;

    /// <summary>JobID (= Character.Job) → per-class stat coefficients (HP/SP/attack/defense/… formula inputs).</summary>
    public FrozenDictionary<byte, PCBaseStatus> PcBaseStatus { get; private set; } = FrozenDictionary<byte, PCBaseStatus>.Empty;

    /// <summary>License (= base-class Job) → per-class STARTING primary stats (Standard_Stat), used to seed
    /// a new character with real class base stats instead of a hardcoded table.</summary>
    public FrozenDictionary<byte, StandardStat> StandardStats { get; private set; } = FrozenDictionary<byte, StandardStat>.Empty;

    /// <summary>
    /// (MapId, spawn Index) → PathEngine navmesh cell, packed as <c>(mapId &lt;&lt; 32) | (uint)index</c>.
    /// This is the value RMI 2506 carries at record offset +17; see NpcSpawnService.BuildOnNpcIn2506.
    /// Loaded from NpcNavCell.json and NavCellFromMesh.json; see MaxKnownNavCell.
    /// </summary>
    public FrozenDictionary<ulong, int> NpcNavCells { get; private set; } = FrozenDictionary<ulong, int>.Empty;

    /// <summary>
    /// Largest navmesh cell index the wire builder may emit (the largest known cell is 2382; this table's own
    /// maximum is 2371). It exists so the wire builder can refuse to emit a cell outside the known range:
    /// the client's PathEngine indexes a per-face array with this value and does not bounds-check it, so an
    /// out-of-range cell crashes the client.
    /// </summary>
    public const int MaxKnownNavCell = 2382;

    /// <summary>Packs a spawn's map and index into the <see cref="NpcNavCells"/> key.</summary>
    public static ulong NavCellKey(uint mapId, int index) => ((ulong)mapId << 32) | (uint)index;

    /// <summary>
    /// Navmesh cell for a spawn, or <c>null</c> when no cell is known for it.
    /// </summary>
    public int? GetNavCell(uint mapId, int index)
        => NpcNavCells.TryGetValue(NavCellKey(mapId, index), out var c) ? c : null;

    /// <summary>Directory holding the per-map navmesh artifacts (data/navmesh), captured at load.</summary>
    private string? _navmeshDir;

    /// <summary>Per-map navmesh, loaded on first use and cached (a map with no artifact caches null).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, NavMesh?> _navmeshes = new();

    /// <summary>
    /// The map's PathEngine navmesh (data/navmesh/&lt;mapId&gt;.nav), loaded on first use and cached, or null when
    /// the map has no artifact or the store has not loaded. Thread-safe; a missing mesh caches null so it is not
    /// retried.
    /// </summary>
    public NavMesh? GetNavMesh(uint mapId)
        => _navmeshDir is null ? null
         : _navmeshes.GetOrAdd(mapId, m => NavMesh.TryLoad(Path.Combine(_navmeshDir, m + ".nav")));

    /// <summary>
    /// The navmesh cell containing a WORLD-space point on <paramref name="mapId"/>, or <c>null</c> when the map
    /// has no navmesh. Unlike <see cref="GetNavCell(uint,int)"/> (spawn points only), this resolves ANY point, so
    /// a moving NPC's 2318 carries the cell it is actually on instead of its stale spawn cell. See
    /// <see cref="NavMesh.CellAt(double,double,double)"/> for the rounding convention.
    /// </summary>
    public int? GetNavCellAt(uint mapId, float worldX, float worldY, float worldZ)
        => GetNavMesh(mapId)?.CellAt(worldX, worldY, worldZ);

    /// <summary>
    /// The store whose <see cref="LoadAsync"/> last completed, or null before any load. The Zone process loads
    /// exactly one (its DI singleton, at startup). It exists for code that has to consult the tables but has no
    /// store in reach: the single 126-byte item-record writer (WorldEntryService.WriteItemInstance) is called
    /// from many sites, most of which never resolve a GameDataStore, and the record's runehole block and the
    /// durability of an unset row are template data. Anything that already holds a store should use that one.
    /// </summary>
    public static GameDataStore? Loaded { get; private set; }

    /// <summary>
    /// Load all game data from the typed-CSV / JSON files in the specified directory (see <see cref="ReadTableAsync{TValue}"/>).
    /// </summary>
    public async Task LoadAsync(string dataDirectory)
    {
        Items = await LoadDictAsync<uint, ItemData>(dataDirectory, "ItemInfo.json", d => d.ID);
        // The 1561-cast item skills: every non-zero ItemInfo.Skill_ID_1 >= ItemSkillMinId (see ItemSkillIds).
        ItemSkillIds = Items.Values
            .Select(i => i.Skill_ID_1)
            .Where(s => s >= ItemSkillMinId)
            .ToFrozenSet();
        Maps = await LoadDictAsync<uint, MapData>(dataDirectory, "Map_List.json", d => d.Map_Index);
        Quests = await LoadDictAsync<uint, QuestData>(dataDirectory, "Quest_Info.json", d => d.Quest_ID);
        QuestRows = await LoadGroupsAsync<uint, QuestData>(dataDirectory, "Quest_Info.json", d => d.Quest_ID);
        Skills = await LoadDictAsync<uint, SkillData>(dataDirectory, "Skill_Table_Base.json", d => d.Skill_Group);
        SkillLevels = await LoadDictAsync<uint, SkillLevelData>(dataDirectory, "Skill_Table_SkillLevel.json", d => d.ID);
        Npcs = await LoadDictAsync<uint, NpcData>(dataDirectory, "NPCInfo.json", d => d.ID);
        ExpTable = await LoadDictAsync<byte, ExpTableEntry>(dataDirectory, "BaseExpTable.json", d => d.Base_Level);
        Positions = await LoadDictAsync<uint, PositionData>(dataDirectory, "Position_Info.json", d => d.Position_ID);
        Teleports = await LoadDictAsync<uint, TeleportData>(dataDirectory, "Teleport.json", d => d.Index);
        // Per-class stat coefficients (JobID -> BaseHP/VitHPValue/APValue/…) that drive the derived-stat
        // and MaxHP/MaxSP formulas. Duplicate JobIDs collapse (last wins); the player rows are 1..18.
        PcBaseStatus = await LoadDictAsync<byte, PCBaseStatus>(dataDirectory, "PCBaseStatus.json", d => d.JobID);
        StandardStats = await LoadDictAsync<byte, StandardStat>(dataDirectory, "Standard_Stat.json", d => d.License);

        // Build merchant lookup: NPC ID → item IDs
        await LoadMerchantDataAsync(dataDirectory);

        // Resolve each NPC's actual shop inventory through its Function slots (needs Npcs + MerchantItems).
        BuildNpcShopItems();

        // Load spawn data: Map ID → spawn entries
        await LoadSpawnDataAsync(dataDirectory);

        // Load server-side NPC stats (HP, ATK, DEF, EXP - editable in NpcServerInfo.xlsx)
        NpcStats = await LoadDictAsync<uint, NpcServerStats>(dataDirectory, "NpcServerInfo.json", d => d.NPC_ID);

        // Load drop tables (editable in DropTable.xlsx)
        await LoadDropTableAsync(dataDirectory);

        // Load the navmesh cell per spawn (not XLSX-derived)
        await LoadNavCellsAsync(dataDirectory);

        // Remember where the per-map navmesh artifacts live, for the position -> cell solver (GetNavCellAt),
        // which moving NPCs use so a chase 2318 carries the cell the NPC is actually on. Loaded lazily per map.
        _navmeshDir = Path.Combine(dataDirectory, "navmesh");

        // Load every remaining data table as a strongly-typed list (auto-generated from data/*.json;
        // see tools/gen_gamedata_models.py and Generated/GameDataTables.g.cs). This maps every
        // table the curated models above don't cover.
        await LoadGeneratedAsync(dataDirectory);

        // Published only once every table is in, so a reader never sees a half-loaded store.
        Loaded = this;
    }

    /// <summary>
    /// Loads the navmesh cell of every spawn, from two files that share the shape
    /// <c>"mapId:index" -&gt; cell</c>: NpcNavCell.json (authoritative) and
    /// NavCellFromMesh.json (computed from the client's navmesh files, used only for the gaps).
    /// </summary>
    /// <remarks>
    /// <para>
    /// NpcNavCell.json is not produced by tools/convert_xlsx.py and has no XLSX behind it. It maps spawns
    /// (matched on AssetIndex and the truncated PosX/PosY x100) to the navmesh cell the original server
    /// sends for them. Coverage is partial: a cell is only known for some spawns.
    /// </para>
    /// <para>
    /// NavCellFromMesh.json is produced by tools/parse_ground_tok.py from the client's per-map
    /// <c>BACKGROUND/ZONE/&lt;map&gt;/navidata/ground.tok</c> files, which are the PathEngine navmesh the
    /// client itself indexes with this field. The cell is the index of the polygon in the mesh's
    /// <c>mappingTo2D</c> section that contains the spawn's position, with ties between vertically
    /// overlapping polygons broken on height. It covers every spawn on every map that has spawns.
    /// </para>
    /// <para>
    /// The mesh file agrees with NpcNavCell.json on all but a handful of rows (listed by
    /// tools/parse_ground_tok.py --validate), and each <c>&lt;anchor&gt;</c> in the files states its own
    /// position as "cell:x,y", which the polygon at that index contains. Where the two tables disagree,
    /// NpcNavCell.json wins.
    /// </para>
    /// </remarks>
    private async Task LoadNavCellsAsync(string dataDirectory)
    {
        var built = new Dictionary<ulong, int>();

        // NpcNavCell.json first: authoritative, must not be overwritten.
        MeasuredNavCellCount = await MergeNavCellFileAsync(
            dataDirectory, "NpcNavCell.json", built, overwrite: true);

        // Mesh-derived second, filling only the spawns NpcNavCell.json does not cover.
        MeshNavCellCount = await MergeNavCellFileAsync(
            dataDirectory, "NavCellFromMesh.json", built, overwrite: false);

        NpcNavCells = built.ToFrozenDictionary();
    }

    /// <summary>
    /// Merges a <c>"mapId:index" -&gt; cell</c> file into <paramref name="built"/>, and returns how
    /// many entries it contributed.
    /// </summary>
    private static async Task<int> MergeNavCellFileAsync(
        string dataDirectory, string filename, Dictionary<ulong, int> built, bool overwrite)
    {
        var path = Path.Combine(dataDirectory, filename);
        if (!File.Exists(path))
            return 0;

        var json = await File.ReadAllBytesAsync(path);
        var raw = JsonSerializer.Deserialize<Dictionary<string, int>>(json, JsonOptions);
        if (raw == null)
            return 0;

        var added = 0;
        foreach (var (k, cell) in raw)
        {
            var sep = k.IndexOf(':');
            if (sep <= 0
                || !uint.TryParse(k.AsSpan(0, sep), out var mapId)
                || !int.TryParse(k.AsSpan(sep + 1), out var index))
                continue;
            // Drop anything the wire could not have carried, so a corrupt table cannot reintroduce
            // the out-of-range cell that crashed the client.
            if (cell < 0 || cell > MaxKnownNavCell)
                continue;

            var key = NavCellKey(mapId, index);
            if (!overwrite && built.ContainsKey(key))
                continue;
            built[key] = cell;
            added++;
        }
        return added;
    }

    /// <summary>
    /// How many navmesh cells came from NpcNavCell.json.
    /// </summary>
    public int MeasuredNavCellCount { get; private set; }

    /// <summary>
    /// How many navmesh cells came from NavCellFromMesh.json, i.e. were computed from the client's
    /// own navmesh because NpcNavCell.json has no entry for that spawn.
    /// </summary>
    public int MeshNavCellCount { get; private set; }

    private static async Task<FrozenDictionary<TKey, TValue>> LoadDictAsync<TKey, TValue>(
        string dir, string filename, Func<TValue, TKey> keySelector)
        where TKey : notnull
    {
        var list = await ReadTableAsync<TValue>(dir, filename);
        if (list == null)
            return FrozenDictionary<TKey, TValue>.Empty;

        var dict = new Dictionary<TKey, TValue>(list.Count);
        foreach (var item in list)
        {
            var key = keySelector(item);
            dict.TryAdd(key, item);
        }
        return dict.ToFrozenDictionary();
    }

    /// <summary>
    /// Load a JSON array file and group its rows by key, keeping every row in file order (the
    /// multi-row counterpart of <see cref="LoadDictAsync{TKey, TValue}"/>, which keeps only the first).
    /// </summary>
    private static async Task<FrozenDictionary<TKey, TValue[]>> LoadGroupsAsync<TKey, TValue>(
        string dir, string filename, Func<TValue, TKey> keySelector)
        where TKey : notnull
    {
        var list = await ReadTableAsync<TValue>(dir, filename);
        if (list == null)
            return FrozenDictionary<TKey, TValue[]>.Empty;

        var groups = new Dictionary<TKey, List<TValue>>();
        foreach (var item in list)
        {
            var key = keySelector(item);
            if (!groups.TryGetValue(key, out var rows))
                groups[key] = rows = new List<TValue>(1);
            rows.Add(item);
        }
        return groups.ToFrozenDictionary(g => g.Key, g => g.Value.ToArray());
    }

    /// <summary>
    /// Load a JSON array file into a plain typed list. Used by the auto-generated table loaders
    /// (Generated/GameDataTables.g.cs) where the table has no curated key selector.
    /// </summary>
    private static async Task<IReadOnlyList<TValue>> LoadListAsync<TValue>(string dir, string filename)
    {
        var list = await ReadTableAsync<TValue>(dir, filename);
        return list ?? (IReadOnlyList<TValue>)Array.Empty<TValue>();
    }

    /// <summary>
    /// THE single per-table read: every table loader above goes through here. Given the table's JSON file name
    /// (e.g. "REFINE_RATE.json") it looks, in this order, for the CUSTOM overlay's typed CSV, the custom JSON,
    /// the data dir's typed CSV (see <see cref="TypedCsv"/>), then its JSON: the first that exists wins.
    /// The custom dir is <c>custom/data</c> beside the data dir (<see cref="CustomDirOf"/>), so custom content can
    /// override or supply any table whole; the data dir keeps only the base content (or just a header for a table
    /// whose base data is missing). Returns null when none exists (or the JSON is literally null).
    /// A table is migrated to CSV by adding the .csv and deleting the .json; nothing else changes.
    /// </summary>
    private static async Task<List<TValue>?> ReadTableAsync<TValue>(string dir, string filename)
    {
        var baseName = Path.GetFileNameWithoutExtension(filename);
        foreach (var root in new[] { CustomDirOf(dir), dir })
        {
            var csvPath = Path.Combine(root, baseName + ".csv");
            if (File.Exists(csvPath))
                return TypedCsv.Parse<TValue>(await File.ReadAllTextAsync(csvPath), csvPath);

            var path = Path.Combine(root, filename);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<List<TValue>>(await File.ReadAllBytesAsync(path), JsonOptions);
        }
        return null;
    }

    /// <summary>The custom-content table dir that overlays <paramref name="dataDir"/>: <c>&lt;dataDir&gt;/../custom/data</c>.</summary>
    private static string CustomDirOf(string dataDir) =>
        Path.GetFullPath(Path.Combine(dataDir, "..", "custom", "data"));

    /// <summary>
    /// Get the EXP required for the next level.
    /// </summary>
    public long GetExpForNextLevel(byte level)
    {
        return ExpTable.TryGetValue(level, out var entry) ? entry.Base_Exp_Next : FallbackExpForNextLevel;
    }

    /// <summary>
    /// Get item data by ID.
    /// </summary>
    public ItemData? GetItem(uint itemId)
    {
        return Items.TryGetValue(itemId, out var item) ? item : null;
    }

    /// <summary>
    /// Get map data by index.
    /// </summary>
    public MapData? GetMap(uint mapIndex)
    {
        return Maps.TryGetValue(mapIndex, out var map) ? map : null;
    }

    /// <summary>
    /// Get items sold by an NPC.
    /// </summary>
    public uint[] GetMerchantItems(uint npcId)
    {
        return MerchantItems.TryGetValue(npcId, out var items) ? items : [];
    }

    /// <summary>
    /// Get every item an NPC's shop offers, resolved through its Function slots (see NpcShopItems).
    /// Use this for shop validation — the client shows this resolved union, not the NPC's own row.
    /// </summary>
    public uint[] GetNpcShopItems(uint npcId)
    {
        return NpcShopItems.TryGetValue(npcId, out var items) ? items : [];
    }

    /// <summary>
    /// Resolve every NPC's shop inventory: the union of its own Merchant items and the items of each
    /// merchant it references via Function_Type 11 → Function_ID (the shop-open capability). A direct
    /// merchant references itself; a field NPC (e.g. 11130000) references a Kafra/general-goods merchant.
    /// </summary>
    private void BuildNpcShopItems()
    {
        var result = new Dictionary<uint, uint[]>();
        foreach (var npc in Npcs.Values)
        {
            var merchantIds = new HashSet<uint>();
            if (MerchantItems.ContainsKey(npc.ID))
                merchantIds.Add(npc.ID);
            foreach (var (type, id) in new[]
                     {
                         (npc.Function_Type_1, npc.Function_ID_1),
                         (npc.Function_Type_2, npc.Function_ID_2),
                         (npc.Function_Type_3, npc.Function_ID_3),
                     })
            {
                if (type == 11 && MerchantItems.ContainsKey(id))
                    merchantIds.Add(id);
            }
            if (merchantIds.Count == 0)
                continue;
            var items = new HashSet<uint>();
            foreach (var mid in merchantIds)
                foreach (var item in MerchantItems[mid])
                    items.Add(item);
            result[npc.ID] = [.. items];
        }
        NpcShopItems = result.ToFrozenDictionary();
    }

    /// <summary>
    /// Get server-side NPC stats (HP, ATK, DEF, EXP etc.) for an NPC.
    /// </summary>
    public NpcServerStats? GetNpcStats(uint npcId)
    {
        return NpcStats.TryGetValue(npcId, out var stats) ? stats : null;
    }

    /// <summary>
    /// Get all NPC/monster spawns on a map.
    /// </summary>
    public SpawnEntry[] GetMapSpawns(uint mapId)
    {
        return MapSpawns.TryGetValue(mapId, out var spawns) ? spawns : [];
    }

    /// <summary>
    /// Get skill level data for a specific skill group and level.
    /// </summary>
    public SkillLevelData? GetSkillLevel(uint skillGroup, byte level)
    {
        var key = skillGroup * SkillLevelKeyStride + level;
        return SkillLevels.TryGetValue(key, out var data) ? data : null;
    }

    /// <summary>
    /// Get drop table entries for a Drop_ID. Returns empty if no drops defined.
    /// </summary>
    public DropTableEntry[] GetDropTable(uint dropId)
    {
        return DropTables.TryGetValue(dropId, out var entries) ? entries : [];
    }

    /// <summary>
    /// Roll drops from a drop table. Returns list of (itemId, count) pairs.
    /// </summary>
    public List<(uint ItemId, int Count)> RollDrops(uint dropId)
    {
        var entries = GetDropTable(dropId);
        var drops = new List<(uint, int)>();
        foreach (var entry in entries)
        {
            var roll = Random.Shared.Next(DropRateScale);
            if (roll < entry.Drop_Rate)
            {
                var count = entry.Min_Count == entry.Max_Count
                    ? entry.Min_Count
                    : Random.Shared.Next(entry.Min_Count, entry.Max_Count + 1);
                drops.Add((entry.Item_ID, count));
            }
        }
        return drops;
    }

    private async Task LoadDropTableAsync(string dataDirectory)
    {
        var entries = await ReadTableAsync<DropTableEntry>(dataDirectory, "DropTable.json");
        if (entries == null)
        {
            DropTables = FrozenDictionary<uint, DropTableEntry[]>.Empty;
            return;
        }
        var grouped = entries
            .GroupBy(e => e.Drop_ID)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Slot).ToArray());
        DropTables = grouped.ToFrozenDictionary();
    }

    private async Task LoadSpawnDataAsync(string dataDirectory)
    {
        var entries = await ReadTableAsync<SpawnEntry>(dataDirectory, "ZoneSpawn.json");
        if (entries == null)
        {
            MapSpawns = FrozenDictionary<uint, SpawnEntry[]>.Empty;
            return;
        }

        var grouped = entries
            .Where(e => e.AssetIndex != 0)
            .GroupBy(e => e.MapId)
            .ToDictionary(g => g.Key, g => g.ToArray());
        MapSpawns = grouped.ToFrozenDictionary();
    }

    private async Task LoadMerchantDataAsync(string dataDirectory)
    {
        var entries = await ReadTableAsync<MerchantEntry>(dataDirectory, "Merchant.json");
        if (entries == null)
        {
            MerchantItems = FrozenDictionary<uint, uint[]>.Empty;
            return;
        }

        var grouped = entries
            .Where(e => e.Item != 0)
            .GroupBy(e => e.ID)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Item).ToArray());
        MerchantItems = grouped.ToFrozenDictionary();
    }
}
