// Putting a library model in the town: an instance of its prefab with its
// footprint centred on a map point, standing on the ground (or `lift` above
// it), turned to face heading `rot` (see Coords.Yaw) and scaled in its own
// axes. Optionally recoloured with one of its kit's variations, and with its
// windows able to glow at night.

using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    public static class Props
    {
        public static GameObject Place(ModelLibrary.Model m, Transform parent, double x, double y, double rot, double sx, double sy, double sz,
            double lift = 0, int variation = 0, bool windows = false, bool castShadows = true)
        {
            var holder = new GameObject(m.name);
            var t = holder.transform;
            t.SetParent(parent, false);
            t.localPosition = Coords.Map(x, y, lift);
            t.localRotation = Coords.Yaw(rot);
            t.localScale = new Vector3((float)sx, (float)sy, (float)sz);
            var model = Object.Instantiate(m.prefab, t, false);
            model.transform.localPosition = -new Vector3(m.min.x + m.size.x / 2, m.min.y, m.min.z + m.size.z / 2);
            if (variation != 0 || windows || !castShadows)
            {
                var kit = ModelLibrary.Instance.KitOf(m.name);
                foreach (var r in model.GetComponentsInChildren<Renderer>())
                {
                    if (!castShadows) r.shadowCastingMode = ShadowCastingMode.Off;
                    if (kit == null) continue;
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++) mats[i] = Materials.ForModel(mats[i], kit, variation, windows);
                    r.sharedMaterials = mats;
                }
            }
            return holder;
        }

        /// <summary>Car models are scaled to one length.</summary>
        public static double CarScale(ModelLibrary.Model car) => TownWorld.CAR_LENGTH / car.size.z;
    }
}
