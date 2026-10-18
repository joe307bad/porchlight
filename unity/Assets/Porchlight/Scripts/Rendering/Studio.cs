using UnityEngine;

namespace Porchlight.Rendering
{
    /// <summary>
    /// Takes pictures of models for the menus (character portraits, building thumbnails). The model
    /// is posed far below the town, photographed with a temporary camera into a texture, and removed.
    /// </summary>
    public static class Studio
    {
        public static readonly Vector3 Spot = new Vector3(0f, -400f, 0f);

        /// <summary>
        /// Photographs <paramref name="subject"/> (already placed at <see cref="Spot"/>), looking at
        /// <paramref name="target"/> from <paramref name="direction"/> (pointing from the target to the
        /// camera), framing <paramref name="visibleHeight"/> metres top to bottom. The subject is destroyed.
        /// </summary>
        public static RenderTexture Photograph(GameObject subject, Vector3 target, Vector3 direction, float visibleHeight,
            int width, int height, Color background, string name)
        {
            var camera = new GameObject("Studio camera").AddComponent<Camera>();
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = background;
            camera.fieldOfView = 24f;
            camera.aspect = (float)width / height;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 200f;

            var distance = visibleHeight * 0.5f / Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            camera.transform.position = target + direction.normalized * distance;
            camera.transform.LookAt(target);

            var texture = new RenderTexture(width, height, 24) { name = name, antiAliasing = 4 };
            camera.targetTexture = texture;
            camera.Render();
            camera.targetTexture = null;

            // Gone now, not at the end of the frame, or it would be in the next picture.
            Object.DestroyImmediate(camera.gameObject);
            Object.DestroyImmediate(subject);
            return texture;
        }

        /// <summary>A flat picture facing up, its top edge towards +Z, showing <paramref name="uv"/> of the texture.</summary>
        public static Mesh ImageQuad(float width, float depth, Rect uv)
        {
            float hw = width * 0.5f, hd = depth * 0.5f;
            var mesh = new Mesh { name = "image" };
            mesh.SetVertices(new[] { new Vector3(-hw, 0f, -hd), new Vector3(-hw, 0f, hd), new Vector3(hw, 0f, hd), new Vector3(hw, 0f, -hd) });
            mesh.SetUVs(0, new[] { new Vector2(uv.xMin, uv.yMin), new Vector2(uv.xMin, uv.yMax), new Vector2(uv.xMax, uv.yMax), new Vector2(uv.xMax, uv.yMin) });
            mesh.SetNormals(new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        public static Material ImageMaterial(Texture texture)
        {
            var shader = Resources.Load<Shader>("PorchlightImage");
            return new Material(shader) { name = texture.name, mainTexture = texture };
        }
    }
}
