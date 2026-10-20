using System;
using Porchlight.Avatars;
using Porchlight.Input;
using Porchlight.Rendering;
using Porchlight.Shared.Content;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using UnityEngine;

namespace Porchlight.App
{
    /// <summary>
    /// The step between building and walking around: a see-through copy of the character follows the
    /// pointer, green where they can stand and red where they can't, and a click drops them there,
    /// facing the way the camera looks. In the town it follows the ground; from the interior editor it
    /// stays on the floor of the cottage being edited. Esc goes back.
    /// </summary>
    public sealed class WalkDrop : MonoBehaviour
    {
        private static readonly Color ValidTint = new Color(0.55f, 1f, 0.55f, 1f);
        private static readonly Color InvalidTint = new Color(1f, 0.35f, 0.35f, 1f);

        private TownDto _town;
        private Camera _camera;
        private BuildingDto _building;
        private int _floor;
        private CharacterRig _ghost;
        private MeshRenderer[] _ghostRenderers;
        private bool _ghostValid;
        private Material _validMaterial;
        private Material _invalidMaterial;
        private Action<Vec2, float, float> _drop;
        private Action _cancel;

        public CharacterType Character { get; private set; }

        /// <summary>Why the spot under the pointer won't do, if it won't.</summary>
        public string Hint { get; private set; }

        /// <summary>Dropping inside a cottage, on one floor, rather than anywhere in the town.</summary>
        public bool InsideBuilding => _building != null;

        /// <param name="building">The cottage being edited, to drop inside it on <paramref name="floor"/>; null for anywhere in the town.</param>
        /// <param name="drop">Called with where to stand, which way to face, and the height of the floor there.</param>
        public void Begin(TownDto town, Camera camera, CharacterType character, Material baseMaterial, BuildingDto building, int floor,
            Action<Vec2, float, float> drop, Action cancel)
        {
            _town = town;
            _camera = camera;
            _building = building;
            _floor = floor;
            _drop = drop;
            _cancel = cancel;
            Character = character;
            Hint = null;

            _validMaterial = new Material(baseMaterial) { name = "Drop ghost (valid)" };
            _validMaterial.SetColor("_Tint", ValidTint);
            _invalidMaterial = new Material(baseMaterial) { name = "Drop ghost (invalid)" };
            _invalidMaterial.SetColor("_Tint", InvalidTint);
            _ghost = CharacterRig.Create(character, _validMaterial, transform);
            _ghostRenderers = _ghost.GetComponentsInChildren<MeshRenderer>();
            _ghostValid = true;
            _ghost.gameObject.SetActive(false);
            enabled = true;
        }

        public void End()
        {
            if (_ghost != null) Destroy(_ghost.gameObject);
            if (_validMaterial != null) Destroy(_validMaterial);
            if (_invalidMaterial != null) Destroy(_invalidMaterial);
            _ghost = null;
            _town = null;
            enabled = false;
        }

        private void Update()
        {
            if (_town == null) return;
            if (PInput.KeyDown(PKey.Escape))
            {
                _cancel();
                return;
            }

            if (HudBar.PointerInBar() || !TryPointer(out var spot, out var floorY))
            {
                _ghost.gameObject.SetActive(false);
                Hint = null;
                return;
            }

            var ground = TownWalking.GroundHeightAt(_town, spot, floorY);
            var valid = CanStand(spot, ground, out var hint);
            Hint = hint;
            var heading = _camera.transform.eulerAngles.y;

            if (valid != _ghostValid)
            {
                _ghostValid = valid;
                foreach (var renderer in _ghostRenderers) renderer.sharedMaterial = valid ? _validMaterial : _invalidMaterial;
            }

            _ghost.transform.SetPositionAndRotation(new Vector3(spot.X, ground, spot.Z), Quaternion.Euler(0f, heading, 0f));
            _ghost.Tick(0f, true, Time.deltaTime);
            _ghost.gameObject.SetActive(true);

            if (valid && PointerGesture.Clicked) _drop(spot, heading, ground);
        }

        /// <summary>Somewhere in the town, clear of walls, buildings and furniture.</summary>
        private bool CanStand(Vec2 spot, float ground, out string hint)
        {
            hint = null;
            var half = _town.Size * 0.5f - TownWalking.CharacterRadius;
            if (Mathf.Abs(spot.X) > half || Mathf.Abs(spot.Z) > half)
            {
                hint = "That's outside the town";
                return false;
            }

            // Standing somewhere solid would push them out of it.
            var settled = TownWalking.MoveAt(_town, spot, Vec2.Zero, ground);
            if (Vec2.Distance(settled, spot) > 0.01f)
            {
                hint = "There's no room to stand there";
                return false;
            }

            return true;
        }

        /// <summary>The spot under the pointer: on the ground, or on the floor of the cottage being edited.</summary>
        private bool TryPointer(out Vec2 spot, out float floorY)
        {
            spot = default;
            floorY = float.NaN;
            var ray = _camera.ScreenPointToRay(PInput.PointerPosition());
            if (_building == null)
            {
                if (!TerrainMeshes.Raycast(_town, ray, out var hit)) return false;
                spot = new Vec2(hit.x, hit.z);
                return true;
            }

            floorY = TownTerrain.BaseHeight(_town, _building) + CottageInterior.FloorHeight(_floor);
            if (!new Plane(Vector3.up, Vector3.up * floorY).Raycast(ray, out var distance)) return false;
            var point = ray.GetPoint(distance);
            spot = new Vec2(point.x, point.z);
            return TownWalking.TryFindInterior(_town, spot, floorY, out var inside, out _) && inside.Id == _building.Id;
        }
    }

    /// <summary>The bottom bar while choosing where to drop the character.</summary>
    public sealed class DropHud : IHudContent
    {
        private const float HeaderZ = 4.6f;
        private const float ContentTop = 3.5f;
        private const float Margin = 2f;
        private const float ActionWidth = 15f;

        private readonly WalkDrop _drop;
        private readonly Action _back;

        public DropHud(WalkDrop drop, Action back)
        {
            _drop = drop;
            _back = back;
        }

        public string StateKey => $"{_drop.Character?.Id}|{_drop.InsideBuilding}|{_drop.Hint}";

        public void Draw(HudCanvas c)
        {
            var x = c.Left + Margin;
            var right = c.Right - Margin - ActionWidth - 2.5f;
            var name = _drop.Character?.DisplayName ?? "your character";
            c.Header("Walk around", x, HeaderZ);
            c.Label(_drop.InsideBuilding ? $"Click a spot on this floor to drop {name} there." : $"Click a spot in the town to drop {name} there.",
                x, 1.6f, HudCanvas.TitleSize, MenuStyle.OnBackground, weight: FontWeight.SemiBold, maxWidth: right - x);
            c.Label("They'll face the way the camera looks. Green is a good spot; red is somewhere they can't stand.", x, -0.2f, HudCanvas.BodySize, MenuStyle.OnBackground, maxWidth: right - x);
            if (!string.IsNullOrEmpty(_drop.Hint)) c.Label(_drop.Hint, x, -1.8f, HudCanvas.BodySize, MenuStyle.Warning, weight: FontWeight.SemiBold, maxWidth: right - x);

            var actions = c.Right - Margin - ActionWidth;
            c.Header("Menu", actions, HeaderZ);
            c.Button(new Rect(actions, ContentTop - 2.1f, ActionWidth, 2.1f), _drop.InsideBuilding ? "Back to the cottage" : "Back to building", "Esc", false, _back);
        }
    }
}
