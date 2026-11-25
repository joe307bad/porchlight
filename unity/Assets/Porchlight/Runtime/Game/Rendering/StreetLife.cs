// A town's street life as the game draws it: its traffic and townsfolk's days
// (Traffic, with its road network, router and everyone's day), its cars, its
// traffic signals and stop signs, and its market stalls. Made before you get to
// the town (CountyDrive, while it builds the town: the traffic on a worker
// thread off the web, the cars, signs and stalls a few a frame), and handed to
// the view as you cross into it (ExploreView.SwitchSpace). Made there, Porchlight's
// was most of a second of one frame: you stopped dead at its edge at speed.
//
// Kept when you leave the town (its cars hidden, its signs left standing) and
// used again if you come back: only a fresh Traffic is wanted then, and that's
// made off the main thread too. Freed with the town's world (everything's
// under its root).

using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Model;
using UnityEngine;

namespace Porchlight.Rendering
{
    public sealed class StreetLife
    {
        /// <summary>A fresh traffic for the next time its town is driven into (null once it's been taken).</summary>
        public Traffic traffic;
        /// <summary>Whether `traffic`'s life has been started already (made ahead: the view catches it up to now if it runs it).</summary>
        public bool started;
        public Fleet fleet;
        public Signage signage;
        public MarketStalls market;
        IEnumerator<string> making;

        /// <summary>The town's traffic, as the view has always made it (the same cars in the same slots every time).</summary>
        public static Traffic NewTraffic(TownModel model, IsoLayout layout) =>
            new Traffic(model, layout, null, new Rng($"{model.seed}::traffic"), Catalog.PickCarModel);

        /// <summary>Its cars, signs and stalls made: ready to hand over.</summary>
        public bool Made => making == null && fleet != null;

        /// <summary>Begin making the cars, signs and stalls for `traffic` (which must be set), under `parent` (the town's root).</summary>
        public void BeginMaking(TownModel model, ModelLibrary library, Transform parent) =>
            making = Steps(traffic, model, library, parent).GetEnumerator();

        IEnumerable<string> Steps(Traffic t, TownModel model, ModelLibrary library, Transform parent)
        {
            fleet = Fleet.Stepwise(t, library, parent, out var cars);
            foreach (var s in cars) yield return s;
            signage = Signage.Stepwise(t.life.net, library, parent, out var signs);
            foreach (var s in signs) yield return s;
            market = MarketStalls.Stepwise(model, t.life.market, library, parent, out var stalls);
            foreach (var s in stalls) yield return s;
        }

        /// <summary>A step of making: false once it's all made.</summary>
        public bool Step()
        {
            if (making == null) return false;
            if (making.MoveNext()) return true;
            making = null;
            return false;
        }

        /// <summary>What the last step made (for the log).</summary>
        public string Current => making?.Current;

        /// <summary>Drawing this traffic from now on (a fresh one of the same town): false if it isn't the same town's.</summary>
        public bool Bind(Traffic t)
        {
            if (!fleet.Bind(t)) return false;
            signage.Bind(t.life.net);
            market.Bind(t.life.market);
            return true;
        }

        /// <summary>Its cars, signs and stalls, gone now (all at once: only when they can't be used).</summary>
        public void Dispose()
        {
            fleet?.Dispose();
            signage?.Dispose();
            market?.Dispose();
            fleet = null;
            signage = null;
            market = null;
            making = null;
        }
    }
}
