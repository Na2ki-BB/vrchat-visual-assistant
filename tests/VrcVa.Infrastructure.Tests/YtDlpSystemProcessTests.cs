using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VrcVa.Infrastructure.Tests;

public sealed class YtDlpSystemProcessTests
{
    [Fact]
    public async Task RealProcess_ArgumentListRoundTripsInjectionLookingQueryLiterally()
    {
        const string query = "ytsearch10: --exec \"self-authored\"\r\n'quotes' & | ; $(synthetic)";
        ProcessStartInfo start = Fixture("echo", "--", query);
        byte[] output = (await Runner().RunAsync(start, default)).StandardOutput;
        Assert.Equal(new[] { "--", query }, JsonSerializer.Deserialize<string[]>(output));
    }

    [Fact]
    public async Task RealProcess_BothPipesExceedOsBufferWithoutDeadlock()
    {
        YtDlpProcessOutput result = await Runner().RunAsync(Fixture("pipes"), default);
        Assert.True(result.HasStandardError);
        byte[] output = result.StandardOutput;
        Assert.Equal(200_000, output.Length);
        Assert.All(output, value => Assert.Equal((byte)'o', value));
    }

    [Fact]
    public async Task RealProcess_CancellationKillsAndReapsChildTree()
    {
        using IYtDlpProcess root = new SystemYtDlpProcessFactory().Start(Fixture("tree"));
        using StreamReader reader = new(root.StandardOutput, Encoding.UTF8, leaveOpen: true);
        string? childId = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using Process child = Process.GetProcessById(int.Parse(childId!));
        root.KillTree();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        await root.WaitForExitAsync(deadline.Token);
        Assert.NotEqual(0, root.ExitCode);
        // On Linux an adopted child can briefly remain a zombie. It is terminated, but not our child to reap.
        if (OperatingSystem.IsWindows())
        {
            await child.WaitForExitAsync(deadline.Token);
            Assert.True(child.HasExited);
        }
        else
        {
            string path = $"/proc/{child.Id}/stat";
            while (File.Exists(path))
            {
                string state;
                try { state = await File.ReadAllTextAsync(path, deadline.Token); }
                catch (FileNotFoundException) { break; }
                if (state[state.LastIndexOf(')') + 2] == 'Z') { break; }
                await Task.Delay(20, deadline.Token);
            }
        }
    }

    [Fact]
    public async Task RealProcess_RunnerCancellationReapsBeforeItReturns()
    {
        RecordingFactory processes = new();
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(150));
        YtDlpProcessRunner runner = new(processes, () => { }, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(Fixture("wait"), cancel.Token));
        Assert.True(processes.DisposedAfterExit);
    }

    private static YtDlpProcessRunner Runner() => new(new SystemYtDlpProcessFactory(), () => { },
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));

    private static ProcessStartInfo Fixture(params string[] args)
    {
        string host = Path.Combine(Path.GetFullPath(Path.Combine(
            System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..")),
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        ProcessStartInfo start = new(host)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "VrcVa.ProcessFixture.dll"));
        foreach (string argument in args) { start.ArgumentList.Add(argument); }
        return start;
    }

    private sealed class RecordingFactory : IYtDlpProcessFactory
    {
        public bool DisposedAfterExit { get; private set; }
        public IYtDlpProcess Start(ProcessStartInfo info) => new RecordingProcess(
            new SystemYtDlpProcessFactory().Start(info), () => DisposedAfterExit = true);
        private sealed class RecordingProcess(IYtDlpProcess process, Action disposed) : IYtDlpProcess
        {
            public Stream StandardOutput => process.StandardOutput;
            public Stream StandardError => process.StandardError;
            public int ExitCode => process.ExitCode;
            public Task WaitForExitAsync(CancellationToken token) => process.WaitForExitAsync(token);
            public void KillTree() => process.KillTree();
            public void Dispose() { _ = process.ExitCode; disposed(); process.Dispose(); }
        }
    }
}
