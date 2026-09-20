namespace GamePingBooster.App.Services.Localization;

/// <summary>
/// The Vietnamese text of the UI. Same keys as <see cref="StringsEn"/>; anything missing here
/// falls back to the English of that table rather than showing a key.
///
/// Two things kept deliberately in English, because translating them would make the screen harder
/// to read rather than easier: "ping" and "relay". Both are what Vietnamese players already say -
/// "độ trễ" is understood but nobody says it about a game, and a relay is the name of a thing this
/// app owns. "Game" stays "game" for the same reason.
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
        ["main.label.relayPing"] = "Ping tới relay",
        ["main.label.loss"] = "Mất gói",
        ["main.label.relay"] = "Máy chủ relay",
        ["main.label.game"] = "Game",
        ["main.label.routing"] = "Định tuyến",
        ["main.label.packets"] = "Gói tin",
        ["main.label.steam"] = "Steam",
        ["steam.on"] = "Đã mở",
        ["steam.off"] = "chưa bật",
        ["steam.tip.on"] = "Store và community của Steam được phân giải qua DNS mã hoá, vì đường mạng này trả về địa chỉ giả cho chúng. Tải game không bị ảnh hưởng - vẫn đi đường của nhà mạng, vì đường đó nhanh hơn.",
        ["steam.tip.off"] = "Hỗ trợ tên miền Steam chưa chạy. Service sẽ tiếp tục thử lại.",
        ["main.games.tip"] = "Xem các game được hỗ trợ",

        ["main.setup.needed"] =
            "Chưa cấu hình relay. Mở Cài đặt rồi nhập địa chỉ relay và khoá của bạn.",
        ["main.setup.signIn"] = "Đăng nhập để bắt đầu: mở menu (góc trên bên phải) rồi chọn Đăng nhập.",
        ["main.server.label"] = "Máy chủ",
        ["main.server.tip"] =
            "Ping ở đây là ping tới chính máy chủ đó. Chế độ Tự động đo cả đường đi tới game lúc bạn bấm Kết nối rồi chọn máy chủ nhanh nhất.",

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
        ["action.connect"] = "Kết nối",
        ["action.disconnect"] = "Ngắt kết nối",

        ["detail.starting"] = "Đang khởi động...",
        ["detail.disconnecting"] = "Đang ngắt kết nối...",
        ["detail.fetchingProfile"] = "Đang lấy danh sách máy chủ mới nhất...",
        ["detail.sending"] = "Đang gửi yêu cầu tới dịch vụ nền...",

        ["value.none"] = "-",
        ["value.ms"] = "{0} ms",
        ["value.msTo"] = "{0} ms tới {1}",
        ["value.ranges"] = "{0} dải IP",
        ["value.packets"] = "{0} gửi / {1} nhận",

        ["game.running"] = "{0} đang chạy",
        ["game.notOpen"] = "{0} chưa mở",
        ["game.noneOpen"] = "Chưa mở game nào - hỗ trợ {0} game",

        ["gamePing.tip.pending"] = "Hiện ra sau khi đo xong kết nối.",
        ["gamePing.tip.measured"] = "Số đo thật: gói thăm dò tới máy chủ của trận này, đi qua đường hầm.",
        ["gamePing.tip.estimated"] =
            "Số ước tính: ping tới {0} cộng với quãng đường từ đó tới máy chủ {1} của game. Game tự chọn máy chủ " +
            "của nó - đổi relay là đổi đường đi tới máy chủ, không đổi được máy chủ. Khi đang trong trận, số này " +
            "bám theo đúng khu vực game đang dùng, nếu nhận ra được từ traffic của game.",
        ["gamePing.tip.theRelay"] = "relay",
        ["gamePing.tip.itsServers"] = "riêng",

        ["relay.automatic"] = "Tự động - nhanh nhất",
        ["relay.withPing"] = "{0} - {1} ms",
        ["relay.noAnswer"] = "{0} - không phản hồi",
        ["relay.notForGame"] = "{0} - không dùng cho {1}",

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
        ["settings.title"] = "Cài đặt relay",
        ["settings.intro"] =
            "Địa chỉ các relay của bạn và khoá dùng để cài chúng. Cả hai đều được in ra lúc bạn chạy bộ cài relay trên máy chủ.",
        ["settings.relays"] = "Địa chỉ relay",
        ["settings.relays.hint"] =
            "Mỗi dòng một địa chỉ. Nếu có nhiều hơn một, app sẽ đo hết rồi dùng cái nhanh nhất, và tự chuyển sang " +
            "cái khác nếu nó ngừng phản hồi.",
        ["settings.psk"] = "Khoá chia sẻ trước",
        ["settings.psk.placeholderKeep"] = "Để trống nếu giữ nguyên khoá cũ",
        ["settings.psk.placeholderNew"] = "44 ký tự",
        ["settings.psk.hintKeep"] = "Đã có khoá được lưu. App không hiện lại khoá, để trống ô này là giữ nguyên.",
        ["settings.psk.hintNew"] = "Bộ cài relay in ra khoá này, ngay cạnh địa chỉ.",
        ["settings.licence"] = "Máy chủ giấy phép",
        ["settings.licence.hint"] =
            "Nơi tài khoản Game Ping Booster của bạn đăng nhập và nơi lấy danh sách game. Đã được điền sẵn - cứ để " +
            "nguyên. Chỉ xoá đi nếu bạn tự chạy relay riêng với khoá ở trên.",
        ["settings.quality"] = "Gửi chất lượng kết nối sau mỗi trận",
        ["settings.quality.hint"] =
            "Ping, các cú giật và chỗ sinh ra mỗi cú giật đó - mạng nhà bạn, đường tới relay, bản thân relay, hay " +
            "máy chủ game - kèm relay và game đang dùng. Không gửi địa chỉ IP, không gửi gì về các phần mềm khác. " +
            "Chỉ gửi giữa các trận, không bao giờ gửi lúc đang chơi.",
        ["settings.language"] = "Ngôn ngữ",
        ["settings.language.hint"] = "Đổi là áp dụng ngay. Chỉ đổi ngôn ngữ của app, không đụng gì tới game.",
        ["settings.saved"] = "Đã lưu. Bấm Kết nối ở cửa sổ chính.",
        ["settings.noService"] = "Dịch vụ nền chưa chạy nên chưa lưu được. Hãy khởi động dịch vụ rồi thử lại.",
        ["settings.serviceUnreachable"] = "Không liên lạc được với dịch vụ nền: {0}",
        ["settings.save"] = "Lưu",
        ["settings.cancel"] = "Huỷ",

        // ------------------------------------------------------------------ update window
        ["update.window.title"] = "Cập nhật",
        ["update.title"] = "Cập nhật lên v{0}",
        ["update.subtitle"] = "Bạn đang dùng v{0}. Dung lượng tải {1}.",
        ["update.subtitleNoSize"] = "Bạn đang dùng v{0}.",
        ["update.body"] =
            "App sẽ tải bản mới về trước. Sau đó Game Ping Booster đóng lại, cài bản mới rồi tự mở lên. Windows sẽ " +
            "hỏi quyền để cài.",
        ["update.warn.game"] =
            "{0} đang chạy. Nếu bạn đang trong trận thì chơi xong hãy cập nhật: cập nhật sẽ ngắt kết nối, và game " +
            "quay về đường mạng thường cho tới khi bạn kết nối lại.",
        ["update.warn.connected"] =
            "Bạn đang kết nối. Cập nhật sẽ ngắt kết nối trong lúc cài; app mở lại thì bấm Kết nối lần nữa.",
        ["update.later"] = "Để sau",
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
        ["svc.measuring"] = "Đang đo các relay...",
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
            "Đã tự ngắt kết nối - {0} phút không mở game nào. Bấm Kết nối trước khi chơi.",

        ["choiceNote.nextConnect"] = "Sẽ áp dụng từ lần kết nối sau.",
        ["choiceNote.notForGame"] = "{0} không dùng cho {1} - đang dùng {2}.",
        ["choiceNote.notAnswering"] = "{0} không phản hồi - đang dùng {1} thay thế.",

        ["refusal.notSignedIn"] =
            "Máy này kết nối qua relay có giấy phép nhưng chưa đăng nhập. Vào menu đăng nhập để lấy giấy phép.",
        ["refusal.expired"] =
            "Giấy phép đã hết hạn {0}. Gia hạn gói rồi đăng nhập lại - relay không chấp nhận giấy phép hết hạn.",

        // ------------------------------------------------------------------ about window
        ["about.title"] = "Giới thiệu",
        ["about.tagline"] = "Đưa traffic của game qua một relay ở nước ngoài, nơi có đường tới máy chủ game tốt hơn đường mà nhà mạng của bạn tự chọn.",
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

        // ------------------------------------------------------------------ notices under the Connect button
        ["notice.profileFailed"] = "Không cập nhật được danh sách game: {0}",
        ["notice.profileUnreachable"] = "Không kết nối được máy chủ giấy phép để cập nhật danh sách game ({0}).",
        ["notice.minutes.one"] = "1 phút",
        ["notice.minutes.many"] = "{0} phút",
        ["notice.clock.ahead"] =
            "Đồng hồ máy này đang nhanh hơn máy chủ giấy phép khoảng {0} - relay từ chối nếu lệch quá {1} giây. Cứ bấm Kết nối: app sẽ tự chỉnh giờ trước.",
        ["notice.clock.behind"] =
            "Đồng hồ máy này đang chậm hơn máy chủ giấy phép khoảng {0} - relay từ chối nếu lệch quá {1} giây. Cứ bấm Kết nối: app sẽ tự chỉnh giờ trước.",
        ["notice.renewFailed"] = "Không gia hạn được giấy phép: {0}",
        ["notice.clearFailed"] = "Không xoá được giấy phép đã hết hạn ({0}).",
        ["notice.renewUnreachable"] = "Không kết nối được máy chủ giấy phép ({0}). App sẽ thử lại sau ít phút.",
        ["notice.quality"] = "App giờ sẽ gửi chất lượng kết nối sau mỗi trận - ping, các cú giật và chúng xuất phát từ đoạn nào trên đường đi, không kèm địa chỉ nào của bạn - cùng địa chỉ các máy chủ game mà app chưa hỗ trợ, để sửa lag tận gốc. Bạn có thể tắt trong Cài đặt.",

        // ------------------------------------------------------------------ report lag window
        ["lag.title"] = "Báo lag",
        ["lag.subtitle"] = "Hãy chạy NGAY LÚC đang bị lag. Hết lag rồi thì không còn gì để đo.",
        ["lag.intro"] = "Công cụ này đo mọi đoạn của kết nối cùng một lúc, rồi gửi kết quả cho bộ phận hỗ trợ để tìm ra chỗ bị lỗi. Mất khoảng 20 giây.",
        ["lag.sent.header"] = "NHỮNG GÌ ĐƯỢC ĐO VÀ GỬI ĐI",
        ["lag.sent.1"] = "• Thời gian phản hồi, độ dao động và tỉ lệ mất gói tới router nhà bạn, tới mạng của nhà mạng, và tới relay.",
        ["lag.sent.2"] = "• Đường mạng tới relay, trong đó có địa chỉ nội bộ của router nhà bạn và địa chỉ các router của nhà mạng.",
        ["lag.sent.3"] = "• Địa chỉ IP công khai của bạn, relay đang dùng, phiên bản app, và game có đang chạy hay không.",
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
        ["lag.progress.relay"] = "Đang đo relay...",
        ["lag.progress.trace"] = "Đang dò đường tới relay...",
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
        ["lag.verdict.international"] = "Mọi đoạn trong nước đều ổn, nhưng đoạn đi ra quốc tế thì không{0}. Đó là đường trung chuyển giữa nhà mạng của bạn và khu vực đặt relay, hoặc đường truyền của chính relay - không nằm trên đường dây nhà bạn và không sửa được từ phía bạn. Nếu cứ lặp lại, hãy thử một relay đi theo đường khác.",
        ["lag.verdict.relay"] = "Đường tới relay là đoạn đầu tiên bị lỗi{0}, trong khi mọi đoạn trong nước đều ổn. Lỗi nằm ở phía ngoài biên giới - trên đường ra quốc tế, hoặc ngay ở cửa vào của relay.",
        ["lag.verdict.relay-udp"] = "Đường vật lý tới relay vẫn ổn nhưng phản hồi của chính relay thì không{0}. Cả hai số đều là một vòng đi-về tới cùng một máy qua cùng một đường, nên chênh lệch không phải do mạng - mà do tiến trình relay, hoặc do chính máy tính này xử lý chậm. Chạy lại một lần bằng dây mạng thay vì Wi-Fi sẽ phân biệt được hai trường hợp.",
        ["lag.verdict.game"] = "Mọi đoạn tới relay đều ổn nhưng ping trong game thì không{0}, nên lỗi nằm sau relay - giữa relay và máy chủ game.",

    };
}
