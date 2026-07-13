using System;
using System.Collections.Generic;
using Buildings;
using Environment.Resources;
using Tiles;
using UnityEngine;
using AnimationState = Villagers.Jobs.AnimationState;

[Serializable]
public class BuilderLogic : JobLogic
{
    [Header("Builder Settings")]
    public float buildSpeed = 1f;
    public float stoppingDistance = 1.5f;
    public int searchRadius = 15;

    [Header("Buildings to Place")]
    public List<BuildingData> buildableTypes = new List<BuildingData>();

    [NonSerialized] private int _buildingTypeIndex = 0;

    private enum BuilderPhase { Building, Placing }

    [NonSerialized] private BuilderPhase _phase;
    [NonSerialized] private Building _currentTarget = null;
    [NonSerialized] private Tile _targetTile = null;
    [NonSerialized] private bool _completedBuilding = false;

    protected override void OnInitialize(JobHandler handler)
    {
        _currentTarget = null;
        _targetTile = null;
        _completedBuilding = false;
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
            case AnimationState.Building:
                return ExecuteBuilding(handler);
            case AnimationState.Idle:
                ExecuteIdle(handler);
                break;
        }
        return false;
    }

    private void ExecuteFindingTarget(JobHandler handler)
    {
        // Phase 1: build any existing unfinished building
        _currentTarget = FindNextBuilding(handler);
        if (_currentTarget != null)
        {
            _phase = BuilderPhase.Building;
            _currentTarget.Reserve();
            handler.villagerMover.StopMoving();
            ChangeState(AnimationState.MovingToTarget, handler);
            return;
        }

        // Guard: if the LLM assigned a specific building type + target area, and a completed
        // building of that type already exists near that target, the assignment referred to
        // work that finished while waiting for the LLM response. Go idle instead of starting
        // a new build by mistake.
        if (!string.IsNullOrEmpty(handler.PreferredBuildingType) && handler.PreferredTargetArea.HasValue)
        {
            if (HasCompletedBuildingAtTarget(handler, handler.PreferredBuildingType))
            {
                currentStatus = "Assigned building already completed. Waiting for instructions...";
                LogInfo($"Target {handler.PreferredBuildingType} near {handler.PreferredTargetArea} already finished — going idle");
                handler.villagerMover.StopMoving();
                ChangeState(AnimationState.Idle, handler);
                return;
            }
        }

        // Phase 2: find an empty tile and place a new foundation
        var data = PickBuildingData(handler);
        if (data != null)
        {
            _targetTile = FindBuildingTile(handler, data);
            if (_targetTile != null)
            {
                _phase = BuilderPhase.Placing;
                handler.villagerMover.StopMoving();
                ChangeState(AnimationState.MovingToTarget, handler);
                return;
            }
        }

        currentStatus = "No building tasks available. Waiting...";
        handler.villagerMover.StopMoving();
        ChangeState(AnimationState.Idle, handler);
    }

    private void ExecuteMovingToTarget(JobHandler handler)
    {
        Vector3 destination;
        Transform tileTransform = null;

        if (_phase == BuilderPhase.Building)
        {
            if (_currentTarget == null) { ChangeState(AnimationState.FindingTarget, handler); return; }
            // The tile is: Building -> "Building" container -> Tile
            tileTransform = _currentTarget.transform.parent?.parent;
            destination = GetBuildSpot(handler, tileTransform);
            currentStatus = $"Moving to build {GetBuildingName()}";
        }
        else
        {
            if (_targetTile == null) { ChangeState(AnimationState.FindingTarget, handler); return; }
            tileTransform = _targetTile.transform;
            destination = _targetTile.transform.position;
            currentStatus = "Moving to place building foundation";
        }

        handler.villagerMover.MoveTo(destination);

        if (!handler.villagerMover.IsNearDestination(stoppingDistance)) return;

        handler.villagerMover.StopMoving();

        // Face the tile center so the villager looks at the building
        if (tileTransform != null)
        {
            Vector3 tileCenter = tileTransform.position + new Vector3(1f, 0f, 1f);
            handler.villagerMover.FaceTarget(tileCenter);
        }

        if (_phase == BuilderPhase.Placing)
        {
            var buildingData = PickBuildingData(handler);

            // Check resources BEFORE placing foundation — don't create orphaned construction sites
            if (buildingData != null && buildingData.levels.Count > 0)
            {
                var levelData = buildingData.levels[0];
                bool hasFood = levelData.foodCost <= 0 || VillageState.Instance.HasResource(ResourceType.Food, levelData.foodCost);
                if (!VillageState.Instance.HasResource(ResourceType.Wood, levelData.woodCost)
                    || !VillageState.Instance.HasResource(ResourceType.Stone, levelData.stoneCost)
                    || !hasFood)
                {
                    string foodPart = levelData.foodCost > 0 ? $", {levelData.foodCost} food" : "";
                    currentStatus = $"Need {levelData.woodCost} wood, {levelData.stoneCost} stone{foodPart} to start {buildingData.buildingType}";
                    _targetTile = null;
                    ChangeState(AnimationState.Idle, handler);
                    return;
                }
            }

            var placementTile = _targetTile;
            _currentTarget = PlaceFoundation(_targetTile, buildingData);
            _targetTile = null;

            if (_currentTarget == null)
            {
                string tilePos = placementTile != null ? placementTile.GridPos.ToString() : "unknown";
                string typeName = buildingData != null ? buildingData.buildingType.ToString() : "building";
                currentStatus = $"Failed to place {typeName} at ({tilePos}). Waiting...";
                LLMController.Instance?.AddRecentEvent(
                    $"Build FAILED: {typeName} at ({tilePos}) — tile occupied or invalid. Pick a different FREE BUILD SITE.");
                ChangeState(AnimationState.Idle, handler);
                return;
            }

            _currentTarget.Reserve();
        }

        // Consume resources once before starting to build the current level
        if (!TryConsumeResourcesForCurrentLevel())
        {
            currentStatus = $"Waiting for resources to build {GetBuildingName()}...";
            _currentTarget.Unreserve();
            _currentTarget = null;
            ChangeState(AnimationState.Idle, handler);
            return;
        }

        ChangeState(AnimationState.Building, handler);
    }

    private bool ExecuteBuilding(JobHandler handler)
    {
        if (_currentTarget == null)
        {
            ChangeState(AnimationState.FindingTarget, handler);
            return false;
        }

        float workApplied = buildSpeed * Time.deltaTime * handler.WorkSpeedMultiplier;
        bool levelCompleted = _currentTarget.AddWork(workApplied);

        currentStatus = $"Building {GetBuildingName()} ({_currentTarget.GetProgressPercent()}%)";

        if (levelCompleted)
        {
            bool finished = _currentTarget.IsFinished();
            _currentTarget.Unreserve();

            if (finished)
            {
                LogEvent($"Completed building: {GetBuildingName()}");
                _completedBuilding = true;
                _currentTarget = null;
                currentStatus = "Finished building. Waiting for instructions...";
                handler.villagerMover.StopMoving();
                ChangeState(AnimationState.Idle, handler);
            }
            else
            {
                _currentTarget = null;
                ChangeState(AnimationState.FindingTarget, handler);
            }

            return true;
        }

        return false;
    }

    private void ExecuteIdle(JobHandler handler)
    {
        // Don't leave idle while waiting for LLM instructions after completing a building,
        // but self-recover after 25 s in case the LLM sends the same assignment again
        // (which skips re-initialization and would leave the builder stuck forever).
        timeSinceLastAction += Time.deltaTime;
        if (_completedBuilding)
        {
            if (timeSinceLastAction >= 25f)
            {
                _completedBuilding = false;
                timeSinceLastAction = 0f;
                ChangeState(AnimationState.FindingTarget, handler);
            }
            return;
        }

        // Don't self-retry if placement failed — wait for LLM to reassign with a
        // different coordinate. Retrying the same target is pointless.
        if (currentStatus != null && currentStatus.Contains("Failed to place"))
            return;

        if (timeSinceLastAction >= 1f)
            ChangeState(AnimationState.FindingTarget, handler);
    }

    private Building PlaceFoundation(Tile tile, BuildingData data)
    {
        if (tile == null || data == null || data.foundationPrefab == null) return null;

        // Prevent placing on a tile that already has a crop or building
        if (tile.HasBuilding || tile.GetComponentInChildren<ResourceNode>() != null)
        {
            LogWarning($"Tile {tile.GridPos} already occupied — skipping foundation");
            return null;
        }

        if (!tile.AllowsBuilding(data.constructionType))
        {
            LogWarning($"Tile {tile.GridPos} does not allow {data.constructionType}");
            return null;
        }

        // Find or create "Building" container under the tile (matches VillageSpawner convention)
        Transform buildingContainer = tile.transform.Find("Building");
        if (buildingContainer == null)
        {
            var container = new GameObject("Building");
            container.transform.SetParent(tile.transform);
            container.transform.localPosition = Vector3.zero;
            buildingContainer = container.transform;
        }

        Vector3 spawnPos = tile.transform.position + data.placementOffset;
        int rotSteps = UnityEngine.Random.Range(0, 4);
        var go = UnityEngine.Object.Instantiate(data.foundationPrefab, spawnPos, Quaternion.identity, buildingContainer);
        if (rotSteps > 0)
        {
            Vector3 tileCenter = tile.transform.position + new Vector3(1f, 0f, 1f);
            go.transform.RotateAround(tileCenter, Vector3.up, rotSteps * 90f);
        }
        go.name = $"{data.buildingType}_{tile.GridPos.x}_{tile.GridPos.y}";

        tile.TrySetBuilding(new ConstructionInstance(data.constructionType, 0f, false));

        var building = go.GetComponent<Building>();
        if (building == null)
            LogWarning($"{data.buildingType} prefab has no Building component!");

        // Advance the type index so the next placement cycles to the next building type
        if (buildableTypes != null && buildableTypes.Count > 0)
            _buildingTypeIndex = (_buildingTypeIndex + 1) % buildableTypes.Count;

        LogEvent($"Placed {data.buildingType} foundation at {tile.GridPos}");
        return building;
    }

    private BuildingData PickBuildingData(JobHandler handler)
    {
        if (buildableTypes == null || buildableTypes.Count == 0) return null;

        string preferred = handler?.PreferredBuildingType;
        if (!string.IsNullOrEmpty(preferred))
        {
            foreach (var data in buildableTypes)
            {
                if (data != null && data.buildingType.ToString().Equals(preferred, System.StringComparison.OrdinalIgnoreCase))
                    return data;
            }
            LogWarning($"LLM requested '{preferred}' but it's not in buildableTypes — falling back to round-robin");
        }

        return buildableTypes[_buildingTypeIndex % buildableTypes.Count];
    }

    private bool TryConsumeResourcesForCurrentLevel()
    {
        if (_currentTarget == null || VillageState.Instance == null) return true;
        if (_currentTarget.buildingData == null) return true;

        // Resources already paid by a previous builder — don't charge again
        if (_currentTarget.resourcesPaidForCurrentLevel) return true;

        int level = _currentTarget.currentLevel;
        if (level >= _currentTarget.buildingData.levels.Count) return true;

        var levelData = _currentTarget.buildingData.levels[level];

        bool hasFood = levelData.foodCost <= 0 || VillageState.Instance.HasResource(ResourceType.Food, levelData.foodCost);
        if (!VillageState.Instance.HasResource(ResourceType.Wood, levelData.woodCost)
            || !VillageState.Instance.HasResource(ResourceType.Stone, levelData.stoneCost)
            || !hasFood)
        {
            string foodPart = levelData.foodCost > 0 ? $", {levelData.foodCost} food" : "";
            currentStatus = $"Need {levelData.woodCost} wood, {levelData.stoneCost} stone{foodPart} to build {GetBuildingName()}";
            return false;
        }

        VillageState.Instance.TrySpendResource(ResourceType.Wood, levelData.woodCost);
        VillageState.Instance.TrySpendResource(ResourceType.Stone, levelData.stoneCost);
        if (levelData.foodCost > 0)
            VillageState.Instance.TrySpendResource(ResourceType.Food, levelData.foodCost);
        _currentTarget.resourcesPaidForCurrentLevel = true;
        return true;
    }

    private string GetBuildingName()
    {
        return _currentTarget?.buildingData != null
            ? _currentTarget.buildingData.buildingType.ToString()
            : "Building";
    }

    private Building FindNextBuilding(JobHandler handler)
    {
        var all = UnityEngine.Object.FindObjectsByType<Building>(FindObjectsSortMode.None);
        Building nearest = null;
        float bestScore = float.MaxValue;
        Vector3 origin = handler.transform.position;

        Vector3? targetAreaWorld = GetTargetAreaWorld(handler);

        foreach (var b in all)
        {
            if (b == null || b.IsReserved || b.IsFinished()) continue;

            float score = Vector3.Distance(origin, b.transform.position);
            if (targetAreaWorld.HasValue)
                score += Vector3.Distance(targetAreaWorld.Value, b.transform.position) * 2f;

            if (score < bestScore)
            {
                bestScore = score;
                nearest = b;
            }
        }

        return nearest;
    }

    private Tile FindBuildingTile(JobHandler handler, BuildingData data)
    {
        if (VillageState.Instance?.TileGrid == null || data == null) return null;

        Vector3 origin = handler.transform.position;
        Vector2Int? preferred = handler.PreferredTargetArea;

        // Use LLM target if provided, otherwise fall back to village core (not builder position)
        Vector2Int centerGrid;
        if (preferred.HasValue)
        {
            centerGrid = preferred.Value;
        }
        else
        {
            centerGrid = VillageState.Instance.GetVillageCore();
        }

        var ct = data.constructionType;
        System.Func<Tile, bool> condition = tile =>
            tile.Archetype != null
            && tile.Archetype.Style == TileStyle.Grass
            && !tile.HasBuilding
            && !tile.HasResource
            && tile.AllowsBuilding(ct)
            && !HasResourceNodeOnTile(tile);

        var candidates = VillageState.Instance.TileGrid.FindTilesInRadius(centerGrid, searchRadius, condition);

        // If nothing found in the preferred radius, search the whole map
        if (candidates.Count == 0)
        {
            LogInfo($"No building tiles within radius {searchRadius} of {centerGrid} — expanding to full map");
            candidates = VillageState.Instance.TileGrid.FindAllTiles(condition);
        }

        if (candidates.Count == 0) return null;

        // Score: proximity to village core is primary, distance from builder is secondary
        Vector3? coreWorld = null;
        if (VillageState.Instance.TileGrid.TryGet(centerGrid, out var coreTile))
            coreWorld = coreTile.transform.position;

        Tile best = null;
        float bestScore = float.MaxValue;

        foreach (var tile in candidates)
        {
            float distToCore = coreWorld.HasValue
                ? Vector3.Distance(coreWorld.Value, tile.transform.position)
                : 0f;
            float buildingProximity = GetNearestBuildingDistance(tile);
            float distToBuilder = Vector3.Distance(origin, tile.transform.position);
            // Primary: close to existing buildings / village core. Secondary: close to builder.
            float score = distToCore * 1.5f + buildingProximity * 1.5f + distToBuilder * 0.3f;

            if (score < bestScore)
            {
                bestScore = score;
                best = tile;
            }
        }

        return best;
    }

    private float GetNearestBuildingDistance(Tile tile)
    {
        var allBuildings = UnityEngine.Object.FindObjectsByType<Building>(FindObjectsSortMode.None);
        float nearest = float.MaxValue;
        foreach (var b in allBuildings)
        {
            if (b == null) continue;
            float d = Vector3.Distance(tile.transform.position, b.transform.position);
            if (d < nearest) nearest = d;
        }
        return nearest == float.MaxValue ? 0f : nearest;
    }

    private bool HasResourceNodeOnTile(Tile tile)
    {
        return tile.GetComponentInChildren<ResourceNode>() != null;
    }

    /// <summary>
    /// Returns a position just outside the tile edge, on the side nearest to the villager.
    /// </summary>
    private Vector3 GetBuildSpot(JobHandler handler, Transform tileTransform)
    {
        if (tileTransform == null)
            return _currentTarget.transform.position;

        Vector3 tileCenter = tileTransform.position + new Vector3(1f, 0f, 1f);
        Vector3 villagerPos = handler.transform.position;

        // Direction from tile center to villager, flattened
        Vector3 dir = villagerPos - tileCenter;
        dir.y = 0f;

        if (dir.sqrMagnitude < 0.01f)
            dir = Vector3.forward;

        // Snap to nearest cardinal direction (N/S/E/W edge)
        if (Mathf.Abs(dir.x) > Mathf.Abs(dir.z))
            dir = new Vector3(Mathf.Sign(dir.x), 0f, 0f);
        else
            dir = new Vector3(0f, 0f, Mathf.Sign(dir.z));

        // Place just outside the tile edge (tile is 2x2, so 1 unit from center = edge)
        float edgeOffset = 1f + stoppingDistance * 0.5f;
        return tileCenter + dir * edgeOffset;
    }

    /// <summary>
    /// Returns true if a FINISHED building of the given type exists on the exact target tile.
    /// Used to detect stale LLM assignments where the building completed during the LLM wait.
    /// </summary>
    private bool HasCompletedBuildingAtTarget(JobHandler handler, string buildingTypeName)
    {
        if (!handler.PreferredTargetArea.HasValue || VillageState.Instance?.TileGrid == null) return false;
        if (!VillageState.Instance.TileGrid.TryGet(handler.PreferredTargetArea.Value, out var tile)) return false;

        var b = tile.PlacedBuilding;
        return b != null
            && b.IsFinished()
            && b.buildingData != null
            && b.buildingData.buildingType.ToString().Equals(buildingTypeName, System.StringComparison.OrdinalIgnoreCase);
    }

    private Vector3? GetTargetAreaWorld(JobHandler handler)
    {
        if (!handler.PreferredTargetArea.HasValue || VillageState.Instance?.TileGrid == null)
            return null;
        if (VillageState.Instance.TileGrid.TryGet(handler.PreferredTargetArea.Value, out var tile))
            return tile.transform.position;
        return null;
    }

    public override void ResetState()
    {
        _currentTarget?.Unreserve();
        base.ResetState();
        _currentTarget = null;
        _targetTile = null;
        _completedBuilding = false;
    }
}
