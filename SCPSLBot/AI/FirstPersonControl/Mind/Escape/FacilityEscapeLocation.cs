using MapGeneration;
using SCPSLBot.AI.FirstPersonControl.Mind.Spacial;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;
using SCPSLBot.Navigation;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Escape
{
    internal class FacilityEscapeLocation : Location
    {
        public FacilityEscapeLocation(RoomSightSense roomSightSense) 
        {
            roomSightSense.OnSensedRoomWithin += OnSensedRoomWithin;
            TryAddEscapePosition();
        }

        private void OnSensedRoomWithin(RoomIdentifier roomWithin)
        {
            TryAddEscapePosition();
        }

        private void TryAddEscapePosition()
        {
            if (Positions.Any())
            {
                return;
            }

            if (!Physics.Raycast(roughEscapePosition, Vector3.down, out var hit, 10f, LayerMask.GetMask("Default")))
            {
                // Some map versions put the escape floor on a generated collision layer. The
                // navigation backend already has the verified walkable surface, so use its nearest
                // point rather than making the escape goal depend on looking at Surface first.
                if (NavigationSystem.Instance.Backend?.TryGetNearestPoint(roughEscapePosition, 60f, out var nearest, out _) == true)
                {
                    AddPosition(nearest);
                }

                return;
            }

            var escapePosition = hit.point + Vector3.up * 1f;

            AddPosition(escapePosition);
        }

        private static readonly Vector3 roughEscapePosition = global::Escape.DefaultEscapeZone.center + new Vector3(0f, 0f, -10f);

        public override string ToString()
        {
            return $"{nameof(FacilityEscapeLocation)}(s): {Positions.Count}";
        }
    }
}
