using InventorySystem;
using InventorySystem.Items;
using InventorySystem.Items.Firearms;
using InventorySystem.Items.Firearms.Modules;
using InventorySystem.Items.Firearms.ShotEvents;
using PlayerRoles;
using PlayerStatsSystem;
using SCPSLBot.AI;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Combat
{
    /// <summary>
    /// Owns human firearm selection, reload cadence, aiming, and firing behavior.
    /// </summary>
    internal sealed class HumanWeaponCombatStrategy
    {
        private const float ChaseDistance = 11f;
        private const float RetreatDistance = 4.5f;
        private const float MicroHidRange = 6.5f;
        private const float MedicalUseHealthFraction = 0.30f;
        private const float ThrowableMinDistance = 8f;
        private const float ThrowableMaxDistance = 20f;
        private const float SpeedItemHealthFraction = 0.65f;

        private readonly FpcBotCombat combat;
        private float nextReloadAttemptTime;
        private float nextMedicalActionTime;
        private float nextThrowableTime;
        private float nextSurrenderAttemptTime;
        private float nextSpeedItemTime;

        public HumanWeaponCombatStrategy(FpcBotCombat combat)
        {
            this.combat = combat;
        }

        public void Run(CombatTarget target)
        {
            var targetPosition = target.Position;
            if (!target.HasLineOfSight && target.IsInvestigating)
            {
                targetPosition = combat.GetInvestigationPosition(target.Position);
            }

            if (TrySurrenderToOverwhelmingTarget(target))
            {
                return;
            }

            var closedDoor = target.HasLineOfSight && combat.TryCloseDoorBetweenBotAndTarget(target.Hub);
            if (closedDoor || combat.IsUnderPressure)
            {
                if (!combat.TryMoveToCover(targetPosition))
                {
                    combat.RetreatFromTarget(targetPosition);
                }
            }
            else if (target.IsInvestigating)
            {
                combat.MoveToCombatPosition(targetPosition);
            }
            else
            {
                var surfaceDoorBlockingTarget = combat.OpenSurfaceDoorTowardTarget(target.Hub);
                if (surfaceDoorBlockingTarget || target.Distance > ChaseDistance)
                {
                    combat.MoveToCombatPosition(targetPosition);
                }
                else
                {
                    combat.StrafeAroundTarget(targetPosition, target.Distance, RetreatDistance, ChaseDistance);
                }
            }

            combat.BotPlayer.LookToPosition(target.AimPoint);
            if (TryUseMedicalItem())
            {
                combat.RetreatFromTarget(targetPosition);
                return;
            }

            if (closedDoor || target.IsInvestigating || !target.HasLineOfSight)
            {
                return;
            }

            if (TryUseMicroHid(target))
            {
                return;
            }

            if (TryUseSpeedItem(target))
            {
                return;
            }

            if (TryUseThrowable(target))
            {
                return;
            }

            var firearm = EnsureFirearmEquipped();
            if (!target.HasLineOfSight || firearm == null || Time.time < combat.NextShotTime)
            {
                return;
            }

            PrepareActionForShot(firearm);
            if (IsReloading(firearm))
            {
                return;
            }

            if (ShouldReload(firearm))
            {
                TryReloadNormally();
                return;
            }

            var settings = FpcBotCombat.CurrentSettings;
            if (!combat.IsAimedAt(target.AimPoint, settings.AimAngleDegrees))
            {
                return;
            }

            combat.NextShotTime = Time.time + settings.ShotCooldownSeconds;
            if (!combat.TryClickDummyAction("Shoot->Click"))
            {
                FireDirectly(firearm, target.Hub);
            }

            BotManager.Instance.NotifyCombatSound(combat.BotPlayer.BotHub.PlayerHub);
        }

        private bool TryUseMicroHid(CombatTarget target)
        {
            if (target.Distance > MicroHidRange)
            {
                return false;
            }

            var inventory = combat.BotPlayer.BotHub.PlayerHub.inventory;
            var microHid = inventory.UserInventory.Items.Values.FirstOrDefault(item => item.ItemTypeId == ItemType.MicroHID);
            if (microHid == null)
            {
                return false;
            }

            if (inventory.CurInstance != microHid)
            {
                inventory.ServerSelectItem(microHid.ItemSerial);
                return true;
            }

            var settings = FpcBotCombat.CurrentSettings;
            if (!combat.IsAimedAt(target.AimPoint, settings.AimAngleDegrees + 5f))
            {
                return true;
            }

            var used = combat.TryClickFirstDummyAction(
                new[]
                {
                    "MicroHID:Shoot->Click",
                    "MicroHID:Fire->Click",
                    "MicroHID:Shoot->Hold",
                    "MicroHID:Fire->Hold",
                });
            if (used)
            {
                BotManager.Instance.NotifyCombatSound(combat.BotPlayer.BotHub.PlayerHub);
            }

            return used;
        }

        private bool TrySurrenderToOverwhelmingTarget(CombatTarget target)
        {
            var hub = combat.BotPlayer.BotHub.PlayerHub;
            var targetRole = target.Hub.roleManager.CurrentRole.RoleTypeId;
            var targetIsFoundation = targetRole is
                RoleTypeId.NtfCaptain or RoleTypeId.NtfPrivate or RoleTypeId.NtfSergeant
                or RoleTypeId.NtfSpecialist or RoleTypeId.FacilityGuard;
            if (hub.roleManager.CurrentRole.RoleTypeId != RoleTypeId.ClassD
                || target.IsInvestigating
                || target.Distance > 14f
                || (target.Hub.roleManager.CurrentRole.Team != Team.SCPs && !targetIsFoundation)
                || Time.time < nextSurrenderAttemptTime)
            {
                return false;
            }

            var health = hub.playerStats.GetModule<HealthStat>();
            var healthFraction = health.MaxValue > 0f ? health.CurValue / health.MaxValue : 1f;
            var hasFirearm = hub.inventory.UserInventory.Items.Values.Any(item => item is Firearm);
            if (healthFraction > 0.3f && hasFirearm && target.Distance > 7f)
            {
                return false;
            }

            nextSurrenderAttemptTime = Time.time + 8f;
            if (!combat.TryClickFirstDummyAction(
                    new[]
                    {
                    "Surrender:Toggle->Click",
                    "Surrender:RaiseHands->Click",
                    "PlayerSurrender:Toggle->Click",
                    "Surrender->Click",
                    }))
            {
                return false;
            }

            combat.BotPlayer.Move.DesiredLocalDirection = Vector3.zero;
            combat.BotPlayer.LookToPosition(target.AimPoint);
            return true;
        }

        private bool TryUseMedicalItem()
        {
            var hub = combat.BotPlayer.BotHub.PlayerHub;
            var health = hub.playerStats.GetModule<HealthStat>();
            if (health == null || health.MaxValue <= 0f || health.CurValue / health.MaxValue > MedicalUseHealthFraction)
            {
                return false;
            }

            var inventory = hub.inventory;
            var preferredItems = new[]
            {
                ItemType.Medkit,
                ItemType.SCP500,
                ItemType.Adrenaline,
                ItemType.Painkillers,
            };
            ItemBase medicalItem = null;
            foreach (var itemType in preferredItems)
            {
                medicalItem = inventory.UserInventory.Items.Values.FirstOrDefault(item => item.ItemTypeId == itemType);
                if (medicalItem != null)
                {
                    break;
                }
            }

            if (medicalItem == null)
            {
                return false;
            }

            if (inventory.CurInstance != medicalItem)
            {
                inventory.ServerSelectItem(medicalItem.ItemSerial);
                return true;
            }

            if (Time.time < nextMedicalActionTime)
            {
                return true;
            }

            nextMedicalActionTime = Time.time + 0.75f;
            var itemName = medicalItem.ItemTypeId.ToString();
            return combat.TryClickFirstDummyAction(
                new[]
                {
                    $"{itemName}:Use->Click",
                    $"{itemName}:Activate->Click",
                    $"{itemName}:Shoot->Click",
                    "Use->Click",
                });
        }

        private bool TryUseThrowable(CombatTarget target)
        {
            if (target.Distance < ThrowableMinDistance
                || target.Distance > ThrowableMaxDistance
                || Time.time < nextThrowableTime)
            {
                return false;
            }

            var inventory = combat.BotPlayer.BotHub.PlayerHub.inventory;
            var grenade = inventory.UserInventory.Items.Values.FirstOrDefault(item =>
                item.ItemTypeId is ItemType.GrenadeHE or ItemType.GrenadeFlash);
            if (grenade == null)
            {
                return false;
            }

            if (inventory.CurInstance != grenade)
            {
                inventory.ServerSelectItem(grenade.ItemSerial);
                return true;
            }

            if (!combat.IsAimedAt(target.AimPoint, FpcBotCombat.CurrentSettings.AimAngleDegrees + 8f))
            {
                return true;
            }

            var itemName = grenade.ItemTypeId.ToString();
            var used = combat.TryClickFirstDummyAction(
                new[]
                {
                    $"{itemName}:Throw->Click",
                    $"{itemName}:Shoot->Click",
                    $"{itemName}:Use->Click",
                    "Throw->Click",
                });
            if (!used)
            {
                nextThrowableTime = Time.time + 2f;
                var firearm = inventory.UserInventory.Items.Values.OfType<Firearm>().FirstOrDefault();
                if (firearm != null)
                {
                    inventory.ServerSelectItem(firearm.ItemSerial);
                }

                return false;
            }

            nextThrowableTime = Time.time + 8f;
            BotManager.Instance.NotifyCombatSound(combat.BotPlayer.BotHub.PlayerHub);
            return true;
        }

        private bool TryUseSpeedItem(CombatTarget target)
        {
            if (!target.HasLineOfSight
                || target.Distance <= ChaseDistance && !combat.IsUnderPressure
                || Time.time < nextSpeedItemTime)
            {
                return false;
            }

            var hub = combat.BotPlayer.BotHub.PlayerHub;
            var health = hub.playerStats.GetModule<HealthStat>();
            if (health == null || health.MaxValue <= 0f || health.CurValue / health.MaxValue < SpeedItemHealthFraction)
            {
                return false;
            }

            var inventory = hub.inventory;
            var cola = inventory.UserInventory.Items.Values.FirstOrDefault(item => item.ItemTypeId == ItemType.SCP207);
            if (cola == null)
            {
                return false;
            }

            if (inventory.CurInstance != cola)
            {
                inventory.ServerSelectItem(cola.ItemSerial);
                return true;
            }

            nextSpeedItemTime = Time.time + 2f;
            var used = combat.TryClickFirstDummyAction(
                new[]
                {
                    "SCP207:Use->Click",
                    "Scp207:Use->Click",
                    "SCP207:Drink->Click",
                    "Scp207:Drink->Click",
                    "Use->Click",
                });
            if (!used)
            {
                var firearm = inventory.UserInventory.Items.Values.OfType<Firearm>().FirstOrDefault();
                if (firearm != null)
                {
                    inventory.ServerSelectItem(firearm.ItemSerial);
                }
            }

            return used;
        }

        private Firearm EnsureFirearmEquipped()
        {
            var inventory = combat.BotPlayer.BotHub.PlayerHub.inventory;
            if (inventory.CurInstance is Firearm currentFirearm)
            {
                return currentFirearm;
            }

            var firearm = inventory.UserInventory.Items.Values.OfType<Firearm>().FirstOrDefault();
            if (firearm == null)
            {
                return null;
            }

            inventory.ServerSelectItem(firearm.ItemSerial);
            PrepareActionForShot(firearm);
            return firearm;
        }

        private static void PrepareActionForShot(Firearm firearm)
        {
            if (!firearm.TryGetModule<AutomaticActionModule>(out var action))
            {
                return;
            }

            action.Cocked = true;
            if (!action.OpenBolt
                && action.AmmoStored <= 0
                && firearm.TryGetModule<IPrimaryAmmoContainerModule>(out var primaryAmmo)
                && primaryAmmo.AmmoStored > 0)
            {
                action.ServerCycleAction();
            }

            action.BoltLocked = false;
            action.ServerResync();
        }

        private static bool ShouldReload(Firearm firearm)
        {
            if (!firearm.TryGetModule<IPrimaryAmmoContainerModule>(out var primaryAmmo))
            {
                return false;
            }

            return HumanWeaponReloadPolicy.ShouldAttemptReload(
                GetLoadedAmmo(firearm, primaryAmmo),
                firearm.Owner.inventory.GetCurAmmo(primaryAmmo.AmmoType));
        }

        private static int GetLoadedAmmo(Firearm firearm, IPrimaryAmmoContainerModule primaryAmmo)
        {
            var loaded = primaryAmmo.AmmoStored;
            if (firearm.TryGetModule<AutomaticActionModule>(out var action))
            {
                loaded += action.AmmoStored;
            }

            return loaded;
        }

        private static bool IsReloading(Firearm firearm)
        {
            return firearm.TryGetModule<IReloaderModule>(out var reloader) && reloader.IsReloadingOrUnloading;
        }

        private void TryReloadNormally()
        {
            if (Time.time < nextReloadAttemptTime)
            {
                return;
            }

            nextReloadAttemptTime = Time.time + 0.45f;
            combat.TryClickDummyAction("Reload->Click");
        }

        private static void FireDirectly(Firearm firearm, ReferenceHub target)
        {
            if (firearm.TryGetModule<IHitregModule>(out var hitreg))
            {
                hitreg.Fire(target, new BulletShotEvent(firearm.ItemId));
                return;
            }

            target.playerStats.DealDamage(new FirearmDamageHandler(firearm, 18f, 0.35f)
            {
                Hitbox = HitboxType.Body,
            });
        }
    }
}
