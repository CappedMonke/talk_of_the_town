using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Buildings
{
    public enum BuildingType
    {
        House,
        Stockpile,
        Farm
    }

    [System.Serializable]
    public class BuildingLevelVisual
    {
        public List<GameObject> buildStages = new List<GameObject>();
        public GameObject finalObject;
        public int workRequired = 10;
    }

    public class Building : MonoBehaviour
    {
        /// <summary>Fired when a building is first placed in the scene.</summary>
        public static event Action<Building> OnBuildingPlaced;

        /// <summary>Fired when a building finishes all construction levels.</summary>
        public static event Action<Building> OnBuildingCompleted;

        public BuildingData buildingData;

        /// <summary>
        /// Repositions all buildings in the scene using their BuildingData.placementOffset.
        /// Call from inspector or at runtime to preview offset changes.
        /// </summary>
        [ContextMenu("Reposition All Buildings From Offset")]
        public static void RepositionAllBuildings()
        {
            var all = FindObjectsByType<Building>(FindObjectsSortMode.None);
            foreach (var b in all)
            {
                if (b == null || b.buildingData == null) continue;
                var tile = b.transform.parent?.parent; // Building container -> Tile
                if (tile == null) continue;

                // Preserve current rotation, reapply around tile center
                float yAngle = b.transform.eulerAngles.y;
                b.transform.position = tile.position + b.buildingData.placementOffset;
                b.transform.rotation = Quaternion.identity;
                if (yAngle > 0.1f)
                {
                    Vector3 tileCenter = tile.position + new Vector3(1f, 0f, 1f);
                    b.transform.RotateAround(tileCenter, Vector3.up, yAngle);
                }
            }
            Debug.Log($"[Building] Repositioned {all.Length} buildings from BuildingData offsets.");
        }

        [HideInInspector]
        public List<BuildingLevelVisual> levels = new List<BuildingLevelVisual>();

        public int currentLevel = 0;
        public float currentWork = 0f;
        public bool resourcesPaidForCurrentLevel = false;

        private bool isReserved = false;
        public bool IsReserved => isReserved;
        public void Reserve() => isReserved = true;
        public void Unreserve() => isReserved = false;

        [Header("Occupancy (Houses only)")]
        public int maxOccupants = 1;
        private int occupantCount = 0;
        public bool HasFreeSlot() => occupantCount < maxOccupants;
        public bool OccupySlot() { if (!HasFreeSlot()) return false; occupantCount++; return true; }
        public void ReleaseSlot() { if (occupantCount > 0) occupantCount--; }

        private class RuntimeLevel
        {
            public List<GameObject> stageInstances = new List<GameObject>();
            public GameObject finalInstance;
            public int workRequired;
        }

        private List<RuntimeLevel> runtimeLevels = new List<RuntimeLevel>();

        public bool IsFinished()
        {
            return currentLevel >= runtimeLevels.Count;
        }

        public bool AddWork(float amount)
        {
            if (IsFinished()) return false;
            var level = runtimeLevels[currentLevel];
            currentWork += amount;
            bool levelCompleted = false;
            if (currentWork >= level.workRequired)
            {
                currentWork -= level.workRequired;
                int finishedLevel = currentLevel;
                currentLevel++;
                resourcesPaidForCurrentLevel = false;
                levelCompleted = true;
                ShowFinalForLevel(finishedLevel);
                ApplyLevelBonuses(finishedLevel);

                if (IsFinished())
                {
                    if (buildingData != null && buildingData.buildingType == BuildingType.House)
                    {
                        OccupySlot(); // Claim the slot immediately so GetAvailableHouseSlots() is accurate
                        VillageState.Instance?.RegisterCompletedHouse(this);
                        StartCoroutine(SpawnVillagerDelayed(buildingData.villagerSpawnDelay));
                    }
                    OnBuildingCompleted?.Invoke(this);
                }
            }
            UpdateVisuals();
            return levelCompleted;
        }

        public float GetProgressFraction()
        {
            if (IsFinished()) return 1f;
            var level = runtimeLevels[currentLevel];
            if (level.workRequired <= 0) return 0f;
            return Mathf.Clamp01(currentWork / level.workRequired);
        }

        public int GetProgressPercent() => Mathf.RoundToInt(GetProgressFraction() * 100f);

        private void Awake()
        {
            // Register with parent tile so it can be found via TileGrid without a scene-wide search
            var parentTile = GetComponentInParent<Tiles.Tile>();
            parentTile?.SetPlacedBuilding(this);

            BuildRuntimeLevels();
            UpdateVisuals();
            OnBuildingPlaced?.Invoke(this);
        }

        private void BuildRuntimeLevels()
        {
            runtimeLevels.Clear();
            if (buildingData != null && buildingData.levels != null && buildingData.levels.Count > 0)
            {
                for (int i = 0; i < buildingData.levels.Count; i++)
                {
                    var ld = buildingData.levels[i];
                    var rl = new RuntimeLevel();
                    rl.workRequired = ld.workRequired;
                    var levelContainer = new GameObject($"Level_{i}");
                    levelContainer.transform.SetParent(transform, false);
                    levelContainer.hideFlags = HideFlags.DontSaveInBuild;
                    if (ld.stagePrefabs != null)
                    {
                        for (int s = 0; s < ld.stagePrefabs.Count; s++)
                        {
                            var prefab = ld.stagePrefabs[s];
                            if (prefab == null) continue;
                            var inst = Instantiate(prefab, levelContainer.transform);
                            inst.SetActive(false);
                            rl.stageInstances.Add(inst);
                        }
                    }
                    if (ld.finalPrefab != null)
                    {
                        var fin = Instantiate(ld.finalPrefab, levelContainer.transform);
                        fin.SetActive(false);
                        rl.finalInstance = fin;
                    }
                    runtimeLevels.Add(rl);
                }
                return;
            }

            for (int i = 0; i < levels.Count; i++)
            {
                var lv = levels[i];
                var rl = new RuntimeLevel();
                rl.workRequired = lv.workRequired;
                if (lv.buildStages != null)
                {
                    for (int s = 0; s < lv.buildStages.Count; s++)
                    {
                        var go = lv.buildStages[s];
                        if (go == null) continue;
                        rl.stageInstances.Add(go);
                    }
                }
                rl.finalInstance = lv.finalObject;
                runtimeLevels.Add(rl);
            }
        }

        public void UpdateVisuals()
        {
            for (int i = 0; i < runtimeLevels.Count; i++)
            {
                var rl = runtimeLevels[i];
                if (rl == null) continue;
                if (rl.stageInstances != null)
                {
                    foreach (var go in rl.stageInstances) if (go != null) go.SetActive(false);
                }
                if (i >= currentLevel && rl.finalInstance != null) rl.finalInstance.SetActive(false);
            }
            if (IsFinished()) return;
            var cur = runtimeLevels[currentLevel];
            if (cur.stageInstances != null && cur.stageInstances.Count > 0)
            {
                float frac = GetProgressFraction();
                int stageIndex = Mathf.Clamp(Mathf.FloorToInt(frac * cur.stageInstances.Count), 0, cur.stageInstances.Count - 1);
                if (cur.stageInstances[stageIndex] != null) cur.stageInstances[stageIndex].SetActive(true);
            }
        }

        private void ApplyLevelBonuses(int levelIndex)
        {
            if (buildingData == null || levelIndex >= buildingData.levels.Count) return;
            var levelData = buildingData.levels[levelIndex];
            if (levelData.bonuses == null || VillageState.Instance == null) return;
            foreach (var bonus in levelData.bonuses)
                VillageState.Instance.ApplyBuildingBonus(bonus, transform.position);
        }

        public void ShowFinalForLevel(int levelIndex)
        {
            if (levelIndex < 0 || levelIndex >= runtimeLevels.Count) return;
            var rl = runtimeLevels[levelIndex];
            if (rl == null) return;
            if (rl.stageInstances != null)
            {
                foreach (var go in rl.stageInstances) if (go != null) go.SetActive(false);
            }
            if (rl.finalInstance != null) rl.finalInstance.SetActive(true);
        }

        private IEnumerator SpawnVillagerDelayed(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (VillageState.Instance == null) yield break;
            // Spawn in front of the building, respecting its rotation
            Vector3 spawnPos = transform.position + transform.forward * 2f;
            VillageState.Instance.SpawnVillager(spawnPos);
        }
    }
}