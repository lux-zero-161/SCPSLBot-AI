using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using SCPSLBot.AI.FirstPersonControl.Mind.Door;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Scp914
{
    internal class WaitForChamberDoorOpening : IAction
    {
        private DoorObstacle doorObstacleBelief;
        private FpcBotPlayer botPlayer;

        public WaitForChamberDoorOpening(FpcBotPlayer botPlayer)
        {
            this.botPlayer = botPlayer;
        }

        public void SetEnabledByBeliefs(FpcMind fpcMind)
        {
        }

        public void SetImpactsBeliefs(FpcMind fpcMind)
        {
            doorObstacleBelief = fpcMind.ActionImpacts<DoorObstacle, DoorEntry?>(this, c => c!.Value.IsScp914ChamberDoor());
        }

        public float Cost => 5f;

        public void Tick()
        {
            var chamberDoor = doorObstacleBelief.GetLastDoor(entry => entry.IsScp914ChamberDoor(), out var goalPosition);
            if (chamberDoor == null)
            {
                return;
            }

            if (chamberDoor.TargetState)
            {
                botPlayer.MoveToPosition(goalPosition);
                return;
            }

            if (Vector3.Distance(botPlayer.PlayerPosition, chamberDoor.transform.position) > 1.4f)
            {
                botPlayer.MoveToPosition(chamberDoor.transform.position);
            }
            else
            {
                botPlayer.Move.DesiredLocalDirection = Vector3.zero;
                botPlayer.LookToPosition(chamberDoor.transform.position + Vector3.up);
            }
        }

        public void Reset()
        {

        }
    }

    internal static class DoorObstacleExtensions
    {
        public static bool IsScp914ChamberDoor(this DoorEntry doorEntry)
        {
            return doorEntry.Door is BasicNonInteractableDoor;
        }
    }
}
