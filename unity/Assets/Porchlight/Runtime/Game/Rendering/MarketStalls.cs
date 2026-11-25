// The market's stalls in the plaza (Explore/Market.cs) on market day
// (Life/Calendar.cs): each a cloth-covered table under a parasol, laid with what
// it sells (carrots and turnips, melons and pumpkins, flowers, or a box of odds
// and ends), facing the plaza's middle. Up from half an hour before the market
// opens to half an hour after it shuts (the stallholders setting up and packing
// away), and only as many as the day's calendar has pitches taken, the same ones
// as the town's life sends stallholders to. Built once, shown and hidden by the
// town clock.

using System;
using System.Collections.Generic;
using Porchlight.Explore;
using Porchlight.Life;
using Porchlight.Model;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Porchlight.Rendering
{
    public sealed class MarketStalls
    {
        /// <summary>A stall's table across (tiles; a tile is 4 m), and its parasol's height.</summary>
        const double TABLE = 0.42, PARASOL = 0.7;
        /// <summary>Up this long before the market opens and after it shuts.</summary>
        const long SETTING_UP = 30 * TownClock.MINUTE;

        static readonly string[][] GOODS =
        {
            new[] { "nature/crop_carrot", "nature/crop_turnip" },
            new[] { "nature/crop_melon", "nature/crop_pumpkin" },
            new[] { "nature/flower_redA", "nature/flower_yellowA", "nature/flower_purpleA" },
            new[] { "furniture/cardboardBoxOpen" },
        };

        readonly GameObject root;
        readonly List<GameObject> stalls = new List<GameObject>();
        readonly string seed;
        MarketPlace market;
        int shown = -1;

        public int Count => stalls.Count;

        /// <summary>The town's market stalls (none if it has no market), hidden until market day.</summary>
        public MarketStalls(TownModel model, MarketPlace market, ModelLibrary library, Transform parent) : this(model, parent)
        {
            foreach (var _ in MakeSteps(market, library)) { }
        }

        MarketStalls(TownModel model, Transform parent)
        {
            root = new GameObject("market");
            root.transform.SetParent(parent, false);
            seed = model.seed;
        }

        /// <summary>The stalls made a few a step (made before you get to its town: CountyDrive).</summary>
        public static MarketStalls Stepwise(TownModel model, MarketPlace market, ModelLibrary library, Transform parent, out IEnumerable<string> steps)
        {
            var m = new MarketStalls(model, parent);
            steps = m.MakeSteps(market, library);
            return m;
        }

        /// <summary>Shown by this market from now on: a fresh one of the same town (its own traffic's, after the town's been left and come back to).</summary>
        public void Bind(MarketPlace next)
        {
            market = next;
            shown = -1;
        }

        IEnumerable<string> MakeSteps(MarketPlace market, ModelLibrary library)
        {
            this.market = market;
            if (market == null) yield break;
            var table = library.Get("furniture/tableCloth");
            var parasols = Array.ConvertAll(Catalog.PARASOLS, n => library.Get(n));
            for (int k = 0; k < market.stalls.Count; k++)
            {
                var s = market.stalls[k];
                var stall = new GameObject($"stall {k}");
                stall.transform.SetParent(root.transform, false);
                double ts = TABLE / Math.Max(table.size.x, table.size.z);
                Props.Place(table, stall.transform, s.at.x, s.at.y, s.heading + Math.PI / 2, ts, ts, ts);
                double top = table.size.y * ts;
                // The parasol behind the table, over the stallholder.
                var p = parasols[k % parasols.Length];
                double ps = PARASOL / p.size.y;
                double bx = s.keeper.x - s.at.x, by = s.keeper.y - s.at.y;
                Props.Place(p, stall.transform, s.at.x + bx * 0.5, s.at.y + by * 0.5, s.heading, ps, ps, ps);
                // The goods along the table.
                var goods = GOODS[s.kind % GOODS.Length];
                double ax = Math.Cos(s.heading), ay = -Math.Sin(s.heading);
                for (int g = 0; g < 3; g++)
                {
                    var m = library.Get(goods[g % goods.Length]);
                    double across = s.kind == 3 ? 0.1 : 0.09;
                    double gs = across / Math.Max(m.size.x, m.size.z);
                    double off = (g - 1) * TABLE * 0.3;
                    Props.Place(m, stall.transform, s.at.x + ax * off, s.at.y + ay * off, s.heading + g, gs, gs, gs, top, castShadows: false);
                }
                stall.SetActive(false);
                stalls.Add(stall);
                if (k % 4 == 3) yield return "stalls" + k;
            }
        }

        /// <summary>Shows the stalls up at `moment` (the town clock).</summary>
        public void Update(long moment)
        {
            if (market == null) return;
            var td = TownCalendar.Of(seed, TownClock.DayOf(moment), true);
            long tod = moment - TownClock.At(td.day, 0);
            int up = td.market && tod >= td.marketOpen - SETTING_UP && tod < td.marketClose + SETTING_UP ? market.Up(td.stalls) : 0;
            if (up == shown) return;
            shown = up;
            for (int k = 0; k < stalls.Count; k++) stalls[k].SetActive(k < up);
        }

        public void Dispose()
        {
            if (root != null) Object.Destroy(root);
        }
    }
}
