using Interactables;
using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using NetworkManagerUtils.Dummies;
using PlayerRoles;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Combat
{
    /// <summary>
    /// Coordinates target acquisition, movement, and the role-specific combat strategies.
    /// Per-role state lives in the strategy components so this type only owns shared execution state.
    /// </summary>
    internal sealed class FpcBotCombat
    {
        private const float DoorInteractDistance = 2f;
        private const float DoorCloseCooldownSeconds = 1.25f;
        private const float SearchStepSeconds = 0.9f;
        private const float SearchRadius = 2.2f;

        private static readonly int DoorMask = LayerMask.GetMask("Door");

        private readonly FpcBotPlayer botPlayer;
        private readonly CombatTargetSelector targetSelector;
        private readonly HumanWeaponCombatStrategy humanStrategy;
        private readonly ScpCombatStrategy scpStrategy;
        private readonly List<DummyAction> dummyActions = new();
        private readonly System.Random random = new();

        private int dummyActionsFrame = -1;
        private float nextStrafeFlipTime;
        private float nextShotTime;
        private float nextSurfaceDoorDebugLogTime;
        private float nextDoorActionTime;
        private float retreatUntil;
        private float nextSearchStepTime;
        private int searchStep;
        private int strafeDirection = 1;

        public static BotCombatDifficulty Difficulty { get; set; } = BotCombatDifficulty.Hardest;

        public bool DiagnosticHasTarget => targetSelector.CurrentTarget != null;
        public bool DiagnosticHasLineOfSight { get; private set; }
        public string DiagnosticTarget => targetSelector.CurrentTarget?.nicknameSync?.MyNick ?? "none";
        public RoleTypeId DiagnosticTargetRole => targetSelector.CurrentTarget?.roleManager?.CurrentRole?.RoleTypeId ?? RoleTypeId.None;
        public string DiagnosticState { get; private set; } = "idle";

        internal FpcBotPlayer BotPlayer => botPlayer;
        internal ReferenceHub CurrentTarget => targetSelector.CurrentTarget;
        internal int StrafeDirection => strafeDirection;
        internal float NextShotTime
        {
            get => nextShotTime;
            set => nextShotTime = value;
        }

        internal bool IsUnderPressure => Time.time < retreatUntil;

        internal static BotCombatDifficultySettings CurrentSettings => BotCombatDifficultySettings.For(Difficulty);

        public FpcBotCombat(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;
            targetSelector = new CombatTargetSelector(botPlayer);
            humanStrategy = new HumanWeaponCombatStrategy(this);
            scpStrategy = new ScpCombatStrategy(this, targetSelector);
        }

        public void NotifyDamagedBy(ReferenceHub attacker)
        {
            if (!targetSelector.NotifyDamagedBy(attacker, CurrentSettings.ChaseAfterLostLosSeconds))
            {
                return;
            }

            scpStrategy.NotifyDamaged();
            nextStrafeFlipTime = 0f;
            retreatUntil = Time.time + 2.5f;
            searchStep = 0;
            nextSearchStepTime = 0f;
        }

        public void NotifyHeard(ReferenceHub source, Vector3 soundPosition)
        {
            if (targetSelector.NotifyHeard(source, soundPosition))
            {
                searchStep = 0;
                nextSearchStepTime = 0f;
            }
        }

        public bool Tick()
        {
            var settings = CurrentSettings;
            var botHub = botPlayer.BotHub.PlayerHub;
            var role = botHub.roleManager.CurrentRole.RoleTypeId;
            scpStrategy.PrepareForTick(role);

            CombatTarget target;
            if (scpStrategy.TrySelectPriorityTarget(role, out target))
            {
                SetCurrentTarget(target.Hub, targetSelector.CurrentTarget != target.Hub);
                targetSelector.ExtendChase(settings.ChaseAfterLostLosSeconds);
            }
            else if (targetSelector.TrySelectVisibleTarget(out target))
            {
                if (targetSelector.CurrentTarget != target.Hub)
                {
                    SetCurrentTarget(target.Hub, true);
                }

                targetSelector.ExtendChase(settings.ChaseAfterLostLosSeconds);
            }
            else if (!targetSelector.TrySelectRememberedTarget(out target)
                     && !targetSelector.TrySelectSurfaceTarget(out target))
            {
                targetSelector.Clear();
                DiagnosticHasLineOfSight = false;
                DiagnosticState = "idle";
                scpStrategy.ClearPriorityTarget();
                return false;
            }
            else if (target != null && targetSelector.CurrentTarget != target.Hub)
            {
                SetCurrentTarget(target.Hub, true);
                targetSelector.ExtendChase(settings.ChaseAfterLostLosSeconds);
            }

            UpdateStrafeDirection();
            scpStrategy.ReleaseHeldInputsIfNeeded();

            if (target == null)
            {
                DiagnosticHasLineOfSight = false;
                DiagnosticState = "idle";
                return false;
            }

            var isScp = botHub.roleManager.CurrentRole.Team == Team.SCPs;
            DiagnosticHasLineOfSight = target.HasLineOfSight;
            DiagnosticState = isScp ? "scp_combat" : "human_combat";

            if (isScp)
            {
                scpStrategy.Run(target, role);
            }
            else
            {
                humanStrategy.Run(target);
            }

            return true;
        }

        private void SetCurrentTarget(ReferenceHub target, bool resetCombatTiming)
        {
            targetSelector.SetCurrentTarget(target);
            if (!resetCombatTiming)
            {
                return;
            }

            nextStrafeFlipTime = 0f;
            nextShotTime = 0f;
        }

        internal void MoveToCombatPosition(Vector3 targetPosition)
        {
            botPlayer.MoveToPosition(targetPosition);
            OpenBlockingDoorOnCombatPath();
        }

        internal Vector3 GetInvestigationPosition(Vector3 lastKnownPosition)
        {
            if (Time.time >= nextSearchStepTime)
            {
                nextSearchStepTime = Time.time + SearchStepSeconds;
                searchStep++;
            }

            var angle = (searchStep % 8) * Mathf.PI * 0.25f;
            var candidate = lastKnownPosition + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * SearchRadius;
            return botPlayer.Navigator.CanReach(candidate) ? candidate : lastKnownPosition;
        }

        internal void RetreatFromTarget(Vector3 targetPosition)
        {
            var away = Vector3.ProjectOnPlane(botPlayer.PlayerPosition - targetPosition, Vector3.up);
            if (away.sqrMagnitude < 0.01f)
            {
                away = -Vector3.ProjectOnPlane(botPlayer.PlayerForward, Vector3.up);
            }

            away.Normalize();
            var side = Vector3.Cross(Vector3.up, away).normalized * strafeDirection * 0.3f;
            var worldMove = Vector3.Normalize(away + side);
            botPlayer.Move.DesiredLocalDirection = botPlayer.FpcRole.FpcModule.transform.InverseTransformDirection(worldMove);
        }

        /// <summary>
        /// Human players often close a door after spotting a pursuer. Only close a door that is
        /// actually between the bot and its target, and rate-limit the interaction so an open /
        /// close race cannot happen while both players are in the doorway.
        /// </summary>
        internal bool TryCloseDoorBetweenBotAndTarget(ReferenceHub target)
        {
            if (target == null || Time.time < nextDoorActionTime)
            {
                return false;
            }

            var botPosition = botPlayer.PlayerPosition + Vector3.up;
            var targetPosition = target.transform.position + Vector3.up;
            var direction = targetPosition - botPosition;
            var distance = direction.magnitude;
            if (distance < 3f)
            {
                return false;
            }

            foreach (var hit in Physics.RaycastAll(botPosition, direction / distance, distance, DoorMask, QueryTriggerInteraction.Ignore))
            {
                var door = hit.collider.GetComponentInParent<DoorVariant>();
                if (TryCloseDoor(door, targetPosition))
                {
                    return true;
                }
            }

            // An open leaf can be outside the straight ray after it swung aside. The perception
            // sense still knows the nearby door, so use its plane as a second, conservative test.
            var doors = botPlayer.Perception.DoorsSense?.DoorsWithinSight;
            if (doors != null)
            {
                foreach (var door in doors)
                {
                    if (TryCloseDoor(door, targetPosition))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool TryCloseDoor(DoorVariant door, Vector3 targetPosition)
        {
            if (door == null
                || !door.IsConsideredOpen()
                || door is DummyDoor
                || door is ElevatorDoor
                || door is BasicNonInteractableDoor)
            {
                return false;
            }

            var doorPosition = door.transform.position + Vector3.up;
            var botSide = Vector3.Dot(door.transform.forward, botPlayer.PlayerPosition - door.transform.position);
            var targetSide = Vector3.Dot(door.transform.forward, targetPosition - door.transform.position);
            if (botSide * targetSide >= 0f
                || Vector3.Distance(botPlayer.PlayerPosition, doorPosition) > DoorInteractDistance + 0.9f
                || Vector3.Distance(targetPosition, doorPosition) < 2.75f)
            {
                return false;
            }

            if (!botPlayer.InteractDoorDirectly(door, DoorInteractDistance + 0.9f))
            {
                return false;
            }

            nextDoorActionTime = Time.time + DoorCloseCooldownSeconds;
            return true;
        }

        private void OpenBlockingDoorOnCombatPath()
        {
            foreach (var (point, nextPoint) in botPlayer.Navigator.PathSegments)
            {
                if (!Physics.Linecast(point, nextPoint, out var hit, LayerMask.GetMask("Door")))
                {
                    continue;
                }

                var door = hit.collider.GetComponentInParent<DoorVariant>();
                if (!CanOpenCombatDoor(door))
                {
                    continue;
                }

                var doorPlane = new Plane(door.transform.forward, door.transform.position);
                var distance = Mathf.Abs(doorPlane.GetDistanceToPoint(botPlayer.PlayerPosition));

                if (distance <= DoorInteractDistance)
                {
                    if (!botPlayer.OpenDoor(door, DoorInteractDistance))
                    {
                        botPlayer.LookToPosition(door.transform.position + Vector3.up);
                    }
                }
                else
                {
                    botPlayer.MoveToPosition(hit.point);
                }

                return;
            }
        }

        private static bool CanOpenCombatDoor(DoorVariant door)
        {
            return door
                   && !door.IsConsideredOpen()
                   && door is not DummyDoor
                   && door is not ElevatorDoor
                   && door is not BasicNonInteractableDoor;
        }

        internal bool OpenSurfaceDoorTowardTarget(ReferenceHub target)
        {
            if (target == null
                || !CombatTargetSelector.IsOnSurface(botPlayer.PlayerPosition)
                || !CombatTargetSelector.IsOnSurface(target.transform.position))
            {
                return false;
            }

            var origin = botPlayer.PlayerPosition + Vector3.up * 0.8f;
            var destination = target.transform.position + Vector3.up * 0.8f;
            var direction = destination - origin;
            var distanceToTarget = direction.magnitude;
            if (distanceToTarget < 0.1f)
            {
                return false;
            }

            var hits = Physics.RaycastAll(
                origin,
                direction / distanceToTarget,
                distanceToTarget,
                LayerMask.GetMask("Door", "Glass"),
                QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));

            foreach (var hit in hits)
            {
                var door = hit.collider.GetComponentInParent<DoorVariant>();
                if (!CanOpenCombatDoor(door))
                {
                    continue;
                }

                var distanceToDoor = Vector3.Distance(botPlayer.PlayerPosition, hit.point);
                LogSurfaceDoorDebug($"blocked by {door.name} at {distanceToDoor:F1}m.");

                if (distanceToDoor <= DoorInteractDistance + 0.75f)
                {
                    botPlayer.LookToPosition(door.transform.position + Vector3.up);
                    if (!botPlayer.OpenDoor(door, DoorInteractDistance + 0.75f)
                        && !botPlayer.InteractDoorDirectly(door, DoorInteractDistance + 0.75f))
                    {
                        LogSurfaceDoorDebug($"interaction failed for {door.name}.");
                    }
                }
                else
                {
                    botPlayer.MoveToPosition(hit.point);
                }

                return true;
            }

            return false;
        }

        private void LogSurfaceDoorDebug(string message)
        {
            if (Time.time < nextSurfaceDoorDebugLogTime)
            {
                return;
            }

            nextSurfaceDoorDebugLogTime = Time.time + 1f;
            if (BotLog.Verbose)
            {
                Debug.Log($"[SCPSLBot] Surface door: {message}");
            }
        }

        internal void StrafeAroundTarget(Vector3 targetPosition, float distance, float retreatDistance, float chaseDistance)
        {
            var settings = CurrentSettings;
            var toTarget = Vector3.ProjectOnPlane(targetPosition - botPlayer.PlayerPosition, Vector3.up);
            if (toTarget.sqrMagnitude < 0.01f)
            {
                botPlayer.Move.DesiredLocalDirection = Vector3.zero;
                return;
            }

            var forward = toTarget.normalized;
            var right = Vector3.Cross(Vector3.up, forward).normalized * strafeDirection;
            var distanceBias = Vector3.zero;

            if (distance < retreatDistance)
            {
                distanceBias = -forward;
            }
            else if (distance > chaseDistance * 0.75f)
            {
                distanceBias = forward * 0.45f;
            }

            var worldMove = Vector3.Normalize(right + distanceBias) * settings.StrafeSpeed;
            botPlayer.Move.DesiredLocalDirection = botPlayer.FpcRole.FpcModule.transform.InverseTransformDirection(worldMove);
        }

        internal bool TryClickFirstDummyAction(IEnumerable<string> actionNames)
        {
            foreach (var actionName in actionNames)
            {
                if (TryClickCategorizedOrFlatDummyAction(actionName))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryClickCategorizedOrFlatDummyAction(string actionName)
        {
            var separatorIndex = actionName.IndexOf(':');
            return separatorIndex > 0 && separatorIndex < actionName.Length - 1
                ? TryClickGroupedDummyAction(actionName[..separatorIndex], actionName[(separatorIndex + 1)..])
                : TryClickDummyAction(actionName);
        }

        private void EnsureDummyActions()
        {
            if (dummyActionsFrame == Time.frameCount)
            {
                return;
            }

            dummyActionsFrame = Time.frameCount;
            dummyActions.Clear();
            dummyActions.AddRange(DummyActionCollector.ServerGetActions(botPlayer.BotHub.PlayerHub));
            botPlayer.BotHub.PlayerHub.inventory.PopulateDummyActions(dummyActions.Add, _ => { });
        }

        internal bool TryClickDummyAction(string actionName)
        {
            if (string.IsNullOrWhiteSpace(actionName))
            {
                return false;
            }

            EnsureDummyActions();
            var dummyAction = FindDummyAction(actionName);
            if (dummyAction.Action == null)
            {
                return false;
            }

            dummyAction.Action.Invoke();
            dummyActionsFrame = -1;
            return true;
        }

        internal bool TryClickGroupedDummyAction(string categoryName, string actionName)
        {
            if (string.IsNullOrWhiteSpace(categoryName) || string.IsNullOrWhiteSpace(actionName))
            {
                return false;
            }

            EnsureDummyActions();
            var dummyAction = FindDummyAction(categoryName, actionName);
            if (dummyAction.Action == null)
            {
                return false;
            }

            dummyAction.Action.Invoke();
            dummyActionsFrame = -1;
            return true;
        }

        private DummyAction FindDummyAction(string actionName)
        {
            foreach (var variant in GetActionNameVariants(actionName))
            {
                var exactMatch = dummyActions.FirstOrDefault(a => string.Equals(a.Name, variant, StringComparison.OrdinalIgnoreCase));
                if (exactMatch.Action != null)
                {
                    return exactMatch;
                }
            }

            var moduleName = GetDummyActionModuleName(actionName);
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                return default;
            }

            return dummyActions
                .Where(a => !string.IsNullOrWhiteSpace(a.Name)
                            && a.Action != null
                            && a.Name.IndexOf(moduleName, StringComparison.OrdinalIgnoreCase) >= 0
                            && a.Name.IndexOf("Destroy", StringComparison.OrdinalIgnoreCase) < 0)
                .OrderBy(a => GetDummyActionScore(a.Name))
                .FirstOrDefault();
        }

        private DummyAction FindDummyAction(string categoryName, string actionName)
        {
            var categoryIndex = dummyActions.FindIndex(a => a.Action == null
                                                           && string.Equals(a.Name, categoryName, StringComparison.OrdinalIgnoreCase));
            if (categoryIndex < 0)
            {
                categoryIndex = dummyActions.FindIndex(a => a.Action == null
                                                           && a.Name.IndexOf(categoryName, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (categoryIndex < 0)
            {
                return default;
            }

            foreach (var variant in GetActionNameVariants(actionName))
            {
                for (var i = categoryIndex + 1; i < dummyActions.Count; i++)
                {
                    var action = dummyActions[i];
                    if (action.Action == null)
                    {
                        break;
                    }

                    if (string.Equals(action.Name, variant, StringComparison.OrdinalIgnoreCase))
                    {
                        return action;
                    }
                }
            }

            return default;
        }

        private static IEnumerable<string> GetActionNameVariants(string actionName)
        {
            var trimmed = actionName.Trim();
            yield return trimmed;

            if (trimmed.IndexOf("->", StringComparison.Ordinal) >= 0)
            {
                yield return trimmed.Replace("->", ".");
            }

            if (trimmed.IndexOf(".", StringComparison.Ordinal) >= 0)
            {
                yield return trimmed.Replace(".", "->");
            }
        }

        private static string GetDummyActionModuleName(string actionName)
        {
            var trimmed = actionName.Trim();
            var arrowIndex = trimmed.IndexOf("->", StringComparison.Ordinal);
            var dotIndex = trimmed.IndexOf(".", StringComparison.Ordinal);
            var splitIndex = arrowIndex >= 0 && dotIndex >= 0
                ? Math.Min(arrowIndex, dotIndex)
                : Math.Max(arrowIndex, dotIndex);

            return splitIndex >= 0 ? trimmed[..splitIndex] : trimmed;
        }

        private static int GetDummyActionScore(string actionName)
        {
            if (actionName.IndexOf("Selected", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0;
            }

            if (actionName.IndexOf("Click", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 1;
            }

            if (actionName.IndexOf("New", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 2;
            }

            if (actionName.IndexOf("Hold", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 3;
            }

            return 4;
        }

        private void UpdateStrafeDirection()
        {
            var settings = CurrentSettings;
            if (Time.time < nextStrafeFlipTime)
            {
                return;
            }

            strafeDirection = random.Next(0, 2) == 0 ? -1 : 1;
            nextStrafeFlipTime = Time.time + settings.MinStrafeFlipSeconds
                                 + (float)random.NextDouble() * (settings.MaxStrafeFlipSeconds - settings.MinStrafeFlipSeconds);
        }

        internal bool IsAimedAt(Vector3 aimPoint, float maxAngle)
        {
            var direction = aimPoint - botPlayer.CameraPosition;
            return direction.sqrMagnitude < 0.01f
                   || Vector3.Angle(botPlayer.CameraForward, direction) <= maxAngle;
        }
    }
}
