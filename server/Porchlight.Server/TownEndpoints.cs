using Porchlight.Shared.Api;
using Porchlight.Shared.Metrics;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;

namespace Porchlight.Server;

public static class TownEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet(ApiRoutes.Health, () => Results.Ok(new { ok = true }));

        var towns = app.MapGroup(ApiRoutes.Towns);

        towns.MapGet("", (TownRepository repo, CancellationToken ct) => repo.ListAsync(ct));

        towns.MapGet("/{id:guid}", async (Guid id, TownRepository repo, CancellationToken ct) =>
            await repo.GetAsync(id, ct) is { } town ? Results.Ok(town) : Results.NotFound());

        towns.MapPost("", async (CreateTownRequest request, TownRepository repo, CancellationToken ct) =>
        {
            var name = request.Name?.Trim() ?? "";
            var size = request.Size ?? TownLimits.DefaultSize;
            if (name.Length is 0 or > TownLimits.MaxNameLength)
                return Error($"Name must be 1-{TownLimits.MaxNameLength} characters");
            if (size is < TownLimits.MinSize or > TownLimits.MaxSize)
                return Error($"Size must be between {TownLimits.MinSize} and {TownLimits.MaxSize}");

            var town = TownFactory.CreateEmpty(Guid.NewGuid().ToString(), name, size);
            await repo.InsertAsync(town, TownMetrics.Compute(town), ct);
            return Results.Created(ApiRoutes.Town(town.Id), town);
        });

        towns.MapPut("/{id:guid}", async (Guid id, TownDto town, TownRepository repo, CancellationToken ct) =>
        {
            town.Id = id.ToString();
            town.Name = town.Name?.Trim() ?? "";

            // The same rules the client ran. Never trust a client's snapshot.
            var validation = TownRules.ValidateTown(town);
            if (!validation.IsValid) return Error("Town is invalid", validation.Errors);

            var (status, saved) = await repo.UpdateAsync(id, town, TownMetrics.Compute(town), ct);
            return status switch
            {
                SaveStatus.NotFound => Results.NotFound(),
                SaveStatus.Conflict => Results.Conflict(new ApiError
                {
                    Error = "This town was saved by someone else since you loaded it",
                }),
                _ => Results.Ok(saved),
            };
        });

        towns.MapDelete("/{id:guid}", async (Guid id, TownRepository repo, CancellationToken ct) =>
            await repo.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());
    }

    private static IResult Error(string message, List<string>? details = null) =>
        Results.BadRequest(new ApiError { Error = message, Details = details ?? [] });
}
