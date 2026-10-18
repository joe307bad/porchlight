using System;
using Porchlight.Input;
using Porchlight.Rendering;
using UnityEngine;

namespace Porchlight.App
{
    /// <summary>
    /// The browser's boot screen: block lettering and sharp-edged menu panels lying flat on the
    /// plaza of the stock town, seen at an angle over the grid. Everything is geometry in the world,
    /// so it sits in perspective with the town. Arrows or the pointer pick an item; Enter, A or a
    /// click chooses it.
    /// </summary>
    public sealed class TitleScreen : MonoBehaviour
    {
        public static readonly string[] Items = { "Build", "Play" };

        private const float PanelWidth = 30f;
        private const float PanelDepth = 6f;
        private const float PanelSpacing = 8f;
        private const float FirstPanelZ = -1f;

        private MeshFilter _panels;
        private MeshFilter _hint;
        private int _selected = -1;
        private Vector2 _lastPointer;

        /// <summary>Called with the index into <see cref="Items"/> when the player chooses one.</summary>
        public event Action<int> Chosen;

        public int Selected => _selected;

        public void Init(Material material)
        {
            MenuStyle.Init();

            // The small lines start at Porchlight's left edge; all the lettering stands the same height.
            var mb = new MeshBuilder();
            const float titleUnit = 0.95f;
            var left = -BlockText.Width("Porchlight", titleUnit) * 0.5f;
            MenuStyle.Header(mb, "welcome to", left, 18.5f, 0.3f);
            MenuStyle.Header(mb, "Porchlight", 0f, 13.2f, titleUnit, TextAlign.Center);
            MenuStyle.Header(mb, "a cozy town builder", left, 7.85f, 0.32f);
            MenuStyle.MeshChild(transform, "Lettering", material).sharedMesh = mb.ToMesh(name: "title lettering");

            _panels = MenuStyle.MeshChild(transform, "Panels", material);
            _hint = FlatText.CreateRenderer(transform, "Hint");
            FlatText.Rebuilt += Rebuild;

            Select(0);
            gameObject.SetActive(false);
        }

        private void OnDestroy() => FlatText.Rebuilt -= Rebuild;

        public void Show(int selected)
        {
            Select(selected);
            gameObject.SetActive(true);
        }

        public void Hide() => gameObject.SetActive(false);

        private void Update()
        {
            if (PInput.NavUp() || PInput.NavLeft()) Select((_selected + Items.Length - 1) % Items.Length);
            if (PInput.NavDown() || PInput.NavRight()) Select((_selected + 1) % Items.Length);

            // Only follow the pointer when it moves, so it doesn't fight the arrow keys.
            var hovered = PanelUnderPointer();
            var pointer = PInput.PointerPosition();
            if (hovered >= 0 && pointer != _lastPointer) Select(hovered);
            _lastPointer = pointer;

            if (PInput.Submit() || (hovered >= 0 && PInput.PrimaryPressedThisFrame())) Chosen?.Invoke(_selected);
        }

        private int PanelUnderPointer()
        {
            if (!MenuStyle.PointerOnFloor(out var p)) return -1;
            for (var i = 0; i < Items.Length; i++)
            {
                if (MenuStyle.Contains(p, 0f, PanelZ(i), PanelWidth, PanelDepth)) return i;
            }

            return -1;
        }

        private void Select(int index)
        {
            if (index == _selected) return;
            _selected = index;
            Rebuild();
        }

        private void Rebuild()
        {
            var mb = new MeshBuilder();
            for (var i = 0; i < Items.Length; i++)
            {
                var focused = i == _selected;
                var z = PanelZ(i);
                var top = MenuStyle.Panel(mb, 0f, z, PanelWidth, PanelDepth, focused);
                var ink = focused ? MenuStyle.Primary : MenuStyle.OnPrimary;
                BlockText.Add(mb, Items[i], new Vector3(0f, top, z), 0.6f, 0.35f, ink, MenuStyle.PrimaryShadow);
                if (focused) BlockText.Add(mb, ">", new Vector3(-PanelWidth * 0.5f + 3f, top, z), 0.6f, 0.35f, ink, MenuStyle.PrimaryShadow);
            }

            mb.ToMesh(_panels.sharedMesh);

            var text = new TextBuilder();
            text.Add("↑ ↓  Choose     Enter  Select", new Vector3(0f, MenuStyle.Floor, -15f), MenuStyle.Body, MenuStyle.OnBackground, TextAlign.Center);
            text.ToMesh(_hint.sharedMesh);
        }

        private static float PanelZ(int index) => FirstPanelZ - index * PanelSpacing;
    }
}
