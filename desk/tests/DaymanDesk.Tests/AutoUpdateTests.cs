using DaymanDesk.Core;

namespace DaymanDesk.Tests;

/// <summary>WO-96: the silent signed auto-update (signature, version, hosts, idle gate).</summary>
public static class AutoUpdateTests
{
    public static void TestSignatureAcceptsSignedAndRejectsTampered()
    {
        var (priv, spki) = DeskSigner.CreateKey();
        var file = Path.Combine(Tmp.Dir(), "DaymanDesk.exe");
        var data = new byte[100_000];
        new Random(7).NextBytes(data);
        File.WriteAllBytes(file, data);
        using var key = DeskSigner.Open(priv);
        var sig = Convert.ToBase64String(key.SignData(data, System.Security.Cryptography.HashAlgorithmName.SHA256));
        Assert.True(Updater.Verify(file, sig, spki), "good signature accepted");
        Assert.True(!Updater.Verify(file, sig), "another key's signature refused by the built-in key");
        data[50_000] ^= 1;
        File.WriteAllBytes(file, data);
        Assert.True(!Updater.Verify(file, sig, spki), "tampered byte refused");
        Assert.True(!Updater.Verify(file, "not base64!", spki), "garbage refused");
        Assert.True(Updater.ImportSpki(Updater.PublicKeySpki) != null, "built-in key imports");
    }

    public static void TestVersionCheck()
    {
        Assert.True(Updater.VersionOk("2.1.0", "2.1.0+abc123", "2.0.0"), "announced and newer");
        Assert.True(!Updater.VersionOk("2.1.0", "2.0.9", "2.0.0"), "file is not the announced version");
        Assert.True(!Updater.VersionOk("2.0.0", "2.0.0", "2.0.0"), "same version");
        Assert.True(!Updater.VersionOk("1.9.0", "1.9.0", "2.0.0"), "downgrade");
        Assert.True(!Updater.VersionOk("2.1.0", null, "2.0.0"), "no file version");
    }

    public static void TestHostAllowList()
    {
        foreach (var ok in new[] { "https://github.com/aliobedat/dayman-desk/releases/latest/download/DaymanDesk.exe",
                     "https://objects.githubusercontent.com/x", "https://release-assets.githubusercontent.com/x", "https://daymanjo.com/desk/DaymanDesk.exe" })
            Assert.True(Updater.AllowedUrl(new Uri(ok)), ok);
        foreach (var bad in new[] { "http://github.com/x", "https://evil.com/x", "https://github.com.evil.com/x",
                     "https://user:pw@github.com/x", "https://daymanjo.com:8443/x", "ftp://daymanjo.com/x" })
            Assert.True(!Updater.AllowedUrl(new Uri(bad)), bad);
    }

    public static void TestIdleGate()
    {
        var now = DateTimeOffset.Parse("2026-11-25T18:00:00+03:00");
        Assert.True(Updater.Idle(0, null, now), "nothing printed yet");
        Assert.True(Updater.Idle(0, now.AddMinutes(-3), now), "3 minutes after the last job");
        Assert.True(!Updater.Idle(0, now.AddMinutes(-2), now), "a job 2 minutes ago");
        Assert.True(!Updater.Idle(1, null, now), "an open connection");
    }

    public static async Task TestDownloadRefusesRedirectToOtherHost()
    {
        var h = new FakeHandler();
        h.Respond = (r, _) =>
        {
            var m = new HttpResponseMessage(System.Net.HttpStatusCode.Found);
            m.Headers.Location = new Uri("https://evil.com/DaymanDesk.exe");
            return m;
        };
        var path = Path.Combine(Tmp.Dir(), "x.exe");
        try { await Updater.DownloadAsync(new HttpClient(h), "https://github.com/a/b", path, 10); }
        catch (InvalidOperationException ex) { Assert.True(ex.Message.Contains("evil.com"), ex.Message); return; }
        throw new AssertException("redirect to another host followed");
    }
}
