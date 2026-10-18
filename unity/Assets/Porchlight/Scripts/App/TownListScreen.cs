using System;
using System.Collections.Generic;
using Porchlight.Input;
using Porchlight.Rendering;
using Porchlight.Shared.Model;
using UnityEngine;

namespace Porchlight.App
{
    /// <summary>
    /// The town list, lying on the plaza: a column of rows that scrolls when there are more towns
    /// than fit. Up/down, the scroll wheel or the pointer move through it; Enter, A or a click opens
    /// a town. When building, the first row starts a new town and turns into a name field.
    /// </summary>
    public sealed class TownListScreen : MonoBehaviour
    {
        private const int VisibleRows = 5;
        private const float RowDepth = 4.2f;
        private const float RowPitch = 5.6f;
        private const float FirstRowZ = 9.2f;
        private const float ListLeft = MenuStyle.ContentLeft;
        private const float ListRight = MenuStyle.ContentRight - 1.6f;
        private const float ScrollbarX = MenuStyle.ContentRight - 0.4f;
        private const float RowPadding = 1.4f;

        private MeshFilter _panels;
        private MeshFilter _text;

        private bool _building;
        private string _subtitle = "";
        private string _footer = "";
        private List<TownSummaryDto> _towns = new List<TownSummaryDto>();
        private string _status;
        private bool _busy;

        private int _selected;
        private int _scroll;
        private bool _editing;
        private string _draft = "";
        private float _caretTime;
        private Vector2 _lastPointer;

        public event Action<string> Open;
        public event Action<string> Create;
        public event Action Refresh;
        public event Action Back;

        private int ItemCount => _towns.Count + (_building ? 1 : 0);
        private bool IsNewTownRow(int item) => _building && item == 0;

        public void Init(Material material)
        {
            MenuStyle.Init();
            _panels = MenuStyle.MeshChild(transform, "Rows", material);
            _text = FlatText.CreateRenderer(transform, "Text");
            FlatText.Rebuilt += Rebuild;
            gameObject.SetActive(false);
        }

        private void OnDestroy() => FlatText.Rebuilt -= Rebuild;

        /// <param name="subtitle">The line under the header, such as who's playing.</param>
        /// <param name="footer">Small print in the corner, such as which server this is.</param>
        public void Show(bool building, string subtitle, string footer)
        {
            _building = building;
            _subtitle = subtitle;
            _footer = footer;
            _editing = false;
            gameObject.SetActive(true);
            Clamp();
            Rebuild();
        }

        public void Hide() => gameObject.SetActive(false);

        /// <summary>New data from the server. <paramref name="busy"/> blocks input while a request is out.</summary>
        public void SetTowns(List<TownSummaryDto> towns, string status, bool busy)
        {
            _towns = towns ?? new List<TownSummaryDto>();
            _status = status;
            _busy = busy;
            Clamp();
            if (gameObject.activeSelf) Rebuild();
        }

        private void Update()
        {
            if (_editing)
            {
                UpdateEditing();
                return;
            }

            if (_busy) return;

            var count = ItemCount;
            if (count > 0)
            {
                if (PInput.NavUp()) SelectAndReveal((_selected + count - 1) % count);
                if (PInput.NavDown()) SelectAndReveal((_selected + 1) % count);

                // The wheel scrolls the list without moving the highlight, as lists usually do.
                var wheel = PInput.Scroll();
                if (wheel != 0f) ScrollTo(_scroll + (wheel > 0f ? -1 : 1));
            }

            var hovered = RowUnderPointer();
            var pointer = PInput.PointerPosition();
            if (hovered >= 0 && pointer != _lastPointer) Select(hovered);
            _lastPointer = pointer;

            if (count > 0 && (PInput.Submit() || (hovered >= 0 && PInput.PrimaryPressedThisFrame()))) Activate(_selected);
            else if (PInput.KeyDown(PKey.R)) Refresh?.Invoke();
            else if (PInput.Back()) Back?.Invoke();
        }

        private void UpdateEditing()
        {
            var changed = false;
            var typed = PInput.TypedText();
            if (typed.Length > 0)
            {
                _draft = (_draft + typed).Substring(0, Math.Min(_draft.Length + typed.Length, TownLimits.MaxNameLength));
                changed = true;
            }

            if (PInput.KeyDown(PKey.Backspace) && _draft.Length > 0)
            {
                _draft = _draft.Substring(0, _draft.Length - 1);
                changed = true;
            }

            if (PInput.Submit() && _draft.Trim().Length > 0)
            {
                _editing = false;
                Create?.Invoke(_draft.Trim());
                changed = true;
            }
            else if (PInput.Back())
            {
                _editing = false;
                changed = true;
            }

            // Blink the caret.
            _caretTime += Time.unscaledDeltaTime;
            if (_caretTime >= 0.5f)
            {
                _caretTime = 0f;
                changed = true;
            }

            if (changed) Rebuild();
        }

        private void Activate(int item)
        {
            if (IsNewTownRow(item))
            {
                _editing = true;
                _draft = "";
                PInput.TypedText(); // drop anything typed before the field opened
                Rebuild();
                return;
            }

            Open?.Invoke(_towns[item - (_building ? 1 : 0)].Id);
        }

        private int RowUnderPointer()
        {
            if (!MenuStyle.PointerOnFloor(out var p)) return -1;
            for (var row = 0; row < VisibleRows; row++)
            {
                var item = _scroll + row;
                if (item >= ItemCount) break;
                if (MenuStyle.Contains(p, (ListLeft + ListRight) * 0.5f, RowZ(row), ListRight - ListLeft, RowDepth)) return item;
            }

            return -1;
        }

        private void Select(int item)
        {
            if (item == _selected) return;
            _selected = item;
            Rebuild();
        }

        private void SelectAndReveal(int item)
        {
            _selected = item;
            if (item < _scroll) _scroll = item;
            if (item >= _scroll + VisibleRows) _scroll = item - VisibleRows + 1;
            Rebuild();
        }

        private void ScrollTo(int scroll)
        {
            var clamped = Mathf.Clamp(scroll, 0, Mathf.Max(0, ItemCount - VisibleRows));
            if (clamped == _scroll) return;
            _scroll = clamped;
            Rebuild();
        }

        private void Clamp()
        {
            _selected = Mathf.Clamp(_selected, 0, Mathf.Max(0, ItemCount - 1));
            _scroll = Mathf.Clamp(_scroll, 0, Mathf.Max(0, ItemCount - VisibleRows));
        }

        private void Rebuild()
        {
            var mb = new MeshBuilder();
            var text = new TextBuilder();

            MenuStyle.Header(mb, _building ? "Build a town" : "Choose a town", ListLeft, 18.6f);
            text.Add(_subtitle, new Vector3(ListLeft, MenuStyle.Floor, MenuStyle.SubtitleZ), MenuStyle.Subtitle, MenuStyle.OnBackground);
            if (!string.IsNullOrEmpty(_status)) text.Add(_status, new Vector3(ListLeft, MenuStyle.Floor, 12.3f), MenuStyle.Label, MenuStyle.Warning, maxWidth: ListRight - ListLeft);

            var width = ListRight - ListLeft;
            var centerX = (ListLeft + ListRight) * 0.5f;
            for (var row = 0; row < VisibleRows; row++)
            {
                var item = _scroll + row;
                if (item >= ItemCount) break;

                var focused = item == _selected && !_busy;
                var z = RowZ(row);
                var top = MenuStyle.Panel(mb, centerX, z, width, RowDepth, focused);
                var ink = focused ? MenuStyle.OnSurface : MenuStyle.OnPrimary;
                var muted = focused ? MenuStyle.OnSurfaceMuted : MenuStyle.OnPrimaryMuted;
                var left = new Vector3(ListLeft + RowPadding, top, z);
                var right = new Vector3(ListRight - RowPadding, top, z);

                if (IsNewTownRow(item))
                {
                    if (_editing)
                    {
                        var caret = Time.unscaledTime % 1f < 0.5f ? "|" : "";
                        text.Add("Name  " + _draft + caret, left, MenuStyle.Title, ink, weight: FontWeight.SemiBold, maxWidth: width * 0.6f);
                        text.Add("Enter  Create     Esc  Cancel", right, MenuStyle.Label, muted, TextAlign.Right);
                    }
                    else
                    {
                        text.Add("+  New town", left, MenuStyle.Title, ink, weight: FontWeight.SemiBold);
                        text.Add("Start an empty town", right, MenuStyle.Label, muted, TextAlign.Right);
                    }

                    continue;
                }

                var town = _towns[item - (_building ? 1 : 0)];
                text.Add(town.Name, left, MenuStyle.Title, ink, weight: FontWeight.SemiBold, maxWidth: width * 0.5f);
                text.Add($"Pop {town.Population}  ·  {town.Jobs} jobs  ·  {town.BuildingCount} buildings", right, MenuStyle.Label, muted, TextAlign.Right);
            }

            Scrollbar(mb);

            var hint = _building ? "↑ ↓  Choose     Enter  Open     R  Refresh     Esc  Back" : "↑ ↓  Choose     Enter  Visit     R  Refresh     Esc  Change character";
            text.Add(hint, new Vector3(ListLeft, MenuStyle.Floor, MenuStyle.HintZ), MenuStyle.Body, MenuStyle.OnBackground);
            text.Add(_footer, new Vector3(MenuStyle.ContentRight, MenuStyle.Floor, MenuStyle.HintZ), MenuStyle.Label, MenuStyle.OnBackground, TextAlign.Right);

            mb.ToMesh(_panels.sharedMesh);
            text.ToMesh(_text.sharedMesh);
        }

        /// <summary>A thin track beside the rows, with a thumb showing which part of the list is in view.</summary>
        private void Scrollbar(MeshBuilder mb)
        {
            var count = ItemCount;
            if (count <= VisibleRows) return;

            var top = RowZ(0) + RowDepth * 0.5f;
            var bottom = RowZ(VisibleRows - 1) - RowDepth * 0.5f;
            var length = top - bottom;
            mb.Box(new Vector3(ScrollbarX, MenuStyle.Floor, (top + bottom) * 0.5f), 0.5f, 0.15f, length, MenuStyle.PrimaryShadow, MenuStyle.Primary);

            var thumb = length * VisibleRows / count;
            var thumbTop = top - (length - thumb) * _scroll / (count - VisibleRows);
            mb.Box(new Vector3(ScrollbarX, MenuStyle.Floor + 0.15f, thumbTop - thumb * 0.5f), 0.5f, 0.2f, thumb, MenuStyle.Primary, MenuStyle.OnPrimary);
        }

        private static float RowZ(int row) => FirstRowZ - row * RowPitch;
    }
}
