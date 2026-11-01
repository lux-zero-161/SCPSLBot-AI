using Interactables;
using InventorySystem;
using InventorySystem.Items;
using InventorySystem.Items.Keycards;
using InventorySystem.Items.Pickups;
using MapGeneration;
using MapGeneration.Distributors;
using PlayerRoles;
using Scp914;
using SCPSLBot.AI.FirstPersonControl.Mind.Item.Actions;
using SCPSLBot.AI.FirstPersonControl.Mind.Scp914;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;
using SCPSLBot.Navigation;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl
{
    /// <summary>
    /// Small, stateful human routine for actions that real players do between fights: search a
    /// nearby locker, take a keycard, visit SCP-914 and collect the transformed card. It deliberately
    /// uses the bot navigator for every move; the routine only owns the interaction decisions.
    /// </summary>
    internal sealed class FpcHumanFacilityRoutine
    {
        private enum ProgressState
        {
            Idle,
            TravelTo914,
            DropCard,
            Configure914,
            WaitFor914,
            CollectFrom914,
            VisitCandy,
            LootCandy,
            Finished,
        }

        private const float LockerCooldownSeconds = 1.25f;
        private const float TransformDurationSeconds = 16f;
        private const float CollectionTimeoutSeconds = 12f;

        private readonly FpcBotPlayer botPlayer;
        private readonly HashSet<int> searchedChambers = new();

        private ProgressState state;
        private float nextLockerActionTime;
        private float next914ActionTime;
        private float collectUntil;
        private float transformationStartedAt;
        private int expected914Generation;
        private bool candyVisited;
        private float candySearchUntil;

        public FpcHumanFacilityRoutine(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;
        }

        public bool Tick()
        {
            if (!IsHumanRole())
            {
                return false;
            }

            // Combat always wins. A bot may resume the non-combat state after the sound/target is
            // gone, but it should never stand at a locker while somebody is attacking it.
            if (botPlayer.Combat.DiagnosticHasTarget)
            {
                return false;
            }

            if (state == ProgressState.Finished)
            {
                return false;
            }

            if (state == ProgressState.Idle)
            {
                if (TryPickUpVisibleKeycard())
                {
                    return true;
                }

                if (IsEscapeRole() && TryFindUpgradeableCard(out _))
                {
                    state = ProgressState.TravelTo914;
                }
                else if (ShouldVisitCandy())
                {
                    state = ProgressState.VisitCandy;
                }
                else if (TrySearchLocker())
                {
                    return true;
                }

                if (state == ProgressState.Idle)
                {
                    return false;
                }
            }

            return state switch
            {
                ProgressState.TravelTo914 => TravelTo914(),
                ProgressState.DropCard => DropCardInto914(),
                ProgressState.Configure914 => Configure914(),
                ProgressState.WaitFor914 => WaitFor914(),
                ProgressState.CollectFrom914 => CollectFrom914(),
                ProgressState.VisitCandy => VisitCandyRoom(),
                ProgressState.LootCandy => LootCandyBowl(),
                _ => false,
            };
        }

        public void Reset()
        {
            state = ProgressState.Idle;
            searchedChambers.Clear();
            nextLockerActionTime = 0f;
            next914ActionTime = 0f;
            collectUntil = 0f;
            transformationStartedAt = 0f;
            expected914Generation = 0;
            candyVisited = false;
            candySearchUntil = 0f;
        }

        private bool IsHumanRole()
        {
            var role = botPlayer.BotHub.PlayerHub.roleManager.CurrentRole;
            return role != null
                   && role.Team != Team.SCPs
                   && role.Team != Team.Dead
                   && role.RoleTypeId != RoleTypeId.Spectator
                   && role.RoleTypeId != RoleTypeId.None;
        }

        private bool IsEscapeRole()
        {
            var role = botPlayer.BotHub.PlayerHub.roleManager.CurrentRole.RoleTypeId;
            return role is RoleTypeId.ClassD or RoleTypeId.Scientist;
        }

        private bool TryFindUpgradeableCard(out KeycardItem card)
        {
            var items = botPlayer.BotHub.PlayerHub.inventory?.UserInventory?.Items;
            if (items != null)
            {
                foreach (var item in items.Values)
                {
                    if (item is not KeycardItem candidate)
                    {
                        continue;
                    }

                    var permissions = candidate.GetPermissions(null);
                    if (!permissions.HasFlag(Interactables.Interobjects.DoorUtils.DoorPermissionFlags.ExitGates))
                    {
                        card = candidate;
                        return true;
                    }
                }
            }

            card = null;
            return false;
        }

        private bool TryPickUpVisibleKeycard()
        {
            var sense = botPlayer.Perception.GetSense<ItemsWithinSightSense>();
            ItemPickupBase closest = null;
            var closestDistance = float.PositiveInfinity;
            foreach (var item in sense.ItemsWithinSight)
            {
                if (!IsUsefulPickup(item)
                    || AlreadyCarriesItem(item.Info.ItemId.ToString()))
                {
                    continue;
                }

                var distance = Vector3.Distance(botPlayer.CameraPosition, item.Position);
                if (distance < closestDistance)
                {
                    closest = item;
                    closestDistance = distance;
                }
            }

            if (closest == null)
            {
                return false;
            }

            if (closestDistance > 1.75f)
            {
                botPlayer.MoveToPosition(closest.Position);
                return true;
            }

            botPlayer.LookToPosition(closest.Position);
            return BotPickup.TryComplete(botPlayer.BotHub.PlayerHub, closest);
        }

        private bool IsUsefulPickup(ItemPickupBase item)
        {
            if (InventoryItemLoader.TryGetItem<KeycardItem>(item.Info.ItemId, out _))
            {
                return true;
            }

            return item.Info.ItemId.ToString() is
                "Medkit" or "SCP500" or "Adrenaline" or "Painkillers" or "SCP207" or "SCP330"
                or "ArmorLight" or "ArmorCombat" or "ArmorHeavy"
                or "GrenadeHE" or "GrenadeFlash" or "MicroHID"
                or "GunE11SR" or "GunAK" or "GunFSP9" or "GunShotgun" or "GunLogicer";
        }

        private bool AlreadyCarriesItem(string itemTypeName)
        {
            return botPlayer.BotHub.PlayerHub.inventory.UserInventory.Items.Values
                .Any(item => item.ItemTypeId.ToString() == itemTypeName);
        }

        private bool ShouldVisitCandy()
        {
            return !candyVisited
                   && !AlreadyCarriesItem("SCP330")
                   && Mathf.Abs(botPlayer.BotHub.PlayerHub.PlayerId) % 2 == 0;
        }

        private bool TryGetCandyRoomGoal(out Vector3 goal)
        {
            var backend = NavigationSystem.Instance.Backend;
            if (backend != null)
            {
                foreach (var room in backend.GetNavigableRooms())
                {
                    if (room == null || !string.Equals(room.Name.ToString(), "Lcz330", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var samples = backend.GetRoomSamples(room);
                    if (samples != null && samples.Count > 0)
                    {
                        goal = samples[0];
                        return true;
                    }
                }
            }

            goal = default;
            return false;
        }

        private bool VisitCandyRoom()
        {
            if (!TryGetCandyRoomGoal(out var goal))
            {
                candyVisited = true;
                state = ProgressState.Idle;
                return false;
            }

            if (RoomUtils.TryGetRoom(botPlayer.PlayerPosition, out var room)
                && string.Equals(room?.Name.ToString(), "Lcz330", StringComparison.OrdinalIgnoreCase))
            {
                state = ProgressState.LootCandy;
                candySearchUntil = Time.time + 8f;
                return LootCandyBowl();
            }

            botPlayer.MoveToPosition(goal);
            return true;
        }

        private bool LootCandyBowl()
        {
            var interactables = botPlayer.Perception.GetSense<InteractablesWithinSightSense>();
            var candyInteractable = interactables.InteractableCollidersWithinSight.FirstOrDefault(interactable =>
                interactable?.Target != null
                && (interactable.Target.GetType().Name.IndexOf("Scp330", StringComparison.OrdinalIgnoreCase) >= 0
                    || interactable.name.IndexOf("330", StringComparison.OrdinalIgnoreCase) >= 0
                    || interactable.name.IndexOf("Candy", StringComparison.OrdinalIgnoreCase) >= 0));

            if (candyInteractable == null)
            {
                if (Time.time >= candySearchUntil)
                {
                    candyVisited = true;
                    state = ProgressState.Idle;
                    return false;
                }

                return true;
            }

            var collider = candyInteractable.GetComponent<Collider>();
            var targetPosition = collider != null ? collider.bounds.center : botPlayer.PlayerPosition;
            if (Vector3.Distance(botPlayer.CameraPosition, targetPosition) > 1.8f)
            {
                botPlayer.MoveToPosition(targetPosition);
                return true;
            }

            botPlayer.LookToPosition(targetPosition);
            if (botPlayer.Interact(candyInteractable))
            {
                candyVisited = true;
                state = ProgressState.Idle;
            }

            return true;
        }

        private bool TrySearchLocker()
        {
            if (Time.time < nextLockerActionTime)
            {
                return false;
            }

            var sense = botPlayer.Perception.GetSense<LockersWithinSightSense>();
            LockerChamber selectedChamber = null;
            var selectedDistance = float.PositiveInfinity;
            foreach (var locker in sense.LockersWithinSight)
            {
                if (locker == null || locker.StructureType is not (StructureType.StandardLocker or StructureType.SmallWallCabinet))
                {
                    continue;
                }

                foreach (var chamber in locker.Chambers)
                {
                    if (chamber == null || searchedChambers.Contains(chamber.GetInstanceID()))
                    {
                        continue;
                    }

                    var distance = Vector3.Distance(botPlayer.PlayerPosition, chamber.transform.position);
                    if (distance < selectedDistance)
                    {
                        selectedChamber = chamber;
                        selectedDistance = distance;
                    }
                }
            }

            if (selectedChamber == null)
            {
                return false;
            }

            var frontPosition = selectedChamber.transform.position - selectedChamber.transform.forward * 0.9f;
            frontPosition.y = botPlayer.PlayerPosition.y;
            if (selectedDistance > 2.2f)
            {
                botPlayer.MoveToPosition(frontPosition);
                return true;
            }

            botPlayer.LookToPosition(selectedChamber.transform.position);
            if (botPlayer.OpenLockerDoor(selectedChamber, 2.5f))
            {
                searchedChambers.Add(selectedChamber.GetInstanceID());
                nextLockerActionTime = Time.time + LockerCooldownSeconds;
            }
            else
            {
                nextLockerActionTime = Time.time + 0.2f;
            }

            return true;
        }

        private bool TravelTo914()
        {
            if (!TryGet914Location(out var location) || !location.IntakeChamberPosition.HasValue)
            {
                state = ProgressState.Idle;
                return false;
            }

            if (RoomUtils.TryGetRoom(botPlayer.PlayerPosition, out var room) && room?.Name == RoomName.Lcz914)
            {
                state = ProgressState.DropCard;
                return DropCardInto914();
            }

            botPlayer.MoveToPosition(location.IntakeChamberPosition.Value);
            return true;
        }

        private bool DropCardInto914()
        {
            if (Time.time < next914ActionTime)
            {
                return true;
            }

            if (!TryFindUpgradeableCard(out var card))
            {
                state = ProgressState.Idle;
                return false;
            }

            var intake = Get914Location()?.IntakeChamberPosition;
            if (!intake.HasValue)
            {
                state = ProgressState.Idle;
                return false;
            }

            if (Vector3.Distance(botPlayer.PlayerPosition, intake.Value) > 1.1f)
            {
                botPlayer.MoveToPosition(intake.Value);
                return true;
            }

            botPlayer.BotHub.PlayerHub.inventory.ServerSelectItem(card.ItemSerial);
            card.ServerDropItem(true);
            state = ProgressState.Configure914;
            next914ActionTime = Time.time + 0.5f;
            return true;
        }

        private bool Configure914()
        {
            if (Time.time < next914ActionTime)
            {
                return true;
            }

            if (!TryGet914Location(out var location) || !location.ControlsPosition.HasValue)
            {
                state = ProgressState.Idle;
                return false;
            }

            var controls = botPlayer.MindRunner.GetBelief<Scp914Controls>();
            var running = botPlayer.MindRunner.GetBelief<Scp914RunningOnSetting>();
            if (running.RunningKnobSetting.HasValue)
            {
                state = ProgressState.WaitFor914;
                expected914Generation = running.ActivationGeneration;
                transformationStartedAt = Time.time;
                collectUntil = Time.time + TransformDurationSeconds + CollectionTimeoutSeconds;
                return true;
            }

            if (Vector3.Distance(botPlayer.PlayerPosition, location.ControlsPosition.Value) > 1.7f)
            {
                botPlayer.MoveToPosition(location.ControlsPosition.Value);
                return true;
            }

            if (controls.KnobSetting != Scp914KnobSetting.Fine)
            {
                if (controls.SettingKnob != null && botPlayer.Interact(controls.SettingKnob))
                {
                    next914ActionTime = Time.time + 0.35f;
                }
                else
                {
                    botPlayer.LookToPosition(location.SettingKnobPosition ?? location.ControlsPosition.Value);
                }

                return true;
            }

            if (controls.StartKnob != null && botPlayer.Interact(controls.StartKnob))
            {
                state = ProgressState.WaitFor914;
                expected914Generation = Math.Max(expected914Generation + 1, running.ActivationGeneration);
                transformationStartedAt = Time.time;
                collectUntil = Time.time + TransformDurationSeconds + CollectionTimeoutSeconds;
            }
            else
            {
                botPlayer.LookToPosition(location.StartKnobPosition ?? location.ControlsPosition.Value);
            }

            return true;
        }

        private bool WaitFor914()
        {
            var running = botPlayer.MindRunner.GetBelief<Scp914RunningOnSetting>();
            var transformedThisActivation = running.ItemsTransformedTime.HasValue
                                            && running.ItemsTransformedGeneration >= expected914Generation
                                            && running.ItemsTransformedTime.Value >= transformationStartedAt;
            if (transformedThisActivation || Time.time >= collectUntil - CollectionTimeoutSeconds)
            {
                state = ProgressState.CollectFrom914;
                return CollectFrom914();
            }

            return true;
        }

        private bool CollectFrom914()
        {
            var location = Get914Location();
            if (location == null || !location.OutakeChamberPosition.HasValue)
            {
                state = ProgressState.Finished;
                return false;
            }

            var output = botPlayer.Perception.GetSense<ItemsWithinSightSense>().ItemsWithinSight
                .Where(item => InventoryItemLoader.TryGetItem<KeycardItem>(item.Info.ItemId, out _))
                .Where(item => Vector3.Distance(item.Position, location.OutakeChamberPosition.Value) <= 3.5f)
                .OrderBy(item => Vector3.Distance(botPlayer.CameraPosition, item.Position))
                .FirstOrDefault();
            if (output != null)
            {
                if (Vector3.Distance(botPlayer.CameraPosition, output.Position) > 1.75f)
                {
                    botPlayer.MoveToPosition(output.Position);
                    return true;
                }

                botPlayer.LookToPosition(output.Position);
                if (BotPickup.TryComplete(botPlayer.BotHub.PlayerHub, output))
                {
                    // If the new card still cannot open the exit gates, the next decision cycle
                    // deliberately upgrades it again instead of abandoning the escape plan.
                    state = ProgressState.Idle;
                    return true;
                }

                return false;
            }

            if (Time.time >= collectUntil)
            {
                state = ProgressState.Finished;
                return false;
            }

            botPlayer.MoveToPosition(location.OutakeChamberPosition.Value);
            return true;
        }

        private bool TryGet914Location(out Scp914Location location)
        {
            location = Get914Location();
            return location != null && location.Positions.Count > 0;
        }

        private Scp914Location Get914Location()
        {
            return botPlayer.MindRunner.GetBelief<Scp914Location>();
        }
    }
}
