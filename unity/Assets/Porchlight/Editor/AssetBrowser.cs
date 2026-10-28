// A window for looking over the models in Models/ before the game uses them
// (Porchlight ▸ Asset Browser): a tab per folder (cars, furniture, ... and any
// new one dropped in), a grid of thumbnails, and a preview to turn round with
// its size and triangle count. Where the game sizes a kind of model by rule
// (cars to one length, people to one height, furniture by a fixed factor) the
// preview shows it at town scale against a grid of 4 m tiles and a person;
// anything else is fitted to its spot when placed, so it shows at file size.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Porchlight.Explore;
using Porchlight.Rendering;
using UnityEditor;
using UnityEngine;
using Avatar = Porchlight.Rendering.Avatar;
using Rect = UnityEngine.Rect;

namespace Porchlight.EditorTools
{
    public sealed class AssetBrowser : EditorWindow
    {
        const string Models = "Assets/Porchlight/Models";
        const string All = "all";
        const float SidePanel = 380;

        [MenuItem("Porchlight/Asset Browser")]
        static void Open() => GetWindow<AssetBrowser>("Asset Browser").Show();

        sealed class Entry
        {
            public string path, folder, name;
            public GameObject prefab;
        }

        List<string> folders = new List<string>();
        List<Entry> entries = new List<Entry>();
        [SerializeField] string folder = All;
        [SerializeField] string search = "";
        [SerializeField] float tile = 110;
        [SerializeField] string selected;
        [SerializeField] bool townScale = true;
        [SerializeField] bool guides = true;
        Vector2 scroll;

        // The preview: its own little scene with the model, the guides and a camera orbiting them.
        PreviewRenderUtility preview;
        GameObject shown, grid, person;
        Material gridMaterial, personMaterial;
        string shownPath;
        bool shownAtTownScale;
        Bounds shownBounds;
        float yaw = 210, pitch = 22, zoom = 1;
        Stats stats;

        sealed class Stats
        {
            public Vector3 fileSize;
            public double? scale;
            public string rule;
            public int triangles, meshes, materials;
            public long bytes;
        }

        void OnEnable()
        {
            AssetPreview.SetPreviewTextureCacheSize(512);
            Scan();
        }

        void OnDisable()
        {
            ClearShown();
            if (preview != null) preview.Cleanup();
            preview = null;
            if (gridMaterial != null) DestroyImmediate(gridMaterial);
            if (personMaterial != null) DestroyImmediate(personMaterial);
        }

        void OnProjectChange()
        {
            Scan();
            shownPath = null;
            Repaint();
        }

        void Scan()
        {
            folders = Directory.Exists(Models)
                ? Directory.GetDirectories(Models).Select(Path.GetFileName).OrderBy(f => f).ToList()
                : new List<string>();
            entries = AssetDatabase.FindAssets("t:GameObject", new[] { Models })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Distinct()
                .Where(p => p.EndsWith(".glb") || p.EndsWith(".gltf") || p.EndsWith(".fbx") || p.EndsWith(".obj") || p.EndsWith(".prefab"))
                .Select(p => new Entry
                {
                    path = p,
                    folder = p.Substring(Models.Length + 1).Split('/')[0],
                    name = Path.GetFileNameWithoutExtension(p),
                })
                .Where(e => folders.Contains(e.folder))
                .OrderBy(e => e.folder).ThenBy(e => e.name)
                .ToList();
            if (folder != All && !folders.Contains(folder)) folder = All;
        }

        IEnumerable<Entry> Visible() => entries.Where(e =>
            (folder == All || e.folder == folder) &&
            (search.Length == 0 || e.name.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0));

        void OnGUI()
        {
            Toolbar();
            var body = new Rect(0, EditorGUIUtility.singleLineHeight * 2 + 8, position.width, position.height - (EditorGUIUtility.singleLineHeight * 2 + 8));
            float side = Mathf.Min(SidePanel, body.width * 0.5f);
            Grid(new Rect(body.x, body.y, body.width - side, body.height));
            Side(new Rect(body.xMax - side, body.y, side, body.height));
        }

        void Toolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var tabs = new List<string> { All };
                tabs.AddRange(folders);
                var labels = tabs.Select(t => $"{t} ({(t == All ? entries.Count : entries.Count(e => e.folder == t))})").ToArray();
                int at = Mathf.Max(0, tabs.IndexOf(folder));
                int now = GUILayout.Toolbar(at, labels, EditorStyles.toolbarButton);
                if (now != at) { folder = tabs[now]; scroll = Vector2.zero; }
                GUILayout.FlexibleSpace();
            }
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                search = GUILayout.TextField(search, EditorStyles.toolbarSearchField, GUILayout.MinWidth(160), GUILayout.MaxWidth(280));
                GUILayout.FlexibleSpace();
                GUILayout.Label("Size", EditorStyles.miniLabel);
                tile = GUILayout.HorizontalSlider(tile, 64, 200, GUILayout.Width(100));
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton)) Scan();
            }
        }

        // The grid ------------------------------------------------------------------------------

        void Grid(Rect area)
        {
            var list = Visible().ToList();
            float label = EditorGUIUtility.singleLineHeight;
            int cols = Mathf.Max(1, Mathf.FloorToInt((area.width - 16) / (tile + 6)));
            int rows = Mathf.CeilToInt(list.Count / (float)cols);
            var content = new Rect(0, 0, area.width - 16, rows * (tile + label + 8) + 6);
            bool loading = false;

            scroll = GUI.BeginScrollView(area, scroll, content);
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                var cell = new Rect(6 + (i % cols) * (tile + 6), 6 + (i / cols) * (tile + label + 8), tile, tile + label);
                if (cell.yMax < scroll.y || cell.y > scroll.y + area.height) continue;

                if (e.prefab == null) e.prefab = AssetDatabase.LoadAssetAtPath<GameObject>(e.path);
                if (e.prefab == null) continue;
                var thumb = AssetPreview.GetAssetPreview(e.prefab);
                if (thumb == null)
                {
                    loading = true;
                    thumb = AssetPreview.GetMiniThumbnail(e.prefab);
                }

                var image = new Rect(cell.x, cell.y, tile, tile);
                if (e.path == selected) EditorGUI.DrawRect(new Rect(cell.x - 3, cell.y - 3, cell.width + 6, cell.height + 6), new Color(0.24f, 0.49f, 0.9f, 0.6f));
                EditorGUI.DrawRect(image, new Color(0, 0, 0, 0.18f));
                if (thumb != null) GUI.DrawTexture(image, thumb, ScaleMode.ScaleToFit);
                GUI.Label(new Rect(cell.x, image.yMax + 2, tile, label), new GUIContent(e.name, e.path), EditorStyles.centeredGreyMiniLabel);

                var ev = Event.current;
                if (ev.type == EventType.MouseDown && cell.Contains(ev.mousePosition))
                {
                    if (ev.button == 0 && ev.clickCount == 2) EditorGUIUtility.PingObject(e.prefab);
                    else if (ev.button == 0) { selected = e.path; ev.Use(); }
                    else if (ev.button == 1) { Menu(e); ev.Use(); }
                }
                if (ev.type == EventType.MouseDrag && cell.Contains(ev.mousePosition) && ev.button == 0)
                {
                    DragAndDrop.PrepareStartDrag();
                    DragAndDrop.objectReferences = new Object[] { e.prefab };
                    DragAndDrop.StartDrag(e.name);
                    ev.Use();
                }
            }
            GUI.EndScrollView();

            if (list.Count == 0) GUI.Label(area, search.Length > 0 ? "Nothing matches." : "No models in this folder.", EditorStyles.centeredGreyMiniLabel);
            if (loading && AssetPreview.IsLoadingAssetPreviews()) Repaint();
        }

        void Menu(Entry e)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Show in Project"), false, () => { Selection.activeObject = e.prefab; EditorGUIUtility.PingObject(e.prefab); });
            menu.AddItem(new GUIContent("Reveal in Finder"), false, () => EditorUtility.RevealInFinder(e.path));
            menu.AddItem(new GUIContent("Copy library name"), false, () => EditorGUIUtility.systemCopyBuffer = $"{e.folder}/{e.name}");
            menu.ShowAsContext();
        }

        // The preview and its numbers -----------------------------------------------------------

        void Side(Rect area)
        {
            EditorGUI.DrawRect(new Rect(area.x, area.y, 1, area.height), new Color(0, 0, 0, 0.35f));
            area = new Rect(area.x + 8, area.y + 6, area.width - 14, area.height - 12);
            var e = entries.FirstOrDefault(x => x.path == selected);
            if (e == null)
            {
                GUI.Label(area, "Pick a model to see it here.", EditorStyles.centeredGreyMiniLabel);
                return;
            }
            if (e.prefab == null) e.prefab = AssetDatabase.LoadAssetAtPath<GameObject>(e.path);

            GUILayout.BeginArea(area);
            GUILayout.Label(e.name, EditorStyles.boldLabel);
            GUILayout.Label($"{e.folder}/{e.name}", EditorStyles.miniLabel);

            var view = GUILayoutUtility.GetRect(area.width, area.width, GUILayout.ExpandWidth(true));
            Show(e, view);

            using (new EditorGUILayout.HorizontalScope())
            {
                townScale = GUILayout.Toggle(townScale, "Town scale", EditorStyles.miniButtonLeft);
                guides = GUILayout.Toggle(guides, "Guides", EditorStyles.miniButtonMid);
                if (GUILayout.Button("Reset view", EditorStyles.miniButtonRight)) { yaw = 210; pitch = 22; zoom = 1; }
            }
            GUILayout.Label("Drag to turn, scroll to zoom.", EditorStyles.centeredGreyMiniLabel);
            GUILayout.Space(6);
            Numbers();
            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Show in Project")) { Selection.activeObject = e.prefab; EditorGUIUtility.PingObject(e.prefab); }
                if (GUILayout.Button("Copy library name")) EditorGUIUtility.systemCopyBuffer = $"{e.folder}/{e.name}";
            }
            GUILayout.EndArea();
        }

        void Numbers()
        {
            if (stats == null) return;
            var s = stats.fileSize;
            Row("File size", $"{s.x:0.##} × {s.y:0.##} × {s.z:0.##} units (w × h × d)");
            if (stats.scale is double k)
            {
                double m = k * Directions.METRES_PER_TILE;
                Row("In town", $"{s.x * m:0.0} × {s.y * m:0.0} × {s.z * m:0.0} m");
                Row("Sized by", stats.rule);
            }
            else Row("In town", stats.rule);
            Row("Grid", shownAtTownScale ? $"{Directions.METRES_PER_TILE:0} m tiles, person {Avatar.PERSON_HEIGHT * Directions.METRES_PER_TILE:0.0} m" : "1 file unit");
            Row("Triangles", stats.triangles.ToString("N0"));
            Row("Meshes", $"{stats.meshes}, {stats.materials} material{(stats.materials == 1 ? "" : "s")}");
            Row("On disk", stats.bytes >= 1 << 20 ? $"{stats.bytes / (double)(1 << 20):0.0} MB" : $"{stats.bytes / 1024.0:0} KB");
        }

        static void Row(string label, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(label, EditorStyles.miniBoldLabel, GUILayout.Width(70));
                GUILayout.Label(value, EditorStyles.wordWrappedMiniLabel);
            }
        }

        /// <summary>How the game sizes a model of this folder, as a factor on its file size, or null where it's fitted to its spot.</summary>
        static (double? scale, string rule) TownScale(string folder, Vector3 size)
        {
            switch (folder)
            {
                case "cars":
                    return (TownWorld.CAR_LENGTH / size.z, $"every car is {TownWorld.CAR_LENGTH * Directions.METRES_PER_TILE:0.0} m long");
                case "people":
                    return (Avatar.PERSON_HEIGHT / size.y, $"people are about {Avatar.PERSON_HEIGHT * Directions.METRES_PER_TILE:0.0} m tall");
                case "furniture":
                    return (Interiors.FURNITURE_SCALE, $"furniture is {Interiors.FURNITURE_SCALE:0.##}× its file size");
                default:
                    return (null, "fitted to its lot or spot when placed; shown at file size");
            }
        }

        void Show(Entry e, Rect view)
        {
            if (preview == null)
            {
                preview = new PreviewRenderUtility();
                preview.camera.fieldOfView = 30;
                preview.camera.nearClipPlane = 0.01f;
                preview.camera.farClipPlane = 1000;
                preview.camera.clearFlags = CameraClearFlags.SolidColor;
                preview.camera.backgroundColor = new Color(0.19f, 0.21f, 0.24f);
                preview.lights[0].intensity = 1.3f;
                preview.lights[0].transform.rotation = Quaternion.Euler(45, 210, 0);
                preview.lights[1].intensity = 0.6f;
                preview.lights[1].transform.rotation = Quaternion.Euler(330, 40, 0);
                preview.ambientColor = new Color(0.35f, 0.37f, 0.42f);
            }
            if (shownPath != e.path || shownAtTownScale != townScale) Load(e);

            var ev = Event.current;
            if (view.Contains(ev.mousePosition))
            {
                if (ev.type == EventType.MouseDrag && ev.button == 0)
                {
                    yaw += ev.delta.x * 0.6f;
                    pitch = Mathf.Clamp(pitch + ev.delta.y * 0.4f, -10, 89);
                    ev.Use();
                }
                else if (ev.type == EventType.ScrollWheel)
                {
                    zoom = Mathf.Clamp(zoom * (1 - ev.delta.y * 0.04f), 0.2f, 8);
                    ev.Use();
                }
            }
            if (ev.type != EventType.Repaint) return;

            if (grid != null) grid.SetActive(guides);
            if (person != null) person.SetActive(guides && shownAtTownScale);

            float radius = Mathf.Max(shownBounds.extents.magnitude, 0.05f);
            if (guides && shownAtTownScale) radius = Mathf.Max(radius, (float)Avatar.PERSON_HEIGHT);
            float distance = radius / Mathf.Sin(preview.camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.1f / zoom;
            var turn = Quaternion.Euler(pitch, yaw, 0);
            var target = shownBounds.center;
            preview.camera.transform.SetPositionAndRotation(target - turn * Vector3.forward * distance, turn);
            preview.camera.nearClipPlane = distance * 0.01f;
            preview.camera.farClipPlane = distance + radius * 20;

            preview.BeginPreview(view, GUIStyle.none);
            preview.Render(true);
            preview.EndAndDrawPreview(view);
        }

        void Load(Entry e)
        {
            ClearShown();
            shownPath = e.path;
            shownAtTownScale = townScale;
            stats = null;
            if (e.prefab == null) return;

            shown = Instantiate(e.prefab);
            shown.hideFlags = HideFlags.HideAndDontSave;
            foreach (Transform t in shown.GetComponentsInChildren<Transform>(true)) t.gameObject.hideFlags = HideFlags.HideAndDontSave;
            // The people face backwards in their files; the game turns them round, so do the same.
            if (e.folder == "people") shown.transform.rotation = Quaternion.Euler(0, 180, 0);

            var raw = RendererBounds(shown);
            var (scale, rule) = TownScale(e.folder, raw.size);
            float k = townScale && scale is double s ? (float)s : 1;
            // Stand it on the ground with its footprint centred, as Props.Place does.
            shown.transform.localScale = Vector3.one * k;
            shown.transform.position = -new Vector3(raw.center.x, raw.min.y, raw.center.z) * k;
            shownBounds = RendererBounds(shown);
            shownAtTownScale = townScale && scale != null;
            preview.AddSingleGO(shown);

            stats = Measure(e, raw.size, scale, rule);
            AddGuides();
        }

        void AddGuides()
        {
            if (gridMaterial == null) gridMaterial = Line(new Color(1, 1, 1, 0.35f));
            if (personMaterial == null) personMaterial = Line(new Color(1f, 0.75f, 0.3f));

            // A grid under the model, a little bigger than it: town tiles at town scale, else file units (finer when it's small).
            float reach = Mathf.Max(shownBounds.extents.x, shownBounds.extents.z, shownAtTownScale ? (float)Avatar.PERSON_HEIGHT : 0) * 1.6f;
            float step = 1;
            while (!shownAtTownScale && reach / step < 1.5f) step /= 4;
            while (reach / step > 24) step *= 4;
            int n = Mathf.CeilToInt(reach / step);
            var points = new List<Vector3>();
            for (int i = -n; i <= n; i++)
            {
                points.Add(new Vector3(i * step, 0, -n * step)); points.Add(new Vector3(i * step, 0, n * step));
                points.Add(new Vector3(-n * step, 0, i * step)); points.Add(new Vector3(n * step, 0, i * step));
            }
            grid = Lines("grid", points, gridMaterial);

            // A person-sized post beside the model, for scale.
            float h = (float)Avatar.PERSON_HEIGHT, w = h * 0.3f, x = -(shownBounds.extents.x + w * 1.5f);
            person = Lines("person", new List<Vector3>
            {
                new Vector3(x - w / 2, 0, 0), new Vector3(x + w / 2, 0, 0),
                new Vector3(x + w / 2, 0, 0), new Vector3(x + w / 2, h, 0),
                new Vector3(x + w / 2, h, 0), new Vector3(x - w / 2, h, 0),
                new Vector3(x - w / 2, h, 0), new Vector3(x - w / 2, 0, 0),
                new Vector3(x - w / 2, 0, 0), new Vector3(x + w / 2, h, 0),
            }, personMaterial);
        }

        GameObject Lines(string name, List<Vector3> points, Material material)
        {
            var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(points);
            mesh.SetIndices(Enumerable.Range(0, points.Count).ToArray(), MeshTopology.Lines, 0);
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            preview.AddSingleGO(go);
            return go;
        }

        static Material Line(Color color)
        {
            var m = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { hideFlags = HideFlags.HideAndDontSave };
            m.SetColor("_BaseColor", color);
            return m;
        }

        void ClearShown()
        {
            foreach (var go in new[] { shown, grid, person })
            {
                if (go == null) continue;
                var filter = go.GetComponent<MeshFilter>();
                if (go != shown && filter != null) DestroyImmediate(filter.sharedMesh);
                DestroyImmediate(go);
            }
            shown = grid = person = null;
        }

        static Bounds RendererBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.one * 0.1f);
            var b = renderers[0].bounds;
            foreach (var r in renderers.Skip(1)) b.Encapsulate(r.bounds);
            return b;
        }

        static Stats Measure(Entry e, Vector3 size, double? scale, string rule)
        {
            var meshes = e.prefab.GetComponentsInChildren<MeshFilter>(true).Select(f => f.sharedMesh)
                .Concat(e.prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(r => r.sharedMesh))
                .Where(m => m != null).ToList();
            var materials = e.prefab.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().Count();
            long triangles = 0;
            foreach (var m in meshes)
                for (int i = 0; i < m.subMeshCount; i++)
                    if (m.GetTopology(i) == MeshTopology.Triangles) triangles += m.GetIndexCount(i) / 3;
            return new Stats
            {
                fileSize = size,
                scale = scale,
                rule = rule,
                triangles = (int)triangles,
                meshes = meshes.Count,
                materials = materials,
                bytes = new FileInfo(e.path).Length,
            };
        }
    }
}
