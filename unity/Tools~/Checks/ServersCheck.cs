// Which server the desktop app plays on (Shared/Net/ServerChoice.cs; desktop/README.md):
// the released app always the live one, whatever an earlier dev build left behind or
// is set around it; a dev build (desktop/dev-mac.sh) wherever it's pointed. And the
// builds wired that way: releases (desktop.yml) built without -porchlightDev, dev-mac.sh with it.
//
//   dotnet run --project "unity/Tools~/Checks" -- servers

using System;
using System.Collections.Generic;
using System.IO;
using Porchlight.Net;

static class ServersCheck
{
    const string DEV = "http://jbhs:5173/";

    public static int Run()
    {
        var fails = new List<string>();
        void Expect(bool ok, string what, string detail = "")
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}{(ok || detail == "" ? "" : ": " + detail)}");
            if (!ok) fails.Add(what);
        }
        string Plays(bool choosable, string[] args, string saved, string env, out ServerChoice.Keep keep) =>
            ServerChoice.Origin(choosable, env, ServerChoice.Chosen(choosable, args, saved, out keep), ServerChoice.LIVE);

        // The released app (not choosable).
        {
            string at = Plays(false, new string[0], DEV, null, out var keep);
            Expect(at == ServerChoice.LIVE && keep == ServerChoice.Keep.Forget,
                "released: a dev build's server left in the shared folder: it plays live, and the file is deleted", $"{at}, {keep}");
            at = Plays(false, new[] { "Porchlight", "-porchlightServer", DEV }, null, null, out keep);
            Expect(at == ServerChoice.LIVE && keep == ServerChoice.Keep.AsIs,
                "released: opened with -porchlightServer: it plays live, and nothing's saved", $"{at}, {keep}");
            at = Plays(false, new string[0], null, DEV, out keep);
            Expect(at == ServerChoice.LIVE, "released: PORCHLIGHT_ORIGIN set for apps: it plays live", at);
            at = Plays(false, new string[0], null, null, out keep);
            Expect(at == ServerChoice.LIVE && keep == ServerChoice.Keep.AsIs, "released: a clean install plays live, touching nothing", $"{at}, {keep}");
        }

        // A dev build (choosable).
        {
            string at = Plays(true, new[] { "-porchlightServer", DEV }, null, null, out var keep);
            Expect(at == DEV && keep == ServerChoice.Keep.Save, "dev: dev-mac.sh's -porchlightServer: it plays there and keeps it", $"{at}, {keep}");
            at = Plays(true, new string[0], DEV, null, out keep);
            Expect(at == DEV && keep == ServerChoice.Keep.AsIs, "dev: opened again: on the server it kept", $"{at}, {keep}");
            at = Plays(true, new[] { "-porchlightServer", "default" }, DEV, null, out keep);
            Expect(at == ServerChoice.LIVE && keep == ServerChoice.Keep.Save, "dev: --server live: back on the live server, the kept one cleared", $"{at}, {keep}");
            at = Plays(true, new[] { "-porchlightServer", "https://porchlight.joebad.com" }, null, null, out keep);
            Expect(at == ServerChoice.LIVE, "dev: the live server typed out is the live server", at);
            at = Plays(true, new string[0], DEV, "http://other:5173/", out keep);
            Expect(at == "http://other:5173/", "dev: PORCHLIGHT_ORIGIN over the kept server, for one run", at);
            at = Plays(true, new string[0], "not a server ::", null, out keep);
            Expect(at == ServerChoice.LIVE, "dev: a kept file that isn't a server is the live one", at);
        }

        // Sign-ins kept apart, and addresses as typed.
        Expect(ServerChoice.SessionFile(ServerChoice.LIVE) == "session" && ServerChoice.SessionFile(DEV) == "session-jbhs-5173",
            "each server its own sign-in: the live one's never sent to a dev server");
        Expect(ServerChoice.TryServer("jbhs:5173", out var typed) && typed == DEV, "a server typed without http:// is http", typed);
        Expect(!ServerChoice.TryServer("ftp://jbhs", out _) && !ServerChoice.TryServer("", out _), "not a server: refused");

        // The builds: releases locked, dev-mac.sh's choosable and an app of its own.
        string Read(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
        string release = Read(".github/workflows/desktop.yml"), dev = Read("desktop/dev-mac.sh"), prod = Read("desktop/prod-mac.sh"), setup = Read("unity/Assets/Porchlight/Editor/Setup.cs");
        if (release == null || dev == null || prod == null || setup == null) Expect(false, "the build files are here (run from the repo's root)");
        else
        {
            Expect(!release.Contains("-porchlightDev"), "the released app (desktop.yml) is built without -porchlightDev: its server can't be changed");
            Expect(dev.Contains("BuildMacApp -porchlightDev"), "dev-mac.sh builds with -porchlightDev: its server can be changed");
            Expect(setup.Contains("if (desktop && DevBuild) d.Add(\"PORCHLIGHT_DEV\")") && setup.Contains("\"com.joebad.porchlight.dev\""),
                "a dev build is PORCHLIGHT_DEV and another app to macOS (its own bundle id, so its own folder)");
            Expect(prod.Contains("PKG_NAME=Porchlight-osx-Setup.pkg") && prod.Contains("releases/latest/download/$PKG_NAME") && prod.Contains("Application Support/Porchlight/Porchlight\"") && !prod.Contains("porchlightServer"),
                "prod-mac.sh erases the released app's folder and installs the newest release, never pointing it anywhere");
        }

        Console.WriteLine(fails.Count == 0 ? "servers: all ok" : $"servers: {fails.Count} failure(s)");
        return fails.Count == 0 ? 0 : 1;
    }
}
