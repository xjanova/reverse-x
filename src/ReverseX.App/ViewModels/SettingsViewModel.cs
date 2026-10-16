using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using ReverseX.App.Services;
using ReverseX.Core;
using ReverseX.Core.Ai;

namespace ReverseX.App.ViewModels;

public sealed class ModelOptionVm : ObservableObject
{
    private readonly SettingsViewModel _owner;
    public ModelOptionVm(ClaudeModel m, SettingsViewModel owner) { Model = m; _owner = owner; }
    public ClaudeModel Model { get; }
    public string Display => Model.Display;
    public string Price => Loc.Format("Set.Model.Price", Fmt(Model.Price.Input), Fmt(Model.Price.Output));
    private static string Fmt(decimal d) => d.ToString("0.###", CultureInfo.InvariantCulture);
    public bool IsSelected
    {
        get => _owner.SelectedModelId == Model.Id;
        set { if (value) _owner.SelectedModelId = Model.Id; }
    }
    public void Refresh() { Raise(nameof(IsSelected)); Raise(nameof(Price)); }
}

/// <summary>Settings page: API key (stored with DPAPI), model, budget per question.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AiServices _svc;
    private string _keyNotice = "";
    private bool _keyNoticeError;
    private string _budgetText;
    private bool _hasKey;
    private string _maskedKey = "";

    public SettingsViewModel(AiServices svc, LicenseViewModel license)
    {
        _svc = svc;
        License = license;
        _budgetText = svc.Prefs.BudgetUsd.ToString("0.00", CultureInfo.InvariantCulture);
        foreach (var m in AiModels.All) Models.Add(new ModelOptionVm(m, this));
        _modelText = svc.Prefs.CustomModel ?? "";
        _urlText = svc.Prefs.CustomBaseUrl ?? "";
        DeleteKeyCommand = new RelayCommand(DeleteKey, () => _hasKey);
        RefreshKeyState();
        Loc.Changed += () => { foreach (var m in Models) m.Refresh(); Raise(nameof(KeyPathText)); Raise(nameof(SelectedPrice)); };
    }

    public event Action? Changed;

    public LicenseViewModel License { get; }

    private string _modelText;
    private string _urlText;

    public ObservableCollection<ModelOptionVm> Models { get; } = new();
    public IReadOnlyList<AiProvider> Providers => AiProviders.All;

    public string SelectedProviderId
    {
        get => _svc.Prefs.Provider;
        set
        {
            if (_svc.Prefs.Provider == value || !AiProviders.IsKnown(value)) return;
            _svc.Prefs.Provider = value;
            var p = AiProviders.Get(value);
            // each vendor starts from its own defaults; typed values belong to the vendor they were typed for
            _svc.Prefs.CustomModel = p.IsAnthropic ? null : p.DefaultModel;
            _svc.Prefs.CustomBaseUrl = null;
            _modelText = _svc.Prefs.CustomModel ?? "";
            _urlText = p.UrlEditable ? p.BaseUrl : "";
            if (p.UrlEditable && p.BaseUrl.Length > 0) _svc.Prefs.CustomBaseUrl = p.BaseUrl;
            KeyNotice = "";
            RefreshKeyState();
            Persist();
            Raise(nameof(SelectedProviderId)); Raise(nameof(IsAnthropic)); Raise(nameof(ShowCustomModel)); Raise(nameof(ShowUrl));
            Raise(nameof(NeedsKey)); Raise(nameof(ModelText)); Raise(nameof(UrlText)); Raise(nameof(KeyPathText)); Raise(nameof(UrlValid));
        }
    }

    public bool IsAnthropic => AiProviders.Get(_svc.Prefs.Provider).IsAnthropic;
    public bool ShowCustomModel => !IsAnthropic;
    public bool ShowUrl => AiProviders.Get(_svc.Prefs.Provider).UrlEditable;
    public bool NeedsKey => AiProviders.Get(_svc.Prefs.Provider).NeedsKey;

    public string ModelText
    {
        get => _modelText;
        set
        {
            if (!Set(ref _modelText, value)) return;
            _svc.Prefs.CustomModel = value.Trim();
            Persist();
        }
    }

    public string UrlText
    {
        get => _urlText;
        set
        {
            if (!Set(ref _urlText, value)) return;
            _svc.Prefs.CustomBaseUrl = value.Trim();
            Raise(nameof(UrlValid));
            if (UrlValid) Persist();
        }
    }
    public bool UrlValid => !ShowUrl || AiProviders.IsAcceptableUrl(_urlText);
    public ICommand DeleteKeyCommand { get; }

    public bool HasKey { get => _hasKey; private set { if (Set(ref _hasKey, value)) Raise(nameof(HasNoKey)); } }
    public bool HasNoKey => !_hasKey;
    public string MaskedKey { get => _maskedKey; private set => Set(ref _maskedKey, value); }
    public string KeyNotice { get => _keyNotice; private set { if (Set(ref _keyNotice, value)) Raise(nameof(HasKeyNotice)); } }
    public bool HasKeyNotice => _keyNotice.Length > 0;
    public bool KeyNoticeError { get => _keyNoticeError; private set => Set(ref _keyNoticeError, value); }
    public string KeyPathText => Loc.Format("Set.Key.Where", _svc.ActiveKeys.FilePath);

    public string SelectedModelId
    {
        get => _svc.Prefs.Model;
        set
        {
            if (_svc.Prefs.Model == value) return;
            _svc.Prefs.Model = value;
            Persist();
            foreach (var m in Models) m.Refresh();
            Raise(nameof(SelectedModelId)); Raise(nameof(SelectedPrice));
        }
    }

    public string SelectedPrice => Models.FirstOrDefault(m => m.IsSelected)?.Price ?? "";

    public string BudgetText
    {
        get => _budgetText;
        set
        {
            if (!Set(ref _budgetText, value)) return;
            if (TryParseBudget(value, out var d)) { _svc.Prefs.BudgetUsd = d; Persist(); }
            Raise(nameof(BudgetValid));
        }
    }
    public bool BudgetValid => TryParseBudget(_budgetText, out _);

    public static bool TryParseBudget(string? s, out decimal d) =>
        decimal.TryParse(s?.Trim().TrimStart('$'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out d) && d >= 0.01m && d <= 1000m;

    private void Persist()
    {
        _svc.Prefs.Save(_svc.PrefsPath);
        Changed?.Invoke();
    }

    private void RefreshKeyState()
    {
        HasKey = _svc.ActiveKeys.HasKey;
        MaskedKey = _hasKey && _svc.ActiveKeys.Load() is { } k ? ApiKeyStore.Mask(k) : "";
    }

    /// <summary>The page hands over the PasswordBox text; it is stored encrypted and the box is cleared by the caller.</summary>
    public void SaveKey(string? key)
    {
        key = key?.Trim() ?? "";
        if (key.Length == 0) { Notice(Loc.Get("Set.Key.Empty"), true); return; }
        if (IsAnthropic && !key.StartsWith("sk-ant-", StringComparison.Ordinal)) { Notice(Loc.Get("Set.Key.Format"), true); return; }
        try
        {
            _svc.ActiveKeys.Save(key);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            AppPaths.Log("Could not store API key"); // no key material in the log
            Notice(Loc.Get("Set.Key.SaveFailed"), true);
            return;
        }
        RefreshKeyState();
        Notice(Loc.Get("Set.Key.Saved"), false);
        Changed?.Invoke();
    }

    private void DeleteKey()
    {
        _svc.ActiveKeys.Delete();
        RefreshKeyState();
        Notice(Loc.Get("Set.Key.Deleted"), false);
        Changed?.Invoke();
    }

    private void Notice(string text, bool error) { KeyNotice = text; KeyNoticeError = error; }
}
