// Builds the model library (Resources/Porchlight/Library.asset) from the glTF
// files in Models/, which glTFast imports: each model becomes a prefab drawn
// with URP Lit materials, each building's window glass becomes a part of its
// own (lit at night) and its ground floor is opened up (Buildings.cs), and the people share one Animator controller whose clips
// are the kit's animations (people/animations.glb) bound to their armature.
// Everything it makes goes in Assets/Porchlight/Generated; run again to
// rebuild (Porchlight ▸ Set Up Project does).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Porchlight.Explore;
using Porchlight.Rendering;
using TMPro;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TextCore.LowLevel;

namespace Porchlight.EditorTools
{
    public static class Library
    {
        const string Models = "Assets/Porchlight/Models";
        const string Out = "Assets/Porchlight/Generated";
        public const string LibraryPath = "Assets/Porchlight/Resources/Porchlight/Library.asset";

        /// <summary>The clips the people play, by the names the controller uses.</summary>
        static readonly (string from, string to, bool loop)[] Clips =
        {
            ("Idle_A", "idle", true),
            ("Walk_A", "walk", true),
            ("Runing_A", "run", true),
            ("Jump_A_Start", "jump", false),
            ("Jump_A_InAir", "fall", true),
            ("Jump_A_Landing", "land", false),
        };

        static List<ModelLibrary.Kit> kits;

        public static void Build()
        {
            foreach (var dir in new[] { Out, Out + "/Materials", Out + "/Prefabs", Out + "/Meshes", Out + "/Animation", Out + "/Fonts" })
                Directory.CreateDirectory(dir);
            AssetDatabase.Refresh();

            var lib = AssetDatabase.LoadAssetAtPath<ModelLibrary>(LibraryPath);
            if (lib == null)
            {
                lib = ScriptableObject.CreateInstance<ModelLibrary>();
                AssetDatabase.CreateAsset(lib, LibraryPath);
            }
            lib.lit = Template("Lit", "Universal Render Pipeline/Lit", false);
            lib.litEmissive = Template("Lit Emissive", "Universal Render Pipeline/Lit", true);
            lib.unlit = Template("Unlit", "Universal Render Pipeline/Unlit", false);
            lib.clear = ClearTemplate();
            lib.particles = ParticlesTemplate();
            lib.font = Font();

            var converted = new Dictionary<Material, Material>();
            var looks = new Dictionary<string, Material>();
            lib.kits = kits = Kits();
            lib.models = Catalog.Statics.Select(name => StaticModel(name, converted, looks)).ToList();
            lib.peopleController = PeopleController();
            lib.people = Catalog.CHARACTERS.Select(name => PersonModel(name, converted, looks, lib.peopleController)).ToList();
            EditorUtility.SetDirty(lib);
            AssetDatabase.SaveAssets();
            Debug.Log($"Porchlight: library built ({lib.models.Count} models, {lib.people.Count} people, {lib.kits.Count} kits)");
        }

        /// <summary>Whether the library is there and holds every model and person the catalog names (buildings opened up).</summary>
        public static bool UpToDate()
        {
            var lib = AssetDatabase.LoadAssetAtPath<ModelLibrary>(LibraryPath);
            if (lib == null) return false;
            var models = new HashSet<string>(lib.models.Select(m => m.name));
            var people = new HashSet<string>(lib.people.Select(p => p.name));
            return Catalog.Statics.All(models.Contains) && Catalog.CHARACTERS.All(people.Contains)
                && lib.models.Where(m => IsBuilding(m.name)).All(m => m.ground != null && m.ground.storey > 0);
        }

        static GameObject Source(string name)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>($"{Models}/{name}.glb");
            if (go == null) throw new Exception($"no model {Models}/{name}.glb");
            return go;
        }

        // Materials ---------------------------------------------------------------------------

        static Material Template(string name, string shader, bool emissive)
        {
            string path = $"{Out}/Materials/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find(shader));
                AssetDatabase.CreateAsset(m, path);
            }
            m.enableInstancing = true;
            if (emissive)
            {
                m.EnableKeyword("_EMISSION");
                m.SetTexture("_EmissionMap", Texture2D.whiteTexture);
                m.SetColor("_EmissionColor", Color.black);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            m.SetFloat("_Smoothness", 0.1f);
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>URP Lit, see-through (alpha-blended): glass you can see into a room through.</summary>
        static Material ClearTemplate()
        {
            string path = $"{Out}/Materials/Clear.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetFloat("_Surface", 1);
            m.SetFloat("_Blend", 0);
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            m.SetInt("_SrcBlendAlpha", (int)BlendMode.One);
            m.SetInt("_DstBlendAlpha", (int)BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            m.SetFloat("_Smoothness", 0.8f);
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>Additive, unlit particles (the fountain's spray).</summary>
        static Material ParticlesTemplate()
        {
            string path = $"{Out}/Materials/Particles.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetFloat("_Surface", 1);
            m.SetFloat("_Blend", 0);
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>
        /// A glTFast material as a URP Lit one. Every model in a kit carries its own copy
        /// of the same material (the kit's colour map), so they're matched by what they
        /// look like and share one.
        /// </summary>
        static Material Convert(Material src, Dictionary<Material, Material> done, Dictionary<string, Material> byLook, string kit)
        {
            if (src == null) return null;
            if (done.TryGetValue(src, out var m)) return m;
            var map = src.HasProperty("baseColorTexture") ? src.GetTexture("baseColorTexture") : null;
            var color = src.HasProperty("baseColorFactor") ? src.GetColor("baseColorFactor") : Color.white;
            float roughness = src.HasProperty("roughnessFactor") ? src.GetFloat("roughnessFactor") : 1;
            string look = $"{kit}-{Safe(src.name)}-{(map != null ? Safe(map.name) : "plain")}-{ColorUtility.ToHtmlStringRGBA(color)}-{roughness:0.00}";
            if (!byLook.TryGetValue(look, out m))
            {
                string path = $"{Out}/Materials/{look}.mat";
                m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null)
                {
                    m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                    AssetDatabase.CreateAsset(m, path);
                }
                m.SetTexture("_BaseMap", map);
                m.SetColor("_BaseColor", color);
                // The kits are flat-shaded and matte (the nature kit says metallic; it isn't meant to look it).
                m.SetFloat("_Metallic", 0);
                m.SetFloat("_Smoothness", Mathf.Clamp01(1 - roughness) * 0.5f);
                m.enableInstancing = true;
                EditorUtility.SetDirty(m);
                byLook[look] = m;
            }
            done[src] = m;
            return m;
        }

        static string Safe(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());

        // Kits: colour variations and window masks ------------------------------------------------

        static bool IsBuilding(string name) => name.StartsWith("suburban/building") || name.StartsWith("commercial/building") || name.StartsWith("industrial/building");

        static List<ModelLibrary.Kit> Kits()
        {
            var kits = new List<ModelLibrary.Kit>();
            foreach (var kit in new[] { "suburban", "commercial", "industrial" })
            {
                var maps = new List<Texture2D> { AssetDatabase.LoadAssetAtPath<Texture2D>($"{Models}/{kit}/Textures/colormap.png") };
                if (Catalog.VARIATIONS.TryGetValue(kit, out var variations))
                    foreach (var v in variations) maps.Add(AssetDatabase.LoadAssetAtPath<Texture2D>($"{Models}/{kit}/Textures/{v}.png"));
                if (maps[0] == null) continue;
                kits.Add(new ModelLibrary.Kit { name = kit, maps = maps.ToArray(), windows = WindowMaterial(kit, maps[0]) });
            }
            return kits;
        }

        /// <summary>A kit's window glass: its colour map, glowing all over (how brightly is set at night).</summary>
        static Material WindowMaterial(string kit, Texture2D map)
        {
            string path = $"{Out}/Materials/{kit}-windows.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetTexture("_BaseMap", map);
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Metallic", 0);
            m.SetFloat("_Smoothness", 0.5f);
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", Color.black);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            return m;
        }

        static readonly Dictionary<string, Color32[]> pixels = new Dictionary<string, Color32[]>();
        static readonly Dictionary<string, Vector2Int> pixelSize = new Dictionary<string, Vector2Int>();

        /// <summary>Whether a colour-map pixel is window glass (the kits' glass blue).</summary>
        static bool IsGlass(Texture2D map, Vector2 uv)
        {
            string path = AssetDatabase.GetAssetPath(map);
            if (!pixels.TryGetValue(path, out var px))
            {
                var read = new Texture2D(2, 2);
                read.LoadImage(File.ReadAllBytes(path));
                pixels[path] = px = read.GetPixels32();
                pixelSize[path] = new Vector2Int(read.width, read.height);
                UnityEngine.Object.DestroyImmediate(read);
            }
            var size = pixelSize[path];
            int x = Mathf.Clamp(Mathf.FloorToInt(Mathf.Repeat(uv.x, 1) * size.x), 0, size.x - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(Mathf.Repeat(uv.y, 1) * size.y), 0, size.y - 1);
            var c = px[y * size.x + x];
            return c.b >= 200 && c.b - c.r >= 40 && c.g - c.r >= 20 && c.g - c.r <= 45 && c.b - c.g >= 15;
        }

        /// <summary>
        /// Split a building's windows off into their own part, drawn with the kit's
        /// glowing window material: the glass-coloured faces that stand upright. Roofs
        /// can be the same blue as the glass; facing up, they stay as they are.
        /// </summary>
        static void SplitWindows(GameObject go, string name, ModelLibrary.Kit kit)
        {
            int part = 0;
            foreach (var f in go.GetComponentsInChildren<MeshFilter>())
            {
                var src = f.sharedMesh;
                var r = f.GetComponent<MeshRenderer>();
                if (src == null || r == null || !src.isReadable) continue;
                var pos = src.vertices;
                var uv = src.uv;
                if (uv.Length != pos.Length) continue;
                var mesh = UnityEngine.Object.Instantiate(src);
                mesh.name = src.name + " (windows)";
                var glass = new List<int>();
                var kept = new List<int[]>();
                for (int s = 0; s < src.subMeshCount; s++)
                {
                    var tris = src.GetTriangles(s);
                    var keep = new List<int>();
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                    {
                        int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                        var normal = f.transform.TransformDirection(Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a])).normalized;
                        bool upright = Mathf.Abs(normal.y) < 0.35f;
                        if (upright && IsGlass(kit.maps[0], (uv[a] + uv[b] + uv[c]) / 3))
                        {
                            glass.Add(a);
                            glass.Add(b);
                            glass.Add(c);
                        }
                        else
                        {
                            keep.Add(a);
                            keep.Add(b);
                            keep.Add(c);
                        }
                    }
                    kept.Add(keep.ToArray());
                }
                if (glass.Count == 0)
                {
                    UnityEngine.Object.DestroyImmediate(mesh);
                    continue;
                }
                mesh.subMeshCount = kept.Count + 1;
                for (int s = 0; s < kept.Count; s++) mesh.SetTriangles(kept[s], s, false);
                mesh.SetTriangles(glass, kept.Count, false);
                mesh.RecalculateBounds();
                string path = $"{Out}/Meshes/{name.Replace('/', '-')}-{part++}.asset";
                AssetDatabase.DeleteAsset(path);
                AssetDatabase.CreateAsset(mesh, path);
                f.sharedMesh = mesh;
                r.sharedMaterials = r.sharedMaterials.Take(kept.Count).Concat(new[] { kit.windows }).ToArray();
            }
        }

        // Prefabs -------------------------------------------------------------------------------

        static ModelLibrary.Model StaticModel(string name, Dictionary<Material, Material> converted, Dictionary<string, Material> looks)
        {
            var go = (GameObject)UnityEngine.Object.Instantiate(Source(name));
            go.name = name.Split('/').Last();
            string kit = name.Split('/')[0];
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                r.sharedMaterials = r.sharedMaterials.Select(m => Convert(m, converted, looks, kit)).ToArray();
                r.shadowCastingMode = ShadowCastingMode.On;
            }
            // Vehicles are turned to face down +z, as the cars are.
            float yaw = Catalog.YawOf(name);
            if (yaw != 0)
            {
                var holder = new GameObject(go.name);
                go.transform.SetParent(holder.transform, false);
                go.transform.localRotation = Quaternion.Euler(0, yaw, 0) * go.transform.localRotation;
                go = holder;
            }
            var withWindows = IsBuilding(name) ? kits.Find(k => k.name == kit) : null;
            ModelLibrary.Ground ground = null;
            if (withWindows != null)
            {
                SplitWindows(go, name, withWindows);
                ground = Buildings.Open(go, name, withWindows.windows, Out + "/Meshes");
            }
            var bounds = BoundsOf(go);
            var prefab = Save(go, name);
            return new ModelLibrary.Model { name = name, prefab = prefab, min = bounds.min, size = bounds.size, ground = ground };
        }

        static GameObject Save(GameObject go, string name)
        {
            string path = $"{Out}/Prefabs/{name.Replace('/', '-')}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            UnityEngine.Object.DestroyImmediate(go);
            return prefab;
        }

        /// <summary>The bounds of everything drawn, in the root's space (skinned meshes as they stand).</summary>
        static Bounds BoundsOf(GameObject go)
        {
            var root = go.transform.worldToLocalMatrix;
            bool any = false;
            var b = new Bounds();
            void Add(Vector3 p)
            {
                p = root.MultiplyPoint3x4(p);
                if (!any)
                {
                    b = new Bounds(p, Vector3.zero);
                    any = true;
                }
                else b.Encapsulate(p);
            }
            foreach (var f in go.GetComponentsInChildren<MeshFilter>())
            {
                if (f.sharedMesh == null) continue;
                var m = f.transform.localToWorldMatrix;
                foreach (var v in f.sharedMesh.vertices) Add(m.MultiplyPoint3x4(v));
            }
            foreach (var s in go.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                var baked = new Mesh();
                s.BakeMesh(baked, true);
                var m = s.transform.localToWorldMatrix;
                foreach (var v in baked.vertices) Add(m.MultiplyPoint3x4(v));
                UnityEngine.Object.DestroyImmediate(baked);
            }
            return b;
        }

        static ModelLibrary.Person PersonModel(string name, Dictionary<Material, Material> converted, Dictionary<string, Material> looks, RuntimeAnimatorController controller)
        {
            var go = (GameObject)UnityEngine.Object.Instantiate(Source(name));
            go.name = name.Split('/').Last();
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                r.sharedMaterials = r.sharedMaterials.Select(m => Convert(m, converted, looks, "people")).ToArray();
                r.shadowCastingMode = ShadowCastingMode.On;
                if (r is SkinnedMeshRenderer s)
                {
                    s.updateWhenOffscreen = false;
                    s.localBounds = new Bounds(Vector3.zero, Vector3.one * 8);
                }
            }
            foreach (var old in go.GetComponents<Animator>()) UnityEngine.Object.DestroyImmediate(old);
            var bounds = BoundsOf(go);
            var head = go.GetComponentsInChildren<Transform>().FirstOrDefault(t => t.name == "Head");
            float headY = head != null ? go.transform.InverseTransformPoint(head.position).y : float.NaN;
            var animator = go.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.avatar = GenericAvatar(go);
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            var prefab = Save(go, name);
            return new ModelLibrary.Person { name = name, prefab = prefab, headY = headY, minY = bounds.min.y, maxY = bounds.max.y };
        }

        static UnityEngine.Avatar shared;

        /// <summary>One generic avatar for everyone (they share the armature).</summary>
        static UnityEngine.Avatar GenericAvatar(GameObject go)
        {
            if (shared != null) return shared;
            string path = $"{Out}/Animation/People.asset";
            AssetDatabase.DeleteAsset(path);
            var avatar = AvatarBuilder.BuildGenericAvatar(go, "");
            avatar.name = "People";
            AssetDatabase.CreateAsset(avatar, path);
            shared = avatar;
            return avatar;
        }

        // Animation -------------------------------------------------------------------------------

        /// <summary>
        /// The kit's clips bound to the people's armature: its animations hang under
        /// an extra "Root" bone the people don't have, so that step comes out of every
        /// path (and Root's own motion is dropped). Only rotations, and the hips'
        /// position, carry over: they fit everyone.
        /// </summary>
        static Dictionary<string, AnimationClip> PeopleClips()
        {
            string src = $"{Models}/{Catalog.PEOPLE_CLIPS}.glb";
            var source = AssetDatabase.LoadAllAssetsAtPath(src).OfType<AnimationClip>().ToDictionary(c => c.name);
            var o = new Dictionary<string, AnimationClip>();
            foreach (var (from, to, loop) in Clips)
            {
                if (!source.TryGetValue(from, out var clip)) throw new Exception($"{src} has no clip {from}");
                var made = new AnimationClip { name = to, frameRate = clip.frameRate };
                foreach (var b in AnimationUtility.GetCurveBindings(clip))
                {
                    if (!b.path.Contains("/Root/")) continue;
                    bool rotation = b.propertyName.StartsWith("m_LocalRotation");
                    bool hips = b.path.EndsWith("/Hips") && b.propertyName.StartsWith("m_LocalPosition");
                    if (!rotation && !hips) continue;
                    var nb = EditorCurveBinding.FloatCurve(b.path.Replace("/Root/", "/"), b.type, b.propertyName);
                    AnimationUtility.SetEditorCurve(made, nb, AnimationUtility.GetEditorCurve(clip, b));
                }
                made.EnsureQuaternionContinuity();
                var settings = AnimationUtility.GetAnimationClipSettings(made);
                settings.loopTime = loop;
                AnimationUtility.SetAnimationClipSettings(made, settings);
                string path = $"{Out}/Animation/{to}.anim";
                AssetDatabase.DeleteAsset(path);
                AssetDatabase.CreateAsset(made, path);
                o[to] = made;
            }
            return o;
        }

        /// <summary>
        /// Idle, walk and run blended by Speed (the walker's pace), played faster or
        /// slower by Cadence; jump, fall and land while Air.
        /// </summary>
        static RuntimeAnimatorController PeopleController()
        {
            var clips = PeopleClips();
            string path = $"{Out}/Animation/People.controller";
            AssetDatabase.DeleteAsset(path);
            var c = AnimatorController.CreateAnimatorControllerAtPath(path);
            c.AddParameter("Speed", AnimatorControllerParameterType.Float);
            c.AddParameter("Cadence", AnimatorControllerParameterType.Float);
            c.AddParameter("Air", AnimatorControllerParameterType.Bool);
            c.AddParameter("Falling", AnimatorControllerParameterType.Bool);
            c.parameters = c.parameters.Select(p =>
            {
                if (p.name == "Cadence") p.defaultFloat = 1;
                return p;
            }).ToArray();
            var sm = c.layers[0].stateMachine;

            var ground = c.CreateBlendTreeInController("Ground", out var tree, 0);
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = "Speed";
            tree.useAutomaticThresholds = false;
            tree.AddChild(clips["idle"], 0);
            tree.AddChild(clips["walk"], 0.4f);
            tree.AddChild(clips["walk"], 1.7f);
            tree.AddChild(clips["run"], 2.5f);
            ground.speedParameterActive = true;
            ground.speedParameter = "Cadence";
            sm.defaultState = ground;

            var jump = sm.AddState("Jump");
            jump.motion = clips["jump"];
            var fall = sm.AddState("Fall");
            fall.motion = clips["fall"];
            var land = sm.AddState("Land");
            land.motion = clips["land"];

            AnimatorStateTransition T(AnimatorState from, AnimatorState to, float duration, bool exit = false, float exitTime = 0)
            {
                var t = from.AddTransition(to);
                t.duration = duration;
                t.hasExitTime = exit;
                t.exitTime = exitTime;
                t.hasFixedDuration = true;
                return t;
            }
            T(ground, jump, 0.08f).AddCondition(AnimatorConditionMode.If, 0, "Air");
            T(jump, fall, 0.15f).AddCondition(AnimatorConditionMode.If, 0, "Falling");
            T(jump, land, 0.06f).AddCondition(AnimatorConditionMode.IfNot, 0, "Air");
            T(fall, land, 0.06f).AddCondition(AnimatorConditionMode.IfNot, 0, "Air");
            T(land, jump, 0.08f).AddCondition(AnimatorConditionMode.If, 0, "Air");
            T(land, ground, 0.15f, true, 0.35f);
            var moveOff = T(land, ground, 0.12f);
            moveOff.AddCondition(AnimatorConditionMode.Greater, 0.6f, "Speed");
            EditorUtility.SetDirty(c);
            return c;
        }

        // Text ------------------------------------------------------------------------------------

        /// <summary>Inter for the town's signs and name tags (glyphs added as they're needed).</summary>
        static TMP_FontAsset Font()
        {
            string path = $"{Out}/Fonts/Inter SemiBold SDF.asset";
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
            if (existing != null) return existing;
            var font = AssetDatabase.LoadAssetAtPath<Font>("Assets/Porchlight/Resources/Porchlight/Fonts/Inter-SemiBold.ttf");
            var asset = TMP_FontAsset.CreateFontAsset(font, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic);
            asset.name = "Inter SemiBold SDF";
            AssetDatabase.CreateAsset(asset, path);
            foreach (var t in asset.atlasTextures)
            {
                t.name = "Inter atlas";
                AssetDatabase.AddObjectToAsset(t, asset);
            }
            asset.material.name = "Inter material";
            AssetDatabase.AddObjectToAsset(asset.material, asset);
            EditorUtility.SetDirty(asset);
            return asset;
        }
    }
}
