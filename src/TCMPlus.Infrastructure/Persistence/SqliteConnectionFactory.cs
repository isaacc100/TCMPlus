using Microsoft.Data.Sqlite;

namespace TCMPlus.Infrastructure.Persistence;

public sealed class SqliteConnectionFactory(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        ForeignKeys = true,
        Mode = SqliteOpenMode.ReadWriteCreate
    }.ToString();

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
