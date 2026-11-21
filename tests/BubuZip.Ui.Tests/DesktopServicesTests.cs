using BubuZip.Desktop;
using BubuZip;
using Xunit;

namespace BubuZip.Ui.Tests;

public sealed class DesktopServicesTests
{
    [Fact]
    public async Task DesktopDialogDispatcherRunsAwayFromTheCallingThread()
    {
        IAsyncDesktopDialogService service = OperatingSystem.IsWindows()
            ? new WindowsDesktopServices() : new LinuxDesktopServices();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var callerThread = Environment.CurrentManagedThreadId;
        var picker = service.ShowDialogAsync(() =>
        {
            var pickerThread = Environment.CurrentManagedThreadId;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return pickerThread;
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(picker.IsCompleted);
        }
        finally { release.Set(); }
        Assert.NotEqual(callerThread, await picker);
    }

    [Fact]
    public void LinuxFileSelectionReturnsAllPathsAndDoesNotInterpretShellCharacters()
    {
        var calls = new List<(string Command, string[] Args)>();
        var answers = new Queue<DesktopProcess.Result>([new(0, "", ""), new(0, "/tmp/a b\u001f/tmp/$(touch bad)\n", "")]);
        var service = new LinuxDesktopServices((command, args) =>
        {
            calls.Add((command, args.ToArray()));
            return answers.Dequeue();
        });
        Assert.Equal(new[] { "/tmp/a b", "/tmp/$(touch bad)" }, service.ChooseInputs());
        Assert.Equal("zenity", calls[1].Command);
        Assert.Contains("--multiple", calls[1].Args);
        Assert.Contains("--separator=\u001f", calls[1].Args);
    }

    [Fact]
    public void LinuxDialogsDistinguishCancellationFromFailures()
    {
        var responses = new Queue<DesktopProcess.Result>([new(1, "", "GTK warning"), new(2, "", "display unavailable")]);
        var service = new LinuxDesktopServices((_, _) => responses.Dequeue());
        Assert.Null(service.ChooseArchive());
        Assert.Contains("display unavailable", Assert.Throws<IOException>(() => service.ChooseArchive()).Message);
    }

    [Fact]
    public void LinuxTrashPreflightsEveryPathAndPassesDistinctNamesAsArguments()
    {
        var root = Path.Combine(Path.GetTempPath(), "BubuZip-Desktop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "a ; $(test).txt");
            File.WriteAllText(path, "content");
            var calls = new List<(string, string[])>();
            var service = new LinuxDesktopServices((command, args) => { calls.Add((command, args.ToArray())); return new(0, "", ""); });
            Assert.Throws<FileNotFoundException>(() => service.Trash([path, Path.Combine(root, "missing")]));
            Assert.Empty(calls);
            service.Trash([path, path]);
            var call = Assert.Single(calls);
            Assert.Equal("gio", call.Item1);
            Assert.Equal(new[] { "trash", "--", path }, call.Item2);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LinuxRevealOpensTheContainingFolderForFiles()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "example.txt"));
        (string Command, string[] Args) call = default;
        var service = new LinuxDesktopServices((command, args) => { call = (command, args.ToArray()); return new(0, "", ""); });
        service.Reveal(path);
        Assert.Equal("xdg-open", call.Command);
        Assert.Equal(new[] { Path.GetDirectoryName(path) }, call.Args);
    }

    [Fact]
    public void WindowsTrashPreflightsAllPathsBeforeMovingAnything()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "BubuZip-Desktop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "existing.txt");
            File.WriteAllText(path, "content");
            var service = new WindowsDesktopServices();
            Assert.Throws<FileNotFoundException>(() =>
            {
                if (OperatingSystem.IsWindows()) service.Trash([path, Path.Combine(root, "missing")]);
            });
            Assert.True(File.Exists(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LinuxGioMovesARealFileToAnIsolatedTrashWhenRequested()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("ECLIPSE_TEST_TRASH") != "1") return;
        var trashHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Assert.False(string.IsNullOrWhiteSpace(trashHome));
        var directory = Path.Combine(trashHome!, "source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "测试 ; document.txt");
            File.WriteAllText(path, "content");
            new LinuxDesktopServices().Trash([path]);
            Assert.False(File.Exists(path));
            Assert.True(Directory.Exists(Path.Combine(trashHome!, "Trash", "files")));
            Assert.Contains(Directory.EnumerateFiles(Path.Combine(trashHome!, "Trash", "files")),
                candidate => Path.GetFileName(candidate) == Path.GetFileName(path));
        }
        finally { Directory.Delete(directory, true); }
    }
}
