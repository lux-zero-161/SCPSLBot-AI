using MapGeneration;
using PlayerRoles;
using UnityEngine;
using SCPSLBot.Warmup;

namespace SCPSLBot.AI.FirstPersonControl.Combat
{
    internal sealed class CombatTarget
    {
        public CombatTarget(
            ReferenceHub hub,
            float distance,
            Vector3 aimPoint,
            bool hasLineOfSight,
            Vector3 position,
            bool isInvestigating)
        {
            Hub = hub;
            Distance = distance;
            AimPoint = aimPoint;
            HasLineOfSight = hasLineOfSight;
            Position = position;
            IsInvestigating = isInvestigating;
        }

        public ReferenceHub Hub { get; }
        public float Distance { get; }
        public Vector3 AimPoint { get; }
        public bool HasLineOfSight { get; }
        public Vector3 Position { get; }
        public bool IsInvestigating { get; }
    }

    /// <summary>
    /// Owns hostile classification, line-of-sight probing, and sticky target memory.
    /// </summary>
    internal sealed class CombatTargetSelector
    {
        private const float MaxTargetDistance = 36f;
        private const float SurfaceTargetDistance = 90f;
        private const float HearingDistance = 42f;
        private const float HearingMemorySeconds = 5f;
        private const float CombatFovDot = 0.2f;
        private const float TargetSwitchLockSeconds = 2f;
        private const float TargetSwitchDistanceRatio = 0.65f;

        private static readonly int CombatVisionMask =
            LayerMask.GetMask("Default", "Door", "InteractableNoPlayerCollision", "Glass");

        private readonly FpcBotPlayer botPlayer;
        private float currentTargetChaseUntil;
        private float currentTargetSelectedTime;
        private Vector3 lastKnownTargetPosition;
        private bool hasLastKnownTargetPosition;
        private bool isInvestigating;

        public CombatTargetSelector(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;
        }

        public ReferenceHub CurrentTarget { get; private set; }

        public bool NotifyDamagedBy(ReferenceHub attacker, float chaseSeconds)
        {
            var botHub = botPlayer.BotHub.PlayerHub;
            if (!AreHostile(botHub, attacker))
            {
                return false;
            }

            CurrentTarget = attacker;
            currentTargetSelectedTime = Time.time;
            currentTargetChaseUntil = Time.time + chaseSeconds;
            RememberPosition(attacker.transform.position, investigating: true);
            return true;
        }

        public bool NotifyHeard(ReferenceHub source, Vector3 soundPosition)
        {
            var botHub = botPlayer.BotHub.PlayerHub;
            if (source == null || !AreHostile(botHub, source))
            {
                return false;
            }

            if (Vector3.Distance(botPlayer.PlayerPosition, soundPosition) > HearingDistance)
            {
                return false;
            }

            CurrentTarget = source;
            currentTargetSelectedTime = Time.time;
            currentTargetChaseUntil = Time.time + HearingMemorySeconds;
            RememberPosition(soundPosition, investigating: true);
            return true;
        }

        public void SetCurrentTarget(ReferenceHub target)
        {
            CurrentTarget = target;
            currentTargetSelectedTime = Time.time;
            if (target != null)
            {
                RememberPosition(target.transform.position, investigating: false);
            }
        }

        public void ExtendChase(float chaseSeconds)
        {
            currentTargetChaseUntil = Time.time + chaseSeconds;
        }

        public void ExpireChase()
        {
            currentTargetChaseUntil = 0f;
        }

        public void Clear()
        {
            CurrentTarget = null;
            currentTargetSelectedTime = 0f;
            currentTargetChaseUntil = 0f;
            hasLastKnownTargetPosition = false;
            isInvestigating = false;
        }

        public bool TrySelectVisibleTarget(out CombatTarget target)
        {
            target = null;
            var botHub = botPlayer.BotHub.PlayerHub;
            var botPosition = botPlayer.PlayerPosition;

            CombatTarget best = null;
            CombatTarget currentVisible = null;
            foreach (var candidate in CombatWorldSnapshot.Get(Time.time))
            {
                if (!AreHostile(botHub, candidate))
                {
                    continue;
                }

                var distance = Vector3.Distance(botPosition, candidate.Position);
                if (distance > MaxTargetDistance)
                {
                    continue;
                }

                if (!IsWithinCombatFov(candidate.Position))
                {
                    continue;
                }

                var selection = BuildTarget(candidate.Hub, distance);
                if (selection == null)
                {
                    continue;
                }

                if (candidate.Hub == CurrentTarget)
                {
                    currentVisible = selection;
                }

                if (best == null || selection.Distance < best.Distance)
                {
                    best = selection;
                }
            }

            target = SelectStickyVisibleTarget(best, currentVisible);
            if (target != null)
            {
                RememberPosition(target.Hub.transform.position, investigating: false);
            }
            return target != null;
        }

        private CombatTarget SelectStickyVisibleTarget(CombatTarget best, CombatTarget currentVisible)
        {
            if (best == null || currentVisible == null || best.Hub == CurrentTarget)
            {
                return best;
            }

            if (Time.time - currentTargetSelectedTime < TargetSwitchLockSeconds)
            {
                return currentVisible;
            }

            return best.Distance <= currentVisible.Distance * TargetSwitchDistanceRatio
                ? best
                : currentVisible;
        }

        public bool TrySelectSurfaceTarget(out CombatTarget target)
        {
            target = null;
            var botHub = botPlayer.BotHub.PlayerHub;
            if (!IsOnSurface(botPlayer.PlayerPosition))
            {
                return false;
            }

            CombatTarget best = null;
            foreach (var candidate in CombatWorldSnapshot.Get(Time.time))
            {
                if (!AreHostile(botHub, candidate) || !candidate.IsOnSurface)
                {
                    continue;
                }

                var distance = Vector3.Distance(botPlayer.PlayerPosition, candidate.Position);
                if (distance > SurfaceTargetDistance)
                {
                    continue;
                }

                if (!IsWithinCombatFov(candidate.Position))
                {
                    continue;
                }

                var selection = BuildTarget(candidate.Hub, distance);
                if (selection == null)
                {
                    continue;
                }

                if (best == null || selection.Distance < best.Distance)
                {
                    best = selection;
                }
            }

            target = best;
            if (target != null)
            {
                RememberPosition(target.Hub.transform.position, investigating: false);
            }
            return target != null;
        }

        public bool TrySelectRememberedTarget(out CombatTarget target)
        {
            target = null;
            if (CurrentTarget == null || Time.time > currentTargetChaseUntil)
            {
                return false;
            }

            if (!AreHostile(botPlayer.BotHub.PlayerHub, CurrentTarget))
            {
                return false;
            }

            if (!hasLastKnownTargetPosition)
            {
                RememberPosition(CurrentTarget.transform.position, investigating: true);
            }

            var distance = Vector3.Distance(botPlayer.PlayerPosition, lastKnownTargetPosition);
            if (distance > MaxTargetDistance * 1.5f)
            {
                return false;
            }

            // Once line of sight is gone, keep the last position fixed and investigate around it;
            // never read the hidden player's current transform to continue a chase through walls.
            target = BuildTarget(CurrentTarget, distance, false, lastKnownTargetPosition, true);
            return true;
        }

        public CombatTarget BuildTarget(
            ReferenceHub hub,
            float distance,
            bool requireLineOfSight = true,
            Vector3? knownPosition = null,
            bool investigating = false)
        {
            if (hub == null)
            {
                return null;
            }

            var bodyBase = knownPosition ?? hub.transform.position;
            var camera = knownPosition.HasValue ? null : hub.PlayerCameraReference;
            var headAimPoint = camera != null ? camera.position : bodyBase + Vector3.up * 1.65f;
            var torsoAimPoint = Vector3.Lerp(bodyBase, headAimPoint, 0.55f);

            if (!requireLineOfSight)
            {
                return new CombatTarget(hub, distance, torsoAimPoint, false, bodyBase, investigating);
            }

            if (HasLineOfSight(botPlayer.CameraPosition, headAimPoint))
            {
                return new CombatTarget(hub, distance, headAimPoint, true, bodyBase, false);
            }

            if (HasLineOfSight(botPlayer.CameraPosition, torsoAimPoint))
            {
                return new CombatTarget(hub, distance, torsoAimPoint, true, bodyBase, false);
            }

            return null;
        }

        public static bool AreHostile(ReferenceHub bot, ReferenceHub candidate)
        {
            if (candidate == null || candidate == bot || !IsCombatTarget(candidate) || !IsCombatTarget(bot))
            {
                return false;
            }

            if (!WarmupManager.Instance.CanHubsFightInWarmup(bot, candidate))
            {
                return false;
            }

            var botTeam = bot.roleManager.CurrentRole.Team;
            var candidateTeam = candidate.roleManager.CurrentRole.Team;
            if (botTeam == Team.SCPs)
            {
                return candidateTeam != Team.SCPs;
            }

            if (candidateTeam == Team.SCPs)
            {
                return true;
            }

            var botRole = bot.roleManager.CurrentRole.RoleTypeId;
            var candidateRole = candidate.roleManager.CurrentRole.RoleTypeId;
            if (IsFoundationHumanRole(botRole) && IsFoundationHumanRole(candidateRole))
            {
                return false;
            }

            if (IsChaosHumanRole(botRole) && IsChaosHumanRole(candidateRole))
            {
                return false;
            }

            return candidateTeam != botTeam;
        }

        private static bool AreHostile(ReferenceHub bot, in CombatWorldEntry candidate)
        {
            if (candidate.Hub == bot || !IsCombatTarget(bot) || !IsCombatTarget(candidate.Hub))
            {
                return false;
            }

            if (!WarmupManager.Instance.CanHubsFightInWarmup(bot, candidate.Hub))
            {
                return false;
            }

            var currentCandidateRole = candidate.Hub.roleManager.CurrentRole;
            if (currentCandidateRole.RoleTypeId != candidate.Role || currentCandidateRole.Team != candidate.Team)
            {
                return AreHostile(bot, candidate.Hub);
            }

            var botRole = bot.roleManager.CurrentRole.RoleTypeId;
            var botTeam = bot.roleManager.CurrentRole.Team;
            if (botTeam == Team.SCPs)
            {
                return candidate.Team != Team.SCPs;
            }

            if (candidate.Team == Team.SCPs)
            {
                return true;
            }

            if (IsFoundationHumanRole(botRole) && IsFoundationHumanRole(candidate.Role))
            {
                return false;
            }

            if (IsChaosHumanRole(botRole) && IsChaosHumanRole(candidate.Role))
            {
                return false;
            }

            return candidate.Team != botTeam;
        }

        public static bool IsOnSurface(Vector3 position)
        {
            return RoomUtils.TryGetRoom(position, out var room) && room.Zone == FacilityZone.Surface;
        }

        private static bool IsCombatTarget(ReferenceHub hub)
        {
            if (hub == null)
            {
                return false;
            }

            var role = hub.roleManager.CurrentRole;
            return role.RoleTypeId != RoleTypeId.None
                   && role.RoleTypeId != RoleTypeId.Spectator
                   && role.Team != Team.Dead
                   && hub.IsAlive();
        }

        private static bool IsFoundationHumanRole(RoleTypeId role)
        {
            return role is RoleTypeId.NtfCaptain
                or RoleTypeId.NtfPrivate
                or RoleTypeId.NtfSergeant
                or RoleTypeId.NtfSpecialist
                or RoleTypeId.FacilityGuard
                or RoleTypeId.Scientist;
        }

        private static bool IsChaosHumanRole(RoleTypeId role)
        {
            return role is RoleTypeId.ChaosConscript
                or RoleTypeId.ChaosMarauder
                or RoleTypeId.ChaosRepressor
                or RoleTypeId.ChaosRifleman
                or RoleTypeId.ClassD;
        }

        private static bool HasLineOfSight(Vector3 origin, Vector3 aimPoint)
        {
            var direction = aimPoint - origin;
            if (direction.sqrMagnitude < 0.0001f)
            {
                return true;
            }

            // Environment-only linecast retains the allocation-free hot path from the original combat loop.
            return !Physics.Linecast(origin, aimPoint, CombatVisionMask, QueryTriggerInteraction.Ignore);
        }

        private bool IsWithinCombatFov(Vector3 targetPosition)
        {
            var direction = targetPosition - botPlayer.CameraPosition;
            return direction.sqrMagnitude < 0.01f
                   || Vector3.Dot(botPlayer.CameraForward.normalized, direction.normalized) >= CombatFovDot;
        }

        private void RememberPosition(Vector3 position, bool investigating)
        {
            lastKnownTargetPosition = position;
            hasLastKnownTargetPosition = true;
            isInvestigating = investigating;
        }
    }
}
