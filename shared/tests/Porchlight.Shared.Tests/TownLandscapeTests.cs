using Porchlight.Shared.Editing;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using Xunit;

namespace Porchlight.Shared.Tests;

public class TownLandscapeTests
{
    private static TownDto NewTown() => TownFactory.CreateEmpty(Ids.New(), "Landscape");

    private static void Raise(TownDto town, float x, float z, int levels) =>
        Assert.True(TownEditor.Apply(town, new ShapeTerrainCommand { X = x, Z = z, PadHalfSize = 0f, Delta = levels }).Ok);

    /// <summary>A plaza with tiles raised every which way, like the terrain tool clicked all over it.</summary>
    private static TownDto Jumble()
    {
        var town = NewTown();
        Assert.True(TownEditor.Apply(town, new PaintTilesCommand { Cells = TownTiles.Area(town, new Vec2(-30f, -30f), new Vec2(30f, 30f)), SurfaceId = "plaza" }).Ok);
        var random = new System.Random(7);
        for (var n = 0; n < 120; n++) Raise(town, random.Next(-7, 8) * 4f + 2f, random.Next(-7, 8) * 4f + 2f, random.Next(1, 4));
        return town;
    }

    [Fact]
    public void AJumbleOfRaisedTilesCanAllBeReachedOnFoot()
    {
        var town = Jumble();
        Assert.True(TownLandscape.CountPatches(town) > 10);

        var play = TownLandscape.ForPlay(town);
        Assert.Equal(1, TownLandscape.CountPatches(play));

        // Mostly steps, with ladders up the walls too tall for them.
        var flights = play.Tiles.Flights?.FindAll(f => f != 0).Count ?? 0;
        var ladders = play.Tiles.Ladders?.FindAll(l => l != 0).Count ?? 0;
        Assert.True(flights > 0, "no steps");
        Assert.True(ladders > 0, "no ladders");
        Assert.True(flights + ladders < TownLandscape.CountPatches(town) + 5, $"{flights} flights and {ladders} ladders");

        // The town itself is untouched, and the same town always comes out the same.
        Assert.Null(town.Tiles.Flights);
        Assert.Null(town.Tiles.Ladders);
        Assert.Equal(play.Tiles.Flights, TownLandscape.ForPlay(town).Tiles.Flights);
        Assert.Equal(play.Tiles.Ladders, TownLandscape.ForPlay(town).Tiles.Ladders);
    }

    [Fact]
    public void StepsClimbFromTheFootOfTheWallToTheTop()
    {
        var town = NewTown();
        Assert.True(TownEditor.Apply(town, new PaintTilesCommand { Cells = TownTiles.Area(town, new Vec2(-10f, -10f), new Vec2(10f, 10f)), SurfaceId = "plaza" }).Ok);
        Raise(town, 2f, 2f, 2);
        var play = TownLandscape.ForPlay(town);
        Assert.Equal(1, TownLandscape.CountPatches(play));

        // One flight, on a tile beside the raised one, rising two levels against it.
        Assert.True(TownTiles.TryCell(play, new Vec2(2f, 2f), out var ti, out var tj));
        var found = false;
        for (var s = 0; s < 4; s++)
        {
            var (di, dj) = TownTiles.Neighbours[s];
            if (!TownTiles.TryFlight(play, ti + di, tj + dj, out var side, out var rise)) continue;
            Assert.Equal((s + 2) % 4, (int)side);
            Assert.Equal(2, rise);
            var foot = TownTiles.Center(play, ti + di, tj + dj);
            var toward = new Vec2(-di, -dj);
            Assert.Equal(0f, TownTerrain.HeightAt(play, foot - toward * 1.5f), 2);
            Assert.Equal(2f, TownTerrain.HeightAt(play, foot + toward * 1.99f), 1);
            for (var t = -1f; t < 1.99f; t += 0.1f)
                Assert.True(TownTerrain.HeightAt(play, foot + toward * (t + 0.1f)) - TownTerrain.HeightAt(play, foot + toward * t) < 0.45f);
            found = true;
        }

        Assert.True(found);
    }

    [Fact]
    public void TallWallsGetALadderToClimb()
    {
        var town = NewTown();
        Raise(town, 2f, 2f, 6);
        var play = TownLandscape.ForPlay(town);
        Assert.Equal(1, TownLandscape.CountPatches(play));

        // Walking at the wall from the south, there's a ladder to take hold of up to the top.
        Assert.True(TownTiles.TryCell(play, new Vec2(2f, 2f), out var ti, out var tj));
        var ladderTile = -1;
        for (var s = 0; s < 4; s++)
        {
            var (di, dj) = TownTiles.Neighbours[s];
            if (TownTiles.HasLadder(play, ti + di, tj + dj, (TileSide)((s + 2) % 4))) ladderTile = s;
        }

        Assert.True(ladderTile >= 0);
        var (ldi, ldj) = TownTiles.Neighbours[ladderTile];
        var toward = new Vec2(-ldi, -ldj);
        var at = new Vec2(2f, 2f) - toward * 2.5f;
        Assert.True(TownWalking.TryLadder(play, at, toward, out var foot, out _, out var top, out var landing));
        Assert.Equal(6f, top, 2);
        Assert.Equal(6f, TownTerrain.HeightAt(play, landing), 2);
        Assert.False(TownWalking.TryLadder(play, at, -toward, out _, out _, out _, out _));
        Assert.True(Vec2.Distance(foot, at) < 1f);

        // From the top, heading back over the edge, the same ladder goes down to its foot.
        Assert.True(TownWalking.TryLadderDown(play, landing, -toward, out var downFoot, out _, out var downTop, out var downLanding));
        Assert.Equal(foot.X, downFoot.X, 2);
        Assert.Equal(foot.Z, downFoot.Z, 2);
        Assert.Equal(6f, downTop, 2);
        Assert.Equal(landing.X, downLanding.X, 2);
        Assert.Equal(landing.Z, downLanding.Z, 2);
        Assert.False(TownWalking.TryLadderDown(play, landing, toward, out _, out _, out _, out _));
    }

    [Fact]
    public void GrassTerracesSoftenIntoSlopes()
    {
        var town = NewTown();
        Assert.True(TownEditor.Apply(town, new ShapeTerrainCommand { X = 0f, Z = 0f, PadHalfSize = 8f, Delta = 1 }).Ok);
        Assert.True(TownLandscape.CountPatches(town) > 1);
        var play = TownLandscape.ForPlay(town);
        Assert.Equal(1, TownLandscape.CountPatches(play));
        Assert.Null(play.Tiles.Flights);
        Assert.Null(play.Tiles.Ladders);
        for (var x = -16f; x < 16f; x += 0.25f)
            Assert.True(MathF.Abs(TownTerrain.HeightAt(play, new Vec2(x + 0.25f, 2f)) - TownTerrain.HeightAt(play, new Vec2(x, 2f))) < 0.2f);
    }

    [Fact]
    public void PlayGroundIsNeverSaved()
    {
        var play = TownLandscape.ForPlay(Jumble());
        Assert.Contains(TownRules.ValidateTown(play).Errors, e => e.Contains("made for play"));
    }
}
