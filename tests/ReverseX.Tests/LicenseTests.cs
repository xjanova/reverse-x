using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using ReverseX.Licensing;
using Xunit;

namespace ReverseX.Tests;

/// <summary>License client against a fake xman API — never the real server (and never its real /demo).</summary>
public sealed class LicenseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rx-lic-" + Guid.NewGuid().ToString("N"));
    private const string Machine = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Secret = "test-guid|test-uuid|tester";
    private static readonly DateTime T0 = new(2026, 10, 16, 3, 0, 0, DateTimeKind.Utc);
    private DateTime _now = T0;

    public LicenseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string File => Path.Combine(_dir, "license.json");

    internal sealed class FakeXman : IXmanClient
    {
        public readonly Dictionary<string, Func<ApiReply>> Routes = new();
        public readonly List<string> Calls = new();
        /// <summary>When set, every call gets this instead (outage simulation).</summary>
        public Func<ApiReply>? Outage;

        public Task<ApiReply> PostAsync(string path, object body) => Answer(path);
        public Task<ApiReply> GetAsync(string pathAndQuery) => Answer(pathAndQuery.Split('?')[0]);

        private Task<ApiReply> Answer(string path)
        {
            Calls.Add(path);
            if (Outage is not null) return Task.FromResult(Outage());
            return Task.FromResult(Routes.TryGetValue(path, out var r) ? r() : Reply(404, """{"success":false,"message":"not found"}"""));
        }
    }

    internal static ApiReply Reply(int status, string? json, DateTimeOffset? date = null) =>
        new((HttpStatusCode)status, json is null ? null : JsonNode.Parse(json) as JsonObject, date);

    private static string Iso(DateTime d) => d.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private FakeXman NewServer(bool canTrial = true)
    {
        var x = new FakeXman();
        x.Routes["/register-device"] = () => Reply(200, """{"success":true,"data":{"device_status":null}}""");
        x.Routes["/check-machine"] = () => Reply(200, """{"success":true,"has_license":false}""");
        x.Routes["/demo/check"] = () => Reply(200, $$$"""{"success":true,"data":{"can_start_demo":{{{(canTrial ? "true" : "false")}}}}}""");
        x.Routes["/demo"] = () => Reply(200, $$$"""{"success":true,"data":{"license_key":"DEMO-AAAA-BBBB-CCCC","expires_at":"{{{Iso(_now.AddDays(14))}}}"}}""");
        return x;
    }

    private void EditState(Action<JsonObject> edit)
    {
        var root = (JsonObject)JsonNode.Parse(System.IO.File.ReadAllText(File))!;
        edit((JsonObject)root["state"]!);
        System.IO.File.WriteAllText(File, root.ToJsonString());
    }

    private LicenseService Service(FakeXman api, string? secret = null) =>
        new(api, new LicenseStore(File, secret ?? Secret), "0.2.0", _ => { }, () => _now, Machine, "hw");

    private async Task<(LicenseService, FakeXman)> ProUser()
    {
        var api = NewServer(canTrial: false);
        api.Routes["/activate"] = () => Reply(200, """{"success":true,"data":{"license_type":"lifetime","expires_at":null}}""");
        var svc = Service(api);
        await svc.RefreshAsync();
        var r = await svc.ActivateAsync("ABCD-EFGH-IJKL-MNOP", false);
        Assert.True(r.Ok, r.Code.ToString());
        Assert.Equal(LicenseTier.Pro, svc.Status.Tier);
        return (svc, api);
    }

    [Fact]
    public void FreshInstall_IsUnregistered_AndNotPro()
    {
        var s = Service(NewServer()).Status;
        Assert.Equal(LicenseTier.Unregistered, s.Tier);
        Assert.Equal(LicenseReason.NotRegistered, s.Reason);
        Assert.False(s.IsPro);
        Assert.False(s.IsRegistered);
    }

    [Fact]
    public async Task FirstRun_RegistersOnline_ThenStartsTheFourteenDayTrial()
    {
        var api = NewServer();
        var svc = Service(api);
        var changed = 0;
        svc.Changed += () => changed++;

        var r = await svc.RefreshAsync();

        Assert.True(r.Ok);
        Assert.Equal(new[] { "/register-device", "/check-machine", "/demo/check", "/demo" }, api.Calls);
        var s = svc.Status;
        Assert.Equal(LicenseTier.Trial, s.Tier);
        Assert.Equal(LicenseReason.TrialActive, s.Reason);
        Assert.Equal(14, s.DaysLeft);
        Assert.True(s.IsPro);
        Assert.True(changed > 0);

        // the trial survives a restart without network
        var offline = new FakeXman { Outage = () => ApiReply.Unreachable };
        var again = Service(offline);
        Assert.Equal(LicenseTier.Trial, again.Status.Tier);
    }

    [Fact]
    public async Task Trial_IsAskedOncePerInstall()
    {
        var api = NewServer(canTrial: false);
        var svc = Service(api);
        await svc.RefreshAsync();
        await svc.RefreshAsync();
        Assert.Single(api.Calls, c => c == "/demo/check");
        Assert.DoesNotContain("/demo", api.Calls);
        Assert.Equal(LicenseTier.Free, svc.Status.Tier);
        Assert.Equal(LicenseReason.FreeNoKey, svc.Status.Reason);
    }

    [Fact]
    public async Task Offline_FirstRun_StaysUnregistered_AndRetriesLater()
    {
        var api = NewServer();
        api.Outage = () => ApiReply.Unreachable;
        var svc = Service(api);
        var r = await svc.RefreshAsync();
        Assert.False(r.Ok);
        Assert.Equal(LicenseActionCode.NeedRegistration, r.Code);
        Assert.Equal(LicenseTier.Unregistered, svc.Status.Tier);
        Assert.Equal(LicenseNote.Offline, svc.Status.Note);
        Assert.Equal(new[] { "/register-device" }, api.Calls);
        Assert.False(System.IO.File.Exists(File));

        api.Outage = null;
        await svc.RefreshAsync();
        Assert.True(svc.Status.IsRegistered);
    }

    [Fact]
    public async Task BlockedDevice_IsNotRegistered()
    {
        var api = NewServer();
        api.Routes["/register-device"] = () => Reply(200, """{"success":true,"data":{"device_status":"blocked"}}""");
        var svc = Service(api);
        await svc.RefreshAsync();
        Assert.Equal(LicenseTier.Unregistered, svc.Status.Tier);
        Assert.Equal(LicenseReason.Blocked, svc.Status.Reason);
    }

    [Fact]
    public async Task Trial_Expired_DropsToFree()
    {
        var svc = Service(NewServer());
        await svc.RefreshAsync();
        Assert.Equal(LicenseTier.Trial, svc.Status.Tier);

        _now = T0.AddDays(13).AddHours(23);
        Assert.Equal(LicenseTier.Trial, svc.Status.Tier);
        Assert.Equal(1, svc.Status.DaysLeft);

        _now = T0.AddDays(14).AddMinutes(1);
        var s = svc.Status;
        Assert.Equal(LicenseTier.Free, s.Tier);
        Assert.Equal(LicenseReason.TrialEnded, s.Reason);
        Assert.False(s.IsPro);
        Assert.True(s.IsRegistered);
    }

    public static IEnumerable<object[]> Outages() => new[]
    {
        new object[] { "offline" },
        new object[] { "500" },
        new object[] { "502-html" },
        new object[] { "503" },
        new object[] { "429" },
    };

    [Theory]
    [MemberData(nameof(Outages))]
    public async Task Outage_NeverRemovesOrDowngradesALicense(string kind)
    {
        var (svc, api) = await ProUser();
        var before = System.IO.File.ReadAllText(File);
        api.Outage = kind switch
        {
            "offline" => () => ApiReply.Unreachable,
            "500" => () => Reply(500, """{"success":false,"message":"Server Error"}"""),
            "502-html" => () => Reply(502, null),
            "503" => () => Reply(503, """{"success":false,"error_code":"INVALID_LICENSE"}"""),
            _ => () => Reply(429, """{"success":false,"error_code":"INVALID_LICENSE","message":"Too Many Attempts."}"""),
        };

        await svc.RefreshAsync();
        var act = await svc.ActivateAsync("ZZZZ-YYYY-XXXX-WWWW", false);

        Assert.False(act.Ok);
        Assert.Equal(LicenseTier.Pro, svc.Status.Tier);
        Assert.Equal(before, System.IO.File.ReadAllText(File));
        Assert.Equal(LicenseTier.Pro, Service(api).Status.Tier);
    }

    [Fact]
    public async Task PaidKey_OfflineMoreThanThirtyDays_PausesPro_UntilXmanAnswers()
    {
        var api = NewServer(canTrial: false);
        api.Routes["/activate"] = () => Reply(200, """{"success":true,"data":{"license_type":"yearly","expires_at":"2027-12-31T00:00:00Z"}}""");
        api.Routes["/validate"] = () => Reply(200, """{"success":true,"is_valid":true,"data":{"license_type":"yearly","expires_at":"2027-12-31T00:00:00Z"}}""");
        var svc = Service(api);
        await svc.RefreshAsync();
        await svc.ActivateAsync("ABCD-EFGH-IJKL-MNOP", false);

        api.Outage = () => ApiReply.Unreachable;
        _now = T0.AddDays(31);
        Assert.Equal(LicenseReason.OfflineTooLong, svc.Status.Reason);
        Assert.Equal("ABCD-…-MNOP", svc.Status.KeyMasked);

        api.Outage = null;
        await svc.RefreshAsync();
        Assert.Equal(LicenseTier.Pro, svc.Status.Tier);
        Assert.Equal(LicenseReason.ProUntil, svc.Status.Reason);
    }

    [Fact]
    public async Task DefiniteAnswer_RevokedKey_DoesDowngrade()
    {
        var (svc, api) = await ProUser();
        api.Routes["/validate"] = () => Reply(200, """{"success":true,"is_valid":false,"data":{"status":"revoked","license_type":"lifetime"}}""");
        await svc.RefreshAsync();
        Assert.Equal(LicenseTier.Free, svc.Status.Tier);
        Assert.Equal(LicenseReason.Revoked, svc.Status.Reason);
    }

    [Fact]
    public async Task EditedLicenseFile_IsRejected()
    {
        var api = NewServer();
        await Service(api).RefreshAsync();
        Assert.Equal(LicenseTier.Trial, Service(new FakeXman { Outage = () => ApiReply.Unreachable }).Status.Tier);

        // turn the trial into a lifetime key by hand
        EditState(s => { s["type"] = "lifetime"; s["expires"] = null; });
        Assert.Contains("lifetime", System.IO.File.ReadAllText(File));

        var s = Service(new FakeXman { Outage = () => ApiReply.Unreachable }).Status;
        Assert.Equal(LicenseTier.Unregistered, s.Tier);
        Assert.Equal(LicenseReason.TamperedFile, s.Reason);
        Assert.False(s.IsPro);
    }

    [Fact]
    public async Task EditedDate_OrHandWrittenFile_OrOtherPcFile_IsRejected()
    {
        await Service(NewServer()).RefreshAsync();
        var good = System.IO.File.ReadAllText(File);
        var offline = new FakeXman { Outage = () => ApiReply.Unreachable };

        EditState(s => s["expires"] = "2099-10-30T03:00:00Z");
        Assert.Equal(LicenseReason.TamperedFile, Service(offline).Status.Reason);

        System.IO.File.WriteAllText(File, good);
        EditState(s => s["last_seen"] = "2020-01-01T00:00:00Z");
        Assert.Equal(LicenseReason.TamperedFile, Service(offline).Status.Reason);

        System.IO.File.WriteAllText(File, good);
        Assert.Equal(LicenseTier.Trial, Service(offline).Status.Tier);

        System.IO.File.WriteAllText(File, """{"v":1,"state":{"machine":"x","registered_at":"2026-01-01T00:00:00Z","key":"K","type":"lifetime","key_state":"active"},"seal":"AAAA"}""");
        Assert.Equal(LicenseReason.TamperedFile, Service(offline).Status.Reason);

        System.IO.File.WriteAllText(File, "not json");
        Assert.Equal(LicenseReason.TamperedFile, Service(offline).Status.Reason);

        // a genuine file copied from another PC (other seal secret)
        System.IO.File.WriteAllText(File, good);
        Assert.Equal(LicenseReason.TamperedFile, Service(offline, secret: "other-guid|other-uuid|tester").Status.Reason);
    }

    [Fact]
    public async Task TamperedFile_GoesOnline_AndTheServerDecides_NoSecondTrial()
    {
        await Service(NewServer()).RefreshAsync();
        System.IO.File.WriteAllText(File, "{}");
        var api = NewServer(canTrial: false); // the server remembers this hardware had its trial
        var svc = Service(api);
        await svc.RefreshAsync();
        Assert.Equal(LicenseTier.Free, svc.Status.Tier);
        Assert.DoesNotContain("/demo", api.Calls);
    }

    [Fact]
    public async Task ClockTurnedBack_LocksTrial_UntilServerTimeComesBack()
    {
        var api = NewServer();
        var server = new DateTimeOffset(T0);
        api.Routes["/register-device"] = () => Reply(200, """{"success":true,"data":{}}""", server);
        var svc = Service(api);
        await svc.RefreshAsync();
        Assert.Equal(LicenseTier.Trial, svc.Status.Tier);

        // ten days of use (the watermark follows), then the PC clock is turned back nine days while offline
        _now = T0.AddDays(10);
        svc.Tick();
        _now = T0.AddDays(1);
        Assert.Equal(LicenseTier.Free, svc.Status.Tier);
        Assert.Equal(LicenseReason.ClockBack, svc.Status.Reason);

        // a fresh start reads the sealed watermark too
        Assert.Equal(LicenseReason.ClockBack, Service(new FakeXman { Outage = () => ApiReply.Unreachable }).Status.Reason);

        // xman answers with its real time: offset fixes the clock, the trial has 4 days left
        server = new DateTimeOffset(T0.AddDays(10));
        await svc.RefreshAsync();
        Assert.Equal(LicenseTier.Trial, svc.Status.Tier);
        Assert.Equal(4, svc.Status.DaysLeft);
    }

    [Fact]
    public async Task ServerClock_DecidesTrialEnd_NotThePcClock()
    {
        var api = NewServer();
        // PC clock is 30 days behind the real date
        var real = T0.AddDays(30);
        api.Routes["/register-device"] = () => Reply(200, """{"success":true,"data":{}}""", new DateTimeOffset(real));
        api.Routes["/demo"] = () => Reply(200, $$$"""{"success":true,"data":{"license_key":"DEMO-1","expires_at":"{{{Iso(real.AddDays(14))}}}"}}""");
        var svc = Service(api);
        await svc.RefreshAsync();
        Assert.Equal(14, svc.Status.DaysLeft);
        _now = T0.AddDays(15); // PC thinks 15 days passed → really 15 days after the trial began
        Assert.Equal(LicenseReason.TrialEnded, svc.Status.Reason);
    }

    [Theory]
    [InlineData("ALREADY_ACTIVATED_OTHER_DEVICE", LicenseActionCode.OtherDevice)]
    [InlineData("INVALID_LICENSE", LicenseActionCode.InvalidKey)]
    [InlineData("LICENSE_EXPIRED", LicenseActionCode.KeyExpired)]
    [InlineData("LICENSE_REVOKED", LicenseActionCode.KeyRevoked)]
    public async Task Activate_ServerRefusals_AreMapped(string code, LicenseActionCode expected)
    {
        var api = NewServer(canTrial: false);
        api.Routes["/activate"] = () => Reply(400, $$"""{"success":false,"error_code":"{{code}}"}""");
        var svc = Service(api);
        var r = await svc.ActivateAsync("ABCD-EFGH-IJKL-MNOP", false);
        Assert.Equal(expected, r.Code);
        Assert.False(svc.Status.IsPro);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("ABCD EFGH IJKL")]
    [InlineData("ABCD-EFGH-<script>")]
    public async Task Activate_BadFormat_NeverCallsServer(string key)
    {
        var api = NewServer();
        var r = await Service(api).ActivateAsync(key, false);
        Assert.Equal(LicenseActionCode.BadFormat, r.Code);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task Activate_DemoKey_IsNotPro()
    {
        var api = NewServer(canTrial: false);
        api.Routes["/activate"] = () => Reply(200, """{"success":true,"data":{"license_type":"demo"}}""");
        var r = await Service(api).ActivateAsync("DEMO-AAAA-BBBB-CCCC", false);
        Assert.Equal(LicenseActionCode.NotProKey, r.Code);
    }

    [Fact]
    public async Task Deactivate_ReleasesPaidKey()
    {
        var (svc, api) = await ProUser();
        api.Routes["/deactivate"] = () => Reply(200, """{"success":true}""");
        var r = await svc.DeactivateAsync();
        Assert.Equal(LicenseActionCode.Deactivated, r.Code);
        Assert.Equal(LicenseTier.Free, svc.Status.Tier);
        Assert.Null(svc.Status.KeyMasked);
    }

    [Fact]
    public void ProductIsReverseX_OnXman()
    {
        Assert.Equal("https://xman4289.com/api/v1/product/reverse-x", XmanApi.ProductApi);
        Assert.Equal("https://xman4289.com/products/reverse-x", new LicenseStatus().PurchaseUrl);
        Assert.True(XmanApi.IsXmanUrl(new Uri("https://xman4289.com/reverse-x/download/1.0.0")));
        Assert.False(XmanApi.IsXmanUrl(new Uri("http://xman4289.com/x")));
        Assert.False(XmanApi.IsXmanUrl(new Uri("https://xman4289.com.evil.example/x")));
        Assert.False(XmanApi.IsXmanUrl(new Uri("https://xman4289.com:8443/x")));
        Assert.False(XmanApi.IsXmanUrl(new Uri("https://github.com/xjanova/reverse-x/releases/download/v1/x.zip")));
    }
}
