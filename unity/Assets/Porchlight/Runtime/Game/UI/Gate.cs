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
            // Which release this is, in a badge under the name (App/BuildInfo.cs), its shadow a block behind it like the name's.
            card.Add(Badge(BuildInfo.Short));
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
            // A dev build of the desktop app (desktop/dev-mac.sh): which server, in a badge as plain as the
            // release's, and a click to choose another. Never in the released app (Platform.ServerChoosable).
            if (Platform.Desktop && Platform.ServerChoosable)
            {
                var server = Badge("Server: " + new Uri(Platform.Instance.Origin).Authority + " \u00b7 change", () => ServerSettings.Show(root));
                server.AddToClassList("signin-server");
                card.Add(server);
            }
            gate.Add(card);
            root.Add(gate);
            root.AddToClassList("signed-out");
            button.Focus();
            return gate;
        }

        /// <summary>Dark text on the card's cream, its shadow a block behind it like the name's; clicked if given a click.</summary>
        static VisualElement Badge(string text, Action onClick = null)
        {
            var badge = new VisualElement();
            badge.AddToClassList("signin-badge");
            var shadow = new VisualElement();
            shadow.AddToClassList("signin-badge-shadow");
            badge.Add(shadow);
            var face = new VisualElement();
            face.AddToClassList("signin-badge-face");
            face.Add(El.Text(text, "signin-badge-text"));
            badge.Add(face);
            if (onClick != null)
            {
                badge.AddToClassList("signin-badge-button");
                badge.RegisterCallback<ClickEvent>(_ => onClick());
            }
            return badge;
        }
    }
}
