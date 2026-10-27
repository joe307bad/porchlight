// Every model the town uses, as prefabs built from the glTF files in Models/
// (Editor/Library.cs makes them: glTFast imports each .glb, its materials
// become URP Lit ones, people get an Animator). The library also holds each
// model's bounds, the kits' colour variations and window glass, the
// people's animator controller, the font, and the material templates every
// runtime material is copied from (so their shader variants are in the build).

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Porchlight.Rendering
{
    public sealed class ModelLibrary : ScriptableObject
    {
        [Serializable]
        public sealed class Model
        {
            public string name;
            public GameObject prefab;
            /// <summary>Bounds in the prefab's own space.</summary>
            public Vector3 min, size;
        }

        [Serializable]
        public sealed class Person
        {
            public string name;
            public GameObject prefab;
            /// <summary>Where the head starts, and the lowest and highest points standing (prefab space).</summary>
            public float headY, minY, maxY;
        }

        [Serializable]
        public sealed class Kit
        {
            public string name;
            /// <summary>The kit's colour maps: [0] its own, then its variations.</summary>
            public Texture2D[] maps;
            /// <summary>The kit's buildings' window glass (their upright glass faces), which glows at night.</summary>
            public Material windows;
        }

        public List<Model> models = new List<Model>();
        public List<Person> people = new List<Person>();
        public List<Kit> kits = new List<Kit>();
        public RuntimeAnimatorController peopleController;
        public TMP_FontAsset font;
        /// <summary>URP Lit; URP Lit with emission; URP Unlit; URP Particles Unlit (additive).</summary>
        public Material lit, litEmissive, unlit, particles;

        const string PATH = "Porchlight/Library";
        static ModelLibrary loaded;
        Dictionary<string, Model> byName;
        Dictionary<string, Person> peopleByName;

        public static ModelLibrary Instance
        {
            get
            {
                if (loaded == null)
                {
                    loaded = Resources.Load<ModelLibrary>(PATH);
                    if (loaded == null) throw new Exception($"Resources/{PATH} is missing: run Porchlight ▸ Set Up Project");
                }
                return loaded;
            }
        }

        public Model Get(string name)
        {
            if (byName == null)
            {
                byName = new Dictionary<string, Model>();
                foreach (var m in models) byName[m.name] = m;
            }
            if (!byName.TryGetValue(name, out var model)) throw new Exception("unknown model " + name);
            return model;
        }

        public Person Character(string name)
        {
            if (peopleByName == null)
            {
                peopleByName = new Dictionary<string, Person>();
                foreach (var p in people) peopleByName[p.name] = p;
            }
            if (!peopleByName.TryGetValue(name, out var person)) throw new Exception("unknown character " + name);
            return person;
        }

        public Kit KitOf(string model)
        {
            string kit = model.Split('/')[0];
            return kits.Find(k => k.name == kit);
        }
    }
}
