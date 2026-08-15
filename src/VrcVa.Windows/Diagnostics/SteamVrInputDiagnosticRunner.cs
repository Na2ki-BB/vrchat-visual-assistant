using System.Diagnostics;
using System.IO;
using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Diagnostics;

internal static class SteamVrInputDiagnosticRunner
{
    private const int TargetPressCount = 20;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    public static async Task<int> RunAsync(string[] args)
    {
        TimeSpan timeout = ParseTimeout(args);
        OpenVrInputInterop.ValidateAbi();
        string manifestPath = OpenVrInputInterop.ResolveActionManifestPath();
        Console.WriteLine("SteamVR Input 2.0 ABI: OK");
        Console.WriteLine($"Action manifest: {Path.GetFileName(manifestPath)}");

        if (!OpenVrInputInterop.TryCreate(out OpenVrInputInterop? input))
        {
            Console.Error.WriteLine("SteamVR is not running; input pass-through check did not start.");
            return 2;
        }

        using (input)
        {
            Console.WriteLine(
                "Ready: keep walking with the left stick and press the right trigger 20 times. "
                + "VRCVA does not bind the joystick and uses action-set priority 0.");

            Stopwatch elapsed = Stopwatch.StartNew();
            int presses = 0;
            bool poseEverValid = false;
            bool selectEverActive = false;
            while (elapsed.Elapsed < timeout && presses < TargetPressCount)
            {
                OpenVrPointerInputSample sample = input!.Poll();
                poseEverValid |= sample.PoseActive && sample.PoseValid && sample.DeviceConnected;
                selectEverActive |= sample.SelectActive;
                if (sample.SelectPressedThisFrame)
                {
                    presses++;
                    Console.WriteLine(
                        $"Trigger {presses}/{TargetPressCount}; pose="
                        + (sample.PoseActive && sample.PoseValid && sample.DeviceConnected
                            ? "valid"
                            : "unavailable"));
                }

                await Task.Delay(8).ConfigureAwait(false);
            }

            Console.WriteLine(
                $"Summary: trigger={presses}/{TargetPressCount}, "
                + $"selectActive={selectEverActive}, poseValid={poseEverValid}");
            if (presses == TargetPressCount && selectEverActive && poseEverValid)
            {
                Console.WriteLine(
                    "Input observation passed. Confirm movement continuity in the headset before promoting this path.");
                return 0;
            }

            Console.Error.WriteLine(
                "Input observation did not meet the 20-trigger gate. Existing VRCVA interaction remains unchanged.");
            return 3;
        }
    }

    internal static TimeSpan ParseTimeout(string[] args)
    {
        for (int index = 1; index < args.Length; index++)
        {
            if (!args[index].Equals("--seconds", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Length
                || !int.TryParse(args[index + 1], out int seconds)
                || seconds is < 10 or > 300)
            {
                throw new ArgumentException("--seconds must be an integer from 10 to 300.");
            }

            return TimeSpan.FromSeconds(seconds);
        }

        return DefaultTimeout;
    }
}
