using Interactables;
using MapGeneration;
using Scp914;
using SCPSLBot.AI.FirstPersonControl.Mind.Spacial;
using SCPSLBot.AI.FirstPersonControl.Perception.Senses;
using SCPSLBot.Navigation;
using System.Linq;
using UnityEngine;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Scp914
{
    internal class Scp914Location : Location
    {
        public Scp914Location(RoomSightSense roomSightSense)
        {
            roomSightSense.OnSensedForeignRoomEntry += OnSensedRoom;

            // The controller is map-global. Waiting until a bot happens to look through the
            // neighbouring doorway made SCP-914 unreachable for a bot that spawned elsewhere.
            // Keep the room-sense subscription for map reloads, but seed the location immediately
            // when the native controller already exists.
            if (Scp914Controller.Singleton != null)
            {
                Update(Scp914Controller.Singleton);
            }
        }

        private void OnSensedRoom(RoomEntry entry)
        {
            if (entry.Room == null || entry.Room.Name != RoomName.Lcz914)
            {
                return;
            }

            this.Update(Scp914Controller.Singleton);
        }

        public Vector3? IntakeChamberPosition { get; private set; }
        public Vector3? OutakeChamberPosition { get; private set; }
        public Vector3? ControlsPosition { get; private set; }
        public Vector3? SettingKnobPosition { get; private set; }
        public Vector3? StartKnobPosition { get; private set; }

        private void Update(Scp914Controller controller)
        {
            if (controller == null)
            {
                return;
            }

            var scp914position = controller.transform.position;
            if (!this.Positions.Contains(scp914position))
            {
                if (!InteractableCollider.AllInstances.TryGetValue(controller, out var interactables))
                {
                    return;
                }

                var interactableColliderPositions = interactables
                    .ToDictionary(pair => (Scp914InteractCode)pair.Key, pair => pair.Value.GetComponent<Collider>().bounds.center);

                if (!interactableColliderPositions.TryGetValue(Scp914InteractCode.ChangeMode, out var settingKnobPosition)
                    || !interactableColliderPositions.TryGetValue(Scp914InteractCode.Activate, out var startKnobPosition))
                {
                    return;
                }

                this.AddPosition(scp914position);
                this.IntakeChamberPosition = controller.IntakeChamber.position;
                this.OutakeChamberPosition = controller.OutputChamber.position;

                this.SettingKnobPosition = settingKnobPosition;
                this.StartKnobPosition = startKnobPosition;
                this.ControlsPosition = this.StartKnobPosition;
            }
        }

        public override string ToString()
        {
            return $"{nameof(Scp914Location)}: {Positions.Count > 0}";
        }
    }
}
