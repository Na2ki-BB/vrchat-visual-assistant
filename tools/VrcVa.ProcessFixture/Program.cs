using System.Diagnostics;
using System.Text.Json;

// Self-authored local process fixture only. No network, yt-dlp, credentials, or filesystem output.
if (args[0] == "echo")
{
    Console.Write(JsonSerializer.Serialize(args.Skip(1).ToArray()));
}
else if (args[0] == "pipes")
{
    await Task.WhenAll(Task.Run(() => Console.Out.Write(new string('o', 200_000))),
        Task.Run(() => Console.Error.Write(new string('e', 60_000))));
}
else if (args[0] == "wait")
{
    Console.WriteLine("ready");
    await Task.Delay(Timeout.Infinite);
}
else if (args[0] == "tree")
{
    ProcessStartInfo childInfo = new(Environment.ProcessPath!) { UseShellExecute = false };
    childInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
    childInfo.ArgumentList.Add("wait");
    using Process child = Process.Start(childInfo)!;
    Console.WriteLine(child.Id);
    Console.Out.Flush();
    await Task.Delay(Timeout.Infinite);
}
