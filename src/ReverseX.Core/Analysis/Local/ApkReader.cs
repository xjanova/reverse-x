using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace ReverseX.Core.Analysis.Local;

/// <summary>
/// Reads an APK (or an XAPK / APKS / APKM bundle) without JADX or a JDK: binary manifest, resources.arsc label,
/// every classes*.dex class list and string pool, native libs, signing certificates, then derives insights.
/// </summary>
public static class ApkReader
{
    public const int ClassCap = 60_000;
    private const long DexCap = 512L * 1024 * 1024;
    private static readonly XNamespace A = AndroidBinaryXml.AndroidNs;

    private static readonly HashSet<string> Dangerous = new(StringComparer.Ordinal)
    {
        "READ_CONTACTS", "WRITE_CONTACTS", "GET_ACCOUNTS", "READ_CALENDAR", "WRITE_CALENDAR", "CAMERA", "RECORD_AUDIO",
        "ACCESS_FINE_LOCATION", "ACCESS_COARSE_LOCATION", "ACCESS_BACKGROUND_LOCATION", "READ_PHONE_STATE", "READ_PHONE_NUMBERS",
        "CALL_PHONE", "ANSWER_PHONE_CALLS", "READ_CALL_LOG", "WRITE_CALL_LOG", "PROCESS_OUTGOING_CALLS", "ADD_VOICEMAIL", "USE_SIP",
        "READ_SMS", "SEND_SMS", "RECEIVE_SMS", "RECEIVE_MMS", "RECEIVE_WAP_PUSH", "READ_EXTERNAL_STORAGE", "WRITE_EXTERNAL_STORAGE",
        "BODY_SENSORS", "BODY_SENSORS_BACKGROUND", "ACTIVITY_RECOGNITION", "READ_MEDIA_IMAGES", "READ_MEDIA_VIDEO", "READ_MEDIA_AUDIO",
        "READ_MEDIA_VISUAL_USER_SELECTED", "POST_NOTIFICATIONS", "NEARBY_WIFI_DEVICES", "BLUETOOTH_SCAN", "BLUETOOTH_CONNECT",
        "BLUETOOTH_ADVERTISE", "UWB_RANGING", "ACCESS_MEDIA_LOCATION",
    };

    private static readonly HashSet<string> Special = new(StringComparer.Ordinal)
    {
        "SYSTEM_ALERT_WINDOW", "WRITE_SETTINGS", "REQUEST_INSTALL_PACKAGES", "MANAGE_EXTERNAL_STORAGE", "QUERY_ALL_PACKAGES",
        "PACKAGE_USAGE_STATS", "BIND_ACCESSIBILITY_SERVICE", "BIND_DEVICE_ADMIN", "BIND_NOTIFICATION_LISTENER_SERVICE",
        "REQUEST_IGNORE_BATTERY_OPTIMIZATIONS", "SCHEDULE_EXACT_ALARM", "USE_FULL_SCREEN_INTENT", "READ_PRIVILEGED_PHONE_STATE",
        "INSTALL_PACKAGES", "DELETE_PACKAGES", "BIND_VPN_SERVICE", "MANAGE_ACCOUNTS", "CHANGE_COMPONENT_ENABLED_STATE",
    };

    // (class-name prefix, display name, category). The first matching prefix wins, so specific ones come first.
    private static readonly (string Prefix, string Name, string Category)[] SdkTable =
    {
        ("com.google.firebase.crashlytics", "Firebase Crashlytics", "crash"),
        ("com.google.firebase.analytics", "Firebase Analytics", "analytics"),
        ("com.google.firebase.messaging", "Firebase Cloud Messaging", "push"),
        ("com.google.firebase.firestore", "Cloud Firestore", "backend"),
        ("com.google.firebase.auth", "Firebase Auth", "backend"),
        ("com.google.firebase.database", "Firebase Realtime DB", "backend"),
        ("com.google.firebase.remoteconfig", "Firebase Remote Config", "backend"),
        ("com.google.firebase", "Firebase (core)", "backend"),
        ("com.google.android.gms.ads", "Google Mobile Ads (AdMob)", "ads"),
        ("com.google.android.gms.maps", "Google Maps", "maps"),
        ("com.google.android.gms.auth", "Google Sign-In", "auth"),
        ("com.google.android.gms.location", "Google Location", "location"),
        ("com.google.android.play.core", "Play Core", "platform"),
        ("com.android.billingclient", "Play Billing", "payment"),
        ("com.google.mlkit", "ML Kit", "ml"),
        ("org.tensorflow.lite", "TensorFlow Lite", "ml"),
        ("com.facebook.ads", "Meta Audience Network", "ads"),
        ("com.facebook.react", "React Native", "framework"),
        ("com.facebook.appevents", "Facebook App Events", "analytics"),
        ("com.facebook.login", "Facebook Login", "auth"),
        ("com.facebook", "Facebook SDK", "social"),
        ("com.appsflyer", "AppsFlyer", "attribution"),
        ("com.adjust.sdk", "Adjust", "attribution"),
        ("io.branch", "Branch", "attribution"),
        ("com.kochava", "Kochava", "attribution"),
        ("com.onesignal", "OneSignal", "push"),
        ("com.braze", "Braze", "engagement"),
        ("com.appboy", "Braze (Appboy)", "engagement"),
        ("com.clevertap", "CleverTap", "engagement"),
        ("com.amplitude", "Amplitude", "analytics"),
        ("com.mixpanel", "Mixpanel", "analytics"),
        ("com.segment.analytics", "Segment", "analytics"),
        ("com.flurry", "Flurry", "analytics"),
        ("com.yandex.metrica", "AppMetrica", "analytics"),
        ("com.microsoft.appcenter", "App Center", "analytics"),
        ("io.sentry", "Sentry", "crash"),
        ("com.bugsnag", "Bugsnag", "crash"),
        ("com.instabug", "Instabug", "crash"),
        ("com.applovin", "AppLovin", "ads"),
        ("com.ironsource", "ironSource", "ads"),
        ("com.unity3d.ads", "Unity Ads", "ads"),
        ("com.unity3d.services", "Unity Ads", "ads"),
        ("com.mbridge", "Mintegral", "ads"),
        ("com.bytedance.sdk", "Pangle", "ads"),
        ("com.vungle", "Vungle / Liftoff", "ads"),
        ("com.chartboost", "Chartboost", "ads"),
        ("com.inmobi", "InMobi", "ads"),
        ("com.startapp", "Start.io", "ads"),
        ("com.unity3d", "Unity", "framework"),
        ("io.flutter", "Flutter", "framework"),
        ("org.apache.cordova", "Cordova", "framework"),
        ("com.getcapacitor", "Capacitor", "framework"),
        ("mono.android", ".NET for Android (Xamarin)", "framework"),
        ("org.godotengine", "Godot", "framework"),
        ("com.badlogic.gdx", "libGDX", "framework"),
        ("androidx.compose", "Jetpack Compose", "ui"),
        ("okhttp3", "OkHttp", "network"),
        ("com.squareup.okhttp", "OkHttp (2.x)", "network"),
        ("retrofit2", "Retrofit", "network"),
        ("io.ktor", "Ktor", "network"),
        ("com.android.volley", "Volley", "network"),
        ("io.grpc", "gRPC", "network"),
        ("com.apollographql", "Apollo GraphQL", "network"),
        ("io.socket", "Socket.IO", "network"),
        ("com.google.gson", "Gson", "data"),
        ("com.squareup.moshi", "Moshi", "data"),
        ("com.fasterxml.jackson", "Jackson", "data"),
        ("kotlinx.serialization", "kotlinx.serialization", "data"),
        ("com.google.protobuf", "Protocol Buffers", "data"),
        ("io.realm", "Realm", "database"),
        ("net.sqlcipher", "SQLCipher", "security"),
        ("net.zetetic", "SQLCipher", "security"),
        ("androidx.room", "Room", "database"),
        ("org.bouncycastle", "Bouncy Castle", "security"),
        ("org.spongycastle", "Spongy Castle", "security"),
        ("com.scottyab.rootbeer", "RootBeer (root check)", "security"),
        ("com.google.android.gms.safetynet", "SafetyNet", "security"),
        ("com.google.android.play.core.integrity", "Play Integrity", "security"),
        ("androidx.security.crypto", "Jetpack Security", "security"),
        ("com.stripe", "Stripe", "payment"),
        ("com.paypal", "PayPal", "payment"),
        ("com.braintreepayments", "Braintree", "payment"),
        ("com.razorpay", "Razorpay", "payment"),
        ("com.bumptech.glide", "Glide", "media"),
        ("com.squareup.picasso", "Picasso", "media"),
        ("io.coil_kt", "Coil", "media"),
        ("coil", "Coil", "media"),
        ("com.google.android.exoplayer2", "ExoPlayer", "media"),
        ("androidx.media3", "Media3", "media"),
        ("com.airbnb.lottie", "Lottie", "media"),
        ("dagger", "Dagger / Hilt", "di"),
        ("org.koin", "Koin", "di"),
        ("io.reactivex", "RxJava", "async"),
        ("kotlinx.coroutines", "Kotlin Coroutines", "async"),
        ("com.tencent.mm.opensdk", "WeChat SDK", "social"),
        ("com.linecorp", "LINE SDK", "social"),
        ("com.twitter", "Twitter/X SDK", "social"),
    };

    public static AndroidModel Read(string path, IProgress<AnalysisProgress>? progress, CancellationToken ct)
    {
        using var outer = ZipFile.OpenRead(path);
        if (outer.GetEntry("AndroidManifest.xml") is not null)
            return ReadApk(path, outer, splits: new List<string>(), extraLibs: new List<AndroidNativeLib>(), progress, ct);

        // Bundle (XAPK / APKS / APKM): pick the base APK, read native libs from the split APKs too.
        var apks = outer.Entries.Where(e => e.FullName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)).ToList();
        if (apks.Count == 0) throw new InvalidDataException("no AndroidManifest.xml and no inner .apk");
        var baseEntry = apks.FirstOrDefault(e => Path.GetFileName(e.FullName).Equals("base.apk", StringComparison.OrdinalIgnoreCase)
                                                || Path.GetFileName(e.FullName).Equals("base-master.apk", StringComparison.OrdinalIgnoreCase))
                        ?? apks.Where(e => !Path.GetFileName(e.FullName).StartsWith("config.", StringComparison.OrdinalIgnoreCase)
                                           && !e.FullName.Contains("split", StringComparison.OrdinalIgnoreCase))
                               .OrderByDescending(e => e.Length).FirstOrDefault()
                        ?? apks.OrderByDescending(e => e.Length).First();
        var tmp = Path.Combine(Path.GetTempPath(), "ReverseX-apk-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(tmp);
        try
        {
            var basePath = Path.Combine(tmp, "base.apk");
            baseEntry.ExtractToFile(basePath);
            var splits = new List<string>();
            var libs = new List<AndroidNativeLib>();
            foreach (var s in apks.Where(e => !ReferenceEquals(e, baseEntry)))
            {
                ct.ThrowIfCancellationRequested();
                splits.Add(s.FullName);
                var sp = Path.Combine(tmp, "split.apk");
                s.ExtractToFile(sp, overwrite: true);
                using (var sz = ZipFile.OpenRead(sp)) libs.AddRange(NativeLibs(sz));
                File.Delete(sp);
            }
            using var bz = ZipFile.OpenRead(basePath);
            return ReadApk(basePath, bz, splits, libs, progress, ct);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry e, long cap)
    {
        if (e.Length > cap) throw new InvalidDataException("entry too large: " + e.FullName);
        using var s = e.Open();
        var buf = new byte[e.Length];
        s.ReadExactly(buf);
        return buf;
    }

    private static List<AndroidNativeLib> NativeLibs(ZipArchive zip)
    {
        var list = new List<AndroidNativeLib>();
        foreach (var e in zip.Entries)
        {
            var parts = e.FullName.Split('/');
            if (parts.Length == 3 && parts[0] == "lib" && parts[2].EndsWith(".so", StringComparison.Ordinal))
                list.Add(new AndroidNativeLib(parts[1], parts[2], e.Length));
        }
        return list;
    }

    private static AndroidModel ReadApk(string path, ZipArchive zip, List<string> splits, List<AndroidNativeLib> extraLibs,
        IProgress<AnalysisProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new AnalysisProgress("apk.manifest", 0, 4));
        ArscReader? arsc = null;
        if (zip.GetEntry("resources.arsc") is { } re && re.Length < 256L * 1024 * 1024)
        {
            try { arsc = new ArscReader(ReadEntry(re, 256L * 1024 * 1024)); }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { }
        }
        var manifestEntry = zip.GetEntry("AndroidManifest.xml") ?? throw new InvalidDataException("AndroidManifest.xml missing");
        var doc = AndroidBinaryXml.Decode(ReadEntry(manifestEntry, 32 * 1024 * 1024), id => arsc?.Resolve(id));
        var root = doc.Root!;
        var app = root.Element("application");
        string? Attr(XElement? e, string name) => e?.Attribute(A + name)?.Value ?? e?.Attribute(name)?.Value;
        int? Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        bool? Bool(string? s) => s is null ? null : s == "true" || s == "-1" ? true : s == "false" || s == "0" ? false : null;

        var pkg = root.Attribute("package")?.Value ?? "";
        var usesSdk = root.Element("uses-sdk");
        var targetSdk = Int(Attr(usesSdk, "targetSdkVersion"));
        var minSdk = Int(Attr(usesSdk, "minSdkVersion"));
        var compileSdk = Int(Attr(root, "compileSdkVersion")) ?? Int(root.Attribute("platformBuildVersionCode")?.Value);

        var perms = root.Elements().Where(e => e.Name.LocalName is "uses-permission" or "uses-permission-sdk-23")
            .Select(e => Attr(e, "name")).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).Distinct()
            .Select(n =>
            {
                var shortName = n.StartsWith("android.permission.", StringComparison.Ordinal) ? n["android.permission.".Length..] : null;
                var prot = shortName is null ? "custom" : Dangerous.Contains(shortName) ? "dangerous" : Special.Contains(shortName) ? "special" : "normal";
                return new AndroidPermission(n, prot);
            })
            .OrderBy(p => p.Protection switch { "dangerous" => 0, "special" => 1, "custom" => 3, _ => 2 }).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
        var declared = root.Elements("permission").Select(e => Attr(e, "name") + (Attr(e, "protectionLevel") is { } pl ? " · " + ProtectionName(pl) : ""))
            .Where(s => s.Length > 0).ToList();

        string Full(string? name) => string.IsNullOrEmpty(name) ? "" : name.StartsWith('.') ? pkg + name : name.Contains('.') ? name : pkg + "." + name;

        var components = new List<AndroidComponent>();
        var schemes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var e in app?.Elements() ?? Enumerable.Empty<XElement>())
        {
            var type = e.Name.LocalName switch { "activity" or "activity-alias" => "activity", "service" => "service", "receiver" => "receiver", "provider" => "provider", _ => null };
            if (type is null) continue;
            var filters = e.Elements("intent-filter").ToList();
            var actions = filters.SelectMany(f => f.Elements("action")).Select(a => Attr(a, "name") ?? "").Where(s => s.Length > 0).Distinct().ToList();
            var cats = filters.SelectMany(f => f.Elements("category")).Select(a => Attr(a, "name") ?? "").Where(s => s.Length > 0).Distinct().ToList();
            var data = filters.SelectMany(f => f.Elements("data")).Select(d =>
            {
                var sc = Attr(d, "scheme"); var host = Attr(d, "host"); var pth = Attr(d, "path") ?? Attr(d, "pathPrefix") ?? Attr(d, "pathPattern"); var mime = Attr(d, "mimeType");
                if (sc is not null && sc is not "http" and not "https") schemes.Add(sc);
                return mime ?? (sc is null && host is null ? "" : $"{sc ?? "*"}://{host ?? "*"}{pth ?? ""}");
            }).Where(s => s.Length > 0).Distinct().ToList();
            var explicitExported = Bool(Attr(e, "exported"));
            // Before Android 12 a component with an intent-filter was exported implicitly; providers defaulted to exported below API 17.
            var exported = explicitExported ?? (filters.Count > 0 || (type == "provider" && (targetSdk ?? 0) < 17));
            components.Add(new AndroidComponent
            {
                Type = type,
                Name = Full(Attr(e, "name") ?? Attr(e, "targetActivity")),
                Exported = exported,
                Permission = Attr(e, "permission") ?? Attr(e, "readPermission"),
                Actions = actions,
                Categories = cats,
                Data = data,
            });
        }

        // ── DEX
        progress?.Report(new AnalysisProgress("apk.dex", 1, 4));
        var collector = new TextScan.Collector();
        var dexEntries = zip.Entries.Where(e => e.FullName.StartsWith("classes", StringComparison.Ordinal) && e.FullName.EndsWith(".dex", StringComparison.Ordinal) && !e.FullName.Contains('/'))
            .OrderBy(e => e.FullName.Length).ThenBy(e => e.FullName, StringComparer.Ordinal).ToList();
        var allClasses = new List<string>();
        long dexBytes = 0;
        var dexNames = new List<string>();
        foreach (var de in dexEntries)
        {
            ct.ThrowIfCancellationRequested();
            if (dexBytes + de.Length > DexCap) break;
            dexBytes += de.Length;
            try
            {
                var info = DexReader.Read(ReadEntry(de, DexCap), s => collector.Scan(s, de.FullName), ct);
                allClasses.AddRange(info.Classes);
                dexNames.Add($"{de.FullName} · dex {info.Version} · {info.Classes.Count:N0} classes");
            }
            catch (InvalidDataException) { dexNames.Add(de.FullName + " · ?"); }
        }

        // ── SDKs & packages
        progress?.Report(new AnalysisProgress("apk.sdk", 2, 4));
        var sdkCounts = new Dictionary<string, (string Category, int Count)>(StringComparer.Ordinal);
        var pkgCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var c in allClasses)
        {
            foreach (var (prefix, name, cat) in SdkTable)
            {
                if (c.StartsWith(prefix, StringComparison.Ordinal) && (c.Length == prefix.Length || c[prefix.Length] == '.'))
                {
                    sdkCounts[name] = (cat, (sdkCounts.TryGetValue(name, out var cur) ? cur.Count : 0) + 1);
                    break;
                }
            }
            var dot = c.LastIndexOf('.');
            var p = dot > 0 ? c[..dot] : "(default)";
            // Group at 3 levels so "com.example.app.ui.x" counts under "com.example.app".
            var parts = p.Split('.');
            var key = string.Join('.', parts.Take(3));
            pkgCounts[key] = pkgCounts.GetValueOrDefault(key) + 1;
        }

        // ── files, native libs, certificates
        progress?.Report(new AnalysisProgress("apk.files", 3, 4));
        var libs = NativeLibs(zip).Concat(extraLibs).ToList();
        var abis = libs.Select(l => l.Abi).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        var folders = zip.Entries.GroupBy(e => e.FullName.Contains('/') ? e.FullName[..e.FullName.IndexOf('/')] + "/" : "(root)")
            .Select(g => new NamedCount(g.Key, g.Sum(e => e.Length))).OrderByDescending(n => n.Count).Take(12).ToList();
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("assets/", StringComparison.Ordinal) && e.Length is > 0 and < 2_000_000
                     && (e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".properties", StringComparison.OrdinalIgnoreCase)
                         || e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)
                         || e.FullName.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))).Take(200))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var sr = new StreamReader(e.Open());
                collector.Scan(sr.ReadToEnd(), e.FullName);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { }
        }
        var certs = ApkSigning.Read(path, zip);

        var framework = DetectFramework(zip, libs, sdkCounts.Keys, allClasses);
        var model = new AndroidModel
        {
            PackageName = pkg,
            Label = Attr(app, "label"),
            VersionName = Attr(root, "versionName"),
            VersionCode = Attr(root, "versionCode"),
            MinSdk = minSdk,
            TargetSdk = targetSdk,
            CompileSdk = compileSdk,
            AppClass = Attr(app, "name") is { } an ? Full(an) : null,
            Debuggable = Bool(Attr(app, "debuggable")) == true,
            AllowBackup = Bool(Attr(app, "allowBackup")),
            CleartextTraffic = Bool(Attr(app, "usesCleartextTraffic")),
            NetworkSecurityConfig = Attr(app, "networkSecurityConfig") is not null,
            Framework = framework,
            Permissions = perms,
            DeclaredPermissions = declared,
            Components = components,
            DexFiles = dexNames,
            ClassCount = allClasses.Count,
            Classes = allClasses.Distinct().OrderBy(s => s, StringComparer.Ordinal).Take(ClassCap).ToList(),
            Packages = pkgCounts.Select(kv => new NamedCount(kv.Key, kv.Value)).OrderByDescending(n => n.Count).Take(400).ToList(),
            Sdks = sdkCounts.Select(kv => new AndroidSdk(kv.Key, kv.Value.Category, kv.Value.Count)).OrderBy(s => s.Category, StringComparer.Ordinal).ThenByDescending(s => s.Classes).ToList(),
            Abis = abis,
            NativeLibs = libs.OrderBy(l => l.Abi, StringComparer.Ordinal).ThenBy(l => l.Name, StringComparer.Ordinal).ToList(),
            Certificates = certs,
            Schemes = schemes.ToList(),
            Urls = collector.UrlList.OrderBy(u => u, StringComparer.Ordinal).ToList(),
            Secrets = collector.SecretList,
            TopFolders = folders,
            FileCount = zip.Entries.Count,
            UncompressedSize = zip.Entries.Sum(e => e.Length),
            Splits = splits,
            ManifestXml = Truncate(doc.ToString(), 400_000),
        };
        progress?.Report(new AnalysisProgress("apk.files", 4, 4));
        return model with { Insights = Insights(model) };
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "\n<!-- … -->";

    private static string ProtectionName(string v) => v switch
    {
        "0" or "0x0" => "normal", "1" or "0x1" => "dangerous", "2" or "0x2" => "signature", "3" or "0x3" => "signatureOrSystem",
        _ => v.StartsWith("0x", StringComparison.Ordinal) && (Convert.ToInt32(v, 16) & 0xF) == 2 ? "signature+" : v,
    };

    private static string DetectFramework(ZipArchive zip, List<AndroidNativeLib> libs, IEnumerable<string> sdks, List<string> classes)
    {
        bool Lib(string n) => libs.Any(l => l.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        bool Entry(string n) => zip.GetEntry(n) is not null;
        if (Lib("libflutter.so")) return "Flutter";
        if (Lib("libreactnativejni.so") || Lib("libhermes.so") || Entry("assets/index.android.bundle")) return "React Native";
        if (Lib("libil2cpp.so")) return "Unity (IL2CPP)";
        if (Lib("libunity.so")) return "Unity (Mono)";
        if (Lib("libmonodroid.so") || Lib("libmonosgen-2.0.so") || zip.Entries.Any(e => e.FullName.StartsWith("assemblies/", StringComparison.Ordinal))) return ".NET for Android / MAUI";
        if (Lib("libgodot_android.so")) return "Godot";
        if (Entry("assets/www/index.html") || Entry("assets/public/index.html")) return sdks.Contains("Capacitor") ? "Capacitor (Ionic)" : "Cordova";
        if (Lib("libcocos2dcpp.so") || Lib("libcocos.so")) return "Cocos";
        if (sdks.Contains("Jetpack Compose")) return "Kotlin · Jetpack Compose";
        if (classes.Any(c => c.StartsWith("kotlin.", StringComparison.Ordinal))) return "Kotlin";
        return classes.Count > 0 ? "Java" : "?";
    }

    private static List<Insight> Insights(AndroidModel m)
    {
        var list = new List<Insight>();
        var seen = EvidenceLevel.Seen;
        if (m.Debuggable) list.Add(Insight.Of("ApkDebuggable", InsightTone.Risk, seen));
        if (m.CleartextTraffic == true) list.Add(Insight.Of("ApkCleartext", InsightTone.Risk, seen));
        else if (m.CleartextTraffic is null && (m.TargetSdk ?? 0) < 28 && !m.NetworkSecurityConfig) list.Add(Insight.Of("ApkCleartextDefault", InsightTone.Warn, EvidenceLevel.Inferred, m.TargetSdk ?? 0));
        if (m.AllowBackup != false) list.Add(Insight.Of("ApkBackup", InsightTone.Warn, m.AllowBackup == true ? seen : EvidenceLevel.Inferred));
        var exposed = m.Components.Where(c => c.Exported && c.Permission is null && !c.Actions.Contains("android.intent.action.MAIN")).ToList();
        if (exposed.Count > 0) list.Add(Insight.Of("ApkExported", InsightTone.Warn, EvidenceLevel.Inferred, exposed.Count, string.Join(", ", exposed.Take(3).Select(c => c.Name.Split('.').Last()))));
        var dangerous = m.Permissions.Count(p => p.Protection == "dangerous");
        if (dangerous > 0) list.Add(Insight.Of("ApkDangerous", dangerous >= 8 ? InsightTone.Warn : InsightTone.Info, seen, dangerous));
        foreach (var p in m.Permissions.Where(p => p.Protection == "special"))
            list.Add(Insight.Of("ApkSpecial", InsightTone.Warn, seen, p.Name.Split('.').Last()));
        if (m.Components.Any(c => c.Actions.Contains("android.accessibilityservice.AccessibilityService")))
            list.Add(Insight.Of("ApkAccessibility", InsightTone.Risk, seen));
        if (m.Certificates.Any(c => c.Subject.Contains("CN=Android Debug", StringComparison.OrdinalIgnoreCase)))
            list.Add(Insight.Of("ApkDebugCert", InsightTone.Risk, seen));
        if (m.Certificates.Count > 0 && m.Certificates.All(c => c.Scheme == "v1"))
            list.Add(Insight.Of("ApkV1Only", InsightTone.Warn, seen));
        if (m.Certificates.Count == 0) list.Add(Insight.Of("ApkUnsigned", InsightTone.Warn, EvidenceLevel.Inferred));
        if (m.TargetSdk is { } t && t < 30) list.Add(Insight.Of("ApkOldTarget", InsightTone.Warn, seen, t));
        if (m.Secrets.Count > 0) list.Add(Insight.Of("Secrets", InsightTone.Risk, EvidenceLevel.Inferred, m.Secrets.Count, string.Join(", ", m.Secrets.Select(s => s.Kind).Distinct().Take(3))));
        var http = m.Urls.Count(u => u.StartsWith("http://", StringComparison.Ordinal) && !u.Contains("localhost") && !u.Contains("127.0.0.1"));
        if (http > 0) list.Add(Insight.Of("HttpUrls", InsightTone.Warn, EvidenceLevel.Inferred, http));
        var trackers = m.Sdks.Where(s => s.Category is "ads" or "analytics" or "attribution" or "engagement").Select(s => s.Name).Distinct().ToList();
        if (trackers.Count > 0) list.Add(Insight.Of("ApkTrackers", InsightTone.Info, EvidenceLevel.Inferred, trackers.Count, string.Join(", ", trackers.Take(4))));
        if (m.Sdks.Any(s => s.Name.StartsWith("RootBeer", StringComparison.Ordinal) || s.Name is "SafetyNet" or "Play Integrity"))
            list.Add(Insight.Of("ApkIntegrity", InsightTone.Info, EvidenceLevel.Inferred));
        if (m.Framework.Length > 0 && m.Framework != "?") list.Add(Insight.Of("ApkFramework", InsightTone.Good, EvidenceLevel.Inferred, m.Framework));
        if (m.ClassCount > 0 && m.Classes.Count(c => c.Split('.').Last().Length <= 2) > m.ClassCount * 0.4)
            list.Add(Insight.Of("ApkObfuscated", InsightTone.Info, EvidenceLevel.Inferred));
        if (m.Schemes.Count > 0) list.Add(Insight.Of("ApkDeepLinks", InsightTone.Info, seen, string.Join(", ", m.Schemes.Take(4))));
        return list;
    }
}
