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
        private bool used914ThisLife;

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

                if (TryFindUpgradeableCard(out _))
                {
                    if (!used914ThisLife)
                    {
                        state = ProgressState.TravelTo914;
                    }
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
            used914ThisLife = false;
        }

        private bool IsHumanRole()
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
                    if (!permissions.HasFlag(Interactables.Interobjects.DoorUtils.DoorPermissionFlags.Checkpoints)
                        && !permissions.HasFlag(Interactables.Interobjects.DoorUtils.DoorPermissionFlags.ExitGates))
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
                if (!InventoryItemLoader.TryGetItem<KeycardItem>(item.Info.ItemId, out _))
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
            if (running.ItemsTransformedTime.HasValue || Time.time >= collectUntil - CollectionTimeoutSeconds)
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
                used914ThisLife = true;
                return false;
            }

            var output = botPlayer.Perception.GetSense<ItemsWithinSightSense>().ItemsWithinSight
                .Where(item => InventoryItemLoader.TryGetItem<KeycardItem>(item.Info.ItemId, out _))
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
                BotPickup.TryComplete(botPlayer.BotHub.PlayerHub, output);
                state = ProgressState.Finished;
                used914ThisLife = true;
                return true;
            }

            if (Time.time >= collectUntil)
            {
                state = ProgressState.Finished;
                used914ThisLife = true;
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
