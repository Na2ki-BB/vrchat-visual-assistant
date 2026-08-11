namespace VrcVa.CaptureFixture;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        using Form window = new()
        {
            Text = "VRChat Capture Fixture",
            ClientSize = new Size(1280, 720),
            StartPosition = FormStartPosition.CenterScreen,
            BackColor = Color.FromArgb(245, 245, 240),
        };
        using Label title = new()
        {
            Text = "EMERGENCY EXIT\r\nKEEP DOOR CLOSED",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(20, 20, 20),
            Font = new Font("Arial", 46, FontStyle.Bold, GraphicsUnit.Pixel),
        };
        window.Controls.Add(title);
        Application.Run(window);
    }
}
