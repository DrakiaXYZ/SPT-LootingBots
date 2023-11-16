using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using DrakiaXYZ.BigBrain.Brains;

using EFT;
using EFT.Interactive;

using LootingBots.Patch.Components;
using LootingBots.Patch.Util;

using UnityEngine;
using UnityEngine.AI;

namespace LootingBots.Brain.Logics
{
    internal class FindLootLogic : CustomLogic
    {
        private readonly LootingBrain _lootingBrain;
        private readonly BotLog _log;
        private readonly Collider[] _colliders;

        private float DetectCorpseDistance
        {
            get { return Mathf.Pow(LootingBots.DetectCorpseDistance.Value, 2); }
        }
        private float DetectContainerDistance
        {
            get { return Mathf.Pow(LootingBots.DetectContainerDistance.Value, 2); }
        }
        private float DetectItemDistance
        {
            get { return Mathf.Pow(LootingBots.DetectItemDistance.Value, 2); }
        }

        public FindLootLogic(BotOwner botOwner)
            : base(botOwner)
        {
            _log = new BotLog(LootingBots.LootLog, botOwner);
            _lootingBrain = botOwner.GetPlayer.gameObject.GetComponent<LootingBrain>();
            _colliders = new Collider[250];
        }

        public enum LootType
        {
            Corpse = 0,
            Container = 1,
            Item = 2
        }

        public override void Update()
        {
            // If the bot has more than the reserved amount of slots needed for ammo, trigger a loot scan
            if (_lootingBrain.Stats.AvailableGridSpaces > LootUtils.RESERVED_SLOT_COUNT)
            {
                FindLootable();
            }
        }

        public void FindLootable()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            // Use the largest detection radius specified in the settings as the main Sphere radius
            float detectionRadius = Mathf.Max(
                LootingBots.DetectItemDistance.Value,
                LootingBots.DetectContainerDistance.Value,
                LootingBots.DetectCorpseDistance.Value
            );

            // Cast a sphere on the bot, detecting any Interacive world objects that collide with the sphere
            var colliderCount = Physics.OverlapSphereNonAlloc(
                BotOwner.Position,
                detectionRadius,
                _colliders,
                LootUtils.LootMask,
                QueryTriggerInteraction.Collide);

            _log.LogInfo($"OverlapSphere: {stopwatch.ElapsedMilliseconds}ms  Items: {colliderCount}");
            stopwatch.Restart();

            // Create a list from just the newly added Colliders (OverlapSphereNonAlloc doesn't clear the array)
            List<Collider> colliders = new List<Collider>();
            for (int j = 0; j < colliderCount; j++) colliders.Add(_colliders[j]);

            // Sort by nearest to bot location
            colliders.Sort((a, b) =>
            {
                var distA = Vector3.Distance(a.bounds.center, BotOwner.Position);
                var distB = Vector3.Distance(b.bounds.center, BotOwner.Position);
                return distA.CompareTo(distB);
            });

            _log.LogInfo($"Sort: {stopwatch.ElapsedMilliseconds}ms");
            stopwatch.Restart();

            // For each object detected, check to see if it is a lootable container and return the first valid entry, since it's sorted by distance already
            foreach (Collider collider in colliders)
            {
                if (collider == null) continue;

                LootableContainer container =
                    collider.gameObject.GetComponentInParent<LootableContainer>();
                LootItem lootItem = collider.gameObject.GetComponentInParent<LootItem>();
                BotOwner corpse = collider.gameObject.GetComponentInParent<BotOwner>();

                bool canLootContainer =
                    LootingBots.ContainerLootingEnabled.Value.IsBotEnabled(
                        BotOwner.Profile.Info.Settings.Role
                    )
                    && container != null // Container exists
                    && !_lootingBrain.IsLootIgnored(container.Id) // Container is not ignored
                    && container.isActiveAndEnabled // Container is marked as active and enabled
                    && container.DoorState != EDoorState.Locked; // Container is not locked

                bool canLootItem =
                    LootingBots.LooseItemLootingEnabled.Value.IsBotEnabled(
                        BotOwner.Profile.Info.Settings.Role
                    )
                    && lootItem != null
                    && !(lootItem is Corpse) // Item is not a corpse
                    && lootItem?.ItemOwner?.RootItem != null // Item exists
                    && !lootItem.ItemOwner.RootItem.QuestItem // Item is not a quest item
                    && _lootingBrain.IsValuableEnough(lootItem.ItemOwner.RootItem) // Item meets value threshold
                    && _lootingBrain.Stats.AvailableGridSpaces
                        > lootItem.ItemOwner.RootItem.GetItemSize() // Bot has enough space to pickup
                    && !_lootingBrain.IsLootIgnored(lootItem.ItemOwner.RootItem.Id); // Item not ignored

                bool canLootCorpse =
                    LootingBots.CorpseLootingEnabled.Value.IsBotEnabled(
                        BotOwner.Profile.Info.Settings.Role
                    )
                    && corpse != null // Corpse exists
                    && corpse.GetPlayer != null // Corpse is a bot corpse and not a static "Dead scav" corpse
                    && !_lootingBrain.IsLootIgnored(corpse.name); // Corpse is not ignored

                if (canLootContainer || canLootItem || canLootCorpse)
                {
                    Vector3 center = collider.bounds.center;
                    // Push the center point to the lowest y point in the collider. Extend it further down by .3f to help container positions of jackets snap to a valid NavMesh
                    center.y = collider.bounds.center.y - collider.bounds.extents.y - 0.4f;

                    // If we havent already visted the lootable, calculate its distance and save the lootable with the shortest distance
                    LootType lootType =
                        container != null
                            ? LootType.Container
                            : lootItem != null
                                ? LootType.Item
                                : LootType.Corpse;

                    Vector3 destination = GetDestination(center);
                    bool isInRange = IsLootInRange(lootType, destination, out float dist);

                    // If we are considering a lootable to be the new closest lootable, make sure the loot is in the detection range specified for the type of loot
                    if (isInRange)
                    {
                        if (canLootContainer)
                        {
                            _lootingBrain.ActiveContainer = container;
                            _lootingBrain.LootObjectPosition = container.transform.position;
                            ActiveLootCache.CacheActiveLootId(container.Id, BotOwner.name);
                            _lootingBrain.DistanceToLoot = dist;
                            _lootingBrain.Destination = destination;
                            break;
                        }
                        else if (canLootCorpse)
                        {
                            _lootingBrain.ActiveCorpse = corpse;
                            _lootingBrain.LootObjectPosition = corpse.Transform.position;
                            ActiveLootCache.CacheActiveLootId(corpse.name, BotOwner.name);
                            _lootingBrain.DistanceToLoot = dist;
                            _lootingBrain.Destination = destination;
                            break;
                        }
                        else
                        {
                            _lootingBrain.ActiveItem = lootItem;
                            _lootingBrain.LootObjectPosition = lootItem.transform.position;
                            ActiveLootCache.CacheActiveLootId(lootItem.ItemOwner.RootItem.Id, BotOwner.name);
                            _lootingBrain.DistanceToLoot = dist;
                            _lootingBrain.Destination = destination;
                            break;
                        }
                    }
                }
            }

            stopwatch.Stop();
            _log.LogInfo($"Loot search time: {stopwatch.ElapsedMilliseconds}ms");
        }

        /**
        * Checks to see if any of the found lootable items are within their detection range specified in the mod settings.
        */
        public bool IsLootInRange(LootType lootType, Vector3 destination, out float dist)
        {
            bool isContainer = lootType == LootType.Container;
            bool isItem = lootType == LootType.Item;
            bool isCorpse = lootType == LootType.Corpse;

            if (destination == Vector3.zero)
            {
                dist = -1f;
                _log.LogDebug($"Unable to snap loot position to NavMesh. Ignoring");
                return false;
            }

            dist = BotOwner.Mover.ComputePathLengthToPoint(destination);

            return (isContainer && DetectContainerDistance >= dist)
                || (isItem && DetectItemDistance >= dist)
                || (isCorpse && DetectCorpseDistance >= dist);
        }

        Vector3 GetDestination(Vector3 center)
        {
            // Try to snap the desired destination point to the nearest NavMesh to ensure the bot can draw a navigable path to the point
            Vector3 pointNearbyContainer = NavMesh.SamplePosition(
                center,
                out NavMeshHit navMeshAlignedPoint,
                1f,
                NavMesh.AllAreas
            )
                ? navMeshAlignedPoint.position
                : Vector3.zero;

            // Since SamplePosition always snaps to the closest point on the NavMesh, sometimes this point is a little too close to the loot and causes the bot to shake violently while looting.
            // Add a small amount of padding by pushing the point away from the nearbyPoint
            Vector3 padding = center - pointNearbyContainer;
            padding.y = 0;
            padding.Normalize();

            // Make sure the point is still snapped to the NavMesh after its been pushed
            return NavMesh.SamplePosition(
                center - padding,
                out navMeshAlignedPoint,
                1f,
                navMeshAlignedPoint.mask
            )
                ? navMeshAlignedPoint.position
                : pointNearbyContainer;
        }
    }
}
