using System;
using System.Collections.Generic;
using Environment.Resources;
using Tiles;
using UnityEngine;
using AnimationState = Villagers.Jobs.AnimationState;
using Random = UnityEngine.Random;

[Serializable]
public class FarmerLogic : JobLogic
{
    [Header("Farmer Settings")]
    public float workSpeed = 1f;
    public float stoppingDistance = 1.5f;
    public float plantTime = 3f;
    public float harvestTime = 5f;
    public int seedCost = 2;
    public int foodProduced = 5;
    public int searchRadius = 15;

    [Header("Crop Prefab")]
    public GameObject cropPrefab;

    private enum FarmerPhase { Planting, Harvesting }

    [NonSerialized] private FarmerPhase _phase;
    [NonSerialized] private Tile _targetTile;
    [NonSerialized] private ResourceNode _targetCrop;
    [NonSerialized] private bool _loggedNoFarm;
    [NonSerialized] private bool _loggedFieldCap;

    protected override void OnInitialize(JobHandler handler)
    {
        _targetTile = null;
        _targetCrop = null;
        _loggedNoFarm = false;
        _loggedFieldCap = false;
        ChangeState(AnimationState.FindingTarget, handler);
    }

    protected override bool ExecuteState(JobHandler handler)
    {
        switch (_currentState)
        {
            case AnimationState.FindingTarget:
                ExecuteFindingTarget(handler);
                break;

            case AnimationState.MovingToTarget:
                ExecuteMovingToTarget(handler);
                break;

            case AnimationState.Planting:
                return ExecutePlanting(handler);

            case AnimationState.Farming:
                return ExecuteHarvesting(handler);

            case AnimationState.Idle:
                ExecuteIdle(handler);
                break;
        }
        return false;
    }

    private void ExecuteFindingTarget(JobHandler handler)
    {
        // Stop if both farming outputs (food and seeds) are at capacity
        if (VillageState.Instance != null)
        {
            int cap = VillageState.Instance.InventoryCapacity;
            bool foodFull  = VillageState.Instance.Food  >= cap;
            bool seedsFull = VillageState.Instance.Seeds >= cap;
            if (foodFull && seedsFull)
            {
                currentStatus = "Storage full (food & seeds)";
                ChangeState(AnimationState.Idle, handler);
                return;
            }
        }

        // Phase 1: Look for mature crops to harvest
        _targetCrop = FindMatureCrop(handler);
        if (_targetCrop != null)
        {
            _phase = FarmerPhase.Harvesting;
            _targetCrop.Reserve();
            handler.villagerMover.StopMoving();
            LogInfo($"Found mature crop at {_targetCrop.transform.position}");
            ChangeState(AnimationState.MovingToTarget, handler);
            return;
        }

        // Phase 2: If no mature crops, try planting (needs seeds)
        if (HasRequiredSeeds())
        {
            _targetTile = FindEmptyGrassTile(handler);
            if (_targetTile != null)
            {
                _phase = FarmerPhase.Planting;
                handler.villagerMover.StopMoving();
                LogInfo($"Found tile to plant at {_targetTile.GridPos}");
                ChangeState(AnimationState.MovingToTarget, handler);
                return;
            }
        }

        currentStatus = _targetCrop == null && !HasRequiredSeeds()
            ? $"Need {seedCost} seeds! Have: {VillageState.Instance?.Seeds ?? 0}. Waiting..."
            : "No crops or planting spots. Waiting...";
        handler.villagerMover.StopMoving();
        ChangeState(AnimationState.Idle, handler);
    }

    private void ExecuteMovingToTarget(JobHandler handler)
    {
        Vector3 destination;
        Transform tileTransform = null;

        if (_phase == FarmerPhase.Harvesting)
        {
            if (_targetCrop == null)
            {
                ChangeState(AnimationState.FindingTarget, handler);
                return;
            }
            // Crop is parented under a tile
            tileTransform = _targetCrop.transform.parent;
            destination = GetWorkSpot(handler, tileTransform);
            currentStatus = $"Moving to crop at {_targetCrop.transform.position}";
        }
        else
        {
            if (_targetTile == null)
            {
                ChangeState(AnimationState.FindingTarget, handler);
                return;
            }
            tileTransform = _targetTile.transform;
            destination = GetWorkSpot(handler, tileTransform);
            currentStatus = $"Moving to plant at {_targetTile.GridPos}";
        }

        handler.villagerMover.MoveTo(destination);

        if (handler.villagerMover.IsNearDestination(stoppingDistance))
        {
            handler.villagerMover.StopMoving();

            if (_phase == FarmerPhase.Planting)
                ChangeState(AnimationState.Planting, handler);
            else
                ChangeState(AnimationState.Farming, handler);
        }
    }

    private Vector3 GetWorkSpot(JobHandler handler, Transform tileTransform)
    {
        if (tileTransform == null)
            return _phase == FarmerPhase.Harvesting
                ? _targetCrop.transform.position
                : _targetTile.transform.position;

        Vector3 tileCenter = tileTransform.position + new Vector3(1f, 0f, 1f);

        // Farmers work in the field — stand in the tile center
        if (_phase == FarmerPhase.Planting || _phase == FarmerPhase.Harvesting)
            return tileCenter;

        return tileCenter;
    }

    private bool ExecutePlanting(JobHandler handler)
    {
        if (_targetTile == null)
        {
            ChangeState(AnimationState.FindingTarget, handler);
            return false;
        }

        timeSinceLastAction += Time.deltaTime * handler.WorkSpeedMultiplier;
        currentStatus = $"Planting ({timeSinceLastAction:F1}/{plantTime:F1})...";

        if (timeSinceLastAction >= plantTime)
        {
            // Prevent planting on a tile that got a building or crop while we were walking/planting
            if (_targetTile.HasBuilding || _targetTile.GetComponentInChildren<ResourceNode>() != null)
            {
                LogWarning($"Tile {_targetTile.GridPos} already occupied — skipping planting");
                _targetTile = null;
                ChangeState(AnimationState.FindingTarget, handler);
                return false;
            }

            if (!TryConsumeSeeds())
            {
                currentStatus = "Out of seeds!";
                _targetTile = null;
                ChangeState(AnimationState.FindingTarget, handler);
                return false;
            }

            // Instantiate crop on the tile
            if (cropPrefab != null)
            {
                var cropPos = _targetTile.transform.position + new Vector3(1f, 0.5f, 1f);
                var cropGO = UnityEngine.Object.Instantiate(
                    cropPrefab,
                    cropPos,
                    Quaternion.identity,
                    _targetTile.transform
                );
                cropGO.name = $"Crop_{_targetTile.GridPos.x}_{_targetTile.GridPos.y}";

                var node = cropGO.GetComponent<ResourceNode>();
                if (node != null)
                {
                    node.resourceType = ResourceNode.ResourceType.Crop;
                    node.canRegrow = true;
                    node.growthStage = ResourceNode.GrowthStage.Seedling;
                    node.currentGrowthTimer = 0f;
                }
            }

            currentStatus = "Planted a crop!";
            LogInfo($"Planted crop at {_targetTile.GridPos}");
            _targetTile = null;
            ChangeState(AnimationState.FindingTarget, handler);
            return true;
        }
        return false;
    }

    private bool ExecuteHarvesting(JobHandler handler)
    {
        if (_targetCrop == null)
        {
            ChangeState(AnimationState.FindingTarget, handler);
            return false;
        }

        timeSinceLastAction += Time.deltaTime * handler.WorkSpeedMultiplier;
        currentStatus = $"Harvesting ({timeSinceLastAction:F1}/{harvestTime:F1})...";

        if (timeSinceLastAction >= harvestTime)
        {
            _targetCrop.Harvest(); // resets to Seedling via regrowth system

            if (VillageState.Instance != null)
            {
                int seedsYield = _targetCrop.seedsYield > 0 ? Random.Range(1, _targetCrop.seedsYield + 1) : 0;
                VillageState.Instance.AddResource(ResourceType.Food, foodProduced);
                if (seedsYield > 0)
                    VillageState.Instance.AddResource(ResourceType.Seed, seedsYield);
                currentStatus = $"Harvested {foodProduced} food and {seedsYield} seeds!";
                LogInfo($"Harvested {foodProduced} food and {seedsYield} seeds!");
            }

            _targetCrop = null;
            ChangeState(AnimationState.FindingTarget, handler);
            return true;
        }
        return false;
    }

    private void ExecuteIdle(JobHandler handler)
    {
        timeSinceLastAction += Time.deltaTime;
        if (timeSinceLastAction >= 1f)
        {
            ChangeState(AnimationState.FindingTarget, handler);
        }
    }

    private ResourceNode FindMatureCrop(JobHandler handler)
    {
        var allNodes = UnityEngine.Object.FindObjectsByType<ResourceNode>(FindObjectsSortMode.None);
        ResourceNode best = null;
        float bestDist = float.MaxValue;
        Vector3 origin = handler.transform.position;

        Vector3? targetAreaWorld = null;
        if (handler.PreferredTargetArea.HasValue && VillageState.Instance?.TileGrid != null)
        {
            var target = handler.PreferredTargetArea.Value;
            // Look up the actual tile world position instead of manual grid-to-world conversion
            if (VillageState.Instance.TileGrid.TryGet(target, out var targetTile))
                targetAreaWorld = targetTile.transform.position;
        }

        foreach (var node in allNodes)
        {
            if (node == null) continue;
            if (node.resourceType != ResourceNode.ResourceType.Crop) continue;
            if (!node.IsMature) continue;
            if (node.isReserved) continue;
            if (!node.gameObject.activeInHierarchy) continue;

            float dist = Vector3.Distance(origin, node.transform.position);

            if (targetAreaWorld.HasValue)
            {
                float distToTarget = Vector3.Distance(targetAreaWorld.Value, node.transform.position);
                dist += distToTarget * 2f;
            }

            if (dist < bestDist)
            {
                bestDist = dist;
                best = node;
            }
        }
        return best;
    }

    private Tile FindEmptyGrassTile(JobHandler handler)
    {
        if (VillageState.Instance == null || VillageState.Instance.TileGrid == null)
            return null;

        // Collect all completed Farm buildings and their planting radii
        var farms = UnityEngine.Object.FindObjectsByType<Buildings.Building>(FindObjectsSortMode.None);
        var farmCoverage = new List<(Vector3 pos, float radius)>();
        foreach (var b in farms)
        {
            if (b == null || !b.IsFinished()) continue;
            if (b.buildingData == null || b.buildingData.buildingType != Buildings.BuildingType.Farm) continue;
            farmCoverage.Add((b.transform.position, b.buildingData.fieldRadius));
        }

        // Without any farm buildings, planting is not allowed
        if (farmCoverage.Count == 0)
        {
            currentStatus = "No farm buildings — cannot plant fields";
            if (!_loggedNoFarm)
            {
                _loggedNoFarm = true;
                LogWarning($"[{handler.name}] FindEmptyGrassTile: no completed Farm buildings found — cannot plant");
            }
            return null;
        }

        LogInfo($"[{handler.name}] FindEmptyGrassTile: {farmCoverage.Count} farm(s) found — " +
                   string.Join(", ", farmCoverage.ConvertAll(f => $"pos={f.pos} r={f.radius}")));

        // Check field capacity (set by FieldCapacity bonuses on Farm buildings)
        if (VillageState.Instance != null && VillageState.Instance.FieldCapacity > 0)
        {
            int currentCrops = CountPlantedCrops();
            if (currentCrops >= VillageState.Instance.FieldCapacity)
            {
                currentStatus = $"Field limit reached ({currentCrops}/{VillageState.Instance.FieldCapacity}) — build more farms";
                if (!_loggedFieldCap)
                {
                    _loggedFieldCap = true;
                    LogWarning($"[{handler.name}] FindEmptyGrassTile: field cap reached ({currentCrops}/{VillageState.Instance.FieldCapacity})");
                }
                return null;
            }
            LogInfo($"[{handler.name}] FindEmptyGrassTile: crops={currentCrops}/{VillageState.Instance.FieldCapacity}");
        }
        else
        {
            LogWarning($"[{handler.name}] FindEmptyGrassTile: FieldCapacity={VillageState.Instance?.FieldCapacity} (0 means uncapped)");
        }

        Vector3 origin = handler.transform.position;

        // Find tiles that are within at least one farm's coverage radius
        bool IsInFarmRange(Tile tile)
        {
            Vector3 tilePos = tile.transform.position;
            foreach (var (farmPos, radius) in farmCoverage)
            {
                if (Vector3.Distance(farmPos, tilePos) <= radius)
                    return true;
            }
            return false;
        }

        // Use preferred target area as search center if the LLM provided one, else use farmer position
        Vector2Int centerGrid;
        if (handler.PreferredTargetArea.HasValue)
        {
            centerGrid = handler.PreferredTargetArea.Value;
        }
        else
        {
            var nearestTile = VillageState.Instance.TileGrid.FindNearestTile(origin);
            centerGrid = nearestTile != null ? nearestTile.GridPos : Vector2Int.zero;
        }

        LogWarning($"[{handler.name}] FindEmptyGrassTile: searching from center={centerGrid} radius={searchRadius}");

        // Debug: count why tiles are being excluded
        int totalChecked = 0, failArchetype = 0, failStyle = 0, failBuilding = 0, failResource = 0, failFarmRange = 0;
        var allTilesInRadius = VillageState.Instance.TileGrid.FindTilesInRadius(centerGrid, searchRadius, _ => true);
        foreach (var t in allTilesInRadius)
        {
            totalChecked++;
            if (t.Archetype == null) { failArchetype++; continue; }
            if (t.Archetype.Style != TileStyle.Grass && t.Archetype.Style != TileStyle.Field) { failStyle++; continue; }
            if (t.HasBuilding) { failBuilding++; continue; }
            if (t.HasResource || HasResourceNodeOnTile(t)) { failResource++; continue; }
            if (!IsInFarmRange(t)) failFarmRange++;
        }
        LogInfo($"[{handler.name}] Tile filter breakdown (radius {searchRadius} of {centerGrid}): " +
                   $"total={totalChecked} noArchetype={failArchetype} wrongStyle={failStyle} " +
                   $"hasBuilding={failBuilding} hasResource={failResource} outOfFarmRange={failFarmRange}");

        System.Func<Tile, bool> condition = tile => tile.Archetype != null
                && (tile.Archetype.Style == TileStyle.Grass || tile.Archetype.Style == TileStyle.Field)
                && !tile.HasBuilding
                && !tile.HasResource
                && !HasResourceNodeOnTile(tile)
                && IsInFarmRange(tile);

        var candidates = VillageState.Instance.TileGrid.FindTilesInRadius(centerGrid, searchRadius, condition);

        if (candidates.Count == 0)
        {
            LogWarning($"[{handler.name}] FindEmptyGrassTile: no tiles in radius {searchRadius} of {centerGrid} — expanding to full map");
            candidates = VillageState.Instance.TileGrid.FindAllTiles(condition);
        }

        if (candidates.Count == 0)
        {
            LogWarning($"[{handler.name}] FindEmptyGrassTile: no valid planting tiles found anywhere on the map");
            return null;
        }

        LogWarning($"[{handler.name}] FindEmptyGrassTile: {candidates.Count} candidate(s) found, picking nearest");

        Tile best = null;
        float bestDist = float.MaxValue;
        foreach (var tile in candidates)
        {
            float d = Vector3.Distance(origin, tile.transform.position);
            if (d < bestDist) { bestDist = d; best = tile; }
        }
        return best;
    }

    private bool HasResourceNodeOnTile(Tile tile)
    {
        return tile.GetComponentInChildren<ResourceNode>() != null;
    }

    private int CountPlantedCrops()
    {
        int count = 0;
        var nodes = UnityEngine.Object.FindObjectsByType<ResourceNode>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        foreach (var n in nodes)
            if (n != null && n.resourceType == ResourceNode.ResourceType.Crop) count++;
        return count;
    }

    private bool HasRequiredSeeds()
    {
        if (VillageState.Instance == null) return true;
        return VillageState.Instance.HasResource(ResourceType.Seed, seedCost);
    }

    private bool TryConsumeSeeds()
    {
        if (VillageState.Instance == null) return true;
        return VillageState.Instance.TrySpendResource(ResourceType.Seed, seedCost);
    }

    public override void ResetState()
    {
        _targetCrop?.Unreserve();
        base.ResetState();
        _targetTile = null;
        _targetCrop = null;
    }
}
