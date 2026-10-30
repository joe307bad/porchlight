// A mission's conversation, low in the middle of the 3D view: who's speaking
// and their line (Enter, Space, A or a click goes on), or your choices
// (click one, or step through them with the arrows or the d-pad and pick with
// Enter or A; 1–9 pick straight away).

using System;
using System.Collections.Generic;
using Porchlight.Missions;
using UnityEngine.UIElements;

namespace Porchlight.UI
{
    public sealed class MissionPanel
    {
        readonly VisualElement root;
        readonly VisualElement card;
        readonly Label speaker, text, hint;
        readonly VisualElement choices;
        string shown;

        public MissionPanel(VisualElement host)
        {
            root = El.Box("dialogue");
            root.pickingMode = PickingMode.Ignore;
            card = El.Box("dialogue-card");
            speaker = El.Text("", "dialogue-speaker", "bold");
            text = El.Text("", "dialogue-text");
            choices = El.Box("dialogue-choices");
            hint = El.Text("", "dialogue-hint");
            card.Add(speaker);
            card.Add(text);
            card.Add(choices);
            card.Add(hint);
            root.Add(card);
            root.style.display = DisplayStyle.None;
            host.Add(root);
        }

        /// <summary>A line (or, with none, the choices) and how to go on; nothing to show hides the panel.</summary>
        public void Show(MissionLine line, IReadOnlyList<string> options, int highlighted, bool pad, Action advance, Action<int> choose)
        {
            string key = line != null ? $"L|{line.speaker}|{line.text}|{pad}" : options.Count > 0 ? $"C|{string.Join("|", options)}|{highlighted}|{pad}" : "";
            if (key == shown) return;
            shown = key;
            root.style.display = key.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            if (key.Length == 0) return;
            choices.Clear();
            card.UnregisterCallback<PointerDownEvent>(OnCardClick);
            onAdvance = null;
            if (line != null)
            {
                speaker.text = line.speaker ?? "";
                speaker.style.display = line.speaker != null ? DisplayStyle.Flex : DisplayStyle.None;
                speaker.EnableInClassList("you", line.speaker == "You");
                text.text = line.text;
                text.style.display = DisplayStyle.Flex;
                hint.text = pad ? "A ▸" : "Enter ▸";
                onAdvance = advance;
                card.RegisterCallback<PointerDownEvent>(OnCardClick);
                return;
            }
            speaker.style.display = DisplayStyle.None;
            text.style.display = DisplayStyle.None;
            hint.text = pad ? "D-pad to choose · A to answer" : "↑ ↓ or 1–9 · Enter to answer";
            for (int i = 0; i < options.Count; i++)
            {
                int at = i;
                var b = new Button(() => choose(at)) { focusable = false };
                b.AddToClassList("dialogue-choice");
                if (i == highlighted) b.AddToClassList("highlighted");
                var number = El.Text($"{i + 1}", "dialogue-choice-key", "mono");
                number.pickingMode = PickingMode.Ignore;
                var label = El.Text(options[i], "dialogue-choice-text");
                label.pickingMode = PickingMode.Ignore;
                b.Add(number);
                b.Add(label);
                choices.Add(b);
            }
        }

        Action onAdvance;

        void OnCardClick(PointerDownEvent e)
        {
            e.StopPropagation();
            onAdvance?.Invoke();
        }

        public void Hide() => Show(null, Array.Empty<string>(), 0, false, null, null);
    }
}
