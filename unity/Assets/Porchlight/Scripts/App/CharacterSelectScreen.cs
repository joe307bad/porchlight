using System;
using Porchlight.Avatars;
using Porchlight.Input;
using Porchlight.Rendering;
using Porchlight.Shared.Content;
using UnityEngine;

namespace Porchlight.App
{
    /// <summary>
    /// "Who's playing?": a portrait card per character lying on the plaza. Left/right or the pointer
    /// picks one; Enter, A or a click plays as them; Esc or B goes back.
    /// </summary>
    public sealed class CharacterSelectScreen : MonoBehaviour
    {
        private const float CardWidth = 27f;
        private const float CardDepth = 24f;
        private const float CardZ = -0.5f;
        private const float CardGap = 2f;
        private const float Inset = 1.2f;
        private const float PortraitDepth = 15.5f;

        private MeshFilter _cards;
        private MeshFilter _text;
        private Transform[] _portraits;
        private bool _canGoBack;
        private int _selected = -1;
        private Vector2 _lastPointer;

        public event Action<int> Chosen;
        public event Action Back;

        public int Selected => _selected;

        public void Init(Material material, bool canGoBack)
        {
            MenuStyle.Init();
            _canGoBack = canGoBack;

            var mb = new MeshBuilder();
            MenuStyle.Header(mb, "Who's playing?", MenuStyle.ContentLeft, 18.6f);
            MenuStyle.MeshChild(transform, "Header", material).sharedMesh = mb.ToMesh(name: "character header");

            _cards = MenuStyle.MeshChild(transform, "Cards", material);
            _text = FlatText.CreateRenderer(transform, "Text");

            // Portraits sit inside each card with a border of card colour around them.
            // The picture is cropped top and bottom to the card's shape.
            var portraitWidth = CardWidth - Inset * 2f;
            var shown = (PortraitDepth / portraitWidth) / ((float)Portraits.Height / Portraits.Width);
            var crop = new Rect(0f, (1f - shown) * 0.5f, 1f, shown);
            _portraits = new Transform[Characters.All.Count];
            for (var i = 0; i < _portraits.Length; i++)
            {
                var image = Studio.ImageMaterial(Portraits.Get(Characters.All[i], material));
                var filter = MenuStyle.MeshChild(transform, Characters.All[i].DisplayName + " portrait", image);
                filter.sharedMesh = Studio.ImageQuad(portraitWidth, PortraitDepth, crop);
                _portraits[i] = filter.transform;
            }

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
            var count = Characters.All.Count;
            if (PInput.NavLeft()) Select((_selected + count - 1) % count);
            if (PInput.NavRight()) Select((_selected + 1) % count);

            var hovered = CardUnderPointer();
            var pointer = PInput.PointerPosition();
            if (hovered >= 0 && pointer != _lastPointer) Select(hovered);
            _lastPointer = pointer;

            if (PInput.Submit() || (hovered >= 0 && PInput.PrimaryPressedThisFrame())) Chosen?.Invoke(_selected);
            else if (_canGoBack && PInput.Back()) Back?.Invoke();
        }

        private int CardUnderPointer()
        {
            if (!MenuStyle.PointerOnFloor(out var p)) return -1;
            for (var i = 0; i < Characters.All.Count; i++)
            {
                if (MenuStyle.Contains(p, CardX(i), CardZ, CardWidth, CardDepth)) return i;
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
            var text = new TextBuilder();
            text.Add("Pick who you'll explore the town as.", new Vector3(MenuStyle.ContentLeft, MenuStyle.Floor, MenuStyle.SubtitleZ), MenuStyle.Subtitle, MenuStyle.OnBackground);

            for (var i = 0; i < Characters.All.Count; i++)
            {
                var character = Characters.All[i];
                var focused = i == _selected;
                var x = CardX(i);
                var top = MenuStyle.Panel(mb, x, CardZ, CardWidth, CardDepth, focused);

                var portraitZ = CardZ + CardDepth * 0.5f - Inset - PortraitDepth * 0.5f;
                _portraits[i].localPosition = new Vector3(x, top + 0.01f, portraitZ);

                var left = x - CardWidth * 0.5f + Inset;
                var textTop = portraitZ - PortraitDepth * 0.5f;
                text.Add(character.DisplayName, new Vector3(left, top, textTop - 2.6f), MenuStyle.TitleLarge,
                    focused ? MenuStyle.OnSurface : MenuStyle.OnPrimary, weight: FontWeight.SemiBold);
                text.Add(character.Tagline, new Vector3(left, top, textTop - 5.2f), MenuStyle.Body,
                    focused ? MenuStyle.OnSurfaceMuted : MenuStyle.OnPrimaryMuted, maxWidth: CardWidth - Inset * 2f);
            }

            var hint = "← →  Choose     Enter  Play" + (_canGoBack ? "     Esc  Back" : "");
            text.Add(hint, new Vector3(MenuStyle.ContentLeft, MenuStyle.Floor, MenuStyle.HintZ), MenuStyle.Body, MenuStyle.OnBackground);

            mb.ToMesh(_cards.sharedMesh);
            text.ToMesh(_text.sharedMesh);
        }

        private static float CardX(int index)
        {
            var count = Characters.All.Count;
            var total = count * CardWidth + (count - 1) * CardGap;
            return -total * 0.5f + CardWidth * 0.5f + index * (CardWidth + CardGap);
        }
    }
}
