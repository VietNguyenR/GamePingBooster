namespace GamePingBooster.Updater;

/// <summary>
/// Every word in the window, Vietnamese and English - the app passes its own language with --lang. The VI text
/// follows the app's wording: the relay is "máy chủ", the background part of the app is "dịch vụ".
/// </summary>
internal sealed class Strings
{
    public static Strings For(string? lang) => lang is not null && lang.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? English : Vietnamese;

    public required string AppName { get; init; }

    // Titles
    public required string Checking { get; init; }
    public required string Downloading { get; init; }
    public required string Verifying { get; init; }
    public required string Preparing { get; init; }
    public required string Installing { get; init; }       // {0} version
    public required string Restarting { get; init; }
    public required string Updated { get; init; }          // {0} version
    public required string Latest { get; init; }
    public required string NotNow { get; init; }
    public required string DownloadFailed { get; init; }
    public required string SignatureFailed { get; init; }
    public required string SetupFailed { get; init; }

    // Detail lines
    public required string CheckingDetail { get; init; }
    public required string DownloadDetail { get; init; }   // {0} done MB, {1} total MB, {2} speed
    public required string DownloadDetailNoTotal { get; init; } // {0} done MB, {1} speed
    public required string VerifyingDetail { get; init; }
    public required string StartingSetup { get; init; }
    public required string ClosingApp { get; init; }
    public required string StoppingService { get; init; }
    public required string Copying { get; init; }          // {0} percent
    public required string RegisteringService { get; init; }
    public required string StartingService { get; init; }
    public required string InstallingDriver { get; init; }
    public required string Firewall { get; init; }
    public required string WaitingService { get; init; }
    public required string Opening { get; init; }
    public required string LatestDetail { get; init; }     // {0} version
    public required string ReopeningOld { get; init; }
    public required string TryLater { get; init; }

    // Reasons nothing is installed now
    public required string ReasonBusy { get; init; }
    public required string ReasonGame { get; init; }
    public required string ReasonLater { get; init; }
    public required string ReasonUnavailable { get; init; }
    public required string ReasonRefused { get; init; }
    public required string ReasonOff { get; init; }
    public required string ReasonSetup { get; init; }
    public required string ReasonTimeout { get; init; }

    // Steps
    public required string StepCheck { get; init; }
    public required string StepDownload { get; init; }
    public required string StepVerify { get; init; }
    public required string StepStop { get; init; }
    public required string StepCopy { get; init; }
    public required string StepConfigure { get; init; }
    public required string StepRestart { get; init; }
    public required string StepOpen { get; init; }

    public required string FooterInstalling { get; init; }
    public required string FooterDownloading { get; init; }
    public required string FooterFailed { get; init; }

    public string[] Steps => [StepCheck, StepDownload, StepVerify, StepStop, StepCopy, StepConfigure, StepRestart, StepOpen];

    public static readonly Strings Vietnamese = new()
    {
        AppName = "Game Ping Booster",
        Checking = "Đang kiểm tra bản cập nhật",
        Downloading = "Đang tải bản cập nhật",
        Verifying = "Đang xác minh bản cập nhật",
        Preparing = "Đang chuẩn bị cài đặt",
        Installing = "Đang cài đặt v{0}",
        Restarting = "Đang khởi động lại dịch vụ",
        Updated = "Đã cập nhật lên v{0}",
        Latest = "Bạn đang dùng bản mới nhất",
        NotNow = "Chưa cài bản cập nhật lúc này",
        DownloadFailed = "Không tải được bản cập nhật",
        SignatureFailed = "Bản cập nhật không hợp lệ",
        SetupFailed = "Cài đặt không thành công",

        CheckingDetail = "Đang tìm phiên bản mới…",
        DownloadDetail = "{0} / {1} MB · {2}",
        DownloadDetailNoTotal = "{0} MB · {1}",
        VerifyingDetail = "Kiểm tra chữ ký số của nhà phát hành",
        StartingSetup = "Đang khởi chạy trình cài đặt…",
        ClosingApp = "Đang đóng ứng dụng…",
        StoppingService = "Đang dừng dịch vụ Game Ping Booster…",
        Copying = "Đang chép tệp mới · {0}%",
        RegisteringService = "Đang đăng ký dịch vụ…",
        StartingService = "Đang khởi động dịch vụ…",
        InstallingDriver = "Đang cài driver mạng ảo…",
        Firewall = "Đang cấu hình tường lửa…",
        WaitingService = "Chờ dịch vụ sẵn sàng…",
        Opening = "Đang mở Game Ping Booster…",
        LatestDetail = "Phiên bản v{0}",
        ReopeningOld = "Đang mở lại phiên bản hiện tại…",
        TryLater = "Sẽ thử lại sau. Đang mở ứng dụng…",

        ReasonBusy = "Đang tăng tốc - bản cập nhật sẽ cài khi bạn tắt.",
        ReasonGame = "Có game đang chạy - bản cập nhật sẽ cài sau khi bạn chơi xong.",
        ReasonLater = "Lần cài trước chưa thành công - sẽ thử lại sau.",
        ReasonUnavailable = "Chưa kiểm tra được bản cập nhật - sẽ thử lại sau.",
        ReasonRefused = "Bản này cần cài thủ công - ứng dụng sẽ hướng dẫn bạn.",
        ReasonOff = "Tự động cập nhật đang tắt trên máy này.",
        ReasonSetup = "Trình cài đặt dừng giữa chừng.",
        ReasonTimeout = "Trình cài đặt không phản hồi.",

        StepCheck = "Kiểm tra bản cập nhật",
        StepDownload = "Tải bản cập nhật",
        StepVerify = "Xác minh chữ ký số",
        StepStop = "Đóng ứng dụng và dừng dịch vụ",
        StepCopy = "Cài đặt tệp mới",
        StepConfigure = "Cấu hình dịch vụ và driver mạng",
        StepRestart = "Khởi động lại dịch vụ",
        StepOpen = "Mở ứng dụng",

        FooterInstalling = "Không cần làm gì thêm - ứng dụng sẽ tự mở lại khi xong.",
        FooterDownloading = "Bạn có thể đợi một chút, việc cài đặt diễn ra tự động.",
        FooterFailed = "Phiên bản hiện tại vẫn dùng bình thường.",
    };

    public static readonly Strings English = new()
    {
        AppName = "Game Ping Booster",
        Checking = "Checking for updates",
        Downloading = "Downloading the update",
        Verifying = "Verifying the update",
        Preparing = "Preparing to install",
        Installing = "Installing v{0}",
        Restarting = "Restarting the service",
        Updated = "Updated to v{0}",
        Latest = "You're on the latest version",
        NotNow = "Not installing the update right now",
        DownloadFailed = "The update could not be downloaded",
        SignatureFailed = "The update is not valid",
        SetupFailed = "The update did not install",

        CheckingDetail = "Looking for a new version…",
        DownloadDetail = "{0} / {1} MB · {2}",
        DownloadDetailNoTotal = "{0} MB · {1}",
        VerifyingDetail = "Checking the publisher's digital signature",
        StartingSetup = "Starting the installer…",
        ClosingApp = "Closing the app…",
        StoppingService = "Stopping the Game Ping Booster service…",
        Copying = "Copying new files · {0}%",
        RegisteringService = "Registering the service…",
        StartingService = "Starting the service…",
        InstallingDriver = "Installing the virtual network driver…",
        Firewall = "Configuring the firewall…",
        WaitingService = "Waiting for the service to be ready…",
        Opening = "Opening Game Ping Booster…",
        LatestDetail = "Version v{0}",
        ReopeningOld = "Opening the current version again…",
        TryLater = "It will try again later. Opening the app…",

        ReasonBusy = "Boosting is on - the update installs once you stop.",
        ReasonGame = "A game is running - the update installs after you finish.",
        ReasonLater = "The last attempt did not finish - it will try again later.",
        ReasonUnavailable = "Updates could not be checked - it will try again later.",
        ReasonRefused = "This version needs a manual install - the app will guide you.",
        ReasonOff = "Automatic updates are switched off on this PC.",
        ReasonSetup = "The installer stopped part-way.",
        ReasonTimeout = "The installer stopped responding.",

        StepCheck = "Check for updates",
        StepDownload = "Download the update",
        StepVerify = "Verify the digital signature",
        StepStop = "Close the app and stop the service",
        StepCopy = "Install the new files",
        StepConfigure = "Configure the service and network driver",
        StepRestart = "Restart the service",
        StepOpen = "Open the app",

        FooterInstalling = "Nothing to do - the app opens again when it's done.",
        FooterDownloading = "Hang on a moment - the install runs by itself.",
        FooterFailed = "The current version keeps working as before.",
    };
}
