// Porchlight on the desktop starts here, not in the game: Velopack installs
// this launcher beside the Unity player and keeps both up to date from the
// repository's GitHub releases (a new one, 0.0.<build>, for every push to main).
//
//   1. Velopack's startup: install/uninstall hooks, and an update downloaded
//      last time is applied now (Velopack then starts us again, on it).
//   2. A copy of us goes off in the background to look for a newer release and
//      download it (only the difference, where it can), for next time.
//   3. The game: on Windows, started beside us; on macOS, in our place (exec),
//      so the Dock shows the one app and it's the game.
//
// So an update lands when the player closes the game and opens it again.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Velopack;
using Velopack.Sources;

namespace Porchlight.Launcher;

static class Program
{
    const string REPO = "https://github.com/joe307bad/porchlight";
    const string FETCH = "--fetch-update";

    [STAThread]
    static int Main(string[] args)
    {
        VelopackApp.Build().Run();
        if (args.Length > 0 && args[0] == FETCH) return Fetch();
        StartFetch();
        return Play(args);
    }

    /// <summary>The newest release, downloaded and waiting for the next start; nothing if there's none or we're not installed (run from a build folder).</summary>
    static int Fetch()
    {
        try
        {
            // PORCHLIGHT_UPDATES: a folder or address of releases to use instead (for trying an update out).
            string? other = Environment.GetEnvironmentVariable("PORCHLIGHT_UPDATES");
            var updates = string.IsNullOrEmpty(other) ? new UpdateManager(new GithubSource(REPO, null, false)) : new UpdateManager(other);
            if (!updates.IsInstalled) return 0;
            var newer = updates.CheckForUpdates();
            if (newer == null) return 0;
            updates.DownloadUpdates(newer);
            Console.WriteLine($"porchlight: {newer.TargetFullRelease.Version} downloaded, for next time");
        }
        catch (Exception e)
        {
            // Offline, rate limited, or another copy already at it: try again next start.
            Console.Error.WriteLine("porchlight: no update this time: " + e.Message);
        }
        return 0;
    }

    static void StartFetch()
    {
        try
        {
            string? self = Environment.ProcessPath;
            if (self == null) return;
            Process.Start(new ProcessStartInfo(self, FETCH) { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("porchlight: couldn't look for updates: " + e.Message);
        }
    }

    static int Play(string[] args)
    {
        string dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo(Path.Combine(dir, "Porchlight.exe")) { UseShellExecute = false, WorkingDirectory = dir };
            foreach (var a in args) start.ArgumentList.Add(a);
            Process.Start(start);
            return 0;
        }
        // macOS: the Unity player beside us in Contents/MacOS, becoming this process.
        string game = Path.Combine(dir, "Porchlight");
        var argv = new IntPtr[args.Length + 2];
        argv[0] = Marshal.StringToCoTaskMemUTF8(game);
        for (int i = 0; i < args.Length; i++) argv[i + 1] = Marshal.StringToCoTaskMemUTF8(args[i]);
        execv(game, argv);
        Console.Error.WriteLine($"porchlight: couldn't start {game} (errno {Marshal.GetLastPInvokeError()})");
        return 1;
    }

    [DllImport("libc", SetLastError = true)]
    static extern int execv([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr[] argv);
}
