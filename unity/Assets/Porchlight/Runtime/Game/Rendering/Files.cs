// Reading the game's files (the models and their textures). In the editor
// they come straight from app/public/models, the same files the TypeScript
// game serves; in a build, from StreamingAssets/models (copied there by the
// build, see Editor/CopyModels.cs). On the web every read is a fetch.

using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Porchlight.Rendering
{
    public static class Files
    {
        public static string ModelsRoot
        {
            get
            {
#if UNITY_EDITOR
                return Path.GetFullPath(Path.Combine(Application.dataPath, "../../app/public/models"));
#else
                return Application.streamingAssetsPath + "/models";
#endif
            }
        }

        /// <summary>The bytes of a file under models/ (path with forward slashes).</summary>
        public static async Task<byte[]> Model(string relative)
        {
            string root = ModelsRoot;
            if (root.Contains("://"))
            {
                using var req = UnityWebRequest.Get(root + "/" + relative);
                var op = req.SendWebRequest();
                while (!op.isDone) await Task.Yield();
                if (req.result != UnityWebRequest.Result.Success) throw new Exception($"couldn't load {relative}: {req.error}");
                return req.downloadHandler.data;
            }
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
#if UNITY_WEBGL && !UNITY_EDITOR
            return File.ReadAllBytes(path);
#else
            return await Task.Run(() => File.ReadAllBytes(path));
#endif
        }

        /// <summary>A path relative to another file's folder ("suburban/x.glb" + "Textures/a.png").</summary>
        public static string Beside(string file, string relative)
        {
            int slash = file.LastIndexOf('/');
            return slash < 0 ? relative : file.Substring(0, slash + 1) + relative;
        }
    }
}
