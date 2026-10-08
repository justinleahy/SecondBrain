using System.Reflection;
using Microsoft.Data.Sqlite;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class SmokeTests
{
    [Fact]
    public async Task StorageAssemblyAndBundledSqliteAreAvailable()
    {
        Assert.Equal("SecondBrain.Storage", Assembly.Load("SecondBrain.Storage").GetName().Name);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE VIRTUAL TABLE smoke USING fts5(text);";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "SELECT json_valid('{}');";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }
}
