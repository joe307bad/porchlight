// A firefighter holding a hose: both arms reach for the branch (the pipe at the
// hose's end), over the Animator's pose (the people's kit has no such pose).

using UnityEngine;

namespace Porchlight.Rendering
{
    /// <summary>
    /// Holds a branch out in front at waist height, pointing along `aim`, sized to the person's
    /// arms: in LateUpdate (after the Animator) each arm is bent to bring its hand to the branch,
    /// the right at the back and the left further along. Directions are in world space, so it
    /// doesn't matter how the armature's own axes lie.
    /// </summary>
    public sealed class HoseGrip : MonoBehaviour
    {
        /// <summary>Which way the branch points (world).</summary>
        public Vector3 aim = Vector3.forward;
        /// <summary>The branch's back, where the hose joins it, and its tip (world). Ready once it's been worked out.</summary>
        public Vector3 Back { get; private set; }
        public Vector3 Tip { get; private set; }
        public bool Ready { get; private set; }
        /// <summary>How far short of the branch the further hand ends up.</summary>
        public float Miss { get; private set; }

        Transform armL, foreL, handL, armR, foreR, handR;

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
            float reach = Vector3.Distance(armR.position, foreR.position) + Vector3.Distance(foreR.position, handR.position);
            var side = armR.position - armL.position;
            var mid = (armR.position + armL.position) / 2;
            var along = aim.sqrMagnitude > 1e-8f ? aim.normalized : Vector3.forward;
            var flat = new Vector3(along.x, 0, along.z);
            flat = flat.sqrMagnitude > 1e-6f ? flat.normalized : transform.forward;
            Back = mid + flat * (reach * 0.42f) + side * 0.2f + Vector3.down * (reach * 0.42f);
            var front = Back + along * (reach * 0.42f);
            Tip = Back + along * Mathf.Max(reach * 1.05f, 0.1f);
            Ready = true;
            Bend(armR, foreR, handR, Back, side);
            Bend(armL, foreL, handL, front, -side);
            Miss = Mathf.Max(Vector3.Distance(handR.position, Back), Vector3.Distance(handL.position, front));
        }

        /// <summary>The arm bent so its hand comes to `to`, the elbow down and out (towards `outward`).</summary>
        static void Bend(Transform arm, Transform fore, Transform hand, Vector3 to, Vector3 outward)
        {
            float a = Vector3.Distance(arm.position, fore.position), b = Vector3.Distance(fore.position, hand.position);
            var line = to - arm.position;
            float d = Mathf.Clamp(line.magnitude, Mathf.Abs(a - b) + 1e-4f, (a + b) * 0.999f);
            var dir = line.normalized;
            var pole = outward.normalized * 0.5f + Vector3.down;
            pole = (pole - dir * Vector3.Dot(pole, dir)).normalized;
            float cos = Mathf.Clamp((a * a + d * d - b * b) / (2 * a * d), -1, 1);
            var elbow = arm.position + dir * (a * cos) + pole * (a * Mathf.Sqrt(1 - cos * cos));
            Aim(arm, fore, elbow - arm.position);
            Aim(fore, hand, to - fore.position);
        }

        /// <summary>Turn `bone` so the way to `child` points along `want`.</summary>
        static void Aim(Transform bone, Transform child, Vector3 want)
        {
            var now = child.position - bone.position;
            if (now.sqrMagnitude < 1e-10f || want.sqrMagnitude < 1e-10f) return;
            bone.rotation = Quaternion.FromToRotation(now.normalized, want.normalized) * bone.rotation;
        }
    }
}
