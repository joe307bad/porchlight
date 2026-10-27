// The two screens in front of the game: the sign-in card and
// "Who's playing?", where an account picks one of its
// characters or makes a new one. Both are overlays on the app's panel.

using System;
using System.Collections.Generic;
using Porchlight.Net;
using Porchlight.UI;
using UnityEngine.UIElements;

namespace Porchlight.App
{
    public static class SignInGate
    {
        /// <summary>Cover the app with the sign-in card. The button navigates away to sign in.</summary>
        public static VisualElement Show(VisualElement root, string provider, string error, Action signIn)
        {
            var gate = new VisualElement { name = "signin" };
            gate.AddToClassList("overlay");
            var card = El.Box("card", "signin-card");
            card.Add(El.Text("Porchlight", "signin-title", "bold"));
            card.Add(El.Text("Grow a town from a seed, then walk its streets.", "signin-text"));
            UnityEngine.UIElements.Button button = null;
            button = El.Btn(provider == "google" ? "Sign in with Google" : "Sign in", null, () =>
            {
                button.SetEnabled(false);
                signIn();
            }, "signin-button", "semibold");
            card.Add(button);
            var err = El.Text(error ?? "", "signin-error");
            if (string.IsNullOrEmpty(error)) err.AddToClassList("hidden");
            card.Add(err);
            gate.Add(card);
            root.Add(gate);
            root.AddToClassList("signed-out");
            button.Focus();
            return gate;
        }
    }

    public static class CharacterPicker
    {
        const string KEY = "porchlight.character";

        /// <summary>This tab's character from earlier (a reload), if it still exists.</summary>
        public static void Remembered(Platform platform, string origin, Action<Character> done)
        {
            string id = platform.Load(KEY, session: true);
            if (string.IsNullOrEmpty(id))
            {
                done(null);
                return;
            }
            Account.ListCharacters(platform, origin, (list, err) => done(list?.Find(c => c.id == id)));
        }

        public static void Forget(Platform platform) => platform.Save(KEY, null, session: true);

        /// <summary>Show the picker; done(character) once one is chosen, or done(null) if they play alone.</summary>
        public static void Pick(VisualElement root, Platform platform, string origin, Func<string> randomLook, Action<Character> done)
        {
            var overlay = new VisualElement { name = "picker" };
            overlay.AddToClassList("overlay");
            var card = El.Box("card", "picker-card");
            var list = new VisualElement();
            var error = El.Text("", "picker-error");
            error.AddToClassList("hidden");
            var name = El.ClickToFocus(new TextField { maxLength = 20, textEdition = { placeholder = "New character’s name" } });
            name.AddToClassList("picker-name");
            UnityEngine.UIElements.Button create = null;

            void ShowError(string message)
            {
                error.text = message ?? "";
                error.EnableInClassList("hidden", string.IsNullOrEmpty(message));
            }

            void Done(Character c)
            {
                overlay.RemoveFromHierarchy();
                if (c != null) platform.Save(KEY, c.id, session: true);
                done(c);
            }

            void Render(List<Character> characters)
            {
                list.Clear();
                foreach (var c in characters)
                {
                    var row = El.Box("picker-row");
                    row.Add(El.Btn(c.name, null, () => Done(c), "picker-play", "semibold"));
                    row.Add(El.Btn("×", $"Delete {c.name}", () =>
                    {
                        if (!platform.Confirm($"Delete {c.name}? Where they were and how they look will be gone.")) return;
                        Account.DeleteCharacter(platform, origin, c.id, err =>
                        {
                            if (err != null) ShowError(err);
                            else Render(characters.FindAll(x => x != c));
                        });
                    }, "picker-delete"));
                    list.Add(row);
                }
                if (characters.Count == 0) list.Add(El.Text("No characters yet. Make one below.", "picker-empty"));
            }

            void Submit()
            {
                if (string.IsNullOrWhiteSpace(name.value))
                {
                    name.Focus();
                    return;
                }
                create.SetEnabled(false);
                ShowError(null);
                Account.CreateCharacter(platform, origin, name.value, randomLook(), (c, err) =>
                {
                    if (c != null) Done(c);
                    else
                    {
                        ShowError(err);
                        create.SetEnabled(true);
                    }
                });
            }

            create = El.Btn("Create", null, Submit, "picker-create", "semibold");
            name.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == UnityEngine.KeyCode.Return || e.keyCode == UnityEngine.KeyCode.KeypadEnter) Submit();
            }, TrickleDown.TrickleDown);
            var form = El.Box("picker-new");
            form.Add(name);
            form.Add(create);

            card.Add(El.Text("Who’s playing?", "picker-title", "bold"));
            card.Add(El.Text("Everyone on this sign-in can have their own character, and play at the same time on another tab or device.", "picker-hint"));
            card.Add(list);
            card.Add(form);
            card.Add(error);
            var alone = El.Link("Play alone (offline)", () => Done(null));
            alone.style.marginTop = 14;
            card.Add(alone);
            overlay.Add(card);
            root.Add(overlay);

            list.Add(El.Text("Loading…", "picker-empty"));
            Account.ListCharacters(platform, origin, (characters, err) =>
            {
                if (characters == null)
                {
                    list.Clear();
                    ShowError(err);
                    name.Focus();
                    return;
                }
                Render(characters);
                name.Focus();
            });
        }
    }
}
