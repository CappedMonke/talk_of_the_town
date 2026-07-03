using System;
using System.Collections.Generic;
using Buildings;
using Tiles;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

/// <summary>
/// Central hub for all village state - inventory, villagers, and map access.
/// Provides unified data for LLM decision-making.
/// </summary>
public class VillageState : MonoBehaviour
{
    public static VillageState Instance { get; private set; }
    
    private const string LogCategory = "VillageState";
    
    [Header("References")]
    [SerializeField] private TileGrid tileGrid;
    
    [Header("Inventory")]
    [SerializeField] private int wood = 0;
    [SerializeField] private int stone = 0;
    [SerializeField] private int seeds = 0;
    [SerializeField] private int iron = 0;
    [SerializeField] private int food = 0;
    
    [Header("Game Speed")]
    [SerializeField] [Range(1f, 50f)] private float gameSpeed = 1f;
    public const float MinGameSpeed = 1f;
    public const float MaxGameSpeed = 50f;

    public event System.Action<float> OnGameSpeedChanged;

    [Header("Village Capacity")]
    [SerializeField] private int populationCap = 5;
    [SerializeField] private int inventoryCapacity = 100;
    private int _fieldCapacity = 0;

    [Header("Villager Spawning")]
    [SerializeField] private GameObject villagerPrefab;
    [SerializeField] private GameObject villagerUIPrefab;
    [SerializeField] private Transform villagerUIContainer;
    [SerializeField] private List<string> villagerNamePool = new List<string>();
    private int _villagerSpawnCount = 0;
    private int _nextVillagerId = 0;
    
    // Local helper wrappers (as you use them now)
    void LogError(string msg)   => GameLog.LogError(LogCategory, msg, this);
    void LogWarning(string msg) => GameLog.LogWarning(LogCategory, msg, this);
    void LogInfo(string msg)    => GameLog.LogInfo(LogCategory, msg, this);
    void LogVerbose(string msg) => GameLog.LogVerbose(LogCategory, msg, this);

    [Header("Registered Villagers")]
    [SerializeField] private List<Villager> villagers = new List<Villager>();

    private List<Building> _completedHouses = new List<Building>();

    private const int GrowthCostWood  = 5;
    private const int GrowthCostStone = 5;
    private const int GrowthCostSeeds = 5;
    private const int GrowthCostFood  = 10;
    
    // Public accessors
    public TileGrid TileGrid => tileGrid;
    public IReadOnlyList<Villager> Villagers => villagers;
    public int PopulationCap => populationCap;
    public int InventoryCapacity => inventoryCapacity;
    public int FieldCapacity => _fieldCapacity;
    public float GameSpeed => gameSpeed;
    
    // Resource accessors
    public int Wood => wood;
    public int Stone => stone;
    public int Seeds => seeds;
    public int Iron => iron;
    public int Food => food;
    
    // Events
    public event Action<ResourceType, int, int> OnResourceChanged;  // type, oldValue, newValue
    public event Action<Villager> OnVillagerRegistered;
    public event Action<Villager> OnVillagerUnregistered;
    
    void Awake()
    {
        if (Instance == null)
            Instance = this;
        else
        {
            Destroy(gameObject);
            return;
        }
    }
    
    void Start()
    {
        ApplyGameSpeed();

        // Auto-find TileGrid if not assigned
        if (tileGrid == null)
        {
            tileGrid = FindFirstObjectByType<TileGrid>();
            if (tileGrid != null)
                LogInfo($"[VillageState] Found TileGrid: {tileGrid.name}");
            else
                LogWarning("[VillageState] No TileGrid found!");
        }
        
        // Auto-register existing villagers
        var existingVillagers = FindObjectsByType<Villager>(FindObjectsSortMode.None);
        foreach (var v in existingVillagers)
            RegisterVillager(v);
    }
    
    #region Game Speed

    public void SetGameSpeed(float speed)
    {
        gameSpeed = Mathf.Clamp(speed, MinGameSpeed, MaxGameSpeed);
        ApplyGameSpeed();
        OnGameSpeedChanged?.Invoke(gameSpeed);
    }

    private void ApplyGameSpeed()
    {
        Time.timeScale = gameSpeed;
        Time.fixedDeltaTime = 0.02f * gameSpeed;
        LogInfo($"[VillageState] Game speed: {gameSpeed}x");
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        gameSpeed = Mathf.Clamp(gameSpeed, MinGameSpeed, MaxGameSpeed);
        if (Application.isPlaying)
            ApplyGameSpeed();
    }
#endif

    #endregion

    #region Villager Registry
    
    public void RegisterVillager(Villager villager)
    {
        if (villager != null && !villagers.Contains(villager))
        {
            if (villager.VillagerId == 0)
            {
                _nextVillagerId++;
                villager.AssignId(_nextVillagerId);
            }
            villagers.Add(villager);
            OnVillagerRegistered?.Invoke(villager);
            SpawnVillagerUI(villager);
            LogInfo($"Registered villager: [{villager.VillagerId}] {villager.villagerName}");
        }
    }

    private void SpawnVillagerUI(Villager villager)
    {
        if (villagerUIPrefab == null || villagerUIContainer == null) return;

        var uiGo = Instantiate(villagerUIPrefab, villagerUIContainer);
        uiGo.name = villager.villagerName;
        var textManager = uiGo.GetComponent<UI.VillagerTextManager>();
        if (textManager != null)
            textManager.SetVillager(villager, villager.GetComponent<VillagerBrain>());
    }
    
    public void UnregisterVillager(Villager villager)
    {
        if (villager != null && villagers.Remove(villager))
        {
            OnVillagerUnregistered?.Invoke(villager);
            LogInfo($"Unregistered villager: [{villager.VillagerId}] {villager.villagerName}");
        }
    }
    
    #endregion
    
    #region Resource Management
    
    public void AddResource(ResourceType type, int amount)
    {
        if (amount <= 0) return;

        int current = GetResource(type);
        if (current >= inventoryCapacity)
        {
            LogInfo($"{type} storage full ({current}/{inventoryCapacity})");
            return;
        }
        amount = Mathf.Min(amount, inventoryCapacity - current);

        int oldValue = current;
        
        switch (type)
        {
            case ResourceType.Wood: wood += amount; break;
            case ResourceType.Stone: stone += amount; break;
            case ResourceType.Seed: seeds += amount; break;
            case ResourceType.Iron: iron += amount; break;
            case ResourceType.Food: food += amount; break;
        }
        
        int newValue = GetResource(type);
        OnResourceChanged?.Invoke(type, oldValue, newValue);
        LogInfo($"+{amount} {type} (now {newValue})");
    }
    
    public bool TrySpendResource(ResourceType type, int amount)
    {
        if (amount <= 0) return true;
        if (GetResource(type) < amount) return false;
        
        int oldValue = GetResource(type);
        
        switch (type)
        {
            case ResourceType.Wood: wood -= amount; break;
            case ResourceType.Stone: stone -= amount; break;
            case ResourceType.Seed: seeds -= amount; break;
            case ResourceType.Iron: iron -= amount; break;
            case ResourceType.Food: food -= amount; break;
        }
        
        int newValue = GetResource(type);
        OnResourceChanged?.Invoke(type, oldValue, newValue);
        LogInfo($"-{amount} {type} (now {newValue})");
        return true;
    }
    
    public int GetResource(ResourceType type)
    {
        return type switch
        {
            ResourceType.Wood => wood,
            ResourceType.Stone => stone,
            ResourceType.Seed => seeds,
            ResourceType.Iron => iron,
            ResourceType.Food => food,
            _ => 0
        };
    }
    
    public bool HasResource(ResourceType type, int amount)
    {
        return GetResource(type) >= amount;
    }

    public void ApplyBuildingBonus(BuildingBonus bonus, Vector3 spawnPosition)
    {
        switch (bonus.type)
        {
            case BuildingBonusType.NewVillager:
                // House registers itself via Building.RegisterCompletedHouse — no auto-spawn here.
                break;
            case BuildingBonusType.InventoryCapacity:
                inventoryCapacity += bonus.value;
                LogInfo($"Inventory capacity increased by {bonus.value} (now {inventoryCapacity})");
                break;
            case BuildingBonusType.FieldCapacity:
                _fieldCapacity += bonus.value;
                LogInfo($"Field capacity increased by {bonus.value} (now {_fieldCapacity})");
                break;
        }
    }

    public void SpawnVillager(Vector3 nearPosition)
    {
        if (villagerPrefab == null)
        {
            LogWarning("Cannot spawn villager: villagerPrefab is not assigned.");
            populationCap++;
            return;
        }

        Vector3 spawnPos = FindNavMeshSpawnPoint(nearPosition);
        var go = Instantiate(villagerPrefab, spawnPos, Quaternion.identity);
        go.name = GetNextVillagerName();

        var villager = go.GetComponent<Villager>();
        if (villager != null)
        {
            villager.villagerName = go.name;
            RegisterVillager(villager);
        }

        populationCap++;
        LogInfo($"Spawned new villager [{villager?.VillagerId ?? 0}] '{go.name}' (population cap now {populationCap})");
    }

    private Vector3 FindNavMeshSpawnPoint(Vector3 origin)
    {
        // Try increasingly large rings around the origin until we land on the NavMesh.
        float[] radii = { 2f, 4f, 8f, 15f };
        foreach (float r in radii)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                float angle = attempt * (360f / 8) * Mathf.Deg2Rad;
                Vector3 candidate = origin + new Vector3(Mathf.Cos(angle) * r, 0f, Mathf.Sin(angle) * r);
                if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                    return hit.position;
            }
        }

        // Last resort: sample directly on the origin
        if (NavMesh.SamplePosition(origin, out NavMeshHit fallback, 20f, NavMesh.AllAreas))
            return fallback.position;

        LogWarning("SpawnVillager: could not find a NavMesh point — spawning at origin");
        return origin;
    }

    private string GetNextVillagerName()
    {
        _villagerSpawnCount++;
        if (villagerNamePool != null && villagerNamePool.Count > 0)
        {
            int index = Random.Range(0, villagerNamePool.Count);
            string name = villagerNamePool[index];
            villagerNamePool.RemoveAt(index);
            return name;
        }
        return $"Villager {_villagerSpawnCount}";
    }

    #endregion

    #region Village Growth

    public void RegisterCompletedHouse(Building house)
    {
        if (house == null || _completedHouses.Contains(house)) return;
        _completedHouses.Add(house);
        int slots = GetAvailableHouseSlots();
        LogInfo($"House registered — available slots: {slots}");
        LLMController.Instance?.AddRecentEvent($"House completed — {slots} free slot(s) now available, villager spawning soon");
    }

    public int CompletedHouseCount => _completedHouses.Count;

    public int GetAvailableHouseSlots()
    {
        int count = 0;
        foreach (var house in _completedHouses)
            if (house != null && house.HasFreeSlot()) count++;
        return count;
    }

    public bool CanSpawnVillager()
    {
        return GetAvailableHouseSlots() > 0
            && HasResource(ResourceType.Wood,  GrowthCostWood)
            && HasResource(ResourceType.Stone, GrowthCostStone)
            && HasResource(ResourceType.Seed,  GrowthCostSeeds)
            && HasResource(ResourceType.Food,  GrowthCostFood);
    }

    public bool TryGrowVillage()
    {
        if (!CanSpawnVillager())
        {
            LogWarning("Cannot grow village: missing resources or no free house slot.");
            return false;
        }

        Building targetHouse = null;
        foreach (var house in _completedHouses)
        {
            if (house != null && house.HasFreeSlot())
            {
                targetHouse = house;
                break;
            }
        }

        if (targetHouse == null) return false;

        TrySpendResource(ResourceType.Wood,  GrowthCostWood);
        TrySpendResource(ResourceType.Stone, GrowthCostStone);
        TrySpendResource(ResourceType.Seed,  GrowthCostSeeds);
        TrySpendResource(ResourceType.Food,  GrowthCostFood);

        targetHouse.OccupySlot();
        SpawnVillager(targetHouse.transform.position);
        return true;
    }

    #endregion

    #region Village Core

    /// <summary>
    /// Returns the grid-space center of the village.
    /// Uses the authoritative spawn position if available, otherwise falls back to
    /// the centroid of placed buildings, then villager positions.
    /// </summary>
    public Vector2Int GetVillageCore()
    {
        // Primary: use the position chosen by VillageSpawner (most reliable)
        if (Tiles.VillageSpawner.SpawnedCoreGridPos.HasValue)
            return Tiles.VillageSpawner.SpawnedCoreGridPos.Value;

        // Fallback: centroid of Building components in scene
        var buildings = UnityEngine.Object.FindObjectsByType<Buildings.Building>(FindObjectsSortMode.None);
        float sumX = 0f, sumZ = 0f;
        int count = 0;

        foreach (var b in buildings)
        {
            if (b == null) continue;
            var pos = b.transform.position;
            sumX += pos.x;
            sumZ += pos.z;
            count++;
        }

        if (count > 0)
        {
            float cellSize = 2f;
            int gx = Mathf.RoundToInt(sumX / count / cellSize);
            int gz = Mathf.RoundToInt(sumZ / count / cellSize);
            return new Vector2Int(gx, gz);
        }

        // Last resort: centroid of villager positions
        foreach (var v in villagers)
        {
            if (v == null) continue;
            sumX += v.GridPosition.x;
            sumZ += v.GridPosition.y;
            count++;
        }

        if (count > 0)
            return new Vector2Int(Mathf.RoundToInt(sumX / count), Mathf.RoundToInt(sumZ / count));

        return Vector2Int.zero;
    }

    #endregion

    #region LLM Data Export
    
    /// <summary>
    /// Get complete village state for LLM consumption
    /// </summary>
    public VillageSnapshot GetSnapshot()
    {
        var snapshot = new VillageSnapshot
        {
            resources = new ResourceSnapshot
            {
                wood = wood,
                stone = stone,
                seeds = seeds,
                iron = iron,
                food = food
            },
            villagers = new List<VillagerSnapshot>()
        };
        
        foreach (var v in villagers)
        {
            if (v == null) continue;
            
            var data = v.GetData();
            snapshot.villagers.Add(new VillagerSnapshot
            {
                id = data.id,
                name = data.name,
                x = data.x,
                y = data.y,
                currentJob = data.currentJob,
                jobStatus = data.jobStatus,
                jobLevel = data.jobLevel,
                tileType = data.tileType
            });
        }
        
        return snapshot;
    }
    
    /// <summary>
    /// Get compact JSON string of village state for LLM prompt
    /// </summary>
    public string GetVillageContextJson()
    {
        var snapshot = GetSnapshot();
        return JsonUtility.ToJson(snapshot, false);
    }
    
    /// <summary>
    /// Get human-readable village summary for LLM prompt
    /// </summary>
    public string GetVillageContextReadable()
    {
        var sb = new System.Text.StringBuilder();
        
        sb.AppendLine("=== VILLAGE RESOURCES ===");
        sb.AppendLine($"Wood: {wood} | Stone: {stone} | Seeds: {seeds} | Iron: {iron} | Food: {food}");
        
        sb.AppendLine();
        sb.AppendLine("=== VILLAGERS ===");
        foreach (var v in villagers)
        {
            if (v == null) continue;
            var d = v.GetData();
            sb.AppendLine($"- [{d.id}] {d.name} at ({d.x},{d.y}) [{d.tileType}]: {d.currentJob} (Lvl {d.jobLevel}) - {d.jobStatus}");
        }
        
        return sb.ToString();
    }
    
    /// <summary>
    /// Get compact map JSON
    /// </summary>
    public string GetMapContextJson()
    {
        if (tileGrid == null) return "{}";
        return tileGrid.ExportToJsonCompact();
    }
    
    /// <summary>
    /// Analyze tiles near a specific position for LLM context
    /// </summary>
    public NearbyTilesInfo GetNearbyTilesInfo(Vector2Int centerPos, int radius = 3)
    {
        var info = new NearbyTilesInfo();
        
        if (tileGrid == null) return info;
        
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                var pos = centerPos + new Vector2Int(dx, dy);
                if (tileGrid.TryGet(pos, out Tile tile))
                {
                    var style = tile.Archetype?.Style ?? TileStyle.Grass;
                    
                    switch (style)
                    {
                        case TileStyle.Forest: info.forestCount++; break;
                        case TileStyle.Mountain: info.mountainCount++; break;
                        case TileStyle.Water: info.waterCount++; break;
                        case TileStyle.Grass: info.grassCount++; break;
                    }
                    
                    // Check for resources
                    var resources = tile.GetAllResourceCounts();
                    foreach (var kvp in resources)
                    {
                        switch (kvp.Key)
                        {
                            case ResourceType.Wood: info.nearbyWood += kvp.Value; break;
                            case ResourceType.Stone: info.nearbyStone += kvp.Value; break;
                        }
                    }
                    
                    // Check for buildings
                    if (tile.HasBuilding)
                        info.buildingCount++;
                }
            }
        }
        
        return info;
    }
    
    #endregion

#if UNITY_EDITOR
    [ContextMenu("Log Village State")]
    private void LogVillageState()
    {
        LogInfo(GetVillageContextReadable());
    }
    
    [ContextMenu("Add 10 Wood (Test)")]
    private void TestAddWood() => AddResource(ResourceType.Wood, 10);

    [ContextMenu("Add 10 Stone (Test)")]
    private void TestAddStone() => AddResource(ResourceType.Stone, 10);

    [ContextMenu("Spawn Villager (Test)")]
    private void TestSpawnVillager() => TryGrowVillage();
#endif
}

#region Data Classes for LLM

[Serializable]
public class VillageSnapshot
{
    public ResourceSnapshot resources;
    public List<VillagerSnapshot> villagers;
}

[Serializable]
public class ResourceSnapshot
{
    public int wood;
    public int stone;
    public int seeds;
    public int iron;
    public int food;
}

[Serializable]
public class VillagerSnapshot
{
    public int id;
    public string name;
    public int x;
    public int y;
    public string currentJob;
    public string jobStatus;
    public int jobLevel;
    public string tileType;
}

[Serializable]
public class NearbyTilesInfo
{
    public int forestCount;
    public int mountainCount;
    public int grassCount;
    public int waterCount;
    public int buildingCount;
    public int nearbyWood;
    public int nearbyStone;
    
    public override string ToString()
    {
        return $"Nearby: {forestCount} forest, {mountainCount} mountain, {grassCount} grass, " +
               $"{buildingCount} buildings, {nearbyWood} wood, {nearbyStone} stone";
    }
}

#endregion