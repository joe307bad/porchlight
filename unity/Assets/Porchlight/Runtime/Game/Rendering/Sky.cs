// The town's light, all Unity's own: a directional sun casting URP's soft
// shadows, a three-colour ambient (sky, horizon, ground), linear fog for the
// close-up camera, bloom and tonemapping on a global volume, and at night
// the street lamps: every bulb glows, and a few real point lights follow the
// lamps nearest the camera. Day and night blend by `dusk` (0 day, 1 night).

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Porchlight.Rendering
{
    public sealed class Sky
    {
        /// <summary>Where the sun is, as a direction from the ground (map x, up, map y).</summary>
        static readonly Vector3 SunFrom = Coords.Map(-0.5, -0.3, 1.15).normalized;
        const int LAMP_LIGHTS = 6;

        static readonly Color DaySky = Coords.Hex(0x87b7d8), NightSky = Coords.Hex(0x101828);
        static readonly Color DaySun = Coords.Hex(0xfff1d8), NightSun = Coords.Hex(0x9fb0e0);
        static readonly Color DayAmbient = Coords.Hex(0xd6e4f5), NightAmbient = Coords.Hex(0x2c3552);
        static readonly Color DayHorizon = Coords.Hex(0xb9c3c0), NightHorizon = Coords.Hex(0x1d2333);
        static readonly Color DayGround = Coords.Hex(0x7d8a5a), NightGround = Coords.Hex(0x12161f);
        public static readonly Color Warm = Coords.Hex(0xffd890);

        readonly Camera camera;
        readonly GameObject root;
        readonly Light sun;
        readonly Light room;
        readonly Light[] lampLights = new Light[LAMP_LIGHTS];
        readonly Volume volume;

        public Sky(Camera camera)
        {
            this.camera = camera;
            root = new GameObject("sky");
            sun = new GameObject("sun").AddComponent<Light>();
            sun.transform.SetParent(root.transform, false);
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.75f;
            sun.transform.rotation = Quaternion.LookRotation(-SunFrom, Vector3.up);
            RenderSettings.sun = sun;

            room = Point("room light", 6);
            for (int i = 0; i < LAMP_LIGHTS; i++) lampLights[i] = Point("lamp light", 2.4f);

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.fogMode = FogMode.Linear;

            var go = new GameObject("post");
            go.transform.SetParent(root.transform, false);
            volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var bloom = profile.Add<Bloom>(true);
            bloom.threshold.Override(1f);
            bloom.intensity.Override(0.7f);
            bloom.scatter.Override(0.65f);
            var tone = profile.Add<Tonemapping>(true);
            tone.mode.Override(TonemappingMode.Neutral);
            volume.sharedProfile = profile;

            var data = camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.renderShadows = true;
        }

        Light Point(string name, float range)
        {
            var l = new GameObject(name).AddComponent<Light>();
            l.transform.SetParent(root.transform, false);
            l.type = LightType.Point;
            l.range = range;
            l.color = Warm;
            l.shadows = LightShadows.None;
            l.enabled = false;
            return l;
        }

        /// <summary>
        /// Light the scene for this frame. `focus`: what the camera looks at (world);
        /// `roomLight`: inside a building, where its ceiling light is.
        /// </summary>
        public void Update(double dusk, bool fog, double fogNear, double fogFar, Vector3 focus,
            IReadOnlyList<Lamp> lamps, IReadOnlyList<double> levels, Vector3? roomLight)
        {
            float d = (float)dusk;
            var sky = Color.Lerp(DaySky, NightSky, d);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = sky;
            sun.color = Color.Lerp(DaySun, NightSun, d);
            sun.intensity = Mathf.Lerp(1.6f, 0.22f, d);
            RenderSettings.ambientSkyColor = Color.Lerp(DayAmbient, NightAmbient, d);
            RenderSettings.ambientEquatorColor = Color.Lerp(DayHorizon, NightHorizon, d);
            RenderSettings.ambientGroundColor = Color.Lerp(DayGround, NightGround, d);
            RenderSettings.fog = fog;
            RenderSettings.fogColor = sky;
            RenderSettings.fogStartDistance = (float)fogNear;
            RenderSettings.fogEndDistance = (float)fogFar;

            Materials.WindowGlow(Mathf.Clamp01(d * 1.2f - 0.1f), Warm);

            room.enabled = roomLight != null;
            if (roomLight != null)
            {
                room.transform.position = roomLight.Value;
                room.intensity = 0.8f + d * 2.2f;
            }

            for (int i = 0; i < lamps.Count; i++) Materials.SetGlow(lamps[i].bulb, Warm, (float)(levels[i] * 3));
            // The lamps nearest what the camera's on light the street for real.
            var near = new List<int>();
            if (d > 0.05f)
            {
                for (int i = 0; i < lamps.Count; i++) if (levels[i] > 0.2) near.Add(i);
                near.Sort((a, b) => Dist(lamps[a], focus).CompareTo(Dist(lamps[b], focus)));
            }
            for (int k = 0; k < LAMP_LIGHTS; k++)
            {
                var l = lampLights[k];
                l.enabled = k < near.Count;
                if (!l.enabled) continue;
                var lamp = lamps[near[k]];
                l.transform.position = Coords.Map(lamp.x, lamp.y, lamp.h - 0.08);
                l.intensity = (float)(levels[near[k]] * 1.6);
            }
        }

        static float Dist(Lamp l, Vector3 focus)
        {
            float dx = (float)l.x - focus.x, dz = (float)-l.y - focus.z;
            return dx * dx + dz * dz;
        }

        public void Dispose() => UnityEngine.Object.Destroy(root);
    }
}
