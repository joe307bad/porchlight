// The desktop app's Settings: which server it plays on. The live one unless
// another is chosen here (a dev server, ./dev.sh --host jbhs's http://jbhs:5173),
// kept by Platform.SaveServer; saving starts the game again there, signed in
// there separately (NativeSignIn keeps a session for each server).
//
// Reached from the sign-in card and the sidebar ("Settings"), in a dev build of
// the desktop app only (desktop/dev-mac.sh; Platform.ServerChoosable): the
// released app always plays on the live server, and the web build plays
// wherever its page is.

using System;
using Porchlight.UI;
using UnityEngine.UIElements;

namespace Porchlight.App
{
    public static class ServerSettings
    {
        /// <summary>Over everything (the sign-in card too); closing it leaves what was there.</summary>
        public static void Show(VisualElement root)
        {
            if (!Platform.ServerChoosable) return;
            root.Q("settings")?.RemoveFromHierarchy();
            var platform = Platform.Instance;
            var overlay = new VisualElement { name = "settings" };
            overlay.AddToClassList("overlay");
            var card = El.Box("card", "picker-card");
            card.Add(El.Text("Settings", "picker-title"));
            card.Add(El.Text("The server: where the game signs in and plays. Saving starts the game again there.", "picker-hint"));

            string current = platform.Origin + "/";
            var field = El.ClickToFocus(new TextField { value = current.TrimEnd('/'), isDelayed = false, textEdition = { placeholder = "http://jbhs:5173" } });
            field.AddToClassList("settings-field");
            card.Add(field);
            var note = El.Text("", "settings-note");
            string env = Environment.GetEnvironmentVariable("PORCHLIGHT_ORIGIN");
            note.text = env != null
                ? $"PORCHLIGHT_ORIGIN is set: this run plays at {env.TrimEnd('/')} whatever is saved here."
                : Platform.ChosenServer == null ? "The live server." : $"The live server is {Platform.DESKTOP_ORIGIN.TrimEnd('/')}.";
            card.Add(note);
            var error = El.Text("", "signin-error", "hidden");
            card.Add(error);

            void Say(string message)
            {
                error.text = message ?? "";
                error.EnableInClassList("hidden", string.IsNullOrEmpty(message));
            }

            UnityEngine.UIElements.Button save = null;
            // The address last checked and found not answering: pressing again saves it anyway.
            string unanswered = null;

            void Keep(string origin)
            {
                Platform.SaveServer(origin);
                if (SameAsNow(origin)) Close();
                else if (!platform.Relaunch()) Say("Saved. Start the game again to play there.");
            }

            bool SameAsNow(string origin) => Platform.SameServer(origin ?? Platform.DESKTOP_ORIGIN, current);

            save = El.Btn("Save and restart", null, () =>
            {
                if (!Platform.TryServer(field.value, out var origin))
                {
                    Say("That isn’t a server’s address: http://name:port");
                    return;
                }
                if (origin == unanswered)
                {
                    Keep(origin);
                    return;
                }
                save.SetEnabled(false);
                Say(null);
                // Is a Porchlight server there? Its sign-in service answers /auth/me (401 signed out).
                platform.Request("GET", origin + "auth/me", null, (status, _) =>
                {
                    save.SetEnabled(true);
                    if (status == 200 || status == 401)
                    {
                        Keep(origin);
                        return;
                    }
                    unanswered = origin;
                    save.text = "Save anyway";
                    Say(status == 0 ? $"Nothing answered at {origin.TrimEnd('/')}." : $"{origin.TrimEnd('/')} answered {status}: not a Porchlight server?");
                });
            }, "signin-button", "semibold", "press");
            field.RegisterValueChangedCallback(_ =>
            {
                unanswered = null;
                save.text = "Save and restart";
                Say(null);
            });
            card.Add(save);

            var links = El.Box("settings-links");
            if (Platform.ChosenServer != null) links.Add(El.Link("Use the live server", () => Keep(null)));
            links.Add(El.Link("Cancel", Close));
            card.Add(links);

            overlay.Add(card);
            root.Add(overlay);

            void Close() => overlay.RemoveFromHierarchy();
        }
    }
}
