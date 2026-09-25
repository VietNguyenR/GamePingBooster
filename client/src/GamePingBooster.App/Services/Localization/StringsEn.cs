namespace GamePingBooster.App.Services.Localization;

/// <summary>
/// The English text of the UI, and the reference every other language is checked against: a key
/// missing from another table falls back to here, so this one is the complete set.
///
/// Keys are grouped by where the words appear, not by what they mean - a translator works through
/// one screen at a time. "svc." is the exception: those are the messages the SERVICE produces. It
/// runs as LocalSystem and has no idea which language the person chose, so it sends a code and its
/// arguments over the pipe and the words are chosen here. See TunnelEngine.SetState.
/// </summary>
internal static class StringsEn
{
    internal static readonly IReadOnlyDictionary<string, string> Values = new Dictionary<string, string>
    {
        // ------------------------------------------------------------------ main window
        ["main.menu.tip"] = "Menu",
        ["main.menu.settings"] = "Settings",
        ["main.menu.games"] = "Supported games",
        ["main.menu.reportLag"] = "Report lag",
        ["main.menu.logs"] = "Open logs folder",
        ["main.menu.about"] = "About",
        ["main.account.signIn"] = "Sign in",
        ["main.account.account"] = "Account",

        ["main.label.gamePing"] = "Ping in game",
        ["main.label.relayPing"] = "Ping to relay",
        ["main.label.loss"] = "Packet loss",
        ["main.label.relay"] = "Relay server",
        ["main.label.game"] = "Game",
        ["main.label.routing"] = "Routing",
        ["main.label.packets"] = "Packets",
        ["main.label.unblock"] = "Unblocked",
        ["unblock.on"] = "On",
        ["unblock.onFor"] = "{0}",
        ["unblock.off"] = "Not enabled",
        ["unblock.tip.on"] = "Names this connection answers falsely are resolved over encrypted DNS instead, so these sites load normally. Downloads are not affected - they stay on your provider's own path, which is faster.",
        ["unblock.tip.off"] = "Name unblocking is not running. The service will keep trying.",
        ["main.games.tip"] = "See supported games",

        ["main.setup.needed"] =
            "No relay configured yet. Open Settings and enter your relay's address and key.",
        ["main.setup.signIn"] = "Sign in to start: open the menu (top right) and choose Sign in.",
        ["main.server.label"] = "Server",
        ["main.server.tip"] =
            "Ping is to the server itself. Automatic measures the whole way to the game when you connect and takes the fastest.",

        ["main.update.available"] = "New version v{0} available",
        ["main.update.required"] = "Update to v{0} to connect again",
        ["main.update.action"] = "Update →",

        // ------------------------------------------------------------------ state and values
        ["status.disconnected"] = "Not connected",
        ["status.connecting"] = "Connecting...",
        ["status.connected"] = "Connected",
        ["status.reconnecting"] = "Reconnecting...",
        ["status.faulted"] = "Error",
        ["status.unknown"] = "Unknown",
        ["action.connect"] = "Connect",
        ["action.disconnect"] = "Disconnect",

        ["detail.starting"] = "Starting up...",
        ["detail.disconnecting"] = "Disconnecting...",
        ["detail.fetchingProfile"] = "Getting the latest server list...",
        ["detail.sending"] = "Sending the request to the background service...",

        ["value.none"] = "-",
        ["value.ms"] = "{0} ms",
        ["value.msTo"] = "{0} ms to {1}",
        ["value.ranges"] = "{0} ranges",
        ["value.packets"] = "{0} up / {1} down",

        ["game.running"] = "{0} is running",
        ["game.notOpen"] = "{0} is not open",
        ["game.noneOpen"] = "No game open - {0} supported",

        ["gamePing.tip.pending"] = "Appears once the connection has been measured.",
        ["gamePing.tip.measured"] = "Measured: echoes to this match's game server, through the tunnel.",
        ["gamePing.tip.estimated"] =
            "Estimated: the ping to {0} plus the distance from there to the game's {1} servers. The game picks " +
            "its own server - changing the relay changes the route to it, not the server. In a match it follows " +
            "the region the game actually uses, where that can be told from its traffic.",
        ["gamePing.tip.theRelay"] = "the relay",
        ["gamePing.tip.itsServers"] = "own",

        ["relay.automatic"] = "Automatic - the fastest",
        ["relay.withPing"] = "{0} - {1} ms",
        ["relay.noAnswer"] = "{0} - no answer",
        ["relay.notForGame"] = "{0} - not used for {1}",
        ["relay.tip.regionsHome"] = "{0} - the relay you are connected to. Matches in some regions go through their own, faster relay: {1}.",
        ["relay.tip.regionsMatch"] = "This match goes through {0}, the faster relay for its region. Everything else goes through {1}. By region: {2}.",

        ["licence.renewing"] =
            "Renewing the licence... If your plan has ended, renew it on the website and it is picked up here.",
        ["licence.notSignedIn"] = "Not signed in",
        ["licence.signedInShipped"] = "Signed in - using the installed game list, not the current one",
        ["licence.signedIn"] = "Signed in",
        ["licence.expired"] = "Licence expired - renewing it automatically",
        ["licence.expiresIn"] = "Licence expires in {0} min",

        ["pipe.closed"] = "The service closed the connection.",
        ["pipe.unreachable"] = "Could not reach the background service. Check that the 'GamePingBooster' service is running.",
        ["pipe.lost"] = "Lost connection to the background service: {0}",

        ["pipe.notConnected"] = "Not connected to the background service.",

        ["tray.show"] = "Show Game Ping Booster",
        ["tray.exit"] = "Exit",
        ["tray.tooltip"] = "Game Ping Booster - {0}",
        ["tray.tooltipWithPing"] = "Game Ping Booster - {0}, {1}",

        // ------------------------------------------------------------------ settings window
        ["settings.title"] = "Relay settings",
        ["settings.intro"] =
            "The addresses of your relays and the key they were installed with. Both are printed by the relay's " +
            "installer when you set up a server.",
        ["settings.relays"] = "Relay addresses",
        ["settings.relays.hint"] =
            "One per line. With more than one, the app measures them all and uses the fastest, and falls back to " +
            "the others if that one stops answering.",
        ["settings.psk"] = "Pre-shared key",
        ["settings.psk.placeholderKeep"] = "Leave blank to keep the current key",
        ["settings.psk.placeholderNew"] = "44 characters",
        ["settings.psk.hintKeep"] = "A key is already saved. It is not shown here, and leaving this blank keeps it.",
        ["settings.psk.hintNew"] = "Printed by the relay's installer, next to the endpoint.",
        ["settings.licence"] = "Licence server",
        ["settings.licence.hint"] =
            "Where your Game Ping Booster account signs in, and where the game list comes from. Already filled in - " +
            "leave it as it is. Clear it only if you run your own relays with the key above.",
        ["settings.quality"] = "Send connection quality after each match",
        ["settings.quality.hint"] =
            "Ping, lag spikes and which part of the path each one came from - your network, the route to the relay, " +
            "the relay, or the game server - plus the relay and game used. No IP addresses and nothing about other " +
            "programs. Sent between matches, never during one.",
        ["settings.language"] = "Language",
        ["settings.language.hint"] = "Applies immediately. Only this app changes; the game is untouched.",
        ["settings.saved"] = "Saved. Press Connect in the main window.",
        ["settings.noService"] = "The service is not running, so there is nothing to save to. Start it and try again.",
        ["settings.serviceUnreachable"] = "Could not reach the service: {0}",
        ["settings.save"] = "Save",
        ["settings.cancel"] = "Cancel",

        // ------------------------------------------------------------------ update window
        ["update.window.title"] = "Update",
        ["update.title"] = "Update to v{0}",
        ["update.subtitle"] = "You have v{0}. Download size {1}.",
        ["update.subtitleNoSize"] = "You have v{0}.",
        ["update.body"] =
            "The new version is downloaded first. Then Game Ping Booster closes, installs it, and opens again by " +
            "itself. Windows will ask for permission to install.",
        ["update.warn.game"] =
            "{0} is running. If you are in a match, finish it first: updating disconnects you, and the game goes " +
            "back to your normal internet route until you connect again.",
        ["update.warn.connected"] =
            "You are connected. The update will disconnect you while it installs; press Connect again when the app reopens.",
        ["update.later"] = "Later",
        ["update.now"] = "Update now",
        ["update.downloading"] = "Downloading the new version...",
        ["update.progress"] = "{0} / {1}",
        ["update.mb"] = "{0} MB",
        ["update.cancel"] = "Cancel",
        ["update.installing.waiting"] = "Waiting for Windows permission to install...",
        ["update.installing.disconnecting"] = "Disconnecting...",
        ["update.installing.waitingFull"] =
            "Waiting for Windows permission to install. Game Ping Booster will close and open again by itself.",
        ["update.releasePage"] = "Open release page",
        ["update.close"] = "Close",
        ["update.fail.download"] =
            "The download failed. Check your internet connection and try again, or download the installer from the release page.",
        ["update.fail.stalled"] = "The download stopped receiving data. Check your internet connection and try again.",
        ["update.fail.mismatch"] =
            "The downloaded file does not match the release, so it was deleted and nothing was installed. Try again, " +
            "or download the installer from the release page.",
        ["update.fail.notStarted"] =
            "The installer did not start. If you answered No when Windows asked for permission, press Update to try again.",
        ["update.fail.installed"] =
            "The update is installed. Close Game Ping Booster and open it again to use the new version.",
        ["update.fail.service"] =
            "The update could not replace the running service. If the Services window is open, close it and try again.",
        ["update.fail.other"] =
            "The update was not installed (setup ended with code {0}). If you answered No when Windows asked for " +
            "permission, press Update to try again. Otherwise, download the installer from the release page.",

        // ------------------------------------------------------------------ from the service
        ["svc.preparing"] = "Preparing...",
        ["svc.measuring"] = "Measuring relays...",
        ["svc.creatingAdapter"] = "Creating the virtual adapter...",
        ["svc.connectFailed"] = "Connection failed",
        ["svc.connectedAccelerating"] = "Connected to {0} - accelerating {1}",
        ["svc.connectedWaiting"] = "Connected to {0} - waiting for {1} to start",
        ["svc.connectedWaitingAny"] = "Connected to {0} - waiting for a supported game to start",
        ["svc.movedRelay"] = "Connected to {0} - moved off {1} for a better route",
        ["svc.movedForGame"] = "Connected to {0} - {1} is not used for {2}",
        ["svc.rescanMoved"] = "Connected to {0} - moved from {1} between matches, {2} ms faster",
        ["svc.reconnecting"] = "Reconnecting via {0} (attempt {1}) - traffic is on the normal path",
        ["svc.reconnected"] = "Reconnected to {0}",
        ["svc.accelerating"] = "Accelerating {0} through {1}",
        ["svc.fixingClock"] = "Correcting the system clock...",
        ["svc.clockWrong"] = "This PC's clock is {0} seconds off and could not be corrected automatically. Set the time in Settings > Time & language, then connect again.",
        ["svc.routeFailed"] = "Failed to update the routing table",
        ["svc.disconnecting"] = "Disconnecting...",
        ["svc.notConnected"] = "Not connected",
        ["svc.idleDisconnect"] =
            "Disconnected automatically - no game was open for {0} minutes. Press Connect before you play.",

        ["choiceNote.nextConnect"] = "Applies the next time you connect.",
        ["choiceNote.notForGame"] = "{0} is not used for {1} - using {2}.",
        ["choiceNote.notAnswering"] = "{0} is not answering - using {1} instead.",

        ["refusal.notSignedIn"] =
            "This installation connects through a licensed relay and is not signed in. Sign in from the menu to get a licence.",
        ["refusal.expired"] =
            "The licence expired {0}. Renew the subscription and sign in again - the relay will not accept an expired licence.",

        // ------------------------------------------------------------------ about window
        ["about.title"] = "About",
        ["about.tagline"] = "Sends a game's traffic through a relay abroad whose route to the game servers beats the one your ISP picks.",
        ["about.author"] = "Author",
        ["about.source"] = "Source code",
        ["about.devBuild"] = "Development build",
        ["about.version"] = "Version {0}",

        // ------------------------------------------------------------------ supported games window
        ["games.title"] = "Supported games",
        ["games.intro"] = "Detected automatically - just open the game. Nothing to choose here.",
        ["games.search"] = "Search by game or process name",
        ["games.badge.running"] = "Running",
        ["games.badge.lastPlayed"] = "Last played",
        ["games.count.one"] = "1 game",
        ["games.count.many"] = "{0} games",
        ["games.loading"] = "Loading...",
        ["games.empty"] = "No game list yet. Sign in, or wait for the game list to download.",
        ["games.noMatch"] = "No game matches \"{0}\".",

        // ------------------------------------------------------------------ shared buttons
        ["common.close"] = "Close",
        ["common.cancel"] = "Cancel",

        // ------------------------------------------------------------------ sign-in window
        ["login.title"] = "Sign in",
        ["login.browser"] = "Sign in with your browser",
        ["login.server"] = "Signing in to {0}",
        ["login.deviceWithKey"] = "This device: {0}... ({1})",
        ["login.device"] = "This device: {0}",
        ["login.copy"] = "Copy link",
        ["login.copied"] = "Copied",
        ["login.waiting"] = "Finish signing in in your browser, then come back here. If it did not open, copy the link below into any browser.",
        ["login.noBrowser"] = "Couldn't open a browser on this PC. Copy the link below, paste it into Chrome, Edge or any browser, and finish signing in there.",
        ["login.cancelled"] = "Cancelled.",
        ["login.retryBrowserOk"] = "{0} The browser part worked - try again.",
        ["login.retry"] = "{0} Try again.",

        // ------------------------------------------------------------------ account window
        ["account.title"] = "Account",
        ["account.loading"] = "Loading...",
        ["account.email"] = "Signed in as",
        ["account.plan"] = "Plan",
        ["account.status"] = "Status",
        ["account.expires"] = "Renews / expires",
        ["account.devices"] = "Devices",
        ["account.thisDevice"] = "This device",
        ["account.signOut"] = "Sign out",
        ["account.signOutHint"] = "Signing out removes this account from this computer and disconnects if the tunnel is up. The game list already downloaded is kept - it is encrypted to this PC and useless anywhere else.",
        ["account.notSignedIn"] = "This machine is not signed in any more. Close this and sign in again.",
        ["account.noPlan"] = "No plan",
        ["account.state.trial"] = "Trial",
        ["account.state.active"] = "Active",
        ["account.state.pastDue"] = "Payment overdue",
        ["account.state.cancelled"] = "Cancelled",
        ["account.state.expired"] = "Expired",
        ["account.state.none"] = "No subscription",
        ["account.devicesOf"] = "{0} of {1}",
        ["account.unreachable"] = "Could not reach {0}: {1}",
        ["account.signOutUnconfirmed"] = "Signed out on this machine, but the service did not confirm: {0}",
        ["format.dateTime"] = "MM/dd/yyyy HH:mm",

        // ------------------------------------------------------------------ licence server answers
        ["licenceErr.emptyGameList"] = "The licence server sent an empty game list.",
        ["licenceErr.emptyAnswer"] = "The licence server sent an empty answer.",
        ["licenceErr.status"] = "The licence server answered {0}.",
        ["licenceErr.profileUnauthorized"] = "Sign in again to update the game list.",
        ["licenceErr.noSubscription"] = "This account has no active subscription.",
        ["licenceErr.tooOften"] = "Asked for the game list too often. It will update later.",
        ["licenceErr.accountExpired"] = "This sign-in has expired. Sign in again.",
        ["licenceErr.signInInvalid"] = "That sign-in is no longer valid. Sign in again.",
        ["licenceErr.deviceLimit"] = "This account is not allowed to add another device.",
        ["licenceErr.notFound"] = "The licence server does not recognise this request. Check the address in settings.",
        ["licenceErr.timeout"] = "The licence server did not answer within {0} seconds.",

        // ------------------------------------------------------------------ browser sign-in (the page in the browser tab, and what the app says)
        ["auth.page.refused.title"] = "Sign-in was refused",
        ["auth.page.refused.detail"] = "You can close this tab and try again from the app.",
        ["auth.page.waiting.title"] = "Waiting for sign-in",
        ["auth.page.waiting.detail"] = "Nothing to do here. Finish signing in on the other tab.",
        ["auth.page.mismatch.title"] = "That sign-in did not match",
        ["auth.page.mismatch.detail"] = "Close this tab and start again from the app.",
        ["auth.page.done.title"] = "Signed in",
        ["auth.page.done.detail"] = "You can close this tab and go back to Game Ping Booster.",
        ["auth.pageReported"] = "The sign-in page reported: {0}",
        ["auth.mismatch"] = "The sign-in that came back is not the one this app started. Nothing was changed. Try again, and if it keeps happening close any other copy of the app first.",
        ["auth.timeout"] = "No answer from the browser within {0} minutes. If no browser window opened, check that Windows has a default browser set.",

        // ------------------------------------------------------------------ notices under the Connect button
        ["notice.profileFailed"] = "Could not update the game list: {0}",
        ["notice.profileUnreachable"] = "Could not reach the licence server to update the game list ({0}).",
        ["notice.minutes.one"] = "1 minute",
        ["notice.minutes.many"] = "{0} minutes",
        ["notice.clock.ahead"] =
            "This PC's clock is about {0} ahead of the licence server - relays refuse anything more than {1} seconds out. Press Connect: the time is corrected automatically first.",
        ["notice.clock.behind"] =
            "This PC's clock is about {0} behind the licence server - relays refuse anything more than {1} seconds out. Press Connect: the time is corrected automatically first.",
        ["notice.renewFailed"] = "Could not renew the licence: {0}",
        ["notice.clearFailed"] = "Could not clear the expired licence ({0}).",
        ["notice.renewUnreachable"] = "Could not reach the licence server ({0}). Will try again shortly.",
        ["notice.quality"] = "The app now sends connection quality after each match - ping, lag spikes and which part of the path they came from, none of your addresses - and the address of any game server we do not cover yet, so lag can be fixed at its source. Turn it off in Settings.",

        // ------------------------------------------------------------------ report lag window
        ["lag.title"] = "Report lag",
        ["lag.subtitle"] = "Run this WHILE the problem is happening. It has nothing to look at afterwards.",
        ["lag.intro"] = "This measures every part of the connection at the same moment, then sends the result to support so the fault can be located. It takes about 20 seconds.",
        ["lag.sent.header"] = "WHAT IS MEASURED AND SENT",
        ["lag.sent.1"] = "• Response time, jitter and packet loss to your home router, your internet provider's network, and the relay.",
        ["lag.sent.2"] = "• The network path to the relay, which includes your router's local address and your provider's router addresses.",
        ["lag.sent.3"] = "• Your public IP address, the relay in use, the app version, and whether the game is running.",
        ["lag.sent.4"] = "• The ping and packet loss figures the app is already showing you.",
        ["lag.notSent.header"] = "WHAT IS NOT SENT",
        ["lag.notSent.1"] = "• Nothing about what you do online — no browsing, no file names, no message or game content.",
        ["lag.notSent.2"] = "• No password, no licence key, no payment detail.",
        ["lag.notSent.3"] = "Your connection stays up throughout. Nothing is disconnected and no setting is changed.",
        ["lag.comment.label"] = "ANYTHING YOU WANT TO ADD (OPTIONAL)",
        ["lag.comment.placeholder"] = "e.g. it spikes every few minutes during a match",
        ["lag.consent"] = "I understand what is measured and sent, and I agree to send it.",
        ["lag.start"] = "Measure and send",
        ["lag.running"] = "Measuring. Keep playing — the point is to catch the problem as it happens.",
        ["lag.starting"] = "Starting...",
        ["lag.progress.relay"] = "Measuring the relay...",
        ["lag.progress.trace"] = "Tracing the path to the relay...",
        ["lag.progress.tick"] = "Measuring... {0}/{1}s",
        ["lag.result.failed"] = "Could not measure",
        ["lag.result.clean"] = "Nothing found",
        ["lag.result.sent"] = "Sent",
        ["lag.result.notSent"] = "Measured, but not sent",
        ["lag.result.noLicence"] = "This installation is not signed in to a licence server, so there was nowhere to send it. Copy this text to whoever is helping you.",
        ["lag.result.thanks"] = "The report has been sent. Thank you.",
        ["lag.result.uploadFailed"] = "The report could not be uploaded: {0}",
        ["lag.verdict.clean"] = "Nothing on the path is misbehaving right now. If the game still felt bad, the problem was not on this path at the moment this ran - report again while it is happening.",
        ["lag.verdict.router"] = "The home network is the first thing that looks wrong{0} - Wi-Fi, the cable, or the router itself. Everything past it inherits this.",
        ["lag.verdict.isp-access"] = "The ISP's access network is the first bad rung{0}. The home network is clean, so this is the line into the building, not the house.",
        ["lag.verdict.isp-core"] = "The ISP's domestic network is the first bad rung{0} - inside the country, before any international link.",
        ["lag.verdict.international"] = "Everything inside the country is clean, and the leg out of it is not{0}. That is the transit between your ISP and the relay's region, or the relay's own uplink - neither is on your line and neither is fixed from this end. If it keeps happening, a relay reached by a different route is the thing to try.",
        ["lag.verdict.relay"] = "The path to the relay is the first bad rung{0}, while everything inside the country is clean. The fault is past the border - on the way out of the country, or at the relay's own front door.",
        ["lag.verdict.relay-udp"] = "The physical path to the relay is clean but the relay's own answers are not{0}. Both numbers are a round trip to the same machine over the same wire, so the difference is not the network - it is the relay process, or this PC's own scheduling. Running this once on a cable instead of Wi-Fi tells the two apart.",
        ["lag.verdict.game"] = "Everything up to the relay is clean and the game ping is not{0}, so the fault is past the relay - between it and the game server.",

    };
}
