using Glowpilot.Service;
using Microsoft.Extensions.Logging;

namespace Glowpilot.Service.Tests;

public class FileLoggerProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"glowpilot-log-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Reads a log file while the provider still holds it open for writing.</summary>
    private string Read(string fileName = FileLoggerProvider.FileName)
    {
        using var stream = new FileStream(
            Path.Combine(_dir, fileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private string PathOf(string fileName) => Path.Combine(_dir, fileName);

    // ── Format ────────────────────────────────────────────────────────────────

    [Fact]
    public void Log_WritesTimestampLevelShortCategoryAndMessage()
    {
        using var provider = new FileLoggerProvider(_dir);

        provider.CreateLogger("Glowpilot.Service.DeviceConnectionManager")
            .LogWarning("Port {Port} busy", "COM5");

        Assert.Matches(
            @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[WRN\] DeviceConnectionManager: Port COM5 busy\r?\n$",
            Read());
    }

    [Fact]
    public void Log_ForeignCategory_KeepsFullName()
    {
        using var provider = new FileLoggerProvider(_dir);

        provider.CreateLogger("Microsoft.Hosting.Lifetime").LogInformation("Application started");

        Assert.Contains("[INF] Microsoft.Hosting.Lifetime: Application started", Read());
    }

    [Fact]
    public void Log_WithException_AppendsExceptionDetailsAfterMessage()
    {
        using var provider = new FileLoggerProvider(_dir);

        provider.CreateLogger("Test").LogError(new InvalidOperationException("boom"), "Serial write failed");

        var text = Read();
        Assert.Contains("[ERR] Test: Serial write failed", text);
        Assert.Contains("System.InvalidOperationException: boom", text);
    }

    // ── File lifecycle ────────────────────────────────────────────────────────

    [Fact]
    public void Log_CreatesMissingDirectoryOnFirstWrite()
    {
        using var provider = new FileLoggerProvider(_dir);
        Assert.False(Directory.Exists(_dir));

        provider.CreateLogger("Test").LogInformation("hello");

        Assert.True(File.Exists(PathOf(FileLoggerProvider.FileName)));
    }

    [Fact]
    public void Log_DoesNotTouchDiskUntilFirstEntry()
    {
        using var provider = new FileLoggerProvider(_dir);

        provider.CreateLogger("Test");

        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void Log_AppendsToExistingFileAcrossRestarts()
    {
        using (var first = new FileLoggerProvider(_dir))
            first.CreateLogger("Test").LogInformation("before restart");

        using (var second = new FileLoggerProvider(_dir))
            second.CreateLogger("Test").LogInformation("after restart");

        var text = Read();
        Assert.Contains("before restart", text);
        Assert.Contains("after restart", text);
    }

    [Fact]
    public void Log_FileIsReadableWhileProviderIsOpen()
    {
        using var provider = new FileLoggerProvider(_dir);
        var logger = provider.CreateLogger("Test");

        logger.LogInformation("first");
        Assert.Contains("first", Read());

        logger.LogInformation("second");
        Assert.Contains("second", Read());
    }

    // ── Rotation ──────────────────────────────────────────────────────────────

    [Fact]
    public void Log_ExceedingMaxSize_RollsToNumberedBackupsAndDropsOldest()
    {
        using var provider = new FileLoggerProvider(_dir, maxFileBytes: 300, retainedFiles: 2);
        var logger = provider.CreateLogger("Test");

        for (var i = 0; i < 40; i++)
            logger.LogInformation("entry-{Index:D3}", i);

        Assert.True(File.Exists(PathOf("service.1.log")));
        Assert.True(File.Exists(PathOf("service.2.log")));
        Assert.False(File.Exists(PathOf("service.3.log")));

        var all = Read("service.2.log") + Read("service.1.log")
                  + (File.Exists(PathOf("service.log")) ? Read() : "");
        Assert.Contains("entry-039", all);
        Assert.DoesNotContain("entry-000", all);
    }

    [Fact]
    public void Log_AfterRoll_CurrentFileStaysNearTheLimit()
    {
        using var provider = new FileLoggerProvider(_dir, maxFileBytes: 300, retainedFiles: 2);
        var logger = provider.CreateLogger("Test");

        for (var i = 0; i < 40; i++)
            logger.LogInformation("entry-{Index:D3}", i);

        // Rolled after the entry that reached the limit, so no file exceeds it by more than one entry.
        foreach (var file in Directory.GetFiles(_dir))
            Assert.True(new FileInfo(file).Length < 300 + 150, $"{Path.GetFileName(file)} grew past the limit");
    }

    // ── Robustness ────────────────────────────────────────────────────────────

    [Fact]
    public void Log_WhenDirectoryCannotBeCreated_DoesNotThrow()
    {
        // A directory path that sits below a regular file can never be created.
        var blocker = Path.GetTempFileName();
        try
        {
            using var provider = new FileLoggerProvider(Path.Combine(blocker, "logs"));
            var logger = provider.CreateLogger("Test");

            var ex = Record.Exception(() =>
            {
                logger.LogInformation("one");
                logger.LogError(new Exception("x"), "two");
            });

            Assert.Null(ex);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void Log_WhenFormatterThrows_DoesNotThrow()
    {
        using var provider = new FileLoggerProvider(_dir);
        var logger = provider.CreateLogger("Test");

        var ex = Record.Exception(() =>
            logger.Log(LogLevel.Error, new EventId(1), "state", null,
                (_, _) => throw new FormatException("bad format")));

        Assert.Null(ex);
    }

    [Fact]
    public void Log_AfterDispose_DoesNotThrowAndWritesNothing()
    {
        var provider = new FileLoggerProvider(_dir);
        var logger = provider.CreateLogger("Test");
        logger.LogInformation("before dispose");
        provider.Dispose();

        var ex = Record.Exception(() => logger.LogInformation("after dispose"));

        Assert.Null(ex);
        Assert.DoesNotContain("after dispose", Read());
    }

    [Fact]
    public void Log_FromManyThreads_WritesOnlyWholeLines()
    {
        const int threads = 8, perThread = 200;
        using var provider = new FileLoggerProvider(_dir);
        var logger = provider.CreateLogger("Test");

        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < perThread; i++)
                logger.LogInformation("thread-{Thread}-msg-{Index:D3}-end", t, i);
        });

        var lines = Read().Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(threads * perThread, lines.Length);
        Assert.All(lines, line =>
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[INF\] Test: thread-\d-msg-\d{3}-end$", line));
    }

    // ── Level names from config ───────────────────────────────────────────────

    [Theory]
    [InlineData("Trace", LogLevel.Trace)]
    [InlineData("Debug", LogLevel.Debug)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("WARNING", LogLevel.Warning)]
    [InlineData("Error", LogLevel.Error)]
    [InlineData("None", LogLevel.None)]
    public void ParseLevel_KnownName_ReturnsLevel(string name, LogLevel expected) =>
        Assert.Equal(expected, FileLoggerProvider.ParseLevel(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("verbose")]
    [InlineData("42")]
    public void ParseLevel_UnknownName_FallsBackToInformation(string? name) =>
        Assert.Equal(LogLevel.Information, FileLoggerProvider.ParseLevel(name));
}
