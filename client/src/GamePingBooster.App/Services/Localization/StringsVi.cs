namespace GamePingBooster.App.Services.Localization;

/// <summary>
/// The Vietnamese text of the UI. Same keys as <see cref="StringsEn"/>; anything missing here
/// falls back to the English of that table rather than showing a key.
///
/// "Ping" is kept in English: it is what Vietnamese players already say - "độ trễ" is understood but
/// nobody says it about a game. "Game" stays "game" for the same reason.
///
/// A relay is "máy chủ" (2026-09-30, the owner's call: "relay" read as jargon), which makes the game's own
/// server always "máy chủ game" - never a bare "máy chủ" - so the two cannot be taken for each other.
/// </summary>
internal static class StringsVi
{
    internal static readonly IReadOnlyDictionary<string, string> Values = new Dictionary<string, string>
    {
        // ------------------------------------------------------------------ main window
        ["main.menu.tip"] = "Menu",
        ["main.menu.settings"] = "Cài đặt",
        ["main.menu.games"] = "Game được hỗ trợ",
        ["main.menu.reportLag"] = "Báo lag",
        ["main.menu.logs"] = "Mở thư mục log",
        ["main.menu.about"] = "Giới thiệu",
        ["main.account.signIn"] = "Đăng nhập",
        ["main.account.account"] = "Tài khoản",

        ["main.label.gamePing"] = "Ping trong game",
        ["main.label.relayPing"] = "Ping tới máy chủ",
        ["main.label.loss"] = "Mất gói tới máy chủ",
        ["main.label.relay"] = "Máy chủ chính",
        ["main.label.adaptive"] = "Máy chủ khu vực",
        ["main.label.game"] = "Game",
        ["main.label.routing"] = "Định tuyến",
        ["main.label.packets"] = "Gói tin",
        ["main.label.unblock"] = "Mở chặn",
        ["unblock.on"] = "Đã mở",
        ["unblock.onFor"] = "{0}",
        ["unblock.off"] = "Chưa bật",
        ["unblock.tip.on"] = "Những tên miền bị đường mạng này trả về địa chỉ giả sẽ được phân giải qua DNS mã hoá, nên các trang đó vào bình thường. Tải về không bị ảnh hưởng - vẫn đi đường của nhà mạng, vì đường đó nhanh hơn.",
        ["unblock.tip.off"] = "Tính năng mở chặn chưa chạy. Service sẽ tiếp tục thử lại.",
        ["main.games.tip"] = "Xem các game được hỗ trợ",

        ["main.setup.needed"] = "Chưa cấu hình máy chủ. Mở Cài đặt rồi nhập địa chỉ máy chủ và khoá của bạn.",
        ["main.setup.signIn"] = "Đăng nhập để bắt đầu: mở menu (góc trên bên phải) rồi chọn Đăng nhập.",
        ["main.server.label"] = "Máy chủ",
        ["main.server.tip"] =
            "Ping ở đây là ping tới chính máy chủ đó. Chế độ Tự động đo cả đường đi tới game lúc bạn bấm Bắt đầu tăng tốc rồi chọn máy chủ nhanh nhất.",

        ["main.update.available"] = "Đã có bản mới v{0}",
        ["main.update.required"] = "Cần cập nhật lên v{0} để kết nối",
        ["main.update.action"] = "Cập nhật →",

        // ------------------------------------------------------------------ state and values
        ["status.disconnected"] = "Chưa kết nối",
        ["status.connecting"] = "Đang kết nối...",
        ["status.connected"] = "Đã kết nối",
        ["status.reconnecting"] = "Đang kết nối lại...",
        ["status.faulted"] = "Lỗi",
        ["status.unknown"] = "Không rõ",
        ["action.connect"] = "Bắt đầu tăng tốc",
        ["action.connect.hint"] = "Tự nhận game bạn đang chơi, không cần chọn",
        ["action.disconnect"] = "Dừng tăng tốc",

        ["detail.starting"] = "Đang khởi động...",
        ["detail.disconnecting"] = "Đang ngắt kết nối...",
        ["detail.fetchingProfile"] = "Đang lấy danh sách máy chủ mới nhất...",
        ["detail.sending"] = "Đang gửi yêu cầu tới dịch vụ nền...",

        ["value.none"] = "-",
        ["value.ms"] = "{0} ms",
        ["value.msTo"] = "{0} ms tới {1}",
        ["value.lossTo"] = "{0}% tới {1}",
        ["value.ranges"] = "{0} dải IP",
        ["value.packets"] = "{0} gửi / {1} nhận",

        ["game.running"] = "{0} đang chạy",
        ["game.notOpen"] = "{0} chưa mở",
        ["game.noneOpen"] = "Chưa mở game nào - hỗ trợ {0} game",

        ["gamePing.tip.pending"] = "Hiện ra sau khi đo xong kết nối.",
        ["gamePing.tip.measured"] = "Số đo thật: gói thăm dò tới máy chủ game của trận này, đi qua đường hầm.",
        ["gamePing.tip.estimated"] =
            "Số ước tính: ping tới {0} cộng với quãng đường từ đó tới máy chủ game {1}. Game tự chọn máy chủ game của nó - " +
            "đổi máy chủ ở đây là đổi đường đi tới máy chủ game, không đổi được máy chủ game. Khi đang trong trận, số này " +
            "bám theo đúng khu vực game đang dùng, nếu nhận ra được từ traffic của game.",
        ["gamePing.tip.nextMatch"] =
            "Số ước tính cho trận tới. Máy chủ game {1} đi qua {0} nhanh hơn qua {2}, nên trận ở đó sẽ đi qua {0}; sảnh và " +
            "mọi thứ khác vẫn đi qua {2}. Số này là ping tới {0} cộng quãng đường từ đó tới máy chủ game.",
        ["gamePing.tip.theRelay"] = "máy chủ",
        ["loss.tip.pending"] = "Hiện ra sau khi kết nối được vài giây.",
        ["loss.tip"] =
            "Tỷ lệ gói kiểm tra từ máy bạn tới {0} không được trả lời, trong khoảng một phút gần nhất. Đây là chặng từ máy " +
            "bạn tới máy chủ, không phải tới máy chủ game. Máy chủ đang mất gói bị xếp sau máy chủ không mất gói khi app " +
            "chọn, kể cả khi nó ping thấp hơn; nếu có máy chủ tốt hơn, app chuyển sang giữa hai trận.",
        ["gamePing.tip.itsServers"] = "riêng",

        ["relay.automatic"] = "Tự động - nhanh nhất",
        ["relay.withPing"] = "{0} - {1} ms",
        ["relay.noAnswer"] = "{0} - không phản hồi",
        ["relay.notForGame"] = "{0} - không dùng cho {1}",
        ["adaptive.tip"] =
            "Các máy chủ nhanh hơn cho những khu vực khác của game này, app tự đo và đo lại sau mỗi trận: {0}. Khi game xếp " +
            "bạn vào máy chủ game ở một trong các khu vực đó, trận sẽ đi qua máy chủ tương ứng và tên nó chuyển màu xanh. " +
            "Mọi thứ khác vẫn đi qua {1}.",
        ["adaptive.tip.none"] =
            "Các máy chủ nhanh hơn cho những khu vực khác của game sẽ hiện ở đây sau khi app đo xong ở sảnh. Hiện chưa có: " +
            "game này chỉ có một khu vực, hoặc không máy chủ nào nhanh hơn máy chủ bạn đang dùng.",
        ["relay.tip.regionsHome"] = "{0} - máy chủ chính bạn đang kết nối. Trận ở một số khu vực đi qua máy chủ khu vực, nhanh hơn: {1}.",

        ["licence.renewing"] =
            "Đang gia hạn giấy phép... Nếu gói của bạn đã hết, gia hạn trên website là app tự nhận.",
        ["licence.notSignedIn"] = "Chưa đăng nhập",
        ["licence.signedInShipped"] = "Đã đăng nhập - đang dùng danh sách game kèm theo bản cài, chưa phải bản mới nhất",
        ["licence.signedIn"] = "Đã đăng nhập",
        ["licence.expired"] = "Giấy phép đã hết hạn - đang tự gia hạn",
        ["licence.expiresIn"] = "Giấy phép hết hạn sau {0} phút",

        ["pipe.closed"] = "Dịch vụ đã đóng kết nối.",
        ["pipe.unreachable"] = "Không liên lạc được với dịch vụ nền. Kiểm tra xem dịch vụ 'GamePingBooster' có đang chạy không.",
        ["pipe.lost"] = "Mất kết nối tới dịch vụ nền: {0}",

        ["pipe.notConnected"] = "Chưa kết nối tới dịch vụ nền.",

        ["tray.show"] = "Mở Game Ping Booster",
        ["tray.exit"] = "Thoát",
        ["tray.tooltip"] = "Game Ping Booster - {0}",
        ["tray.tooltipWithPing"] = "Game Ping Booster - {0}, {1}",

        // ------------------------------------------------------------------ settings window
        ["settings.title"] = "Cài đặt máy chủ",
        ["settings.intro"] =
            "Địa chỉ các máy chủ của bạn và khoá dùng để cài chúng. Cả hai đều được in ra lúc bạn chạy bộ cài trên VPS.",
        ["settings.relays"] = "Địa chỉ máy chủ",
        ["settings.relays.hint"] =
            "Mỗi dòng một địa chỉ. Nếu có nhiều hơn một, app sẽ đo hết rồi dùng cái nhanh nhất, và tự chuyển sang " +
            "cái khác nếu nó ngừng phản hồi.",
        ["settings.psk"] = "Khoá chia sẻ trước",
        ["settings.psk.placeholderKeep"] = "Để trống nếu giữ nguyên khoá cũ",
        ["settings.psk.placeholderNew"] = "44 ký tự",
        ["settings.psk.hintKeep"] = "Đã có khoá được lưu. App không hiện lại khoá, để trống ô này là giữ nguyên.",
        ["settings.psk.hintNew"] = "Bộ cài máy chủ in ra khoá này, ngay cạnh địa chỉ.",
        ["settings.licence"] = "Máy chủ giấy phép",
        ["settings.licence.hint"] =
            "Nơi tài khoản Game Ping Booster của bạn đăng nhập và nơi lấy danh sách game. Đã được điền sẵn - cứ để nguyên. " +
            "Chỉ xoá đi nếu bạn tự chạy máy chủ riêng với khoá ở trên.",
        ["settings.quality"] = "Gửi chất lượng kết nối sau mỗi trận",
        ["settings.quality.hint"] =
            "Ping, các cú giật và chỗ sinh ra mỗi cú giật đó - mạng nhà bạn, đường tới máy chủ, bản thân máy chủ, hay máy " +
            "chủ game - kèm máy chủ và game đang dùng. Không gửi địa chỉ IP, không gửi gì về các phần mềm khác. Chỉ gửi " +
            "giữa các trận, không bao giờ gửi lúc đang chơi.",
        ["settings.language"] = "Ngôn ngữ",
        ["settings.language.hint"] = "Đổi là áp dụng ngay. Chỉ đổi ngôn ngữ của app, không đụng gì tới game.",
        ["settings.saved"] = "Đã lưu. Bấm Bắt đầu tăng tốc ở cửa sổ chính.",
        ["settings.noService"] = "Dịch vụ nền chưa chạy nên chưa lưu được. Hãy khởi động dịch vụ rồi thử lại.",
        ["settings.serviceUnreachable"] = "Không liên lạc được với dịch vụ nền: {0}",
        ["settings.save"] = "Lưu",
        ["settings.cancel"] = "Huỷ",

        // ------------------------------------------------------------------ update window
        // ------------------------------------------------------------------ closing the window
        ["tray.notice.title"] = "Game Ping Booster vẫn đang chạy",
        ["tray.notice.body"] = "App đang chạy dưới khay và vẫn tăng tốc khi bạn chơi. Bấm vào biểu tượng để mở lại.",
        ["close.window.title"] = "Đóng app",
        ["close.title"] = "Bạn muốn làm gì?",
        ["close.body.tray"] =
            "Thu xuống khay: app vẫn chạy và vẫn tăng tốc khi bạn chơi. Mở lại bằng biểu tượng ở góc phải thanh tác vụ.",
        ["close.body.quit"] = "Thoát: ngắt tăng tốc và tắt hẳn app.",
        ["close.remember"] = "Ghi nhớ lựa chọn này (đổi lại trong Cài đặt)",
        ["close.tray"] = "Thu xuống khay",
        ["close.quit"] = "Thoát",
        ["settings.close"] = "Khi đóng cửa sổ",
        ["settings.close.ask"] = "Hỏi mỗi lần",
        ["settings.close.tray"] = "Thu xuống khay",
        ["settings.close.quit"] = "Thoát app",
        ["settings.close.hint"] = "Áp dụng ngay, không cần bấm Lưu.",

        ["update.window.title"] = "Cập nhật",
        ["update.title"] = "Đã có bản mới v{0}",
        ["update.subtitle"] = "Bạn đang dùng v{0}. Dung lượng tải {1}.",
        ["update.subtitleNoSize"] = "Bạn đang dùng v{0}.",
        ["update.body"] =
            "App sẽ tải bản mới về trước. Sau đó Game Ping Booster đóng lại, cài bản mới rồi tự mở lên. Windows sẽ " +
            "hỏi quyền để cài.",
        ["update.warn.game"] =
            "{0} đang chạy. Nếu bạn đang trong trận thì chơi xong hãy cập nhật: cập nhật sẽ ngắt kết nối, và game " +
            "quay về đường mạng thường cho tới khi bạn kết nối lại.",
        ["update.warn.connected"] =
            "Bạn đang kết nối. Cập nhật sẽ ngắt kết nối trong lúc cài; app mở lại thì bấm Bắt đầu tăng tốc lần nữa.",
        ["update.later"] = "Huỷ",
        ["update.now"] = "Cập nhật ngay",
        ["update.downloading"] = "Đang tải bản mới...",
        ["update.progress"] = "{0} / {1}",
        ["update.mb"] = "{0} MB",
        ["update.cancel"] = "Huỷ",
        ["update.installing.waiting"] = "Đang chờ Windows cho phép cài...",
        ["update.installing.disconnecting"] = "Đang ngắt kết nối...",
        ["update.installing.waitingFull"] =
            "Đang chờ Windows cho phép cài. Game Ping Booster sẽ đóng lại rồi tự mở lên.",
        ["update.releasePage"] = "Mở trang tải bản mới",
        ["update.close"] = "Đóng",
        ["update.fail.download"] =
            "Tải không thành công. Kiểm tra lại mạng rồi thử lại, hoặc tải bộ cài từ trang tải bản mới.",
        ["update.fail.stalled"] = "Quá trình tải bị đứng giữa chừng. Kiểm tra lại mạng rồi thử lại.",
        ["update.fail.mismatch"] =
            "File tải về không khớp với bản phát hành nên đã bị xoá và không cài gì cả. Thử lại, hoặc tải bộ cài " +
            "từ trang tải bản mới.",
        ["update.fail.notStarted"] =
            "Bộ cài không khởi động được. Nếu bạn vừa bấm No lúc Windows hỏi quyền, bấm Cập nhật để thử lại.",
        ["update.fail.installed"] =
            "Bản mới đã được cài. Đóng Game Ping Booster rồi mở lại để dùng bản mới.",
        ["update.fail.service"] =
            "Bản cập nhật không thay được dịch vụ đang chạy. Nếu cửa sổ Services đang mở, đóng nó lại rồi thử lại.",
        ["update.fail.other"] =
            "Chưa cài được bản mới (bộ cài kết thúc với mã {0}). Nếu bạn vừa bấm No lúc Windows hỏi quyền, bấm Cập " +
            "nhật để thử lại. Nếu không, tải bộ cài từ trang tải bản mới.",

        // ------------------------------------------------------------------ from the service
        ["svc.preparing"] = "Đang chuẩn bị...",
        ["svc.measuring"] = "Đang đo các máy chủ...",
        ["svc.creatingAdapter"] = "Đang tạo card mạng ảo...",
        ["svc.connectFailed"] = "Kết nối thất bại",
        ["svc.connectedAccelerating"] = "Đã kết nối tới {0} - đang tăng tốc {1}",
        ["svc.connectedWaiting"] = "Đã kết nối tới {0} - đang chờ {1} khởi động",
        ["svc.connectedWaitingAny"] = "Đã kết nối tới {0} - đang chờ bạn mở một game được hỗ trợ",
        ["svc.movedRelay"] = "Đã kết nối tới {0} - chuyển khỏi {1} để đi đường tốt hơn",
        ["svc.movedForGame"] = "Đã kết nối tới {0} - {1} không dùng cho {2}",
        ["svc.rescanMoved"] = "Đã kết nối tới {0} - chuyển từ {1} giữa hai trận, nhanh hơn {2} ms",
        ["svc.reconnecting"] = "Đang kết nối lại qua {0} (lần {1}) - traffic đang đi đường mạng thường",
        ["svc.reconnected"] = "Đã kết nối lại tới {0}",
        ["svc.accelerating"] = "Đang tăng tốc {0} qua {1}",
        ["svc.fixingClock"] = "Đang chỉnh lại giờ hệ thống...",
        ["svc.clockWrong"] = "Đồng hồ máy này lệch {0} giây và không tự chỉnh được. Hãy chỉnh giờ trong Cài đặt > Thời gian & ngôn ngữ rồi kết nối lại.",
        ["svc.routeFailed"] = "Không cập nhật được bảng định tuyến",
        ["svc.disconnecting"] = "Đang ngắt kết nối...",
        ["svc.notConnected"] = "Chưa kết nối",
        ["svc.idleDisconnect"] =
            "Đã tự ngắt kết nối - {0} phút không mở game nào. Bấm Bắt đầu tăng tốc trước khi chơi.",

        ["choiceNote.nextConnect"] = "Sẽ áp dụng từ lần kết nối sau.",
        ["choiceNote.notForGame"] = "{0} không dùng cho {1} - đang dùng {2}.",
        ["choiceNote.notAnswering"] = "{0} không phản hồi - đang dùng {1} thay thế.",

        ["refusal.notSignedIn"] = "Máy này kết nối qua máy chủ có giấy phép nhưng chưa đăng nhập. Vào menu đăng nhập để lấy giấy phép.",
        ["refusal.expired"] = "Giấy phép đã hết hạn {0}. Gia hạn gói rồi đăng nhập lại - máy chủ không chấp nhận giấy phép hết hạn.",

        // ------------------------------------------------------------------ about window
        ["about.title"] = "Giới thiệu",
        ["about.tagline"] =
            "Đưa traffic của game qua máy chủ tăng tốc, nơi có đường tới máy chủ game tốt hơn đường mà nhà mạng của bạn tự " +
            "chọn.",
        ["about.author"] = "Tác giả",
        ["about.source"] = "Mã nguồn",
        ["about.devBuild"] = "Bản phát triển",
        ["about.version"] = "Phiên bản {0}",

        // ------------------------------------------------------------------ supported games window
        ["games.title"] = "Game được hỗ trợ",
        ["games.intro"] = "App tự nhận ra game - bạn chỉ cần mở game, không phải chọn gì ở đây.",
        ["games.search"] = "Tìm theo tên game hoặc tên tiến trình",
        ["games.badge.running"] = "Đang chạy",
        ["games.badge.lastPlayed"] = "Chơi gần đây",
        ["games.count.one"] = "1 game",
        ["games.count.many"] = "{0} game",
        ["games.loading"] = "Đang tải...",
        ["games.empty"] = "Chưa có danh sách game. Hãy đăng nhập, hoặc đợi danh sách game tải về.",
        ["games.noMatch"] = "Không có game nào khớp với \"{0}\".",

        // ------------------------------------------------------------------ shared buttons
        ["common.close"] = "Đóng",
        ["common.cancel"] = "Huỷ",

        // ------------------------------------------------------------------ sign-in window
        ["login.title"] = "Đăng nhập",
        ["login.browser"] = "Đăng nhập bằng trình duyệt",
        ["login.server"] = "Đăng nhập vào {0}",
        ["login.deviceWithKey"] = "Thiết bị này: {0}... ({1})",
        ["login.device"] = "Thiết bị này: {0}",
        ["login.copy"] = "Chép link",
        ["login.copied"] = "Đã chép",
        ["login.waiting"] = "Hoàn tất đăng nhập trên trình duyệt rồi quay lại đây. Nếu trình duyệt không tự mở, hãy chép link bên dưới vào trình duyệt bất kỳ.",
        ["login.noBrowser"] = "Không mở được trình duyệt trên máy này. Chép link bên dưới, dán vào Chrome, Edge hoặc trình duyệt bất kỳ rồi đăng nhập ở đó.",
        ["login.cancelled"] = "Đã huỷ.",
        ["login.retryBrowserOk"] = "{0} Phần trên trình duyệt đã xong - hãy thử lại.",
        ["login.retry"] = "{0} Hãy thử lại.",

        // ------------------------------------------------------------------ account window
        ["account.title"] = "Tài khoản",
        ["account.loading"] = "Đang tải...",
        ["account.email"] = "Đăng nhập bằng",
        ["account.plan"] = "Gói",
        ["account.status"] = "Trạng thái",
        ["account.expires"] = "Gia hạn / hết hạn",
        ["account.devices"] = "Thiết bị",
        ["account.thisDevice"] = "Thiết bị này",
        ["account.signOut"] = "Đăng xuất",
        ["account.signOutHint"] = "Đăng xuất sẽ gỡ tài khoản này khỏi máy tính và ngắt kết nối nếu đang kết nối. Danh sách game đã tải về vẫn được giữ lại - nó được mã hoá riêng cho máy này và không dùng được ở máy khác.",
        ["account.notSignedIn"] = "Máy này không còn đăng nhập nữa. Đóng cửa sổ này rồi đăng nhập lại.",
        ["account.noPlan"] = "Chưa có gói",
        ["account.state.trial"] = "Dùng thử",
        ["account.state.active"] = "Đang hoạt động",
        ["account.state.pastDue"] = "Quá hạn thanh toán",
        ["account.state.cancelled"] = "Đã huỷ",
        ["account.state.expired"] = "Đã hết hạn",
        ["account.state.none"] = "Chưa đăng ký gói",
        ["account.devicesOf"] = "{0} / {1}",
        ["account.unreachable"] = "Không kết nối được tới {0}: {1}",
        ["account.signOutUnconfirmed"] = "Đã đăng xuất trên máy này, nhưng dịch vụ nền chưa xác nhận: {0}",
        ["format.dateTime"] = "dd/MM/yyyy HH:mm",

        // ------------------------------------------------------------------ licence server answers
        ["licenceErr.emptyGameList"] = "Máy chủ giấy phép trả về danh sách game rỗng.",
        ["licenceErr.emptyAnswer"] = "Máy chủ giấy phép không trả về gì.",
        ["licenceErr.status"] = "Máy chủ giấy phép trả lời mã {0}.",
        ["licenceErr.profileUnauthorized"] = "Hãy đăng nhập lại để cập nhật danh sách game.",
        ["licenceErr.noSubscription"] = "Tài khoản này chưa có gói nào đang hoạt động.",
        ["licenceErr.tooOften"] = "Đã hỏi danh sách game quá nhiều lần. App sẽ tự cập nhật sau.",
        ["licenceErr.accountExpired"] = "Phiên đăng nhập đã hết hạn. Hãy đăng nhập lại.",
        ["licenceErr.signInInvalid"] = "Phiên đăng nhập đó không còn hợp lệ. Hãy đăng nhập lại.",
        ["licenceErr.deviceLimit"] = "Tài khoản này không được thêm thiết bị nữa.",
        ["licenceErr.notFound"] = "Máy chủ giấy phép không nhận ra yêu cầu này. Kiểm tra lại địa chỉ trong Cài đặt.",
        ["licenceErr.timeout"] = "Máy chủ giấy phép không trả lời trong {0} giây.",

        // ------------------------------------------------------------------ browser sign-in (the page in the browser tab, and what the app says)
        ["auth.page.refused.title"] = "Đăng nhập bị từ chối",
        ["auth.page.refused.detail"] = "Bạn có thể đóng tab này và thử lại từ app.",
        ["auth.page.waiting.title"] = "Đang chờ đăng nhập",
        ["auth.page.waiting.detail"] = "Không cần làm gì ở đây. Hãy hoàn tất đăng nhập ở tab kia.",
        ["auth.page.mismatch.title"] = "Lượt đăng nhập không khớp",
        ["auth.page.mismatch.detail"] = "Đóng tab này và bắt đầu lại từ app.",
        ["auth.page.done.title"] = "Đã đăng nhập",
        ["auth.page.done.detail"] = "Bạn có thể đóng tab này và quay lại Game Ping Booster.",
        ["auth.pageReported"] = "Trang đăng nhập báo: {0}",
        ["auth.mismatch"] = "Lượt đăng nhập trả về không phải lượt mà app này vừa bắt đầu. Chưa có gì bị thay đổi. Hãy thử lại, và nếu vẫn bị thì tắt các bản app khác đang mở trước.",
        ["auth.timeout"] = "Trình duyệt không phản hồi trong {0} phút. Nếu không thấy cửa sổ trình duyệt nào mở ra, hãy kiểm tra Windows đã chọn trình duyệt mặc định chưa.",

        // ------------------------------------------------------------------ notices under the Start boosting button
        ["notice.profileFailed"] = "Không cập nhật được danh sách game: {0}",
        ["notice.profileUnreachable"] = "Không kết nối được máy chủ giấy phép để cập nhật danh sách game ({0}).",
        ["notice.minutes.one"] = "1 phút",
        ["notice.minutes.many"] = "{0} phút",
        ["notice.clock.ahead"] =
            "Đồng hồ máy này đang nhanh hơn máy chủ giấy phép khoảng {0} - lệch quá {1} giây là không kết nối được. Cứ bấm Bắt đầu tăng tốc: app sẽ tự chỉnh giờ trước.",
        ["notice.clock.behind"] =
            "Đồng hồ máy này đang chậm hơn máy chủ giấy phép khoảng {0} - lệch quá {1} giây là không kết nối được. Cứ bấm Bắt đầu tăng tốc: app sẽ tự chỉnh giờ trước.",
        ["notice.renewFailed"] = "Không gia hạn được giấy phép: {0}",
        ["notice.clearFailed"] = "Không xoá được giấy phép đã hết hạn ({0}).",
        ["notice.renewUnreachable"] = "Không kết nối được máy chủ giấy phép ({0}). App sẽ thử lại sau ít phút.",
        ["notice.quality"] = "App giờ sẽ gửi chất lượng kết nối sau mỗi trận - ping, các cú giật và chúng xuất phát từ đoạn nào trên đường đi, không kèm địa chỉ nào của bạn - cùng địa chỉ các máy chủ game mà app chưa hỗ trợ, để sửa lag tận gốc. Bạn có thể tắt trong Cài đặt.",

        // ------------------------------------------------------------------ report lag window
        ["lag.title"] = "Báo lag",
        ["lag.subtitle"] = "Hãy chạy NGAY LÚC đang bị lag. Hết lag rồi thì không còn gì để đo.",
        ["lag.intro"] = "Công cụ này đo mọi đoạn của kết nối cùng một lúc, rồi gửi kết quả cho bộ phận hỗ trợ để tìm ra chỗ bị lỗi. Mất khoảng 20 giây.",
        ["lag.sent.header"] = "NHỮNG GÌ ĐƯỢC ĐO VÀ GỬI ĐI",
        ["lag.sent.1"] =
            "• Thời gian phản hồi, độ dao động và tỉ lệ mất gói tới router nhà bạn, tới mạng của nhà mạng, và tới máy chủ.",
        ["lag.sent.2"] =
            "• Đường mạng tới máy chủ, trong đó có địa chỉ nội bộ của router nhà bạn và địa chỉ các router của nhà mạng.",
        ["lag.sent.3"] = "• Địa chỉ IP công khai của bạn, máy chủ đang dùng, phiên bản app, và game có đang chạy hay không.",
        ["lag.sent.4"] = "• Các số ping và mất gói mà app đang hiển thị cho bạn.",
        ["lag.notSent.header"] = "NHỮNG GÌ KHÔNG GỬI ĐI",
        ["lag.notSent.1"] = "• Không gì về việc bạn làm trên mạng — không lịch sử duyệt web, không tên file, không nội dung tin nhắn hay game.",
        ["lag.notSent.2"] = "• Không mật khẩu, không khoá giấy phép, không thông tin thanh toán.",
        ["lag.notSent.3"] = "Kết nối của bạn vẫn giữ nguyên suốt lúc đo. Không ngắt kết nối và không đổi cài đặt nào.",
        ["lag.comment.label"] = "BẠN MUỐN GHI THÊM GÌ (KHÔNG BẮT BUỘC)",
        ["lag.comment.placeholder"] = "ví dụ: cứ vài phút lại giật một lần khi đang trong trận",
        ["lag.consent"] = "Tôi hiểu những gì được đo và gửi đi, và đồng ý gửi.",
        ["lag.start"] = "Đo và gửi",
        ["lag.running"] = "Đang đo. Cứ tiếp tục chơi — mục đích là bắt được lỗi đúng lúc nó xảy ra.",
        ["lag.starting"] = "Đang bắt đầu...",
        ["lag.progress.relay"] = "Đang đo máy chủ...",
        ["lag.progress.trace"] = "Đang dò đường tới máy chủ...",
        ["lag.progress.tick"] = "Đang đo... {0}/{1} giây",
        ["lag.result.failed"] = "Không đo được",
        ["lag.result.clean"] = "Không phát hiện vấn đề",
        ["lag.result.sent"] = "Đã gửi",
        ["lag.result.notSent"] = "Đã đo nhưng chưa gửi",
        ["lag.result.noLicence"] = "Máy này chưa đăng nhập vào máy chủ giấy phép nên không có chỗ để gửi. Hãy chép đoạn này gửi cho người đang hỗ trợ bạn.",
        ["lag.result.thanks"] = "Đã gửi báo cáo. Cảm ơn bạn.",
        ["lag.result.uploadFailed"] = "Không tải báo cáo lên được: {0}",
        ["lag.verdict.clean"] = "Lúc đo, không có đoạn nào trên đường đi bị lỗi. Nếu game vẫn thấy tệ thì vấn đề không nằm trên đường này vào lúc đo - hãy báo lại đúng lúc đang bị.",
        ["lag.verdict.router"] = "Mạng trong nhà là chỗ đầu tiên có vấn đề{0} - Wi-Fi, dây mạng, hoặc chính router. Mọi đoạn phía sau đều bị ảnh hưởng theo.",
        ["lag.verdict.isp-access"] = "Mạng truy nhập của nhà mạng là đoạn đầu tiên bị lỗi{0}. Mạng trong nhà vẫn ổn, nên lỗi nằm ở đường dây vào tới nhà, không phải bên trong nhà bạn.",
        ["lag.verdict.isp-core"] = "Mạng trong nước của nhà mạng là đoạn đầu tiên bị lỗi{0} - ngay trong nước, trước khi ra đường quốc tế.",
        ["lag.verdict.international"] =
            "Mọi đoạn trong nước đều ổn, nhưng đoạn đi ra quốc tế thì không{0}. Đó là đường trung chuyển giữa nhà mạng của " +
            "bạn và khu vực đặt máy chủ, hoặc đường truyền của chính máy chủ - không nằm trên đường dây nhà bạn và không " +
            "sửa được từ phía bạn. Nếu cứ lặp lại, hãy thử một máy chủ đi theo đường khác.",
        ["lag.verdict.relay"] =
            "Đường tới máy chủ là đoạn đầu tiên bị lỗi{0}, trong khi mọi đoạn trong nước đều ổn. Lỗi nằm ở phía ngoài biên " +
            "giới - trên đường ra quốc tế, hoặc ngay ở cửa vào của máy chủ.",
        ["lag.verdict.relay-udp"] =
            "Đường vật lý tới máy chủ vẫn ổn nhưng phản hồi của chính máy chủ thì không{0}. Cả hai số đều là một vòng đi-về " +
            "tới cùng một máy qua cùng một đường, nên chênh lệch không phải do mạng - mà do phần mềm trên máy chủ, hoặc do " +
            "chính máy tính này xử lý chậm. Chạy lại một lần bằng dây mạng thay vì Wi-Fi sẽ phân biệt được hai trường hợp.",
        ["lag.verdict.game"] =
            "Mọi đoạn tới máy chủ đều ổn nhưng ping trong game thì không{0}, nên lỗi nằm sau máy chủ của app - giữa nó và " +
            "máy chủ game.",

    };
}
