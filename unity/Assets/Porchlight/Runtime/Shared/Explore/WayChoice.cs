// Which way you've chosen to be shown, one at a time: to a spot you picked on
// the map, to a player you're following (from room to room), or a trip across
// the county (to a house, or a town on the county map). Choosing one is instead
// of whichever you had: it says which that was, for the game to stop. (A
// mission's guide is the mission's, not a choice of yours: it isn't one of these.)
// Pure, so it's checked without an engine (Tools~/Checks WayCheck.cs).

namespace Porchlight.Explore
{
    public enum Way { None, Spot, Player, Trip }

    public sealed class WayChoice
    {
        public Way Current { get; private set; }
        /// <summary>Whose or where: the player's character id, the trip's id; null for a spot.</summary>
        public string Id { get; private set; }

        /// <summary>Show this way now: the way it replaces (to stop), or Way.None if there wasn't one, or it's the same.</summary>
        public (Way way, string id) Choose(Way way, string id = null)
        {
            var was = (Current, Id);
            Current = way;
            Id = way == Way.None ? null : id;
            if (was.Current == Way.None || was.Current == way && was.Id == id) return (Way.None, null);
            return was;
        }

        /// <summary>This way has ended (got there, stopped, gone): nothing's shown, if it was the one shown.</summary>
        public void Ended(Way way, string id = null)
        {
            if (Current == way && (id == null || Id == id))
            {
                Current = Way.None;
                Id = null;
            }
        }

        /// <summary>This is the way being shown.</summary>
        public bool Showing(Way way, string id = null) => Current == way && (id == null || Id == id);
    }
}
