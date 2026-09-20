using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Threading;
using GamePingBooster.App.Services;
using GamePingBooster.App.Services.Localization;
using GamePingBooster.Core.Ipc;

namespace GamePingBooster.App.ViewModels;

/// <summary>
/// The app's only view model. Keeps things simple: one toggle button, one status line, a few
/// numbers. INotifyPropertyChanged is hand-written rather than pulling in an MVVM library - the
/// app has a single screen, not worth another dependency in a Native AOT binary.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly PipeClient _pipe;

    public MainViewModel(PipeClient pipe)
    {
        _pipe = pipe;
        _pipe.StatusReceived += OnStatus;
        _pipe.Disconnected += OnDisconnected;

        // Every line on this screen is computed, so switching language is a matter of telling the
        // bindings to ask again. This view model lives as long as the app, so there is nothing to
        // unsubscribe from.
        Loc.Changed += OnLanguageChanged;
    }

    // ------------------------------------------------------------ licence
    //
    // All of this is absent on a self-hosted installation: no licenceUrl means no sign-in button,
    // no licence line, nothing to explain. A new installation is NOT self-hosted - the service
    // starts it pointed at the licence server (ServiceConfig.DefaultLicenceUrl).

    private string? _licenceUrl;
    public string? LicenceUrl
    {
        get => _licenceUrl;
        private set
        {
            if (!Set(ref _licenceUrl, value)) return;
            Raise(nameof(ShowLicence));
            Raise(nameof(SetupText));
        }
    }

    /// <summary>This machine's device public key, hex. Public, and needed to sign in.</summary>
    public string? DevicePublicKey { get; private set; }

    /// <summary>Which copy the service is actually using: shipped, pushed or cached.</summary>
    private string? _profileSource;
    public string? ProfileSource
    {
        get => _profileSource;
        private set { if (Set(ref _profileSource, value)) Raise(nameof(LicenceText)); }
    }

    private bool _hasToken;
    public bool HasToken
    {
        get => _hasToken;
        private set
        {
            if (!Set(ref _hasToken, value)) return;
            Raise(nameof(LicenceText));
            Raise(nameof(AccountMenuText));
            Raise(nameof(SetupText));
        }
    }

    private DateTimeOffset? _tokenExpiresAt;
    public DateTimeOffset? TokenExpiresAt
    {
        get => _tokenExpiresAt;
        private set
        {
            if (!Set(ref _tokenExpiresAt, value)) return;
            // A moved expiry IS the evidence a renewal worked, so any complaint about the last
            // one has stopped being true and should not be left on screen.
            _licenceNotice = null;
            Raise(nameof(LicenceNotice));
            Raise(nameof(LicenceText));
        }
    }

    /// <summary>
    /// Why the service would refuse to connect for licence reasons, or null when it would not.
    ///
    /// Decided in the service and only displayed here. Working it out again in the UI would put
    /// two copies of one rule on either side of the pipe, and the copy that matters is the one
    /// that can actually stop a handshake.
    /// </summary>
    private string? _licenceRefusal;
    public string? LicenceRefusal
    {
        get => _licenceRefusal;
        private set
        {
            if (!Set(ref _licenceRefusal, value)) return;
            Raise(nameof(LicenceBlocked));
            Raise(nameof(LicenceText));
            Raise(nameof(LicenceBrush));
            Raise(nameof(CanPressAction));
        }
    }

    public bool LicenceBlocked => !string.IsNullOrWhiteSpace(LicenceRefusal);

    /// <summary>
    /// The licence line is normally a quiet footnote and should stay one - "signed in, valid
    /// until Thursday" is not news. A refusal is the opposite: it is the reason the only button
    /// on the window does nothing, so it stops being grey.
    ///
    /// A ready-made brush rather than a colour string, for the same reason as StatusBrush: a
    /// string bound to IBrush goes through a TypeConverter, and TypeConverters are exactly what
    /// the Native AOT trimmer removes.
    /// </summary>
    private static readonly IBrush LicenceQuiet = new SolidColorBrush(Color.FromRgb(0x78, 0x78, 0x78));

    public IBrush LicenceBrush => LicenceBlocked ? Brushes.Orange : LicenceQuiet;

    /// <summary>Whether this installation has a licence server at all.</summary>
    public bool ShowLicence => !string.IsNullOrWhiteSpace(LicenceUrl);

    /// <summary>What the menu item says. One entry, two states, no dead end either way.</summary>
    public string AccountMenuText => Loc.T(HasToken ? "main.account.account" : "main.account.signIn");

    // ------------------------------------------------------------ updates

    private AvailableUpdate? _update;

    /// <summary>A newer release found by UpdateChecker, or null. Set on the UI thread.</summary>
    public AvailableUpdate? Update
    {
        get => _update;
        set
        {
            if (!Set(ref _update, value)) return;
            Raise(nameof(HasUpdate));
            Raise(nameof(UpdateFooterText));
        }
    }

    public bool HasUpdate => Update is not null;

    private bool _updateRequired;

    /// <summary>
    /// The licence server refused this version as older than its minimum (426), so nothing connects
    /// until the update is installed. Set on the UI thread, never cleared: only a new version fixes it.
    /// </summary>
    public bool UpdateRequired
    {
        get => _updateRequired;
        set
        {
            if (!Set(ref _updateRequired, value)) return;
            Raise(nameof(UpdateFooterText));
        }
    }

    /// <summary>The footer line, and only there when a newer release exists.</summary>
    public string UpdateFooterText => Update is null
        ? ""
        : Loc.F(UpdateRequired ? "main.update.required" : "main.update.available", Update.Version);

    /// <summary>
    /// The last thing the renewer had to say, if anything.
    ///
    /// It gets its own property rather than borrowing Detail, which the service overwrites on
    /// every status push - a message written there would be gone within the second and nobody
    /// would ever see it. Cleared as soon as the state it described stops being true.
    /// </summary>
    private string? _licenceNotice;
    public string? LicenceNotice
    {
        get => _licenceNotice;
        set { if (Set(ref _licenceNotice, value)) Raise(nameof(LicenceText)); }
    }

    public string LicenceText
    {
        get
        {
            // A refusal outranks everything else here. It is the reason Connect is dead, and a
            // line saying "signed in, licence valid until..." next to a button that will not
            // work is worse than no line at all.
            //
            // Except when this machine is still signed in. The service's refusal then says "sign
            // in again", which is how paying customers ended up signing out and in: the token had
            // only run out, or been cleared while a plan lapsed, and TokenRefresher replaces it
            // on its own the moment the licence server agrees. Say that instead, or what the last
            // attempt was told - a real "no active subscription" still comes through that way.
            var expired = !HasToken || TokenExpiresAt is not { } until || until <= DateTimeOffset.UtcNow;
            if (LicenceBlocked && expired && RefreshTokenStore.Exists())
            {
                return !string.IsNullOrEmpty(LicenceNotice)
                    ? LicenceNotice!
                    : Loc.T("licence.renewing");
            }
            if (LicenceBlocked) return LicenceRefusal!;
            if (!string.IsNullOrEmpty(LicenceNotice)) return LicenceNotice!;
            if (!HasToken) return Loc.T("licence.notSignedIn");

            // A licence server that is set but has never sent a game list means the ranges are
            // whatever the installer carried. The tunnel works, so nothing else would say so.
            if (!string.IsNullOrWhiteSpace(LicenceUrl) && ProfileSource == "shipped")
            {
                return Loc.T("licence.signedInShipped");
            }
            if (TokenExpiresAt is not { } expiry) return Loc.T("licence.signedIn");

            // Renewal happens on its own at half of remaining life, so an expiry hours away is
            // normal and not something to alarm anybody about. Only say something when it is
            // close enough that the renewal has evidently not been happening.
            //
            // No date otherwise. This expiry is the TOKEN's - a day away and moved forward by every
            // renewal - and "valid until tomorrow" was read as the plan ending tomorrow, then as a
            // trial gaining a day each time somebody signed in again. The plan's real end is on
            // the Account screen, which asks the licence server for it.
            var left = expiry - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) return Loc.T("licence.expired");
            if (left < TimeSpan.FromHours(2)) return Loc.F("licence.expiresIn", $"{left.TotalMinutes:F0}");
            return Loc.T("licence.signedIn");
        }
    }

    // ------------------------------------------------------------ displayed state

    private TunnelState _state = TunnelState.Disconnected;
    public TunnelState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(StatusText));
            Raise(nameof(StatusBrush));
            Raise(nameof(ActionButtonText));
            Raise(nameof(IsBusy));
            Raise(nameof(CanPressAction));
            Raise(nameof(CanChooseRelay));
        }
    }

    private string _detail = Loc.T("detail.starting");
    public string Detail { get => _detail; private set => Set(ref _detail, value); }

    /// <summary>
    /// Which key <see cref="Detail"/> was written from, when the WINDOW wrote it rather than the
    /// service - null once a status has overwritten it. Kept so that a language change can say the
    /// same line again: before the service has ever answered, there is no status to rebuild it from.
    /// </summary>
    private string? _detailKey = "detail.starting";

    private void SetDetail(string key)
    {
        _detailKey = key;
        Detail = Loc.T(key);
    }

    // ------------------------------------------------------- configuration state
    //
    // A freshly installed machine has no relay and no key, and pressing Connect could only fail
    // with a message about a missing configuration. Knowing this up here means the button can
    // point at the settings screen instead, which is the only useful thing to do next.

    private bool _configured;
    public bool Configured
    {
        get => _configured;
        private set
        {
            if (!Set(ref _configured, value)) return;
            Raise(nameof(NeedsSetup));
            Raise(nameof(CanPressAction));
        }
    }

    public bool NeedsSetup => !Configured;

    /// <summary>
    /// What the setup banner asks for. With a licence server and no sign-in, that is signing in -
    /// the relays and the credential both come from the account - and telling a new customer to
    /// "enter your relay's address and key" would send them looking for values they were never given.
    /// </summary>
    public string SetupText => Loc.T(ShowLicence && !HasToken ? "main.setup.signIn" : "main.setup.needed");

    // ------------------------------------------------------------ relay choice
    //
    // One list for every game: a relay is the app's server, and which of the GAME's regions a match
    // lands in is the game's business. Automatic stays the default; a choice is saved by the service
    // and applies from the next connect. See TunnelEngine.SelectRelayAsync for what happens when the
    // chosen relay does not answer - the player is connected through the fastest one and told.

    private IReadOnlyList<RelayChoiceItem> _relayItems = [RelayChoiceItem.Automatic];
    public IReadOnlyList<RelayChoiceItem> RelayItems
    {
        get => _relayItems;
        private set
        {
            if (!Set(ref _relayItems, value)) return;
            Raise(nameof(ShowRelayChoice));
        }
    }

    private RelayChoiceItem? _selectedRelay = RelayChoiceItem.Automatic;

    /// <summary>
    /// The relay picked in the list. Set by the person, it is sent to the service; set from a status,
    /// it is only shown - <see cref="_applyingChoice"/> tells the two apart.
    /// </summary>
    public RelayChoiceItem? SelectedRelay
    {
        get => _selectedRelay;
        set
        {
            // The ComboBox writes null while its items are being replaced; that is not a choice.
            if (value is null || !Set(ref _selectedRelay, value) || _applyingChoice) return;
            _ = SendRelayChoiceAsync(value.Id);
        }
    }

    private bool _applyingChoice;

    /// <summary>The choice the service last confirmed, so a heartbeat does not undo a pick still on its way down.</summary>
    private string? _confirmedChoice;
    private bool _choicePending;

    private string? _relayChoiceNote;
    public string? RelayChoiceNote
    {
        get => _relayChoiceNote;
        private set
        {
            if (!Set(ref _relayChoiceNote, value)) return;
            Raise(nameof(HasRelayChoiceNote));
        }
    }

    public bool HasRelayChoiceNote => !string.IsNullOrEmpty(RelayChoiceNote);

    /// <summary>Only worth showing when there is more than one relay to choose from.</summary>
    public bool ShowRelayChoice => RelayItems.Count > 2;

    /// <summary>Locked while a connect is on its way, like the button: nothing changes under a connect in progress.</summary>
    public bool CanChooseRelay => !IsBusy;

    /// <summary>Asks the service for the relay list. Called when it may have changed and when the list is opened.</summary>
    public void RefreshRelays()
    {
        _ = SendQuietlyAsync(new CommandMessage { Verb = "relays" });
    }

    private async Task SendRelayChoiceAsync(string? relayId)
    {
        _choicePending = true;
        await SendQuietlyAsync(new CommandMessage { Verb = "set-relay-choice", RelayId = relayId ?? "" })
            .ConfigureAwait(false);
    }

    private async Task SendQuietlyAsync(CommandMessage command)
    {
        try
        {
            await _pipe.SendAsync(command).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The service is unreachable, which the window already says. The list simply stays as it is.
            Dispatcher.UIThread.Post(() => _choicePending = false);
        }
    }

    /// <summary>
    /// Takes the list from a relays or set-relay-choice reply. The same relays as before are updated where
    /// they stand: replacing the items of an open ComboBox closes it and drops the highlight, and the list
    /// is refreshed every few seconds while it is open.
    /// </summary>
    private void ApplyRelayList(List<RelayOption> relays)
    {
        var current = RelayItems.Skip(1).ToList();
        if (current.Count == relays.Count &&
            current.Zip(relays).All(pair => string.Equals(pair.First.Id, pair.Second.Id, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var (item, relay) in current.Zip(relays)) item.Update(relay.Name, relay.Location, relay.PingMs, relay.NotForGame);
            if (!_choicePending) ShowChoice(_confirmedChoice);
            return;
        }

        var items = new List<RelayChoiceItem> { RelayChoiceItem.Automatic };
        items.AddRange(relays.Select(r => new RelayChoiceItem(r.Id, r.Name, r.Location, r.PingMs, r.NotForGame)));

        _applyingChoice = true;
        try
        {
            RelayItems = items;
            ShowChoice(_confirmedChoice);
        }
        finally
        {
            _applyingChoice = false;
        }
    }

    /// <summary>Selects the item for <paramref name="relayId"/> without sending anything.</summary>
    private void ShowChoice(string? relayId)
    {
        var item = RelayItems.FirstOrDefault(i => string.Equals(i.Id, relayId, StringComparison.OrdinalIgnoreCase))
                   ?? RelayChoiceItem.Automatic;
        _applyingChoice = true;
        try
        {
            if (!ReferenceEquals(_selectedRelay, item))
            {
                _selectedRelay = item;
                Raise(nameof(SelectedRelay));
            }
        }
        finally
        {
            _applyingChoice = false;
        }
    }

    private long? _relaysAskedForProfile;
    private bool _relaysAsked;

    /// <summary>The configured endpoints, for the settings screen to open with. Never the key.</summary>
    public IReadOnlyList<string> RelayEndpoints { get; private set; } = [];

    private string? _error;
    public string? Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value)) Raise(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    private double? _pingMs;
    public double? PingMs
    {
        get => _pingMs;
        private set
        {
            if (Set(ref _pingMs, value)) Raise(nameof(PingText));
        }
    }

    private double? _gamePingMs;
    public double? GamePingMs
    {
        get => _gamePingMs;
        private set
        {
            if (!Set(ref _gamePingMs, value)) return;
            Raise(nameof(GamePingText));
            Raise(nameof(GamePingTip));
        }
    }

    private bool _gamePingDirect;
    public bool GamePingDirect
    {
        get => _gamePingDirect;
        private set
        {
            if (!Set(ref _gamePingDirect, value)) return;
            Raise(nameof(GamePingText));
            Raise(nameof(GamePingTip));
        }
    }

    private string? _gameRegionName;
    public string? GameRegionName
    {
        get => _gameRegionName;
        private set
        {
            if (!Set(ref _gameRegionName, value)) return;
            Raise(nameof(GamePingText));
            Raise(nameof(GamePingTip));
        }
    }

    private double? _lossRatio;
    public double? LossRatio
    {
        get => _lossRatio;
        private set
        {
            if (Set(ref _lossRatio, value)) Raise(nameof(LossText));
        }
    }

    private bool _gameRunning;
    public bool GameRunning
    {
        get => _gameRunning;
        private set
        {
            if (Set(ref _gameRunning, value)) Raise(nameof(GameText));
        }
    }

    private string? _gameName;
    public string? GameName
    {
        get => _gameName;
        private set
        {
            if (Set(ref _gameName, value)) Raise(nameof(GameText));
        }
    }

    private int _gameCount;
    public int GameCount
    {
        get => _gameCount;
        private set
        {
            if (!Set(ref _gameCount, value)) return;
            Raise(nameof(GameText));
            Raise(nameof(HasGames));
            Raise(nameof(GameTextBrush));
        }
    }

    private string? _relayName;
    public string? RelayName
    {
        get => _relayName;
        private set
        {
            if (!Set(ref _relayName, value)) return;
            Raise(nameof(RelayText));
            Raise(nameof(GamePingTip));
        }
    }

    private int _activeRoutes;
    public int ActiveRoutes
    {
        get => _activeRoutes;
        private set
        {
            if (Set(ref _activeRoutes, value)) Raise(nameof(RouteText));
        }
    }

    private bool _steamDns;

    /// <summary>
    /// Whether the service has Steam's names answered over encrypted DNS right now.
    ///
    /// Reported separately from everything else on this screen because it is a separate product:
    /// it fixes a block, not a ping, and a player whose Steam works while their ping did not move
    /// has to be able to see which half did what.
    /// </summary>
    public bool SteamDns
    {
        get => _steamDns;
        private set
        {
            if (!Set(ref _steamDns, value)) return;
            Raise(nameof(SteamText));
            Raise(nameof(SteamTip));
        }
    }

    private string? _steamDnsDetail;

    /// <summary>
    /// The service's own sentence about the Steam fix - how many names it answered, or why it is
    /// off. Shown in the tooltip, never as the line itself: it is English, written by a process
    /// that cannot know the user's language, and the line has to be in theirs.
    /// </summary>
    public string? SteamDnsDetail
    {
        get => _steamDnsDetail;
        private set
        {
            if (!Set(ref _steamDnsDetail, value)) return;
            Raise(nameof(SteamTip));
            // The line itself reads this to tell "the service says off" from "the service never
            // mentioned it", so it has to be told when it changes.
            Raise(nameof(SteamText));
        }
    }

    private long _packetsSent;
    public long PacketsSent
    {
        get => _packetsSent;
        private set
        {
            if (Set(ref _packetsSent, value)) Raise(nameof(PacketsText));
        }
    }

    private long _packetsReceived;
    public long PacketsReceived
    {
        get => _packetsReceived;
        private set
        {
            if (Set(ref _packetsReceived, value)) Raise(nameof(PacketsText));
        }
    }

    // --------------------------------------------------------- derived UI properties

    public string StatusText => Loc.T(State switch
    {
        TunnelState.Disconnected => "status.disconnected",
        TunnelState.Connecting => "status.connecting",
        TunnelState.Connected => "status.connected",
        TunnelState.Reconnecting => "status.reconnecting",
        TunnelState.Faulted => "status.faulted",
        _ => "status.unknown",
    });

    /// <summary>
    /// A ready-made brush instead of a colour string: binding a string to IBrush goes through a
    /// TypeConverter, and TypeConverters are exactly what the Native AOT trimmer removes.
    /// </summary>
    public IBrush StatusBrush => State switch
    {
        TunnelState.Connected => Brushes.LimeGreen,
        TunnelState.Connecting or TunnelState.Reconnecting => Brushes.Orange,
        TunnelState.Faulted => Brushes.OrangeRed,
        _ => Brushes.Gray,
    };

    public string ActionButtonText => Loc.T(State is TunnelState.Connected or TunnelState.Connecting
        ? "action.disconnect"
        : "action.connect");

    public bool IsBusy => State is TunnelState.Connecting or TunnelState.Reconnecting;
    // Nothing to connect to until a relay and a key exist, so the button is dead until then and
    // the UI says why. Letting it be pressed would produce a failure whose only cure is the
    // settings screen the user has not been told about.
    //
    // A licence refusal kills it for the same reason, with one exception: Disconnect stays
    // available. A licence that lapses while a tunnel is up must not trap the user in a session
    // they cannot end - the session was authorised when it started, and the button that ends it
    // has nothing to do with the licence.
    public bool CanPressAction => !IsBusy && Configured
        && (State is TunnelState.Connected or TunnelState.Connecting || !LicenceBlocked);

    /// <summary>
    /// The headline: what the game is expected to show, and where.
    ///
    /// A dash until a region has been measured. That happens when the profile declares no
    /// landmark for the game, or when no relay could echo one - both are real states and both are
    /// better shown as "unknown" than as the relay ping wearing a label that says game ping.
    /// </summary>
    /// <summary>
    /// A tilde when the number is the landmark estimate rather than a measurement against the
    /// game's own server. One character, and it is the difference between a number that came off
    /// the real path and one derived from a stand-in host - which is exactly the distinction a
    /// player is making when they hold this up against the ping in the game.
    /// </summary>
    public string GamePingText => GamePingMs is { } g
        ? (GamePingDirect ? "" : "~") +
          (GameRegionName is { } region
              ? Loc.F("value.msTo", $"{g:F0}", region)
              : Loc.F("value.ms", $"{g:F0}"))
        : Loc.T("value.none");

    /// <summary>
    /// What the in-game figure is, on hover. The estimate is to the game's servers in a region, and a player who
    /// has just picked a relay in another city reads "to Singapore" as the app ignoring the pick - so it says
    /// that the game chooses its server and the relay only changes the road there.
    /// </summary>
    public string GamePingTip => GamePingMs is null
        ? Loc.T("gamePing.tip.pending")
        : GamePingDirect
            ? Loc.T("gamePing.tip.measured")
            : Loc.F("gamePing.tip.estimated",
                RelayName ?? Loc.T("gamePing.tip.theRelay"),
                GameRegionName ?? Loc.T("gamePing.tip.itsServers"));

    public string PingText => PingMs is { } p ? Loc.F("value.ms", $"{p:F0}") : Loc.T("value.none");
    public string LossText => LossRatio is { } l ? $"{l * 100:F1}%" : Loc.T("value.none");
    public string RelayText => RelayName ?? Loc.T("value.none");
    public string RouteText => ActiveRoutes > 0 ? Loc.F("value.ranges", ActiveRoutes) : Loc.T("value.none");

    /// <summary>
    /// A dash only when the service did not report on this at all.
    ///
    /// Unlike every other value on this card, it does NOT go blank on disconnect: the Steam fix
    /// runs with the service and not with the tunnel, so a player who has stopped boosting still
    /// has working Steam and the line has to keep saying so.
    ///
    /// That second case is why this is not simply <c>SteamDns ? on : off</c>. The field is additive,
    /// so a service older than the feature sends nothing and the JSON default arrives here as
    /// false - which this line then announced as "not enabled", with a tooltip telling the player
    /// to press Connect to turn on something that build cannot do. Seen for real on 2026-09-20: the
    /// UI was new, the running service was the installed one from the day before, and the card
    /// accused a working setup of a failure.
    ///
    /// A service that HAS the feature always sends a detail line, whether it worked or not. So a
    /// missing detail means "not reported", and the honest answer to that is the dash.
    /// </summary>
    public string SteamText
    {
        get
        {
            if (!SteamDns && SteamDnsDetail is null) return Loc.T("value.none");
            return Loc.T(SteamDns ? "steam.on" : "steam.off");
        }
    }

    /// <summary>
    /// The explanation in the user's language, with the service's own line appended when it has
    /// something to add - which it does when the fix could not be turned on, and that reason is
    /// the only place a player can read why.
    /// </summary>
    public string SteamTip
    {
        get
        {
            var text = Loc.T(SteamDns ? "steam.tip.on" : "steam.tip.off");
            return string.IsNullOrWhiteSpace(SteamDnsDetail)
                ? text
                : text + Environment.NewLine + Environment.NewLine + SteamDnsDetail;
        }
    }

    /// <summary>
    /// The game being played, or how many are supported. A count and not the names: the line has
    /// to stay one short line however many games the profile grows to.
    /// </summary>
    /// <summary>Whether the Game line opens the supported games list: only when there is a list.</summary>
    public bool HasGames => GameCount > 0;

    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
    private static readonly IBrush ValueBrush = new SolidColorBrush(Color.FromRgb(0xab, 0xab, 0xab));

    /// <summary>Blue when the Game line can be clicked, the usual grey when it cannot.</summary>
    public IBrush GameTextBrush => HasGames ? LinkBrush : ValueBrush;

    public string GameText => GameName is not null
        ? Loc.F(GameRunning ? "game.running" : "game.notOpen", GameName)
        : GameCount > 0 ? Loc.F("game.noneOpen", GameCount) : Loc.T("value.none");

    /// <summary>
    /// Packet counters. Not cosmetic: when the tunnel connects but traffic does not flow, the
    /// first question is always whether the client is sending at all, and this answers it
    /// without attaching a packet capture.
    /// </summary>
    public string PacketsText => Loc.F("value.packets", PacketsSent, PacketsReceived);

    // ------------------------------------------------------------------- actions

    /// <summary>
    /// Set while something is waiting for the tunnel to actually be down, so the status push
    /// that says so can release it.
    /// </summary>
    private TaskCompletionSource? _teardown;

    /// <summary>
    /// Brings the tunnel down and waits for the service to confirm, or for the timeout.
    ///
    /// Waiting matters because the caller is on its way out. Sending the verb and leaving
    /// immediately means the pipe is disposed while the request may still be in the buffer, and
    /// the tunnel stays up: the adapter, the pinned route and every game route survive an app
    /// that looks closed, with nothing on screen to say so and no way to press Disconnect.
    ///
    /// The timeout is not a formality either. Teardown removes routes, releases the adapter and
    /// joins two pump threads - quick since routing stopped going through netsh, but seconds on a
    /// bad day. What it must never do is hold the window open indefinitely, so the wait is
    /// capped and the service is left to finish on its own if it is slow. The verb having been
    /// sent is the part that matters; the wait is only so the user sees it happen.
    /// </summary>
    public async Task DisconnectAndWaitAsync(TimeSpan timeout)
    {
        if (State is TunnelState.Disconnected) return;

        var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _teardown = wait;
        _holdConnecting = false;
        try
        {
            SetDetail("detail.disconnecting");
            await _pipe.DisconnectTunnelAsync().ConfigureAwait(true);
            await Task.WhenAny(wait.Task, Task.Delay(timeout)).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // The service may already be gone, or the pipe broken. Neither is a reason to
            // refuse to close the window - and a service that is gone has no tunnel either.
        }
        finally
        {
            _teardown = null;
        }
    }

    // Set for the few seconds between pressing Connect and the connect reaching the service, while
    // the profile is being fetched. A second press in that window would otherwise read the
    // Connecting state, send a disconnect, and then watch the first press connect anyway.
    private bool _connectInFlight;

    /// <summary>
    /// Holds the window on "Connecting..." from the press until the SERVICE says it has taken the connect
    /// up - a status of Connecting, Connected or Reconnecting.
    ///
    /// Without it the window let go in the middle. The press set Connecting, then spent seconds fetching
    /// the profile before the connect was even sent, and the service's once-a-second heartbeat - still
    /// saying Disconnected, or Faulted from an earlier attempt - put "Not connected" back on screen and
    /// the button back under the finger. People pressed it again, thinking the first press had failed.
    ///
    /// Only the service's state is held back: every other field of those statuses still applies. And it
    /// is not held for ever - see <see cref="ConnectHoldAfterSend"/> - because a connect the service never
    /// takes up (a version mismatch, which answers Faulted without starting) must still show its answer.
    /// </summary>
    private bool _holdConnecting;

    /// <summary>When the connect verb was sent, or null while the profile is still being fetched.</summary>
    private DateTimeOffset? _connectSentAt;

    /// <summary>
    /// How long after sending the connect a stale Disconnected or Faulted is still ignored. The service
    /// handles pipe commands in order, so the profile pushes just before it can take a moment, but it
    /// reports Connecting as its first act on a connect.
    /// </summary>
    private static readonly TimeSpan ConnectHoldAfterSend = TimeSpan.FromSeconds(15);

    /// <param name="profileSync">
    /// When given, the latest profile is fetched from the licence server BEFORE the connect is sent.
    ///
    /// Without it, Connect used whatever profile was pulled when the app started, so a relay added
    /// in the dashboard while the app was open simply did not exist until a restart - and the
    /// lobby-tunnel switch could not reach a client that stayed open all day either.
    ///
    /// Ordering is what makes this work rather than a race: the fetch ends by writing set-profile
    /// to the pipe, the service handles pipe commands one at a time in the order they arrive, and
    /// ConnectAsync reloads the profile from disk. So the connect always sees the profile that was
    /// just pushed.
    ///
    /// A fetch that fails never stops the connect. ProfileSync reports it and returns - offline,
    /// licence server down, or the twelve-an-hour limit - and the service connects on the profile
    /// it already has, which is exactly what pressing Connect did before this existed.
    /// </param>
    public async Task ToggleAsync(ProfileSync? profileSync = null)
    {
        if (_connectInFlight) return;

        try
        {
            Error = null;
            if (State is TunnelState.Connected or TunnelState.Connecting)
            {
                // A disconnect asked for while a connect is still held on screen (the tray can send
                // one) wants the Disconnected that follows shown, not held back as stale.
                _holdConnecting = false;
                await _pipe.DisconnectTunnelAsync().ConfigureAwait(false);
            }
            else
            {
                _connectInFlight = true;
                _holdConnecting = true;
                _connectSentAt = null;
                State = TunnelState.Connecting;

                if (profileSync is not null && !string.IsNullOrWhiteSpace(LicenceUrl))
                {
                    SetDetail("detail.fetchingProfile");
                    // ConfigureAwait(true): this is called from a click on the UI thread, and the
                    // next line raises PropertyChanged - off the UI thread that breaks Avalonia's
                    // bindings in ways that surface later and somewhere else.
                    await profileSync
                        .SyncAsync(LicenceUrl, DevicePublicKey, force: true)
                        .ConfigureAwait(true);
                }

                SetDetail("detail.sending");
                await _pipe.ConnectTunnelAsync().ConfigureAwait(true);
                _connectSentAt = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _holdConnecting = false;
            State = TunnelState.Faulted;
            Error = ex.Message;
        }
        finally
        {
            _connectInFlight = false;
        }
    }

    // ------------------------------------------------------- updates from the service

    /// <summary>
    /// The last status the service pushed, whole and unprocessed.
    ///
    /// Kept because the lag report samples it once a second for twenty seconds: rungs 5 and 6 are
    /// the service's own live keepalive RTT and game ping, and the point is to watch them MOVE.
    /// The bound properties on this view model each hold one field, formatted for display, which
    /// is the wrong shape for that. Volatile: written on the UI thread, read from the diagnostic
    /// run, and a stale read costs one sample out of twenty.
    /// </summary>
    public StatusMessage? LastStatus { get; private set; }

    /// <summary>Whether the service sends connection quality after each match; null from a service too old to.</summary>
    public bool? QualitySharing { get; private set; }

    /// <summary>
    /// Says everything on this screen again in the language just chosen.
    ///
    /// Every text property here is computed from state, so the words follow as soon as the bindings
    /// ask again - which is what these Raise calls are for. The three lines the SERVICE wrote are
    /// the exception: those are rebuilt from the code and arguments of the last status, which is
    /// why they are stored rather than only displayed. See ApplyServiceText.
    /// </summary>
    private void OnLanguageChanged()
    {
        if (LastStatus is { } status) ApplyServiceText(status);
        else if (_detailKey is { } key) Detail = Loc.T(key);

        Raise(nameof(AccountMenuText));
        Raise(nameof(SetupText));
        Raise(nameof(UpdateFooterText));
        Raise(nameof(LicenceText));
        Raise(nameof(StatusText));
        Raise(nameof(ActionButtonText));
        Raise(nameof(GamePingText));
        Raise(nameof(GamePingTip));
        Raise(nameof(PingText));
        Raise(nameof(LossText));
        Raise(nameof(RelayText));
        Raise(nameof(RouteText));
        Raise(nameof(SteamText));
        Raise(nameof(SteamTip));
        Raise(nameof(GameText));
        Raise(nameof(PacketsText));

        foreach (var item in RelayItems) item.RelabelForLanguage();
    }

    /// <summary>
    /// The three lines the service supplies: the detail under the status, the licence refusal, and
    /// the note about the chosen relay.
    ///
    /// Each arrives twice - as English, and as a language key with its arguments - and the key wins
    /// when this build knows it. A service too old to send keys, or one sending a key from a newer
    /// version, falls through to the English it also sent, which is what this UI always did.
    /// </summary>
    private void ApplyServiceText(StatusMessage status)
    {
        _detailKey = null;
        Detail = Say(status.DetailCode, status.DetailArgs, status.Detail) ?? "";
        LicenceRefusal = Say(status.LicenceRefusalCode, status.LicenceRefusalArgs, status.LicenceRefusal);
        RelayChoiceNote = Say(status.RelayChoiceNoteCode, status.RelayChoiceNoteArgs, status.RelayChoiceNote);
    }

    private static string? Say(string? code, List<string>? args, string? english)
    {
        if (string.IsNullOrEmpty(code)) return english;
        return args is { Count: > 0 } ? Loc.F(code, [.. args]) : Loc.T(code);
    }

    private void OnStatus(StatusMessage status) => Dispatcher.UIThread.Post(() =>
    {
        LastStatus = status;

        // Whoever is closing the app can stop waiting. Faulted counts: the tunnel is not up, and
        // holding the window open for five seconds over a teardown that already failed helps
        // nobody.
        if (status.State is TunnelState.Disconnected or TunnelState.Faulted)
        {
            _teardown?.TrySetResult();
        }

        ApplyRelayChoice(status);

        // A press of Connect the service has not taken up yet: keep saying Connecting, with the
        // window's own line about what it is doing, and none of an earlier attempt's error.
        if (_holdConnecting)
        {
            var stale = status.State is TunnelState.Disconnected or TunnelState.Faulted;
            var waited = _connectSentAt is { } sent && DateTimeOffset.UtcNow - sent > ConnectHoldAfterSend;
            if (stale && !waited)
            {
                ApplyDetails(status);
                return;
            }
            _holdConnecting = false;
        }

        State = status.State;
        _detailKey = null;
        Detail = Say(status.DetailCode, status.DetailArgs, status.Detail) ?? "";
        Error = status.Error;
        ApplyDetails(status);
    });

    /// <summary>
    /// The relay list and the saved choice from a status. The list is asked for, not pushed: once at
    /// start, whenever a new profile has been written, and after each connect - which is when the
    /// service has new round trips to show beside each relay.
    /// </summary>
    private void ApplyRelayChoice(StatusMessage status)
    {
        if (status.AckVerb is "relays" or "set-relay-choice" && status.Relays is { } relays)
        {
            if (status.AckVerb == "set-relay-choice") _choicePending = false;
            _confirmedChoice = status.RelayChoice;
            ApplyRelayList(relays);
        }
        else if (!_choicePending && !string.Equals(_confirmedChoice, status.RelayChoice, StringComparison.OrdinalIgnoreCase))
        {
            // Changed somewhere else - config.json edited, or a relay that left the profile.
            _confirmedChoice = status.RelayChoice;
            ShowChoice(_confirmedChoice);
        }

        RelayChoiceNote = Say(status.RelayChoiceNoteCode, status.RelayChoiceNoteArgs, status.RelayChoiceNote);

        var profileStamp = status.ProfileUpdatedAt ?? 0;
        var connectedNow = status.State == TunnelState.Connected && State != TunnelState.Connected;
        if (!_relaysAsked || _relaysAskedForProfile != profileStamp || connectedNow)
        {
            _relaysAsked = true;
            _relaysAskedForProfile = profileStamp;
            RefreshRelays();
        }
    }

    /// <summary>Everything a status carries except the tunnel's state, its detail line and its error.</summary>
    private void ApplyDetails(StatusMessage status)
    {
        PingMs = status.TunnelPingMs;
        GamePingMs = status.GamePingMs;
        GamePingDirect = status.GamePingDirect;
        GameRegionName = status.GameRegionName;
        LossRatio = status.LossRatio;
        GameRunning = status.GameRunning;
        GameName = status.GameName;
        GameCount = status.GameCount;
        RelayName = status.RelayName;
        RelayEndpoints = status.RelayEndpoints;
        Configured = status.Configured;
        LicenceUrl = status.LicenceUrl;
        LicenceRefusal = Say(status.LicenceRefusalCode, status.LicenceRefusalArgs, status.LicenceRefusal);
        ProfileSource = status.ProfileSource;
        QualitySharing = status.QualitySharing;
        DevicePublicKey = status.DevicePublicKey;
        HasToken = status.HasToken;
        TokenExpiresAt = status.TokenExpiresAt is { } unix
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : null;
        ActiveRoutes = status.ActiveRoutes;
        SteamDns = status.SteamDns;
        SteamDnsDetail = status.SteamDnsDetail;
        PacketsSent = status.PacketsSent;
        PacketsReceived = status.PacketsReceived;
    }

    private void OnDisconnected(string reason) => Dispatcher.UIThread.Post(() =>
    {
        // The pipe itself dropped. Nothing more is coming, so anything waiting on a status that
        // says "down" would wait out its whole timeout for an answer that cannot arrive - and a
        // connect held on screen would be held for one that never comes.
        _teardown?.TrySetResult();
        _holdConnecting = false;

        State = TunnelState.Disconnected;
        Detail = reason;
        PingMs = null;
        GamePingMs = null;
        GamePingDirect = false;
        GameRegionName = null;
        LossRatio = null;
        ActiveRoutes = 0;

        // Cleared here and NOT on an ordinary disconnect. This method runs when the pipe itself
        // dropped, which means the service is gone - and the service going takes the name policy
        // with it, so the fix really is off. A tunnel disconnect is the opposite case: the service
        // is alive, Steam still works, and the line must keep saying so.
        SteamDns = false;
        SteamDnsDetail = null;

        PacketsSent = 0;
        PacketsReceived = 0;
    });

    // --------------------------------------------------- INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// One line of the relay list on the main window: automatic, or a relay with its ping. Updated in place as
/// new pings arrive, so an open list keeps its place.
/// </summary>
public sealed class RelayChoiceItem : INotifyPropertyChanged
{
    /// <summary>
    /// The "let the app choose" line. Its name is not passed in and not stored: it is the one item
    /// whose text is ours rather than a relay's, so it is read from the language table every time
    /// and follows a language change without being rebuilt.
    /// </summary>
    public static readonly RelayChoiceItem Automatic = new(null, "", null, null, null);

    public RelayChoiceItem(string? id, string name, string? location, double? pingMs, string? notForGame)
    {
        Id = id;
        _notForGame = notForGame;
        _label = LabelFor(id, name, location, pingMs, notForGame);
    }

    /// <summary>The relay id, or null for automatic.</summary>
    public string? Id { get; }

    private string _label;
    public string Label => Id is null ? Loc.T("relay.automatic") : _label;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Says the label again in the language now in use. See <see cref="Automatic"/>.</summary>
    public void RelabelForLanguage() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));

    private string? _notForGame;

    /// <summary>
    /// False for a relay the operator has taken off the game in play (/admin/relays): listed, greyed out and
    /// not selectable, so a player who remembers it can see why it is gone.
    /// </summary>
    public bool IsAvailable => _notForGame is null;

    public void Update(string name, string? location, double? pingMs, string? notForGame)
    {
        var availabilityChanged = notForGame != _notForGame;
        _notForGame = notForGame;
        if (availabilityChanged) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAvailable)));

        var label = LabelFor(Id, name, location, pingMs, notForGame);
        if (label == _label) return;
        _label = label;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
    }

    private static string LabelFor(string? id, string name, string? location, double? pingMs, string? notForGame)
    {
        var place = location is null || name.Contains(location, StringComparison.OrdinalIgnoreCase) ? name : $"{name} ({location})";
        if (id is null) return place;
        if (notForGame is not null) return Loc.F("relay.notForGame", place, notForGame);
        return pingMs is { } ms ? Loc.F("relay.withPing", place, $"{ms:F0}") : Loc.F("relay.noAnswer", place);
    }
}
