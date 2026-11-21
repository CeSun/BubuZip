using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using BubuZip.Core;
using BubuZip.Desktop;
using Xunit;

namespace BubuZip.Ui.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacFinderRequestFileTests
{
    [Fact]
    public void PrivateRequestPreservesUnicodeSpacesAndLiteralCharactersAndIsConsumedOnce()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        var paths = new[] { Path.Combine(fixture.Parent, "中文 文档 📦.zip"), Path.Combine(fixture.Parent, "a & $(literal).txt") };
        var path = fixture.Write(paths: paths);

        var request = fixture.Reader.ReadIfRequest(path)!;

        Assert.Equal(ArchiveShellAction.CompressZip, request.Action);
        Assert.Equal(paths, request.Paths);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root));
        Assert.ThrowsAny<IOException>(() => fixture.Reader.ReadIfRequest(path));
    }

    [Fact]
    public async Task ConcurrentFileEventsCannotExecuteTheSameRequestTwice()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        var path = fixture.Write();
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            try { return fixture.Reader.ReadIfRequest(path); }
            catch (IOException) { return null; }
        })));
        Assert.Single(results, result => result is not null);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root));
    }

    [Theory]
    [InlineData("open", ArchiveShellAction.Open)]
    [InlineData("extract", ArchiveShellAction.Extract)]
    [InlineData("extract-here", ArchiveShellAction.ExtractHere)]
    [InlineData("extract-folder", ArchiveShellAction.ExtractFolder)]
    [InlineData("test", ArchiveShellAction.Test)]
    [InlineData("compress", ArchiveShellAction.Compress)]
    [InlineData("compress-7z", ArchiveShellAction.CompressSevenZip)]
    [InlineData("compress-zip", ArchiveShellAction.CompressZip)]
    public void EachSupportedMenuVerbUsesTheSameDecoderForInitialAndLaterFileEvents(string verb, ArchiveShellAction expected)
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        var first = fixture.Write(action: verb);
        Assert.Equal(expected, fixture.Reader.ReadIfRequest(first)!.Action);
        var later = fixture.Write(action: verb);
        Assert.Equal(expected, fixture.Reader.ReadIfRequest(later)!.Action);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(later));
    }

    [Fact]
    public void OrdinaryArchivePathsAreNotTreatedAsExecutableRequests()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        Assert.Null(fixture.Reader.ReadIfRequest(Path.Combine(fixture.Parent, "ordinary.zip")));
    }

    [Fact]
    public void RequestsOutsideThePrivateDirectoryAreRejectedAndPreserved()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        var path = fixture.Write();
        var outside = Path.Combine(fixture.Parent, Path.GetFileName(path));
        File.Move(path, outside);
        Assert.Throws<InvalidDataException>(() => fixture.Reader.ReadIfRequest(outside));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void NestedRequestPathsAreRejected()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        var path = fixture.Write();
        var nested = Path.Combine(fixture.Root, "nested");
        Directory.CreateDirectory(nested);
        var moved = Path.Combine(nested, Path.GetFileName(path));
        File.Move(path, moved);
        Assert.Throws<InvalidDataException>(() => fixture.Reader.ReadIfRequest(moved));
        Assert.True(File.Exists(moved));
    }

    [Theory]
    [InlineData("not-a-uuid.bubuzip-request")]
    [InlineData("12345678-1234-4321-ABCD-1234567890AB.bubuzip-request")]
    [InlineData("12345678-1234-4321-abcd-1234567890ab.BUBUZIP-REQUEST")]
    public void RequestFilenamesMustBeCanonicalLowercaseUuids(string name)
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        var path = fixture.Write();
        var invalid = Path.Combine(fixture.Root, name);
        File.Move(path, invalid);
        Assert.Throws<InvalidDataException>(() => fixture.Reader.ReadIfRequest(invalid));
        Assert.True(File.Exists(invalid));
    }

    [Theory]
    [InlineData(-601)]
    [InlineData(61)]
    public void ExpiredOrFuturePayloadTimestampsCannotExecute(int seconds)
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        var path = fixture.Write(createdAt: fixture.Now.ToUnixTimeSeconds() + seconds);
        Assert.Throws<InvalidDataException>(() => fixture.Reader.ReadIfRequest(path));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData(-601)]
    [InlineData(61)]
    public void ExpiredOrFutureFileTimesCannotExecute(int seconds)
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        var path = fixture.Write();
        File.SetLastWriteTimeUtc(path, fixture.Now.AddSeconds(seconds).UtcDateTime);
        Assert.Throws<InvalidDataException>(() => fixture.Reader.ReadIfRequest(path));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("compress-zip --delete")]
    [InlineData("OPEN")]
    [InlineData("")]
    public void UnknownOrInjectedVerbsCannotExecute(string action)
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        var path = fixture.Write(action: action);
        Assert.Throws<InvalidDataException>(() => fixture.Reader.ReadIfRequest(path));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("relative.zip")]
    [InlineData("file:///tmp/archive.zip")]
    [InlineData("https://example.com/archive.zip")]
    [InlineData("/tmp/line\nbreak.zip")]
    [InlineData("/tmp/control\0.zip")]
    [InlineData("")]
    public void PayloadPathsMustBeLocalAbsolutePathsWithoutControlCharacters(string selected)
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        var path = fixture.Write(paths: [selected]);
        Assert.Throws<InvalidDataException>(() => fixture.Reader.ReadIfRequest(path));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public void EmptyOrOversizedSelectionsCannotExecute(int count)
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        var path = fixture.Write(paths: Enumerable.Repeat(Path.Combine(fixture.Parent, "item.zip"), count).ToArray());
        Assert.Throws<InvalidDataException>(() => fixture.Reader.ReadIfRequest(path));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{not-json}")]
    [InlineData("{\"version\":2,\"action\":\"open\",\"paths\":[\"/tmp/item.zip\"],\"createdAt\":0}")]
    [InlineData("{\"version\":1,\"action\":\"open\",\"paths\":null,\"createdAt\":0}")]
    [InlineData("{\"version\":1,\"action\":\"open\",\"paths\":[null],\"createdAt\":0}")]
    [InlineData("{\"version\":1,\"action\":\"open\",\"paths\":[\"/tmp/item.zip\"],\"createdAt\":0,\"extra\":true}")]
    public void InvalidSchemasRemainAvailableForDiagnosisWithoutExecuting(string json)
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        var path = fixture.WriteJson(Encoding.UTF8.GetBytes(json));
        Assert.Throws<InvalidDataException>(() => fixture.Reader.ReadIfRequest(path));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(MacFinderRequestFile.MaximumBytes + 1)]
    public void OversizedOrEmptyRequestIsRejectedBeforeJsonIsRead(int size)
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        var path = fixture.WriteJson(new byte[size]);
        Assert.Throws<InvalidDataException>(() => fixture.Reader.ReadIfRequest(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void PubliclyReadableRequestFilesAreRejected()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        var path = fixture.Write();
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        Assert.Throws<InvalidDataException>(() => fixture.Reader.ReadIfRequest(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void NonPrivateRequestDirectoryIsRejected()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        var path = fixture.Write();
        File.SetUnixFileMode(fixture.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
        Assert.Throws<InvalidDataException>(() => fixture.Reader.ReadIfRequest(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void CurrentOwnerIsReadFromNativeMetadataRatherThanAssumedFromTheFilename()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        var path = fixture.Write();
        var wrongOwner = new MacFinderRequestFile(fixture.Root, () => fixture.Now, MacFinderRequestFile.GetUid() + 1);
        Assert.Contains("所有者", Assert.Throws<InvalidDataException>(() => wrongOwner.ReadIfRequest(path)).Message);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void RequestSymlinkCannotReadOrDeleteItsTarget()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        var original = fixture.Write();
        var target = Path.Combine(fixture.Parent, "target");
        File.Move(original, target);
        File.CreateSymbolicLink(original, target);
        Assert.Throws<InvalidDataException>(() => fixture.Reader.ReadIfRequest(original));
        Assert.True(File.Exists(target));
        Assert.NotNull(new FileInfo(original).LinkTarget);
    }

    [Fact]
    public void RequestDirectorySymlinkIsRejected()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        var path = fixture.Write();
        var target = fixture.Root + "-target";
        Directory.Move(fixture.Root, target);
        Directory.CreateSymbolicLink(fixture.Root, target);
        Assert.Throws<InvalidDataException>(() => fixture.Reader.ReadIfRequest(path));
        Assert.True(File.Exists(Path.Combine(target, Path.GetFileName(path))));
    }

    [Fact]
    public void DefaultRequestRootUsesTheRealUserContainer()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var root = MacFinderRequestFile.DefaultRequestRoot;
        Assert.True(Path.IsPathFullyQualified(root));
        Assert.EndsWith("/Library/Containers/io.bubuzip.app.finder/Data/Library/Application Support/BubuZip/FinderRequests", root);
    }

    private sealed class Fixture : IDisposable
    {
        public string Parent { get; } = ArchivePaths.ResolveUserPath(Path.Combine(Path.GetTempPath(), "FinderRequest-" + Guid.NewGuid().ToString("N")));
        public string Root { get; }
        public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        public MacFinderRequestFile Reader { get; }

        public Fixture()
        {
            Root = Path.Combine(Parent, "FinderRequests");
            Directory.CreateDirectory(Root);
            File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Reader = new(Root, () => Now);
        }

        public string Write(string action = "compress-zip", string[]? paths = null, long? createdAt = null)
        {
            var value = new MacFinderWireRequest(1, action, paths ?? [Path.Combine(Parent, "归档 文件.zip")], createdAt ?? Now.ToUnixTimeSeconds());
            return WriteJson(JsonSerializer.SerializeToUtf8Bytes(value, MacFinderRequestJsonContext.Default.MacFinderWireRequest));
        }

        public string WriteJson(byte[] value)
        {
            var path = Path.Combine(Root, Guid.NewGuid().ToString("D") + MacFinderRequestFile.Suffix);
            File.WriteAllBytes(path, value);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetLastWriteTimeUtc(path, Now.UtcDateTime);
            return path;
        }

        public void Dispose() => Directory.Delete(Parent, recursive: true);
    }
}
