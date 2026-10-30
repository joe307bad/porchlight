// A townsperson's wave: their right arm raised over the Animator's pose (the
// people's kit has no wave of its own).

using UnityEngine;

namespace Porchlight.Rendering
{
    /// <summary>
    /// Raises a person's right arm over whatever the Animator posed, in LateUpdate (after
    /// the Animator): the upper arm up and out from the shoulder, the forearm up and
    /// rocking side to side. Directions are taken in world space from the bones
    /// themselves, so it doesn't matter how the armature's own axes lie.
    /// </summary>
    public sealed class ArmWave : MonoBehaviour
    {
        const float RATE = 11;
        const float SWAY = 0.45f;
        public bool on;
        Transform arm, forearm, hand, left, right;
        float weight, t;

        public bool Bind(Transform root)
        {
            foreach (var b in root.GetComponentsInChildren<Transform>(true))
            {
                if (b.name == "Arm.R") arm = b;
                else if (b.name == "ForeArm.R") forearm = b;
                else if (b.name == "Hand.R") hand = b;
                else if (b.name == "Shoulder.L") left = b;
                else if (b.name == "Shoulder.R") right = b;
            }
            bool ok = arm != null && forearm != null && hand != null && left != null && right != null;
            enabled = ok;
            return ok;
        }

        void LateUpdate()
        {
            weight = Mathf.MoveTowards(weight, on ? 1 : 0, Time.deltaTime * (on ? 4 : 3));
            if (weight <= 0) return;
            t += Time.deltaTime;
            float w = weight * weight * (3 - 2 * weight);
            var up = Vector3.up;
            var side = right.position - left.position;
            side = side.sqrMagnitude > 1e-10f ? side.normalized : Vector3.zero;
            Aim(arm, forearm, (up * 0.8f + side * 0.6f).normalized, w);
            float sway = Mathf.Sin(t * RATE) * SWAY;
            Aim(forearm, hand, (up * Mathf.Cos(sway) + side * Mathf.Sin(sway)).normalized, w);
        }

        /// <summary>Turn `bone` so the way to `child` points along `want` (by `w`, 0 to 1).</summary>
        static void Aim(Transform bone, Transform child, Vector3 want, float w)
        {
            var now = child.position - bone.position;
            if (now.sqrMagnitude < 1e-10f) return;
            bone.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(now.normalized, want), w) * bone.rotation;
        }
    }
}
