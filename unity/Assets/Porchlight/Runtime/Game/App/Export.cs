// Downloads (ui/export.ts): the TownModel as JSON, typed arrays as
// { $typed, base64 } like model/serialize.ts, and the whole blueprint as a PNG
// drawn off screen at up to 2048 pixels on its longer side.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Porchlight.Core;
using Porchlight.Model;
using Porchlight.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Porchlight.App
{
    public static class Export
    {
        /// <summary>Target resolution of the exported image's longer side, in pixels.</summary>
        const int PNG_SIZE = 2048;

        public static void Json(Platform platform, TownModel model)
        {
            if (model == null) return;
            platform.Download(Encoding.UTF8.GetBytes(Encode(model)), $"porchlight-{model.seed}.json", "application/json");
        }

        public static IEnumerator Png(Platform platform, TownModel model, ISet<string> visible, PanelSettings template)
        {
            if (model == null) yield break;
            int scale = Math.Max(1, PNG_SIZE / Math.Max(model.width, model.height));
            int w = model.width * scale, h = model.height * scale;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            var settings = UnityEngine.Object.Instantiate(template);
            settings.targetTexture = rt;
            settings.scaleMode = PanelScaleMode.ConstantPixelSize;
            settings.scale = 1;
            settings.clearColor = true;
            settings.colorClearValue = new Color(0, 0, 0, 0);
            var go = new GameObject("PNG export");
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            var view = new BlueprintView();
            view.style.position = Position.Absolute;
            view.style.left = 0;
            view.style.top = 0;
            view.style.width = w;
            view.style.height = h;
            view.visibleLayers.Clear();
            foreach (var id in visible) view.visibleLayers.Add(id);
            doc.rootVisualElement.Add(view);
            view.SetModel(model);
            view.camera.scale = scale;
            view.camera.ox = 0;
            view.camera.oy = 0;
            view.Redraw();
            yield return null;
            view.camera.scale = scale;
            view.camera.ox = 0;
            view.camera.oy = 0;
            view.Redraw();
            yield return null;
            yield return new WaitForEndOfFrame();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new UnityEngine.Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.Destroy(tex);
            UnityEngine.Object.Destroy(go);
            UnityEngine.Object.Destroy(settings);
            rt.Release();
            UnityEngine.Object.Destroy(rt);
            platform.Download(png, $"porchlight-{model.seed}.png", "image/png");
        }

        // JSON, in the model's field order.

        static string Encode(TownModel m)
        {
            var sb = new StringBuilder();
            var o = new Obj(sb);
            o.Str("seed", m.seed);
            o.Key("params");
            var p = new Obj(sb);
            foreach (var spec in ParamSpec.All) p.Num(spec.key, m.@params[spec.key]);
            p.End();
            o.Num("width", m.width);
            o.Num("height", m.height);
            o.Raw("heightmap", Typed("Float32Array", Bytes(m.heightmap)));
            o.Num("waterLevel", m.waterLevel);
            o.Raw("center", V(m.center));
            o.Num("townRadius", m.townRadius);
            o.Raw("gates", Arr(m.gates, V));
            o.Raw("rivers", Arr(m.rivers, r => $"{{\"id\":{r.id},\"points\":{Arr(r.points, V)},\"widthAt\":{Arr(r.widthAt, N)}}}"));
            o.Raw("roads", $"{{\"nodes\":{Arr(m.roads.nodes, n => $"{{\"id\":{n.id},\"at\":{V(n.at)}}}")},\"edges\":{Arr(m.roads.edges, e => $"{{\"id\":{e.id},\"from\":{e.from},\"to\":{e.to},\"kind\":\"{Names.Of(e.kind)}\",\"points\":{Arr(e.points, V)}}}")}}}");
            o.Raw("bridges", Arr(m.bridges, b => $"{{\"id\":{b.id},\"roadEdgeId\":{b.roadEdgeId},\"from\":{V(b.from)},\"to\":{V(b.to)}}}"));
            o.Raw("blocks", Arr(m.blocks, b => $"{{\"id\":{b.id},\"polygon\":{Arr(b.polygon, V)},\"holes\":{Arr(b.holes, hole => Arr(hole, V))}}}"));
            o.Raw("lots", Arr(m.lots, l => $"{{\"id\":{l.id},\"blockId\":{l.blockId},\"polygon\":{Arr(l.polygon, V)},\"frontageEdgeId\":{l.frontageEdgeId}}}"));
            o.Raw("buildings", Arr(m.buildings, b => $"{{\"id\":{b.id},\"lotId\":{b.lotId},\"footprint\":{Arr(b.footprint, V)},\"tiles\":{Arr(b.tiles, t => $"{{\"x\":{t.x},\"y\":{t.y}}}")},\"kind\":\"{Names.Of(b.kind)}\",\"floors\":{b.floors},\"facing\":\"{Names.Of(b.facing)}\"}}"));
            o.Raw("landmarks", Arr(m.landmarks, l => $"{{\"id\":{l.id},\"kind\":\"{Names.Of(l.kind)}\",\"at\":{V(l.at)},\"lotIds\":{Arr(l.lotIds, i => i.ToString(CultureInfo.InvariantCulture))}}}"));
            var t = m.tiles;
            o.Raw("tiles", $"{{\"width\":{t.width},\"height\":{t.height},\"terrain\":{Typed("Uint8Array", t.terrain)},\"overlay\":{Typed("Uint8Array", t.overlay)},\"buildingId\":{Typed("Int32Array", Bytes(t.buildingId))},\"elevation\":{Typed("Uint8Array", t.elevation)},\"walkable\":{Typed("Uint8Array", t.walkable)}}}");
            o.Raw("spawn", V(m.spawn));
            if (m.fields != null)
            {
                var parts = new List<string>();
                if (m.fields.flow != null) parts.Add($"\"flow\":{Typed("Float32Array", Bytes(m.fields.flow))}");
                if (m.fields.roadCost != null) parts.Add($"\"roadCost\":{Typed("Float32Array", Bytes(m.fields.roadCost))}");
                o.Raw("fields", "{" + string.Join(",", parts) + "}");
            }
            o.End();
            return sb.ToString();
        }

        static string N(double v) => double.IsFinite(v) ? Js.NumberToString(v) : "null";

        static string V(Vec2 v) => $"{{\"x\":{N(v.x)},\"y\":{N(v.y)}}}";

        static string Arr<T>(IEnumerable<T> items, Func<T, string> f) => items == null ? "[]" : "[" + string.Join(",", Select(items, f)) + "]";

        static IEnumerable<string> Select<T>(IEnumerable<T> items, Func<T, string> f)
        {
            foreach (var x in items) yield return f(x);
        }

        static string Typed(string name, byte[] bytes) => $"{{\"$typed\":\"{name}\",\"base64\":\"{Convert.ToBase64String(bytes ?? new byte[0])}\"}}";

        static byte[] Bytes(float[] a)
        {
            if (a == null) return new byte[0];
            var b = new byte[a.Length * 4];
            Buffer.BlockCopy(a, 0, b, 0, b.Length);
            return b;
        }

        static byte[] Bytes(int[] a)
        {
            if (a == null) return new byte[0];
            var b = new byte[a.Length * 4];
            Buffer.BlockCopy(a, 0, b, 0, b.Length);
            return b;
        }

        sealed class Obj
        {
            readonly StringBuilder sb;
            bool first = true;

            public Obj(StringBuilder sb)
            {
                this.sb = sb;
                sb.Append('{');
            }

            public void Key(string k)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(k).Append("\":");
            }

            public void Num(string k, double v)
            {
                Key(k);
                sb.Append(N(v));
            }

            public void Str(string k, string v)
            {
                Key(k);
                sb.Append(Porchlight.Core.Json.Serialize(v));
            }

            public void Raw(string k, string json)
            {
                Key(k);
                sb.Append(json);
            }

            public void End() => sb.Append('}');
        }
    }
}
