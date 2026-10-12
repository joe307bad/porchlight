using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Porchlight.Shared.Metrics;
using Porchlight.Shared.Model;

namespace Porchlight.Server;

public enum SaveStatus
{
    Saved,
    NotFound,
    Conflict,
}

/// <summary>
/// Stores each town as a JSONB layout plus metric columns, so the town list never has to load a
/// layout. Version is an optimistic-concurrency counter that goes up by one on every save.
/// </summary>
public sealed class TownRepository(NpgsqlDataSource db, ILogger<TownRepository> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Schema is created on startup for the prototype. Move to real migrations once it settles.
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS towns (
            id              uuid PRIMARY KEY,
            name            text        NOT NULL,
            version         integer     NOT NULL,
            schema_version  integer     NOT NULL,
            population      integer     NOT NULL DEFAULT 0,
            jobs            integer     NOT NULL DEFAULT 0,
            building_count  integer     NOT NULL DEFAULT 0,
            road_length_m   real        NOT NULL DEFAULT 0,
            layout          jsonb       NOT NULL,
            created_at      timestamptz NOT NULL DEFAULT now(),
            updated_at      timestamptz NOT NULL DEFAULT now()
        );
        CREATE INDEX IF NOT EXISTS towns_updated_at_idx ON towns (updated_at DESC);
        """;

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        const int attempts = 15;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var cmd = db.CreateCommand(Schema);
                await cmd.ExecuteNonQueryAsync(ct);
                return;
            }
            catch (NpgsqlException e) when (attempt < attempts)
            {
                log.LogWarning("Postgres not ready ({Message}); retrying ({Attempt}/{Attempts})", e.Message, attempt, attempts);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }

    public async Task<List<TownSummaryDto>> ListAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, name, version, population, jobs, building_count, road_length_m, updated_at
            FROM towns ORDER BY updated_at DESC
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var result = new List<TownSummaryDto>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new TownSummaryDto
            {
                Id = reader.GetGuid(0).ToString(),
                Name = reader.GetString(1),
                Version = reader.GetInt32(2),
                Population = reader.GetInt32(3),
                Jobs = reader.GetInt32(4),
                BuildingCount = reader.GetInt32(5),
                RoadLengthMeters = reader.GetFloat(6),
                UpdatedAt = reader.GetFieldValue<DateTimeOffset>(7),
            });
        }

        return result;
    }

    public async Task<TownDto?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT name, version, layout FROM towns WHERE id = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var town = JsonSerializer.Deserialize<TownDto>(reader.GetString(2), Json)!;
        town.Id = id.ToString();
        town.Name = reader.GetString(0);
        town.Version = reader.GetInt32(1);
        return town;
    }

    public async Task InsertAsync(TownDto town, TownMetrics metrics, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO towns (id, name, version, schema_version, population, jobs, building_count, road_length_m, layout)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = Guid.Parse(town.Id) });
        cmd.Parameters.Add(new NpgsqlParameter { Value = town.Name });
        cmd.Parameters.Add(new NpgsqlParameter { Value = town.Version });
        AddLayoutParameters(cmd, town, metrics);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(SaveStatus Status, TownDto? Town)> UpdateAsync(Guid id, TownDto town, TownMetrics metrics, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE towns
            SET name = $2, version = version + 1, schema_version = $4, population = $5, jobs = $6,
                building_count = $7, road_length_m = $8, layout = $9, updated_at = now()
            WHERE id = $1 AND version = $3
            RETURNING version
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        cmd.Parameters.Add(new NpgsqlParameter { Value = town.Name });
        cmd.Parameters.Add(new NpgsqlParameter { Value = town.Version });
        AddLayoutParameters(cmd, town, metrics);

        if (await cmd.ExecuteScalarAsync(ct) is int newVersion)
        {
            town.Version = newVersion;
            return (SaveStatus.Saved, town);
        }

        return (await ExistsAsync(id, ct) ? SaveStatus.Conflict : SaveStatus.NotFound, null);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("DELETE FROM towns WHERE id = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private async Task<bool> ExistsAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT 1 FROM towns WHERE id = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    // Parameters $4..$9: everything that comes from the layout.
    private static void AddLayoutParameters(NpgsqlCommand cmd, TownDto town, TownMetrics metrics)
    {
        cmd.Parameters.Add(new NpgsqlParameter { Value = town.SchemaVersion });
        cmd.Parameters.Add(new NpgsqlParameter { Value = metrics.Population });
        cmd.Parameters.Add(new NpgsqlParameter { Value = metrics.Jobs });
        cmd.Parameters.Add(new NpgsqlParameter { Value = metrics.BuildingCount });
        cmd.Parameters.Add(new NpgsqlParameter { Value = metrics.RoadLengthMeters });
        cmd.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(town, Json), NpgsqlDbType = NpgsqlDbType.Jsonb });
    }
}
