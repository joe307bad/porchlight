// The sign-in card in front of the game: an overlay on the app's panel.
// (After it, FrontDoor.cs: who's playing, and where they live.)

using System;
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
            card.Add(El.Text("Porchlight", "signin-title"));
            UnityEngine.UIElements.Button button = null;
            button = El.Btn(provider == "google" ? "Sign in with Google" : "Sign in", null, () =>
            {
                button.SetEnabled(false);
                signIn();
            }, "signin-button", "semibold", "press");
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
}
