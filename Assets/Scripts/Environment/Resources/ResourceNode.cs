using System;
using UnityEngine;

namespace Environment.Resources
{
    public class ResourceNode : MonoBehaviour
    {
        /// <summary>Fired when a resource node is exhausted (harvested to depletion or set to regrow).</summary>
        public static event Action<ResourceNode> OnNodeExhausted;

        /// <summary>Fired when a regrowing resource reaches Mature stage.</summary>
        public static event Action<ResourceNode> OnNodeRegrown;

        public enum ResourceType
        {
            Tree,
            Stone,
            Seed,
            Water,
            Crop,
        }

        public enum GrowthStage { Seedling, Growing, Mature }

        public ResourceType resourceType = ResourceType.Tree;
        public int resourceAmount = 5;

        public bool isReserved = false;

        [Header("Growth")]
        public GrowthStage growthStage = GrowthStage.Mature;
        public bool canRegrow = false;
        public float growthTime = 30f;
        public float currentGrowthTimer = 0f;
        [Tooltip("Resource amount to restore on regrow to Mature")]
        public int defaultResourceAmount = 5;

        [Header("Stage Visuals (optional)")]
        public GameObject seedlingVisual;
        public GameObject growingVisual;
        public GameObject matureVisual;

        [Header("Mine Shaft")]
        [Tooltip("Mark this node as a mine shaft: infinite stone but very slow regrowth. Miners use regular stone first.")]
        public bool isMineShaft = false;

        [Header("Crop")]
        [Tooltip("Maximum seeds returned when this crop is harvested. Actual yield is random between 1 and this value.")]
        public int seedsYield = 1;

        public bool IsMature => growthStage == GrowthStage.Mature;

        // Cached tree renderers for stump-mode toggling
        private Renderer[] _selfRenderers;
        private GameObject _activeStump;

        void Start()
        {
            if (defaultResourceAmount <= 0)
                defaultResourceAmount = resourceAmount;
            _selfRenderers = GetComponentsInChildren<Renderer>();
            UpdateVisuals();
        }

        void Update()
        {
            if (growthStage == GrowthStage.Mature) return;

            currentGrowthTimer += Time.deltaTime;
            if (currentGrowthTimer >= growthTime)
            {
                currentGrowthTimer = 0f;
                if (growthStage == GrowthStage.Seedling)
                    growthStage = GrowthStage.Growing;
                else if (growthStage == GrowthStage.Growing)
                {
                    growthStage = GrowthStage.Mature;
                    OnNodeRegrown?.Invoke(this);
                }

                UpdateVisuals();
            }
        }

        public void Reserve() => isReserved = true;
        public void Unreserve() => isReserved = false;

        public void Harvest()
        {
            // Mine shafts are infinite: just restore the resource amount and stay available
            if (isMineShaft)
            {
                resourceAmount = defaultResourceAmount;
                isReserved = false;
                return;
            }

            OnNodeExhausted?.Invoke(this);

            if (canRegrow)
            {
                growthStage = GrowthStage.Seedling;
                currentGrowthTimer = 0f;
                resourceAmount = defaultResourceAmount;
                isReserved = false;
                UpdateVisuals();
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private void UpdateVisuals()
        {
            // Stump mode: auto-managed for regrowing trees when ResourceRegrowthManager is present
            if (canRegrow && resourceType == ResourceType.Tree && !isMineShaft)
            {
                var mgr = ResourceRegrowthManager.Instance;
                if (mgr != null && mgr.HasStumps)
                {
                    bool isMature = growthStage == GrowthStage.Mature;

                    // Toggle own mesh renderers
                    if (_selfRenderers != null)
                        foreach (var r in _selfRenderers)
                            if (r != null) r.enabled = isMature;

                    // Spawn stump child when cut down
                    if (!isMature && _activeStump == null)
                        _activeStump = mgr.SpawnStump(transform.position, transform.rotation, transform);
                    // Remove stump when tree is fully grown back
                    else if (isMature && _activeStump != null)
                    {
                        Destroy(_activeStump);
                        _activeStump = null;
                    }
                    return;
                }
            }

            // Fallback: manual per-stage visuals
            if (seedlingVisual == null && growingVisual == null && matureVisual == null)
                return;

            if (seedlingVisual != null) seedlingVisual.SetActive(growthStage == GrowthStage.Seedling);
            if (growingVisual != null) growingVisual.SetActive(growthStage == GrowthStage.Growing);
            if (matureVisual != null) matureVisual.SetActive(growthStage == GrowthStage.Mature);
        }
    }
}
