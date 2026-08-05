using Testcontainers.PostgreSql;

namespace AngstromCommander.Server.Tests.Integration;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString => this._container.GetConnectionString();

    public ValueTask InitializeAsync()
    {
        return new ValueTask(this._container.StartAsync());
    }

    public ValueTask DisposeAsync()
    {
        return this._container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollectionDefinition : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
