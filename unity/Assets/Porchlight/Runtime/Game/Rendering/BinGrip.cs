// Hands on a wheelie bin's handle: both arms reach for it over the Animator's
// pose (the people's kit has no carry or lift of its own), the left hand to
// the left grip and the right to the right, wherever the bin is: wheeled
// along tipped back on its wheels, or heaved up and tipped into a truck.

using UnityEngine;

namespace Porchlight.Rendering
{
    /// <summary>
    /// In LateUpdate (after the Animator), bends each arm to bring its hand to a grip on the
    /// handle (two points the bin carries), eased in and out by `on`. The elbows go down and
    /// out, as HoseGrip's do. Directions are world space, so the armature's own axes don't matter.
    /// </summary>
    public sealed class BinGrip : MonoBehaviour
    {
        public Transform left, right;
        public bool on;
        /// <summary>How far short of its grip the further hand ends up (an arm only reaches so far).</summary>
        public float Miss { get; private set; }

        Transform armL, foreL, handL, armR, foreR, handR;
        float weight;

        public bool Bind(Transform root)
        {
            foreach (var b in root.GetComponentsInChildren<Transform>(true))
            {
                if (b.name == "Arm.L") armL = b;
                else if (b.name == "ForeArm.L") foreL = b;
                else if (b.name == "Hand.L") handL = b;
                else if (b.name == "Arm.R") armR = b;
                else if (b.name == "ForeArm.R") foreR = b;
                else if (b.name == "Hand.R") handR = b;
            }
            bool ok = armL != null && foreL != null && handL != null && armR != null && foreR != null && handR != null;
            enabled = ok;
            return ok;
        }

        void LateUpdate()
        {
            weight = Mathf.MoveTowards(weight, on && left != null && right != null ? 1 : 0, Time.deltaTime * 6);
            if (weight <= 0 || left == null || right == null) return;
            float w = weight * weight * (3 - 2 * weight);
            var side = armR.position - armL.position;
            // Each hand eases from where the Animator put it to its grip.
            var toL = Vector3.Lerp(handL.position, left.position, w);
            var toR = Vector3.Lerp(handR.position, right.position, w);
            HoseGrip.Bend(armL, foreL, handL, toL, -side);
            HoseGrip.Bend(armR, foreR, handR, toR, side);
            Miss = weight < 1 ? 0 : Mathf.Max(Vector3.Distance(handL.position, left.position), Vector3.Distance(handR.position, right.position));
        }
    }
}
