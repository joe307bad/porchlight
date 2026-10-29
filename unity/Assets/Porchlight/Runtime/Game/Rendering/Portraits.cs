// Pictures of your character and your car for the boxes beside the minimap:
// each model stands well away from the town on its own layer, lit by its own
// light and filmed by its own small camera into a render texture the UI shows.
// The character idles as it does in the street. For the pickers, a still of
// every character and every car, filmed the same way once and copied into
// plain textures (a render texture nothing draws into any more can come back
// blank, as it did in the browser).

using System;
using System.Linq;
using Porchlight.Explore;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Porchlight.Rendering
{
    public sealed class Portraits : IDisposable
    {
        public const string PORTRAIT_LAYER = "Portrait";
        const int SIZE = 192;
        /// <summary>Far below the town, out of every other camera's sight.</summary>
        static readonly Vector3 AWAY = new Vector3(0, -500, 0);
        /// <summary>The car stands this far along from the character, out of its camera's view.</summary>
        const float CAR_OFFSET = 20;

        public readonly RenderTexture person, car;
        readonly ModelLibrary library;
        readonly GameObject root;
        readonly Camera personCamera, carCamera;
        readonly int layer;
        readonly Walker standing = new Walker(null, 0, 0) { heading = 0.5 };
        Avatar avatar;
        GameObject carModel;
        string look;
        int carIndex = -1;
        /// <summary>The stills, once asked for; and what's filming them (gone once they're taken).</summary>
        Texture2D[] lookStills, carStills;
        /// <summary>What the stills' cameras are filming into, and the still each is copied to when they're done.</summary>
        readonly System.Collections.Generic.List<(RenderTexture film, Texture2D still)> filming = new System.Collections.Generic.List<(RenderTexture, Texture2D)>();
        GameObject stills;
        readonly System.Collections.Generic.List<(Avatar avatar, Walker at)> stillAvatars = new System.Collections.Generic.List<(Avatar, Walker)>();
        int stillFrames;
        /// <summary>Frames to film the stills for: the characters settle into their idle first.</summary>
        const int STILL_FRAMES = 4;
        /// <summary>How far apart the stills' subjects stand (each camera sees only its own).</summary>
        const float STILL_SPACING = 8;

        /// <summary>The layer the portraits are on (left out of the town's camera).</summary>
        public static int Layer
        {
            get
            {
                int l = LayerMask.NameToLayer(PORTRAIT_LAYER);
                return l >= 0 ? l : 31;
            }
        }

        public Portraits(ModelLibrary library)
        {
            this.library = library;
            layer = Layer;
            root = new GameObject("portraits");
            root.transform.position = AWAY;
            person = Texture(nameof(person));
            car = Texture(nameof(car));
            personCamera = Film(person, new Vector3(0, 0.2f, 0), 0.2f, 0.95f);
            carCamera = Film(car, new Vector3(CAR_OFFSET, 0.15f, 0), 0.15f, 2.1f);
            Light(new Vector3(0, 0, 0));
            Light(new Vector3(CAR_OFFSET, 0, 0));
        }

        static RenderTexture Texture(string name)
        {
            var rt = new RenderTexture(SIZE, SIZE, 24, RenderTextureFormat.ARGB32) { name = "portrait " + name, antiAliasing = 4 };
            rt.Create();
            return rt;
        }

        Camera Film(RenderTexture target, Vector3 subject, float lookHeight, float distance, Transform parent = null)
        {
            var go = new GameObject("portrait camera");
            go.transform.SetParent(parent ?? root.transform, false);
            var cam = go.AddComponent<Camera>();
            cam.targetTexture = target;
            cam.cullingMask = 1 << layer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Coords.Hex(0x2b2a27);
            cam.fieldOfView = 30;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 10;
            var look = subject + new Vector3(0, lookHeight - subject.y, 0);
            go.transform.localPosition = look + Quaternion.Euler(18, 30, 0) * new Vector3(0, 0, -distance);
            go.transform.LookAt(root.transform.TransformPoint(look));
            var data = go.AddComponent<UniversalAdditionalCameraData>();
            data.renderShadows = false;
            data.renderPostProcessing = false;
            return cam;
        }

        void Light(Vector3 at, Transform parent = null)
        {
            var go = new GameObject("portrait light");
            go.transform.SetParent(parent ?? root.transform, false);
            go.transform.localPosition = at + new Vector3(-1.2f, 2.2f, -1.6f);
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.range = 6;
            l.intensity = 3;
            l.color = Coords.Hex(0xfff4e4);
            l.cullingMask = 1 << layer;
            l.shadows = LightShadows.None;
        }

        void OnLayer(GameObject go)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }

        public void SetLook(string next)
        {
            if (next == look || next == null) return;
            look = next;
            avatar?.Dispose();
            avatar = new Avatar(library.Character(next), Avatar.PERSON_HEIGHT, 0, root.transform);
            avatar.SnapYaw(standing.heading);
            OnLayer(avatar.root);
        }

        public void SetCar(int model)
        {
            if (model == carIndex || model < 0 || model >= Catalog.CARS.Length) return;
            carIndex = model;
            if (carModel != null) UnityEngine.Object.Destroy(carModel);
            var m = library.Get(Catalog.CARS[model]);
            double s = Props.CarScale(m);
            carModel = Props.Place(m, root.transform, CAR_OFFSET, 0, 0.6, s, s, s, castShadows: false);
            OnLayer(carModel);
        }

        /// <summary>A still of each of Catalog.CHARACTERS, in order (filmed over the next few frames).</summary>
        public Texture2D[] LookStills()
        {
            if (lookStills != null) return lookStills;
            lookStills = new Texture2D[Catalog.CHARACTERS.Length];
            for (int i = 0; i < lookStills.Length; i++)
            {
                var at = new Vector3(i * STILL_SPACING, 0, 40);
                var a = new Avatar(library.Character(Catalog.CHARACTERS[i]), Avatar.PERSON_HEIGHT, 0, StillsRoot());
                a.SnapYaw(standing.heading);
                OnLayer(a.root);
                stillAvatars.Add((a, new Walker(null, at.x, -at.z) { heading = standing.heading }));
                lookStills[i] = Still("look " + i, out var film);
                Film(film, at + new Vector3(0, 0.2f, 0), 0.2f, 0.95f, stills.transform);
                Light(at, stills.transform);
            }
            return lookStills;
        }

        /// <summary>A still of each of the first Catalog.CarCount of Catalog.CARS (the cars), in order.</summary>
        public Texture2D[] CarStills()
        {
            if (carStills != null) return carStills;
            carStills = new Texture2D[Catalog.CarCount];
            for (int i = 0; i < carStills.Length; i++)
            {
                var at = new Vector3(i * STILL_SPACING, 0, 60);
                var m = library.Get(Catalog.CARS[i]);
                double s = Props.CarScale(m);
                var go = Props.Place(m, StillsRoot(), at.x, -at.z, 0.6, s, s, s, castShadows: false);
                OnLayer(go);
                carStills[i] = Still("car " + i, out var film);
                Film(film, at + new Vector3(0, 0.15f, 0), 0.15f, 2.1f, stills.transform);
                Light(at, stills.transform);
            }
            return carStills;
        }

        /// <summary>A still, blank (the cameras' background) until it's filmed, and the render texture it's filmed into.</summary>
        Texture2D Still(string name, out RenderTexture film)
        {
            film = Texture(name);
            var still = new Texture2D(SIZE, SIZE, TextureFormat.RGBA32, false) { name = "still " + name };
            var blank = new Color32[SIZE * SIZE];
            Color32 bg = Coords.Hex(0x2b2a27);
            for (int i = 0; i < blank.Length; i++) blank[i] = bg;
            still.SetPixels32(blank);
            still.Apply();
            filming.Add((film, still));
            return still;
        }

        /// <summary>Copy what's been filmed into the stills, and let the render textures go.</summary>
        void Keep()
        {
            var was = RenderTexture.active;
            var flat = RenderTexture.GetTemporary(SIZE, SIZE, 0, RenderTextureFormat.ARGB32);
            foreach (var (film, still) in filming)
            {
                // Through a plain texture first: resolves the multisampling.
                Graphics.Blit(film, flat);
                RenderTexture.active = flat;
                still.ReadPixels(new UnityEngine.Rect(0, 0, SIZE, SIZE), 0, 0, false);
                still.Apply();
                film.Release();
                UnityEngine.Object.Destroy(film);
            }
            RenderTexture.active = was;
            RenderTexture.ReleaseTemporary(flat);
            filming.Clear();
        }

        Transform StillsRoot()
        {
            if (stills == null)
            {
                stills = new GameObject("stills");
                stills.transform.SetParent(root.transform, false);
            }
            // Whatever was just added gets its own few frames.
            stillFrames = 0;
            return stills.transform;
        }

        /// <summary>Keep the character idling; film only while the boxes are on screen (and the stills until they're taken).</summary>
        public void Update(double dt, bool showing)
        {
            personCamera.enabled = carCamera.enabled = showing;
            if (showing) avatar?.Update(standing, dt);
            if (stills == null) return;
            foreach (var (a, at) in stillAvatars) a.Update(at, dt);
            if (++stillFrames < STILL_FRAMES) return;
            Keep();
            foreach (var (a, _) in stillAvatars) a.Dispose();
            stillAvatars.Clear();
            UnityEngine.Object.Destroy(stills);
            stills = null;
        }

        public void Dispose()
        {
            UnityEngine.Object.Destroy(root);
            foreach (var (film, _) in filming)
            {
                film.Release();
                UnityEngine.Object.Destroy(film);
            }
            foreach (var still in (lookStills ?? new Texture2D[0]).Concat(carStills ?? new Texture2D[0])) UnityEngine.Object.Destroy(still);
            person.Release();
            car.Release();
            UnityEngine.Object.Destroy(person);
            UnityEngine.Object.Destroy(car);
        }
    }
}
