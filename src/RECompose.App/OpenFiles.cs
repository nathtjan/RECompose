// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Pipes;
using System.Runtime.Versioning;
using Microsoft.Win32;
using RECompose.Core.Services;

namespace RECompose.App;

/// <summary>
/// Opening a service file from the file manager: the first RECompose imports it, and a second launch (the OS starts one per
/// double-click on Windows) hands the path to the running one over a pipe and quits.
/// </summary>
internal static class OpenFiles
{
    private static readonly string PipeName = "RECompose-open-" + Environment.UserName;

    public static bool IsService(string path) =>
        path.EndsWith(ServiceExchange.Extension, StringComparison.OrdinalIgnoreCase) || path.EndsWith(ServiceExchange.OldExtension, StringComparison.OrdinalIgnoreCase);

    public static string? FromArgs(IEnumerable<string> args) => args.FirstOrDefault(a => IsService(a) && File.Exists(a));

    /// <summary>Sends the path to the running RECompose; false when none answers.</summary>
    public static bool Forward(string path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1000);
            using var writer = new StreamWriter(client);
            writer.Write(Path.GetFullPath(path));
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Calls <paramref name="onPath"/> (on a background thread) for each service file another launch forwards.</summary>
    public static void Listen(Action<string> onPath) => _ = Task.Run(async () =>
    {
        while (true)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server);
                var path = await reader.ReadToEndAsync();
                if (IsService(path) && File.Exists(path))
                    onPath(path);
            }
            catch (IOException)
            {
                await Task.Delay(500); // a broken client; listen again
            }
        }
    });
}

/// <summary>The Windows file type for service files, so double-clicking one opens RECompose (the installer hooks call this).</summary>
[SupportedOSPlatform("windows")]
internal static class FileTypes
{
    private const string ProgId = "RECompose.Service";

    public static void Register(string exe, string root = @"Software\Classes")
    {
        Set($@"{root}\{ServiceExchange.Extension}", ProgId);
        Set($@"{root}\{ProgId}", "RECompose service");
        Set($@"{root}\{ProgId}\DefaultIcon", $"\"{exe}\",0");
        Set($@"{root}\{ProgId}\shell\open\command", $"\"{exe}\" \"%1\"");
    }

    public static void Unregister(string root = @"Software\Classes")
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"{root}\{ServiceExchange.Extension}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"{root}\{ProgId}", throwOnMissingSubKey: false);
    }

    private static void Set(string key, string value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(key);
        k.SetValue(string.Empty, value);
    }
}
