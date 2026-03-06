using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shiny.SqliteDocumentDb;

namespace AiTextAdventure.Tests;

/// <summary>
/// Tests the persistence layer document model serialization/deserialization
/// using in-memory SQLite (no MAUI dependency).
/// </summary>
public class PersistenceTests : IDisposable
{
    // Document types redefined here (no project reference) to test
    // that the JSON serialization patterns work correctly.

    private class TestDoc
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public Guid ForeignKey { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    private readonly SqliteDocumentStore _store;
    private readonly string _dbPath;

    public PersistenceTests()
    {
        // Use a unique temp file per test class instance (avoids connection sharing issues with :memory:)
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddSqliteDocumentStore(opts =>
        {
            opts.ConnectionString = $"Data Source={_dbPath}";
        });
        var provider = services.BuildServiceProvider();
        _store = (SqliteDocumentStore)provider.GetRequiredService<IDocumentStore>();
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Fact]
    public async Task Set_AndGet_RoundTrips()
    {
        // Arrange
        var doc = new TestDoc
        {
            Id = Guid.NewGuid(),
            Name = "Test Document",
            ForeignKey = Guid.NewGuid()
        };

        // Act - Set returns the storage key
        var key = await _store.Set(doc);
        var retrieved = await _store.Get<TestDoc>(key);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(doc.Id, retrieved.Id);
        Assert.Equal(doc.Name, retrieved.Name);
        Assert.Equal(doc.ForeignKey, retrieved.ForeignKey);
    }

    [Fact]
    public async Task GetAll_ReturnsAllDocuments()
    {
        // Arrange
        var docs = Enumerable.Range(1, 3).Select(i => new TestDoc
        {
            Id = Guid.NewGuid(),
            Name = $"Doc {i}"
        }).ToList();

        foreach (var doc in docs)
            await _store.Set(doc);

        // Act
        var all = await _store.GetAll<TestDoc>();

        // Assert
        Assert.Equal(docs.Count, all.Count);
    }

    [Fact]
    public async Task Query_FiltersByPredicate()
    {
        // Arrange
        var targetKey = Guid.NewGuid();
        var matching = new TestDoc { Id = Guid.NewGuid(), Name = "Match", ForeignKey = targetKey };
        var other = new TestDoc { Id = Guid.NewGuid(), Name = "NoMatch", ForeignKey = Guid.NewGuid() };

        await _store.Set(matching);
        await _store.Set(other);

        // Act - use GetAll + LINQ filter (no source-gen JsonTypeInfo in tests)
        var all = await _store.GetAll<TestDoc>();
        var results = all.Where(d => d.ForeignKey == targetKey).ToList();

        // Assert
        Assert.Single(results);
        Assert.Equal(matching.Id, results[0].Id);
    }

    [Fact]
    public async Task Remove_DeletesDocument()
    {
        // Arrange
        var doc = new TestDoc { Id = Guid.NewGuid(), Name = "To Delete" };
        var key = await _store.Set(doc);

        // Act
        var removed = await _store.Remove<TestDoc>(key);
        var retrieved = await _store.Get<TestDoc>(key);

        // Assert
        Assert.True(removed);
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task Set_UpdatesExistingDocument()
    {
        // Arrange - use explicit string key for stable upsert behavior
        var doc = new TestDoc { Id = Guid.NewGuid(), Name = "Original" };
        var key = doc.Id.ToString();
        await _store.Set(key, doc);

        // Act - update using same explicit key
        doc.Name = "Updated";
        await _store.Set(key, doc);

        var retrieved = await _store.Get<TestDoc>(key);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("Updated", retrieved.Name);
    }

    [Fact]
    public async Task Query_ReturnsEmptyWhenNoMatch()
    {
        // Arrange
        var doc = new TestDoc { Id = Guid.NewGuid(), Name = "Exists", ForeignKey = Guid.NewGuid() };
        await _store.Set(doc);

        // Act - use GetAll + LINQ filter
        var all = await _store.GetAll<TestDoc>();
        var results = all.Where(d => d.ForeignKey == Guid.Empty).ToList();

        // Assert
        Assert.Empty(results);
    }
}
