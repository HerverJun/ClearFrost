namespace ClearFrost.Tests;

public static class TestCollections
{
    public const string SqliteGlobalPool = "SQLite global pool";
}

[CollectionDefinition(TestCollections.SqliteGlobalPool, DisableParallelization = true)]
public sealed class SqliteGlobalPoolCollection
{
}
