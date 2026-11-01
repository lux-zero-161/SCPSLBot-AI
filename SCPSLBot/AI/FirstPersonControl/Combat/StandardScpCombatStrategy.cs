using CustomPlayerEffects;
using PlayerRoles;
using System;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Combat
{
    /// <summary>
    /// Dispatches the standard SCP chase and attack actions for roles without a full bespoke strategy.
    /// </summary>
    internal sealed class StandardScpCombatStrategy
    {
        private const float AttackRange = 3.2f;
        private const float Scp049AttackRange = 2.4f;
        private const float Scp0492AttackRange = 2.4f;
        private const float Scp106AttackRange = 4f;
        private const float StrictAttackAimAngle = 14f;
        private const float Scp049SenseCooldown = 4f;
        private const float SpecialAbilityCooldown = 2.5f;

        private static readonly string[] Scp939AttackActions =
        {
            "Scp939ClawAbility:Shoot->Click",
        };

        private static readonly string[] Scp106AttackActions =
        {
            "Scp106Attack:Shoot->Click",
        };

        private static readonly string[] Scp106MovementActions =
        {
            "Scp106StalkAbility:Shoot->Click",
            "Scp106Stalk:Shoot->Click",
            "Scp106SinkholeAbility:Shoot->Click",
            "Scp106Sinkhole:Shoot->Click",
            "Scp106TeleportAbility:Shoot->Click",
            "Scp106Teleport:Shoot->Click",
            "Scp106PortalAbility:Shoot->Click",
            "Scp106Portal:Shoot->Click",
        };

        private static readonly string[] Scp939UtilityActions =
        {
            "Scp939LungeAbility:Shoot->Click",
            "Scp939Lunge:Shoot->Click",
            "Scp939AmnesticCloudAbility:Shoot->Click",
            "Scp939AmnesticCloud:Shoot->Click",
        };

        private static readonly string[] Scp3114UtilityActions =
        {
            "Scp3114DisguiseAbility:Shoot->Click",
            "Scp3114Disguise:Shoot->Click",
        };

        private static readonly string[] Scp3114AttackActions =
        {
            "Scp3114Slap:Shoot->Click",
        };

        private static readonly string[] Scp096AttackActions =
        {
            "Scp096AttackAbility:Shoot->Click",
        };

        private static readonly string[] Scp049SenseActions =
        {
            "Scp049SenseAbility:ToggleFlashlight->Click",
        };

        private static readonly string[] Scp049AttackActions =
        {
            "Scp049AttackAbility:Shoot->Click",
        };

        private static readonly string[] Scp0492AttackActions =
        {
            "ZombieAttackAbility:Shoot->Click",
            "Shoot->Click",
        };

        private readonly FpcBotCombat combat;
        private readonly Scp096CombatStrategy scp096Strategy;
        private float nextScp049SenseTime;
        private float nextSpecialAbilityTime;

        public StandardScpCombatStrategy(FpcBotCombat combat, Scp096CombatStrategy scp096Strategy)
        {
            this.combat = combat;
            this.scp096Strategy = scp096Strategy;
        }

        public float GetAttackRange(RoleTypeId role)
        {
            return role switch
            {
                RoleTypeId.Scp049 => Scp049AttackRange,
                RoleTypeId.Scp0492 => Scp0492AttackRange,
                RoleTypeId.Scp106 => Scp106AttackRange,
                _ => AttackRange,
            };
        }

        public void TryUseChaseAbility(RoleTypeId role, CombatTarget target)
        {
            if (target == null)
            {
                return;
            }

            if (role == RoleTypeId.Scp049)
            {
                if (Time.time < nextScp049SenseTime)
                {
                    return;
                }

                if (combat.TryClickFirstDummyAction(Scp049SenseActions))
                {
                    nextScp049SenseTime = Time.time + Scp049SenseCooldown;
                }

                return;
            }

            if (Time.time < nextSpecialAbilityTime)
            {
                return;
            }

            var shouldUse = role switch
            {
                RoleTypeId.Scp106 => target.Distance > 6f || !target.HasLineOfSight,
                RoleTypeId.Scp939 => target.Distance > 5f,
                RoleTypeId.Scp3114 => target.Distance > 8f,
                _ => false,
            };
            if (!shouldUse)
            {
                return;
            }

            var actions = role switch
            {
                RoleTypeId.Scp106 => Scp106MovementActions,
                RoleTypeId.Scp939 => Scp939UtilityActions,
                RoleTypeId.Scp3114 => Scp3114UtilityActions,
                _ => Array.Empty<string>(),
            };
            if (combat.TryClickFirstDummyAction(actions))
            {
                nextSpecialAbilityTime = Time.time + SpecialAbilityCooldown;
            }
        }

        public bool TryUseStrictAttack(RoleTypeId role, CombatTarget target, ScpCombatStrategy coordinator)
        {
            if (role is not (RoleTypeId.Scp049 or RoleTypeId.Scp0492 or RoleTypeId.Scp106))
            {
                return false;
            }

            if (target.Distance > GetAttackRange(role) || Time.time < coordinator.NextAttackTime)
            {
                return true;
            }

            if (!combat.IsAimedAt(ScpCombatStrategy.GetAimPoint(target), StrictAttackAimAngle))
            {
                return true;
            }

            coordinator.NextAttackTime = Time.time + coordinator.AttackInterval;
            TryUseAttack(role, target.Hub);
            return true;
        }

        public bool TryUseAttack(RoleTypeId role, ReferenceHub target = null)
        {
            if (role == RoleTypeId.Scp096 && scp096Strategy.TryUseNativeAttack(target))
            {
                return true;
            }

            if (role == RoleTypeId.Scp106 && TryForceScp106PocketOnCorrodingTarget())
            {
                return true;
            }

            var actions = role switch
            {
                RoleTypeId.Scp049 => Scp049AttackActions,
                RoleTypeId.Scp0492 => Scp0492AttackActions,
                RoleTypeId.Scp096 => Scp096AttackActions,
                RoleTypeId.Scp106 => Scp106AttackActions,
                RoleTypeId.Scp3114 => Scp3114AttackActions,
                RoleTypeId.Scp939 => Scp939AttackActions,
                _ => Array.Empty<string>(),
            };

            return combat.TryClickFirstDummyAction(actions);
        }

        private bool TryForceScp106PocketOnCorrodingTarget()
        {
            if (combat.CurrentTarget == null)
            {
                return false;
            }

            var effects = combat.CurrentTarget.playerEffectsController;
            var corroding = effects.GetEffect<Corroding>();
            if (!corroding.IsEnabled)
            {
                return false;
            }

            corroding.AttackerHub = combat.BotPlayer.BotHub.PlayerHub;
            effects.EnableEffect<PocketCorroding>();
            return true;
        }
    }
}
