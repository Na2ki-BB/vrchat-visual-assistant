using System.Windows.Threading;
using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Diagnostics;

internal static class OpenVrDiagnosticRunner
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    public static async Task<int> RunAsync(Dispatcher dispatcher)
    {
        using SteamVrResultPanel panel = new(dispatcher);
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        panel.Hidden += (_, _) => closed.TrySetResult();

        const string sampleText = """
            これはVRChat Visual Assistantの表示テストです。

            結果は自動では消えません。右上の「閉じる」を選ぶまで表示されます。

            この文章はスクロール動作を確認するための固定ダミーです。画像、OCR結果、翻訳結果、APIキーは使用していません。

            1. コントローラーのレーザーポインターをパネルへ向けます。
            2. スティックまたはタッチ操作で下へスクロールします。
            3. 右側のスクロール位置が動くことを確認します。
            4. 上へ戻せることも確認します。
            5. 最後に右上の「閉じる」を選びます。

            長い翻訳結果でも、読む速さに合わせて表示を維持できます。
            次のSCANを開始した場合は、古い結果をいったん閉じ、新しい結果へ置き換えます。

            このパネルはSteamVRの公式OpenVR Overlay APIで表示しています。VRChatへDLLを注入したり、VRChatを改造したりしません。

            ここまでスクロールできれば長文操作の確認は成功です。
            """;

        bool displayed = panel.TryShow("VR結果パネル操作テスト", sampleText);
        if (!displayed)
        {
            Console.Error.WriteLine("SteamVR is not running or its OpenVR library could not be found.");
            return 2;
        }

        Console.WriteLine("Overlay displayed. Scroll it and select Close in VR within two minutes.");
        Task completed = await Task.WhenAny(closed.Task, Task.Delay(TestTimeout));
        if (completed != closed.Task)
        {
            Console.Error.WriteLine("Overlay test timed out before Close was selected.");
            return 3;
        }

        Console.WriteLine("Overlay close event received.");
        return 0;
    }
}
