// The junction design (docs/street-editor.md §5): what the design stage (Gen/Design.cs) gives each road and
// junction for the traffic it'll carry. Turn pockets, a protected left, a roundabout, a slip road, more lanes, a
// median. The road network (Life/RoadNet.cs) builds its lanes from it, the town draws it, and Manage and the
// street editor show it. An editor's overrides (Skeleton.roads, Skeleton.junctions) win over it.

using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Model
{
    public enum JunctionForm { Plain, Roundabout }

    /// <summary>How a junction is run once designed: the design stage's choice, or an editor's.</summary>
    public enum DesignControl { Auto, Free, Stop, AllWayStop, Signal, Yield }

    /// <summary>A turn pocket: a short lane for turners at one end of a road, in one direction.</summary>
    public sealed class Pocket
    {
        /// <summary>True: at the road's `to` end (for traffic going from→to); false: at its `from` end (to→from).</summary>
        public bool atTo;
        /// <summary>True: a left-turn pocket (beside the centre line); false: a right-turn pocket (in the parking lane).</summary>
        public bool left;
        /// <summary>Its storage, from the stop line back (tiles), not counting the taper.</summary>
        public double length;
    }

    public sealed class EdgeDesign
    {
        public int edge;
        /// <summary>Through lanes from→to and to→from; 0 one way is a one-way road.</summary>
        public int lanesFwd = 1, lanesBack = 1;
        /// <summary>A raised median between the two ways (a divided road).</summary>
        public bool divided;
        /// <summary>The carriageway's width (tiles), kerb to kerb, parking lanes not counted.</summary>
        public double width;
        public readonly List<Pocket> pockets = new List<Pocket>();
        /// <summary>The design flow each way (cars an hour at the peak).</summary>
        public double flowFwd, flowBack;
        /// <summary>A collector upgraded to carry the traffic off an overloaded arterial (a relief route).</summary>
        public bool relief;
        /// <summary>An editor locked it: the design stage left it as it was.</summary>
        public bool locked;
        /// <summary>Its design flow over its capacity, the busier way (1: full).</summary>
        public double vc;

        public bool OneWay => lanesFwd == 0 || lanesBack == 0;
    }

    /// <summary>A slip road: a short one-way road taking right-turners from one leg to the next, round the junction.</summary>
    public sealed class Slip
    {
        public int fromEdge, toEdge;
        /// <summary>Its curve, from where it leaves the one road to where it joins the other.</summary>
        public List<Vec2> points = new List<Vec2>();
    }

    public sealed class NodeDesign
    {
        public int node;
        public JunctionForm form;
        /// <summary>A roundabout's radius to the middle of its ring (tiles).</summary>
        public double radius;
        /// <summary>Auto: as the road network chooses by rank (town-life §2).</summary>
        public DesignControl control;
        /// <summary>A signal with a leading left-turn stage for the busiest approach's pocket.</summary>
        public bool protectedLeft;
        public readonly List<Slip> slips = new List<Slip>();
        /// <summary>Cars entering an hour at the peak.</summary>
        public double entering;
    }

    /// <summary>Something the design couldn't fix: a road still over capacity, a roundabout with no room.</summary>
    public sealed class DesignNote
    {
        public Vec2 at;
        public string text;
    }

    public sealed class RoadDesign
    {
        /// <summary>One per road edge, by edge id.</summary>
        public readonly List<EdgeDesign> edges = new List<EdgeDesign>();
        /// <summary>One per road node, by node id.</summary>
        public readonly List<NodeDesign> nodes = new List<NodeDesign>();
        public readonly List<DesignNote> notes = new List<DesignNote>();

        public EdgeDesign Edge(int id) => id >= 0 && id < edges.Count ? edges[id] : null;
        public NodeDesign Node(int id) => id >= 0 && id < nodes.Count ? nodes[id] : null;
    }
}
