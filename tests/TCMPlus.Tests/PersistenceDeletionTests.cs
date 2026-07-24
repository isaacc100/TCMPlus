using Microsoft.Data.Sqlite;
using TCMPlus.Domain.Models;
using TCMPlus.Infrastructure.Persistence;

namespace TCMPlus.Tests;

public sealed class PersistenceDeletionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TCMPlusTests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "tcm.sqlite");

    [Fact]
    public async Task Soft_deleted_stations_remain_in_sqlite_but_are_hidden_from_active_queries()
    {
        var factory = await CreateInitializedFactoryAsync();
        var repository = new SqliteStationRepository(factory);
        var station = new Station(Guid.NewGuid(), "Bay 1", "Bed", 1, 1, 8, 7);
        await repository.AddAsync(station);

        await repository.SoftDeleteAsync(station.Id, DateTimeOffset.UtcNow);

        Assert.Empty(await repository.GetAllAsync());
        await using var connection = factory.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT deleted_at_utc FROM stations WHERE id = @id;";
        command.Parameters.AddWithValue("@id", station.Id.ToString("N"));
        Assert.False(string.IsNullOrWhiteSpace((string?)await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Deleting_a_patient_removes_the_patient_and_their_lifecycle_events()
    {
        var factory = await CreateInitializedFactoryAsync();
        var stations = new SqliteStationRepository(factory);
        var patients = new SqlitePatientRepository(factory);
        var station = new Station(Guid.NewGuid(), "Bay 1", "Bed", 1, 1, 8, 7);
        var patient = new Patient(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, station.Id, null, null, null);
        await stations.AddAsync(station);
        await patients.AddAsync(patient);
        await patients.AddEventAsync(new PatientEvent(Guid.NewGuid(), patient.Uid, patient.PatientNumber, PatientEventType.Added, DateTimeOffset.UtcNow, null, station.Name));

        await patients.DeleteAsync(patient.Uid);

        Assert.Empty(await patients.GetAllAsync());
        Assert.Empty(await patients.GetAllEventsAsync());
        Assert.Single(await stations.GetAllAsync());
    }

    [Fact]
    public async Task Initializer_adds_soft_delete_support_to_existing_station_tables()
    {
        Directory.CreateDirectory(_directory);
        var factory = new SqliteConnectionFactory(DatabasePath);
        await using (var connection = factory.OpenConnection())
        {
            await using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE stations (
                    id TEXT PRIMARY KEY NOT NULL,
                    name TEXT NOT NULL,
                    station_type TEXT NOT NULL,
                    grid_x REAL NOT NULL,
                    grid_y REAL NOT NULL,
                    grid_width REAL NOT NULL,
                    grid_height REAL NOT NULL
                );
                """;
            await create.ExecuteNonQueryAsync();
        }

        await new DatabaseInitializer(factory).InitializeAsync();

        await using var migrated = factory.OpenConnection();
        await using var columns = migrated.CreateCommand();
        columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('stations') WHERE name = 'deleted_at_utc';";
        Assert.Equal(1L, await columns.ExecuteScalarAsync());
    }

    private async Task<SqliteConnectionFactory> CreateInitializedFactoryAsync()
    {
        Directory.CreateDirectory(_directory);
        var factory = new SqliteConnectionFactory(DatabasePath);
        await new DatabaseInitializer(factory).InitializeAsync();
        return factory;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}
