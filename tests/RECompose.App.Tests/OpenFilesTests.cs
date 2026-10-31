// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Win32;

namespace RECompose.App.Tests;

// Opening a service file by double-click.
public sealed class OpenFilesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rec-open-" + Guid.NewGuid().ToString("N"));

    public OpenFilesTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public void The_file_manager_passes_the_service_file_among_the_arguments()
    {
        var service = Path.Combine(_folder, "Sunday.RecService");
        File.WriteAllText(service, "x");
        var other = Path.Combine(_folder, "notes.txt");
        File.WriteAllText(other, "x");

        Assert.Equal(service, OpenFiles.FromArgs(["--flag", other, service]));
        Assert.Null(OpenFiles.FromArgs([other, Path.Combine(_folder, "gone.recservice")]));
    }

    [Fact]
    public async Task A_second_launch_hands_its_file_to_the_running_one()
    {
        var service = Path.Combine(_folder, "Sunday.recservice");
        File.WriteAllText(service, "x");
        var received = new TaskCompletionSource<string>();
        OpenFiles.Listen(path => received.TrySetResult(path));

        var sent = false;
        for (var i = 0; i < 20 && !sent; i++)
            sent = OpenFiles.Forward(service);

        Assert.True(sent);
        Assert.Equal(service, await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_installer_registers_the_file_type_and_the_uninstaller_removes_it()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var root = @"Software\RECompose-tests\" + Guid.NewGuid().ToString("N");
        try
        {
            FileTypes.Register(@"C:\Apps\RECompose.exe", root);

            using (var command = Registry.CurrentUser.OpenSubKey($@"{root}\RECompose.Service\shell\open\command"))
                Assert.Equal("\"C:\\Apps\\RECompose.exe\" \"%1\"", command?.GetValue(string.Empty));
            using (var ext = Registry.CurrentUser.OpenSubKey($@"{root}\.recservice"))
                Assert.Equal("RECompose.Service", ext?.GetValue(string.Empty));

            FileTypes.Unregister(root);
            Assert.Null(Registry.CurrentUser.OpenSubKey($@"{root}\.recservice"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\RECompose-tests", throwOnMissingSubKey: false);
        }
    }
}
