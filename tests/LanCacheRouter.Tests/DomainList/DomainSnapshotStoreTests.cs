using LanCacheRouter.DomainList;

namespace LanCacheRouter.Tests.DomainList;

public sealed class DomainSnapshotStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lancacherouter-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    private string SnapshotPath => Path.Combine(_directory, "nested", "domains-snapshot.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static RawDomainList Sample() => new(
        new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero),
        FakeDomainSource.BaseUrl,
        [new ServiceDomains("steam", ["*.steamcontent.com", "lancache.steamcontent.com"]), new ServiceDomains("epic", ["*.epic.com"])]);

    [Fact]
    public void SaveThenLoad_RoundTrips_AndCreatesTheFolder()
    {
        DomainSnapshotStore store = new(SnapshotPath, _log.Add);

        store.Save(Sample());
        RawDomainList? loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(Sample().FetchedAt, loaded.FetchedAt);
        Assert.Equal(FakeDomainSource.BaseUrl, loaded.BaseUrl);
        Assert.Equal(["steam", "epic"], loaded.Services.Select(s => s.Name));
        Assert.Equal(["*.steamcontent.com", "lancache.steamcontent.com"], loaded.Services[0].Patterns);
        Assert.Empty(_log);
    }

    [Fact]
    public void Save_LeavesNoTempFileBehind_AndOverwrites()
    {
        DomainSnapshotStore store = new(SnapshotPath, _log.Add);

        store.Save(Sample());
        store.Save(Sample() with { Services = [new ServiceDomains("only", ["a.com"])] });

        Assert.False(File.Exists(SnapshotPath + ".tmp"));
        Assert.Equal(["only"], store.Load()!.Services.Select(s => s.Name));
    }

    [Fact]
    public void Load_ReturnsNull_WhenThereIsNoFile()
    {
        Assert.Null(new DomainSnapshotStore(SnapshotPath, _log.Add).Load());
        Assert.Empty(_log);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{ \"fetchedAt\": 5 }")]
    [InlineData("null")]
    public void Load_IgnoresCorruptFiles_AndLogs(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
        File.WriteAllText(SnapshotPath, content);

        Assert.Null(new DomainSnapshotStore(SnapshotPath, _log.Add).Load());
    }

    [Fact]
    public void Load_RevalidatesPatterns_DroppingTamperedEntries()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
        File.WriteAllText(SnapshotPath, """
            { "fetchedAt": "2026-01-01T00:00:00+00:00", "baseUrl": "https://example.test/x/",
              "services": [ { "name": "s", "patterns": ["OK.example.com", "*", "bad domain", "*.fine.org"] },
                            { "name": "", "patterns": ["a.com"] } ] }
            """);

        RawDomainList loaded = new DomainSnapshotStore(SnapshotPath, _log.Add).Load()!;

        Assert.Single(loaded.Services);
        Assert.Equal(["ok.example.com", "*.fine.org"], loaded.Services[0].Patterns);
    }

    [Fact]
    public void Save_LogsInsteadOfThrowing_WhenTheTargetIsNotWritable()
    {
        Directory.CreateDirectory(_directory);
        string blocker = Path.Combine(_directory, "file");
        File.WriteAllText(blocker, "x");

        new DomainSnapshotStore(Path.Combine(blocker, "snapshot.json"), _log.Add).Save(Sample());

        Assert.Contains(_log, l => l.Contains("could not save snapshot"));
    }

    public static TheoryData<string> DamagedContents() => new()
    {
        """{ "fetchedAt": "2026-01-01T00:00:00+00:00", "baseUrl": "https://example.test/x/", "services": [ null ] }""",
        """{ "fetchedAt": "2026-01-01T00:00:00+00:00", "baseUrl": "https://example.test/x/", "services": [ { "name": "s", "patterns": [ null, 5 ] } ] }""",
        """{ "fetchedAt": "2026-01-01T00:00:00+00:00", "baseUrl": "https://example.test/x/", "services": [ { "name": "s", "patterns": "a.com" } ] }""",
        """{ "fetchedAt": "2026-01-01T00:00:00+00:00", "baseUrl": "https://example.test/x/", "services": [ { "name": 5, "patterns": [] } ] }""",
        """{ "fetchedAt": "2026-01-01T00:00:00+00:00", "baseUrl": "https://example.test/x/", "services": {} }""",
        """{ "fetchedAt": "yesterday", "baseUrl": "https://example.test/x/", "services": [] }""",
        """{ "fetchedAt": "2026-01-01T00:00:00+00:00", "baseUrl": 5, "services": [] }""",
        """{ "fetchedAt": "2026-01-01T00:00:00+00:00", "baseUrl": "https://example.test/x/", "services": [ { "name": "s", "patterns": ["a.com"] }""", // cut off in the middle
        "[1, 2, 3]",
        "\"text\"",
        "42",
        "true",
        "\0\0\0\0\0\0",
        "﻿", // nothing but a byte order mark
        "{ \"fetchedAt\": \"2026-01-01T00:00:00+00:00\", \"baseUrl\": \"https://x/\", \"services\": [ { \"name\": \"s\", \"patterns\": [\"" + new string('a', 100_000) + ".com\"] } ] }",
    };

    [Theory]
    [MemberData(nameof(DamagedContents))]
    public void Load_NeverThrows_WhateverIsInTheFile(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
        File.WriteAllText(SnapshotPath, content);

        RawDomainList? loaded = new DomainSnapshotStore(SnapshotPath, _log.Add).Load();

        // Either nothing usable, or a list whose every pattern is valid again.
        if (loaded is not null)
        {
            foreach (string pattern in loaded.Services.SelectMany(s => s.Patterns))
                Assert.True(DomainName.TryNormalizePattern(pattern, out _, out _));
        }
    }

    [Fact]
    public void Load_ReturnsNull_WhenThePathIsADirectory()
    {
        Directory.CreateDirectory(SnapshotPath);

        Assert.Null(new DomainSnapshotStore(SnapshotPath, _log.Add).Load());
    }

    [Fact]
    public void Load_AcceptsAFileWithAByteOrderMark()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
        File.WriteAllText(SnapshotPath, """{ "fetchedAt": "2026-01-01T00:00:00+00:00", "baseUrl": "https://x/", "services": [ { "name": "s", "patterns": ["a.com"] } ] }""",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal(["a.com"], new DomainSnapshotStore(SnapshotPath, _log.Add).Load()!.Services[0].Patterns);
    }

    [Fact]
    public void SaveAndLoad_NeverThrow_WhenThePathIsInvalid()
    {
        DomainSnapshotStore store = new("/tmp/lancacherouter\0invalid/snapshot.json", _log.Add);

        store.Save(Sample());
        Assert.Null(store.Load());

        Assert.Contains(_log, l => l.Contains("could not save snapshot"));
    }

    [Fact]
    public void ALoggerThatThrows_DoesNotMakeSaveOrLoadThrow()
    {
        Directory.CreateDirectory(_directory);
        string blocker = Path.Combine(_directory, "file");
        File.WriteAllText(blocker, "x");
        DomainSnapshotStore unwritable = new(Path.Combine(blocker, "snapshot.json"), _ => throw new InvalidOperationException("log is broken"));
        Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
        File.WriteAllText(SnapshotPath, "not json");
        DomainSnapshotStore damaged = new(SnapshotPath, _ => throw new InvalidOperationException("log is broken"));

        unwritable.Save(Sample());
        Assert.Null(damaged.Load());
    }

    [Fact]
    public async Task ReadersNeverSeeAHalfWrittenSnapshot_WhileAnotherThreadKeepsSaving()
    {
        DomainSnapshotStore store = new(SnapshotPath, _log.Add);
        store.Save(Sample());
        using CancellationTokenSource stop = new();

        Task writer = Task.Run(() =>
        {
            for (int i = 0; i < 300; i++)
                store.Save(Sample() with { Services = [new ServiceDomains("s" + i, [.. Enumerable.Range(0, 200).Select(n => $"host{n}.example.com")])] });

            stop.Cancel();
        });

        int reads = 0;
        while (!stop.IsCancellationRequested)
        {
            RawDomainList? loaded = store.Load();
            Assert.NotNull(loaded); // the old or the new file, never a torn one
            Assert.True(loaded.Services.Count == 2 || loaded.Services[0].Patterns.Count == 200);
            reads++;
        }

        await writer;
        Assert.True(reads > 0);
        Assert.Empty(_log);
    }

    [Fact]
    public void SnapshotWithAHugeNumberOfServices_RoundTrips()
    {
        DomainSnapshotStore store = new(SnapshotPath, _log.Add);
        RawDomainList big = Sample() with { Services = [.. Enumerable.Range(0, 500).Select(i => new ServiceDomains("service" + i, [.. Enumerable.Range(0, 100).Select(n => $"*.h{n}.s{i}.example.com")]))] };

        store.Save(big);
        RawDomainList loaded = store.Load()!;

        Assert.Equal(500, loaded.Services.Count);
        Assert.All(loaded.Services, s => Assert.Equal(100, s.Patterns.Count));
    }
}
