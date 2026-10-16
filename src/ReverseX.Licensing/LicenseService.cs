namespace ReverseX.Licensing;

/// <summary>
/// This PC's Reverse X license on the xman studio product API (same flow as Anti X / WinXTools / BrainX):
/// register-device on the first start (nothing runs before that), then the saved key is validated,
/// or a key already bound to this PC is taken back (check-machine), or the one 14-day trial starts;
/// otherwise Free. Paid keys keep working 30 days without reaching the server; only real answers from
/// the server change the state, so an outage (offline, 5xx, 429) never downgrades or deletes a license.
/// Dates are judged by the server's clock (HTTP Date) plus a forward-only watermark, so turning the PC
/// clock back does not stretch a trial or a key.
/// </summary>
public sealed class LicenseService : ILicenseSource, IDisposable
{
    private static readonly HashSet<string> PaidTypes = new(StringComparer.OrdinalIgnoreCase) { "monthly", "yearly", "lifetime", "weekly", "daily" };
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromDays(30);
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(6);
    public static readonly TimeSpan RetryUnregistered = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TickEvery = TimeSpan.FromMinutes(10);

    private readonly IXmanClient _api;
    private readonly LicenseStore _store;
    private readonly Action<string> _log;
    private readonly Func<DateTime> _utcNow;
    private readonly string _version;
    private readonly string _machineId;
    private readonly string? _hardwareHash;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private LicenseState _s;
    private bool _tampered;
    private LicenseNote _note;
    private string? _serverMessage;

    public event Action? Changed;

    /// <summary>The real service: xman API, this PC's identity, the sealed license file in <paramref name="dataDir"/>.</summary>
    public LicenseService(string dataDir, string version, Action<string> log)
        : this(new XmanApi("ReverseX/" + version), new LicenseStore(Path.Combine(dataDir, "license.json"), MachineIdentity.SealSecret), version, log,
               () => DateTime.UtcNow, MachineIdentity.MachineId, MachineIdentity.HardwareHash)
    {
    }

    internal LicenseService(IXmanClient api, LicenseStore store, string version, Action<string> log, Func<DateTime> utcNow, string machineId, string? hardwareHash)
    {
        _api = api;
        _store = store;
        _version = version;
        _log = log;
        _utcNow = utcNow;
        _machineId = machineId;
        _hardwareHash = hardwareHash;
        var read = store.Load(out var saved);
        if (read == StoreRead.Ok && saved!.MachineId == machineId)
        {
            _s = saved;
        }
        else
        {
            // edited / copied / another PC's file: start over as unregistered (the trial lives on the server, it cannot restart)
            _tampered = read == StoreRead.Tampered || (read == StoreRead.Ok && saved!.MachineId != machineId);
            if (_tampered) _log("License file rejected (seal or machine mismatch)");
            _s = new LicenseState { MachineId = machineId };
        }
    }

    // ================================================================ time

    /// <summary>PC clock corrected by the server's (offset from the last definite answer).</summary>
    private DateTime TrustedNow(LicenseState s) => _utcNow().AddSeconds(s.ServerOffsetSeconds);

    private bool ClockBack(LicenseState s) => s.LastSeenUtc is { } seen && TrustedNow(s) < seen - ClockTolerance;

    /// <summary>The "now" every date is compared with: never earlier than the watermark.</summary>
    private DateTime EffectiveNow(LicenseState s)
    {
        var now = TrustedNow(s);
        return s.LastSeenUtc is { } seen && seen > now ? seen : now;
    }

    /// <summary>A definite answer: the server's Date header becomes the reference clock and resets the watermark.</summary>
    private void SyncClock(ApiReply reply)
    {
        lock (_gate)
        {
            if (reply.ServerDate is { } server)
            {
                _s.ServerOffsetSeconds = (server.UtcDateTime - _utcNow()).TotalSeconds;
                _s.LastSeenUtc = server.UtcDateTime;
            }
            else
            {
                var now = TrustedNow(_s);
                if (_s.LastSeenUtc is null || now > _s.LastSeenUtc) _s.LastSeenUtc = now;
            }
        }
    }

    /// <summary>Moves the watermark forward while the app runs (never back). Called on a timer.</summary>
    internal void Tick()
    {
        var before = Status;
        lock (_gate)
        {
            if (_s.RegisteredAt is null || ClockBack(_s)) return;
            _s.LastSeenUtc = TrustedNow(_s);
            Save();
        }
        RaiseIfChanged(before);
    }

    // ================================================================ status

    public LicenseStatus Status
    {
        get
        {
            lock (_gate) return Compute(_s, EffectiveNow(_s), ClockBack(_s), _tampered, _note, _serverMessage);
        }
    }

    internal static LicenseStatus Compute(LicenseState s, DateTime now, bool clockBack, bool tampered, LicenseNote note, string? serverMessage)
    {
        var demo = string.Equals(s.Type, "demo", StringComparison.OrdinalIgnoreCase);
        var paid = PaidTypes.Contains(s.Type ?? "") && !string.IsNullOrEmpty(s.Key);
        int? daysLeft = s.ExpiresAt is { } e && e > now ? Math.Max(0, (int)Math.Ceiling((e - now).TotalDays)) : null;
        LicenseStatus Make(LicenseTier tier, LicenseReason reason) => new()
        {
            Tier = tier, Reason = reason, Note = note, ServerMessage = serverMessage, DaysLeft = daysLeft,
            KeyMasked = Mask(s.Key), LicenseType = s.Type, ExpiresAt = s.ExpiresAt, LastVerifiedAt = s.VerifiedAt,
            HasPaidKey = paid && s.RegisteredAt is not null,
        };

        if (s.RegisteredAt is null)
            return Make(LicenseTier.Unregistered, s.DeviceBlocked ? LicenseReason.Blocked : tampered ? LicenseReason.TamperedFile : LicenseReason.NotRegistered);
        if (string.IsNullOrEmpty(s.Key)) return Make(LicenseTier.Free, LicenseReason.FreeNoKey);

        var expired = s.ExpiresAt is { } exp && exp <= now;
        switch (s.KeyState)
        {
            case "active" when clockBack:
                return Make(LicenseTier.Free, LicenseReason.ClockBack);
            case "active" when demo && !expired:
                return Make(LicenseTier.Trial, LicenseReason.TrialActive);
            case "active" when demo:
                return Make(LicenseTier.Free, LicenseReason.TrialEnded);
            case "active" when !PaidTypes.Contains(s.Type ?? ""):
                return Make(LicenseTier.Free, LicenseReason.NotProKey);
            case "active" when expired:
                return Make(LicenseTier.Free, LicenseReason.ProExpired);
            case "active" when s.VerifiedAt is { } v && now - v > OfflineGrace:
                return Make(LicenseTier.Free, LicenseReason.OfflineTooLong);
            case "active":
                return Make(LicenseTier.Pro, s.ExpiresAt is null ? LicenseReason.ProLifetime : LicenseReason.ProUntil);
            case "revoked":
                return Make(LicenseTier.Free, LicenseReason.Revoked);
            case "other_machine":
                return Make(LicenseTier.Free, LicenseReason.OtherMachine);
            default:
                return Make(LicenseTier.Free, demo ? LicenseReason.TrialEnded : LicenseReason.KeyExpired);
        }
    }

    internal static string? Mask(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        return key.Length <= 9 ? key[..Math.Min(4, key.Length)] + "…" : key[..4] + "-…-" + key[^4..];
    }

    // ================================================================ background

    /// <summary>Registers (retrying every minute until it works), then re-checks every 6 hours.</summary>
    public void Start() => _ = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await RefreshAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log("License check failed: " + ex.Message); }
            var wait = Status.IsRegistered ? RefreshEvery : RetryUnregistered;
            var until = DateTime.UtcNow + wait;
            try
            {
                while (DateTime.UtcNow < until)
                {
                    var step = until - DateTime.UtcNow;
                    await Task.Delay(step < TickEvery ? step : TickEvery, ct).ConfigureAwait(false);
                    Tick();
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One full round with the server.</summary>
    public async Task<LicenseActionResult> RefreshAsync()
    {
        await _busy.WaitAsync().ConfigureAwait(false);
        var before = Status;
        try
        {
            if (!await RegisterCoreAsync().ConfigureAwait(false)) return new(false, LicenseActionCode.NeedRegistration, _serverMessage);

            string? key;
            lock (_gate) key = _s.Key;
            if (!string.IsNullOrEmpty(key)) await ValidateCoreAsync(key).ConfigureAwait(false);
            lock (_gate) key = _s.KeyState == "active" ? _s.Key : null;
            if (string.IsNullOrEmpty(key) && !await CheckMachineCoreAsync().ConfigureAwait(false)) await TrialCoreAsync().ConfigureAwait(false);
            return new(true, LicenseActionCode.Ok);
        }
        finally
        {
            _busy.Release();
            RaiseIfChanged(before);
        }
    }

    private async Task<bool> RegisterCoreAsync()
    {
        var reply = await _api.PostAsync("/register-device", new Dictionary<string, object?>
        {
            ["machine_id"] = _machineId,
            ["machine_name"] = Cut(MachineIdentity.MachineName, 255),
            ["os_version"] = Cut(MachineIdentity.OsVersion, 255),
            ["app_version"] = Cut(_version, 50),
            ["hardware_hash"] = _hardwareHash,
        }).ConfigureAwait(false);
        lock (_gate)
        {
            var registered = _s.RegisteredAt is not null;
            if (!reply.IsDefinitive)
            {
                // ลงทะเบียนแล้วใช้ต่อได้แม้ออฟไลน์ · ยังไม่เคยลงทะเบียน = ต้องรอเน็ต
                _note = LicenseNote.Offline;
                return registered;
            }
        }
        SyncClock(reply);
        var blocked = string.Equals(reply.Data.Str("device_status"), "blocked", StringComparison.OrdinalIgnoreCase);
        lock (_gate)
        {
            if (reply.Success && !blocked)
            {
                _s.RegisteredAt ??= TrustedNow(_s);
                _s.DeviceBlocked = false;
                _tampered = false;
            }
            else if (blocked)
            {
                _s.DeviceBlocked = true;
                _s.RegisteredAt = null;
            }
            Save();
            var registered = _s.RegisteredAt is not null;
            _note = registered ? LicenseNote.None : LicenseNote.RegisterFailed;
            _serverMessage = registered ? null : reply.ServerMessage;
            return registered;
        }
    }

    private async Task ValidateCoreAsync(string key)
    {
        var reply = await _api.PostAsync("/validate", new Dictionary<string, object?>
        {
            ["license_key"] = key,
            ["machine_id"] = _machineId,
            ["app_version"] = Cut(_version, 50),
        }).ConfigureAwait(false);
        if (!reply.IsDefinitive || reply.ErrorCode == "PRODUCT_NOT_FOUND")
        {
            lock (_gate) _note = LicenseNote.ValidateLater;
            return;
        }
        SyncClock(reply);
        if (reply.Success)
        {
            var data = reply.Data;
            var valid = reply.Json.Bool("is_valid") == true;
            var state = valid ? "active" : string.Equals(data.Str("status"), "revoked", StringComparison.OrdinalIgnoreCase) ? "revoked" : "expired";
            SetKey(key, data.Str("license_type") ?? data.Str("type"), data.Date("expires_at"), state);
            return;
        }
        if (reply.ErrorCode == "INVALID_LICENSE")
        {
            // ไม่ได้ผูกกับเครื่องนี้แล้ว: เครื่องนี้อาจมีคีย์อื่นที่ผูกอยู่ ไม่งั้นถือว่าคีย์ไปอยู่เครื่องอื่น
            if (await CheckMachineCoreAsync().ConfigureAwait(false)) return;
            lock (_gate)
            {
                _s.KeyState = "other_machine";
                _s.VerifiedAt = TrustedNow(_s);
                Save();
            }
        }
    }

    /// <summary>True when the server says this PC already owns a license (paid or trial) and it was taken back.</summary>
    private async Task<bool> CheckMachineCoreAsync()
    {
        var reply = await _api.PostAsync("/check-machine", new Dictionary<string, object?> { ["machine_id"] = _machineId }).ConfigureAwait(false);
        if (!reply.IsDefinitive || !reply.Success || reply.Json.Bool("has_license") != true) return false;
        SyncClock(reply);
        var data = reply.Data;
        var key = data.Str("license_key");
        if (string.IsNullOrWhiteSpace(key)) return false;
        SetKey(key.Trim().ToUpperInvariant(), data.Str("license_type") ?? data.Str("type"), data.Date("expires_at"), "active");
        return true;
    }

    /// <summary>The one server-side 14-day trial, asked at most once per install and only if the server says it may start.</summary>
    private async Task TrialCoreAsync()
    {
        lock (_gate)
        {
            if (_s.TrialTried) return;
        }
        var check = await _api.PostAsync("/demo/check", new Dictionary<string, object?>
        {
            ["machine_id"] = _machineId,
            ["hardware_hash"] = _hardwareHash,
        }).ConfigureAwait(false);
        // ร้านยังไม่เปิดสินค้านี้ (404 / PRODUCT_NOT_FOUND): ยังไม่นับว่าขอทดลองแล้ว
        if (!check.IsDefinitive || check.Status == System.Net.HttpStatusCode.NotFound || check.ErrorCode == "PRODUCT_NOT_FOUND") return;
        SyncClock(check);
        lock (_gate)
        {
            _s.TrialTried = true;
            Save();
        }
        if (!check.Success || check.Data.Bool("can_start_demo") != true) return;

        var reply = await _api.PostAsync("/demo", new Dictionary<string, object?>
        {
            ["machine_id"] = _machineId,
            ["machine_name"] = Cut(MachineIdentity.MachineName, 255),
            ["hardware_hash"] = _hardwareHash,
            ["app_version"] = Cut(_version, 50),
        }).ConfigureAwait(false);
        if (!reply.IsDefinitive || !reply.Success) return;
        SyncClock(reply);
        var data = reply.Data;
        var expires = data.Date("expires_at");
        DateTime now;
        lock (_gate) now = TrustedNow(_s);
        if (expires is null && data.Long("seconds_remaining") is > 0 and var secs) expires = now.AddSeconds(secs);
        if (expires is null && data.Long("days_remaining") is > 0 and var days) expires = now.AddDays(days);
        var key = data.Str("license_key") ?? "DEMO";
        SetKey(key, "demo", expires, "active");
        _log($"Trial started, ends {expires:u}");
    }

    // ================================================================ owner actions

    /// <summary>Activates a paid key here. With <paramref name="moveFromOtherPc"/> the server unbinds it from its old PC first.</summary>
    public async Task<LicenseActionResult> ActivateAsync(string licenseKey, bool moveFromOtherPc)
    {
        var key = (licenseKey ?? "").Trim().ToUpperInvariant();
        if (key.Length < 8 || key.Length > 64 || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            return new(false, LicenseActionCode.BadFormat);
        await _busy.WaitAsync().ConfigureAwait(false);
        var before = Status;
        try
        {
            if (!await RegisterCoreAsync().ConfigureAwait(false)) return new(false, LicenseActionCode.NeedRegistration, _serverMessage);
            var body = new Dictionary<string, object?>
            {
                ["license_key"] = key,
                ["machine_id"] = _machineId,
                ["machine_fingerprint"] = _hardwareHash ?? _machineId,
                ["machine_name"] = Cut(MachineIdentity.MachineName, 255),
                ["app_version"] = Cut(_version, 50),
            };
            if (moveFromOtherPc) body["force_rebind"] = "true";
            var reply = await _api.PostAsync("/activate", body).ConfigureAwait(false);
            if (!reply.IsDefinitive) return new(false, LicenseActionCode.Offline);
            SyncClock(reply);
            if (reply.Success)
            {
                var type = reply.Data.Str("license_type") ?? reply.Data.Str("type") ?? "";
                if (!PaidTypes.Contains(type)) return new(false, LicenseActionCode.NotProKey);
                SetKey(key, type, reply.Data.Date("expires_at"), "active");
                return new(true, LicenseActionCode.Activated);
            }
            return reply.ErrorCode switch
            {
                "ALREADY_ACTIVATED_OTHER_DEVICE" => new(false, LicenseActionCode.OtherDevice),
                "INVALID_LICENSE" => new(false, LicenseActionCode.InvalidKey),
                "LICENSE_EXPIRED" => new(false, LicenseActionCode.KeyExpired),
                "LICENSE_REVOKED" => new(false, LicenseActionCode.KeyRevoked),
                _ => new(false, LicenseActionCode.ServerError, reply.ServerMessage),
            };
        }
        finally
        {
            _busy.Release();
            RaiseIfChanged(before);
        }
    }

    /// <summary>Frees the paid key from this PC so it can be used on another one.</summary>
    public async Task<LicenseActionResult> DeactivateAsync()
    {
        await _busy.WaitAsync().ConfigureAwait(false);
        var before = Status;
        try
        {
            string? key;
            bool paid;
            lock (_gate)
            {
                key = _s.Key;
                paid = PaidTypes.Contains(_s.Type ?? "");
            }
            if (string.IsNullOrEmpty(key) || !paid) return new(false, LicenseActionCode.NoPaidKey);
            var reply = await _api.PostAsync("/deactivate", new Dictionary<string, object?> { ["license_key"] = key, ["machine_id"] = _machineId }).ConfigureAwait(false);
            if (!reply.IsDefinitive) return new(false, LicenseActionCode.Offline);
            SyncClock(reply);
            if (!reply.Success && reply.ErrorCode != "INVALID_LICENSE") return new(false, LicenseActionCode.ServerError, reply.ServerMessage);
            lock (_gate)
            {
                _s.Key = null;
                _s.Type = null;
                _s.ExpiresAt = null;
                _s.KeyState = "";
                Save();
            }
            return new(true, LicenseActionCode.Deactivated);
        }
        finally
        {
            _busy.Release();
            RaiseIfChanged(before);
        }
    }

    // ================================================================ helpers

    private void SetKey(string key, string? type, DateTime? expires, string state)
    {
        lock (_gate)
        {
            _s.Key = key;
            _s.Type = type ?? _s.Type;
            _s.ExpiresAt = expires;
            _s.KeyState = state;
            _s.VerifiedAt = TrustedNow(_s);
            _note = LicenseNote.None;
            // เครื่องที่เคยมีคีย์ Pro แล้วไม่ขอช่วงทดลองอีก (ทดลองมีไว้สำหรับเครื่องใหม่เท่านั้น)
            if (PaidTypes.Contains(_s.Type ?? "")) _s.TrialTried = true;
            Save();
        }
    }

    private void RaiseIfChanged(LicenseStatus before)
    {
        var after = Status;
        if (after.Tier != before.Tier || after.Reason != before.Reason || after.Note != before.Note || after.DaysLeft != before.DaysLeft
            || after.KeyMasked != before.KeyMasked || after.ExpiresAt != before.ExpiresAt)
            Changed?.Invoke();
    }

    private void Save()
    {
        try { _store.Save(_s); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log("License file not saved: " + ex.Message); }
    }

    private static string Cut(string s, int max) => s.Length > max ? s[..max] : s;

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
