using Testcontainers.PostgreSql;

namespace AngstromCommander.Server.Tests.Integration;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString => this._container.GetConnectionString();

    public Task InitializeAsync()
    {
        return this._container.StartAsync();
    }

    public Task DisposeAsync()
    {
        return this._container.DisposeAsync().AsTask();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollectionDefinition : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
