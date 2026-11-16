// Sat in a seat (Santa in his sleigh): over the Animator's standing pose (the
// people's kit has no sitting of its own), the hips come down onto the seat,
// the thighs go out level in front, the shins hang down and the arms reach
// forward and down to the reins; eased in and out by how far sat down they are.

using UnityEngine;

namespace Porchlight.Rendering
{
    /// <summary>
    /// In LateUpdate (after the Animator), bends a person into a seat whose top is where their root stands (their
    /// feet's soles, stood), by `weight` (0 stood, 1 sat). Directions are the root's, in world space, so the
    /// armature's own axes don't matter.
    /// </summary>
    public sealed class SeatPose : MonoBehaviour
    {
        /// <summary>How far sat down (0 stood as the Animator has them, 1 sat).</summary>
        public float weight;

        Transform root, hips, upperL, legL, footL, upperR, legR, footR, armL, foreL, handL, armR, foreR, handR;

        public bool Bind(Transform root)
        {
            this.root = root;
            foreach (var b in root.GetComponentsInChildren<Transform>(true))
            {
                switch (b.name)
                {
                    case "Hips": hips = b; break;
                    case "UpperLeg.L": upperL = b; break;
                    case "Leg.L": legL = b; break;
                    case "Foot.L": footL = b; break;
                    case "UpperLeg.R": upperR = b; break;
                    case "Leg.R": legR = b; break;
                    case "Foot.R": footR = b; break;
                    case "Arm.L": armL = b; break;
                    case "ForeArm.L": foreL = b; break;
                    case "Hand.L": handL = b; break;
                    case "Arm.R": armR = b; break;
                    case "ForeArm.R": foreR = b; break;
                    case "Hand.R": handR = b; break;
                }
            }
            bool ok = hips != null && upperL != null && legL != null && footL != null && upperR != null && legR != null && footR != null;
            enabled = ok;
            return ok;
        }

        void LateUpdate()
        {
            if (weight <= 0 || root == null) return;
            float w = Mathf.Clamp01(weight);
            w = w * w * (3 - 2 * w);
            Vector3 up = root.up, forward = root.forward;
            // Towards the left side, whichever way the armature has it.
            var left = Vector3.ProjectOnPlane(upperL.position - upperR.position, up).normalized;
            float thigh = Vector3.Distance(upperL.position, legL.position);
            if (thigh < 1e-5f) return;
            // The hips down onto the seat (the tops of the thighs a little over it) and back a little, so the knees
            // come out over its front edge.
            float hipJoint = Vector3.Dot((upperL.position + upperR.position) * 0.5f - root.position, up);
            float drop = hipJoint - thigh * 0.45f;
            hips.position -= (up * drop + forward * (thigh * 0.2f)) * w;
            // Thighs out level in front, shins hanging down (a little forward), knees a little apart.
            var thighWay = (forward * 0.97f - up * 0.12f).normalized;
            var shinWay = (-up * 0.92f + forward * 0.3f).normalized;
            Aim(upperL, legL, (thighWay + left * 0.08f).normalized, w);
            Aim(legL, footL, shinWay, w);
            Aim(upperR, legR, (thighWay - left * 0.08f).normalized, w);
            Aim(legR, footR, shinWay, w);
            // Hands forward and down to the reins.
            if (armL != null && foreL != null && handL != null)
            {
                Aim(armL, foreL, (forward * 0.55f - up * 0.75f + left * 0.15f).normalized, w);
                Aim(foreL, handL, (forward * 0.95f - up * 0.15f).normalized, w);
            }
            if (armR != null && foreR != null && handR != null)
            {
                Aim(armR, foreR, (forward * 0.55f - up * 0.75f - left * 0.15f).normalized, w);
                Aim(foreR, handR, (forward * 0.95f - up * 0.15f).normalized, w);
            }
        }

        /// <summary>Turn `bone` (by `w` of the way) so the way to `child` points along `want`.</summary>
        static void Aim(Transform bone, Transform child, Vector3 want, float w)
        {
            var now = child.position - bone.position;
            if (now.sqrMagnitude < 1e-12f) return;
            var turned = Quaternion.FromToRotation(now.normalized, want) * bone.rotation;
            bone.rotation = Quaternion.Slerp(bone.rotation, turned, w);
        }
    }
}
