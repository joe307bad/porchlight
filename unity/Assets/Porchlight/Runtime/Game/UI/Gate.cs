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
        public static VisualElement Show(VisualElement root, string provider, string error, Action signIn, string label = null)
        {
            var gate = new VisualElement { name = "signin" };
            gate.AddToClassList("overlay");
            var card = El.Box("card", "signin-card");
            // The lantern, beside the name.
            var brand = new VisualElement();
            brand.AddToClassList("signin-brand");
            var logo = new Image { image = UnityEngine.Resources.Load<UnityEngine.Texture2D>("Porchlight/UI/LogoShadow"), scaleMode = UnityEngine.ScaleMode.ScaleToFit };
            logo.AddToClassList("signin-logo");
            if (logo.image == null) UnityEngine.Debug.LogWarning("porchlight: no logo for the sign-in card");
            else UnityEngine.Debug.Log("porchlight: the sign-in card");
            brand.Add(logo);
            brand.Add(El.Text("Porchlight", "signin-title"));
            card.Add(brand);
            UnityEngine.UIElements.Button button = null;
            button = El.Btn(label ?? (provider == "google" ? "Sign in with Google" : "Sign in"), null, () =>
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
