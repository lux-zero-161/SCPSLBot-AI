using Interactables.Interobjects.DoorUtils;
using MapGeneration;
using PlayerRoles;
using SCPSLBot.AI.FirstPersonControl.Mind.Door;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;
using SCPSLBot.Navigation;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Roaming
{
    internal sealed class FpcZoneRoam
    {
        private const float TargetReachedDistance = 1.75f;
        private const float SameRoomTargetMinDistance = 5f;
        private const float DoorInteractDistance = 2f;
        private const float RecentTargetRadius = 3.5f;
        private const int RecentTargetLimit = 8;

        private readonly FpcBotPlayer botPlayer;
        private readonly System.Random random = new();
        private readonly List<Vector3> candidates = new();
        private readonly List<Vector3> recentTargets = new();
        private int explorationPass;
        private float nextStrategicZoneMoveTime;

        private Vector3? targetPosition;
        private FacilityZone? targetZone;

        private Vector3 progressAnchor;
        private float progressAnchorTime;

        public FpcZoneRoam(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;
        }

        private static INavigationBackend Backend => NavigationSystem.Instance.Backend;

        public bool Tick()
        {
            var roomSightSense = botPlayer.Perception.GetSense<RoomSightSense>();
            var roomWithin = roomSightSense.RoomWithin;
            if (!roomWithin)
            {
                return TickWithoutRoom();
            }

            var abandoned = botPlayer.StuckRecovery.ConsumeAbandonRequest();
            var roamStuck = IsRoamStuck();
            if (roamStuck || abandoned)
            {
                // The crossing we were attempting is what blocks us; make the next plan avoid it
                // instead of picking a new target that routes through the same obstacle.
                botPlayer.Navigator.ReportBlockedCrossing();
            }

            if (ShouldPickTarget(roomWithin) || roamStuck || abandoned)
            {
                PickTarget(roomSightSense, roomWithin);
                ResetProgress();
            }

            if (!targetPosition.HasValue)
            {
                return false;
            }

            botPlayer.MoveToPosition(targetPosition.Value);
            OpenBlockingNonKeycardDoor();
            return true;
        }

        // Detects a roam target the bot cannot reach (e.g. blocked by an unopenable keycard door)
        // so it abandons that target and wanders elsewhere instead of grinding into the obstacle.
        private bool IsRoamStuck()
        {
            var position = botPlayer.PlayerPosition;
            if (progressAnchorTime <= 0f
                || Vector3.Distance(
                    Vector3.ProjectOnPlane(position, Vector3.up),
                    Vector3.ProjectOnPlane(progressAnchor, Vector3.up)) > 0.5f)
            {
                progressAnchor = position;
                progressAnchorTime = Time.time;
                return false;
            }

            return Time.time - progressAnchorTime > 4f;
        }

        private void ResetProgress()
        {
            progressAnchor = botPlayer.PlayerPosition;
            progressAnchorTime = Time.time;
        }

        private bool ShouldPickTarget(RoomIdentifier roomWithin)
        {
            if (!targetPosition.HasValue || targetZone != roomWithin.Zone)
            {
                return true;
            }

            return Vector3.Distance(botPlayer.PlayerPosition, targetPosition.Value) <= TargetReachedDistance;
        }

        private void PickTarget(RoomSightSense roomSightSense, RoomIdentifier roomWithin)
        {
            candidates.Clear();

            if (TryPickStrategicZoneTarget(roomWithin, out var strategicTarget))
            {
                targetPosition = strategicTarget;
                targetZone = roomWithin.Zone;
                RememberTarget(strategicTarget);
                nextStrategicZoneMoveTime = Time.time + 45f;
                return;
            }

            foreach (var entry in roomSightSense.ForeignRoomEntries)
            {
                var room = entry.Room;
                if (room != null
                    && !IsForbiddenScpRoom(room)
                    && room.Zone == roomWithin.Zone
                    && (room.Name == RoomName.Unnamed || room.Name != roomWithin.Name))
                {
                    candidates.Add(entry.Position);
                }
            }

            if (candidates.Count == 0)
            {
                AddFarSamples(Backend?.GetRoomSamples(roomWithin));
            }

            if (candidates.Count == 0)
            {
                AddZoneSamples(roomWithin.Zone);
            }

            if (candidates.Count == 0)
            {
                AddAllSamples();
            }

            if (candidates.Count == 0)
            {
                targetPosition = null;
                targetZone = roomWithin.Zone;
                return;
            }

            targetPosition = PickReachable();
            targetZone = roomWithin.Zone;
        }

        private bool TryPickStrategicZoneTarget(RoomIdentifier roomWithin, out Vector3 target)
        {
            target = default;
            var role = botPlayer.BotHub.PlayerHub.roleManager.CurrentRole.RoleTypeId;
            var isFoundationPatrol = role is RoleTypeId.NtfCaptain
                or RoleTypeId.NtfPrivate
                or RoleTypeId.NtfSergeant
                or RoleTypeId.NtfSpecialist
                or RoleTypeId.ChaosConscript
                or RoleTypeId.ChaosMarauder
                or RoleTypeId.ChaosRepressor
                or RoleTypeId.ChaosRifleman;
            if (!isFoundationPatrol || Time.time < nextStrategicZoneMoveTime)
            {
                return false;
            }

            FacilityZone? destination = roomWithin.Zone switch
            {
                FacilityZone.Surface => FacilityZone.Entrance,
                FacilityZone.Entrance => FacilityZone.HeavyContainment,
                FacilityZone.HeavyContainment => FacilityZone.Entrance,
                _ => null,
            };
            return destination.HasValue && TryPickZoneSample(destination.Value, out target);
        }

        private bool TryPickZoneSample(FacilityZone zone, out Vector3 target)
        {
            var backend = Backend;
            if (backend != null)
            {
                foreach (var room in backend.GetNavigableRooms())
                {
                    if (room == null || room.Zone != zone)
                    {
                        continue;
                    }

                    foreach (var sample in backend.GetRoomSamples(room))
                    {
                        if (botPlayer.Navigator.CanReach(sample))
                        {
                            target = sample;
                            return true;
                        }
                    }
                }
            }

            target = default;
            return false;
        }

        // Prefer a reachable point that has not just been visited. A purely random picker often
        // selects the same room sample again, which makes a bot appear to jump or turn in place.
        private Vector3 PickReachable()
        {
            var selected = candidates[random.Next(candidates.Count)];
            var fallback = selected;
            Vector3? verticalCandidate = null;
            var preferVertical = ++explorationPass % 3 == 0;
            var foundFresh = false;
            for (var attempt = 0; attempt < Math.Min(candidates.Count, 12); attempt++)
            {
                var candidate = candidates[random.Next(candidates.Count)];
                if (!botPlayer.Navigator.CanReach(candidate))
                {
                    continue;
                }

                fallback = candidate;
                if (Mathf.Abs(candidate.y - botPlayer.PlayerPosition.y) > 1f)
                {
                    verticalCandidate = candidate;
                }

                if (IsRecentTarget(candidate))
                {
                    continue;
                }

                if (preferVertical && Mathf.Abs(candidate.y - botPlayer.PlayerPosition.y) <= 1f)
                {
                    continue;
                }

                selected = candidate;
                foundFresh = true;
                break;
            }

            if (!foundFresh)
            {
                selected = preferVertical && verticalCandidate.HasValue ? verticalCandidate.Value : fallback;
            }

            RememberTarget(selected);
            return selected;
        }

        private bool IsRecentTarget(Vector3 candidate)
        {
            foreach (var recent in recentTargets)
            {
                if (Vector3.SqrMagnitude(candidate - recent) <= RecentTargetRadius * RecentTargetRadius)
                {
                    return true;
                }
            }

            return false;
        }

        private void RememberTarget(Vector3 target)
        {
            recentTargets.RemoveAll(previous => Vector3.SqrMagnitude(previous - target) <= RecentTargetRadius * RecentTargetRadius);
            recentTargets.Add(target);
            if (recentTargets.Count > RecentTargetLimit)
            {
                recentTargets.RemoveAt(0);
            }
        }

        private bool TickWithoutRoom()
        {
            if (!targetPosition.HasValue || Vector3.Distance(botPlayer.PlayerPosition, targetPosition.Value) <= TargetReachedDistance)
            {
                PickFallbackZoneTarget();
            }

            if (targetPosition.HasValue)
            {
                botPlayer.MoveToPosition(targetPosition.Value);
                return true;
            }

            return false;
        }

        private void PickFallbackZoneTarget()
        {
            var nearestKnownZone = GetNearestKnownZone();
            if (!nearestKnownZone.HasValue)
            {
                targetPosition = null;
                targetZone = null;
                return;
            }

            candidates.Clear();
            AddZoneSamples(nearestKnownZone.Value);
            if (candidates.Count == 0)
            {
                AddAllSamples();
            }

            if (candidates.Count == 0)
            {
                targetPosition = null;
                targetZone = nearestKnownZone;
                return;
            }

            targetPosition = PickReachable();
            targetZone = nearestKnownZone;
        }

        private void AddFarSamples(IReadOnlyList<Vector3> samples)
        {
            if (samples == null)
            {
                return;
            }

            var position = botPlayer.PlayerPosition;
            foreach (var sample in samples)
            {
                if (Vector3.Distance(position, sample) >= SameRoomTargetMinDistance)
                {
                    candidates.Add(sample);
                }
            }
        }

        private void AddZoneSamples(FacilityZone zone)
        {
            var backend = Backend;
            if (backend == null)
            {
                return;
            }

            foreach (var room in backend.GetNavigableRooms())
            {
                if (room != null && !IsForbiddenScpRoom(room) && room.Zone == zone)
                {
                    AddFarSamples(backend.GetRoomSamples(room));
                }
            }
        }

        private void AddAllSamples()
        {
            var backend = Backend;
            if (backend == null)
            {
                return;
            }

            foreach (var room in backend.GetNavigableRooms())
            {
                if (!IsForbiddenScpRoom(room))
                {
                    AddFarSamples(backend.GetRoomSamples(room));
                }
            }
        }

        private bool IsForbiddenScpRoom(RoomIdentifier room)
        {
            return botPlayer.BotHub.PlayerHub.roleManager.CurrentRole.Team == Team.SCPs
                   && room != null
                   && room.Name == RoomName.Lcz914;
        }

        private FacilityZone? GetNearestKnownZone()
        {
            var backend = Backend;
            if (backend == null)
            {
                return null;
            }

            FacilityZone? nearest = null;
            var nearestDistance = float.PositiveInfinity;
            var position = botPlayer.PlayerPosition;
            foreach (var room in backend.GetNavigableRooms())
            {
                foreach (var sample in backend.GetRoomSamples(room))
                {
                    var distance = Vector3.SqrMagnitude(sample - position);
                    if (distance < nearestDistance)
                    {
                        nearestDistance = distance;
                        nearest = room.Zone;
                    }
                }
            }

            return nearest;
        }

        private void OpenBlockingNonKeycardDoor()
        {
            var doorObstacle = botPlayer.MindRunner.GetBelief<DoorObstacle>();
            if (!doorObstacle.IsAny || !doorObstacle.Doors.Values.Any(entry => entry.IsInteractable(DoorPermissionFlags.None)))
            {
                return;
            }

            var doorToOpen = doorObstacle.GetLastDoor(DoorPermissionFlags.None, out var goalPos);
            if (!doorToOpen)
            {
                return;
            }

            var doorPlane = new Plane(doorToOpen.transform.forward, doorToOpen.transform.position);
            var distance = Mathf.Abs(doorPlane.GetDistanceToPoint(botPlayer.PlayerPosition));

            if (!doorToOpen.TargetState && distance <= DoorInteractDistance)
            {
                if (!botPlayer.OpenDoor(doorToOpen, DoorInteractDistance))
                {
                    botPlayer.LookToPosition(doorToOpen.transform.position + Vector3.up);
                }
            }

            if (!doorToOpen.TargetState || distance > DoorInteractDistance)
            {
                botPlayer.MoveToPosition(goalPos);
            }
        }
    }
}
