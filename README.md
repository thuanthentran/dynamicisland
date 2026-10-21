# Dynamic Island

Island ở giữa mép trên màn hình Windows 11: điều khiển media, lời bài hát, thông báo và trạng thái hệ thống, với ba cỡ chuyển đổi bằng nội suy mượt.

## Ba cỡ

| Cỡ | Khi nào | Nội dung |
|---|---|---|
| Nhỏ | Luôn hiện, cao 36, rộng theo nội dung | Wifi · âm lượng · pin · giờ; có media thì thêm artwork và equalizer theo âm thanh thật |
| Lớn | Rê chuột lên island và giữ yên (hoặc click) | 720 px, hai cột: media đầy đủ, tua, 3 dòng lời bên trái; tối đa 3 thông báo từ Notification Center bên phải; nút cài đặt, tay nắm thu nhỏ nhanh |
| Vừa | Rời cỡ lớn khi media đang phát | Thẻ media kiểu Quick Settings: app nguồn, tên bài, nghệ sĩ, artwork, tiến trình, điều khiển; rê lại → cỡ lớn, để yên → cỡ nhỏ |

Giao diện theo Windows 11: hình chữ nhật bo 8 px nổi dưới mép trên như flyout, token màu Fluent, Segoe UI Variable, icon Segoe Fluent (bộ glyph của taskbar và Quick Settings), toggle/slider/nút kiểu Win11, màu accent của hệ thống.

- **Thông báo mới** ở cỡ nhỏ/vừa: nội dung trượt ra trái, thông báo trượt vào, hết thời gian thì trượt ngược lại.
- **Tay nắm** ở giữa mép trên của cỡ lớn: thu thẳng về cỡ nhỏ, bỏ qua cỡ vừa.
- **Kéo cửa sổ / Snap**: giữ chuột trên island thì không mở, cho tới khi con trỏ rời island.
- **Fullscreen** (game, video, trình chiếu): island ẩn hẳn, hiện lại khi thoát.
- **Nền**: theo theme hệ thống; có media thì artwork làm mờ ánh nhẹ dưới lớp nền acrylic.

## Yêu cầu

- Windows 11 (22000+), x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) để build
- Developer Mode (chỉ cần để đăng ký package cho thông báo)

## Build và chạy

Bản phát hành là một file exe self-contained, không cần cài .NET:

```powershell
dotnet publish .\src\DynamicIsland -c Release -o .\artifacts\DynamicIsland
.\artifacts\DynamicIsland\DynamicIsland.exe
```

Khi phát triển có thể dùng `dotnet build .\src\DynamicIsland` (bản Debug cần .NET 10 runtime trên máy).

## Bật thông báo

`UserNotificationListener` yêu cầu package identity. Dự án dùng sparse package (packaged with external location): exe giữ nguyên chỗ, package chỉ cấp identity cho exe trong thư mục đã đăng ký.

```powershell
.\packaging\Register-DevPackage.ps1               # mặc định: artifacts\DynamicIsland, cần Developer Mode
.\packaging\Register-DevPackage.ps1 -AppDir <thư mục chứa exe>
.\packaging\Register-DevPackage.ps1 -Unregister   # gỡ
```

Chuyển exe sang thư mục khác thì phải đăng ký lại. Publish đè lên cùng thư mục thì không cần.

## Dữ liệu và mạng

- Cài đặt: `%LOCALAPPDATA%\DynamicIsland\settings.json`. Cache lời: `%LOCALAPPDATA%\DynamicIsland\Cache\Lyrics`.
- Chỉ kết nối mạng để tìm lời bài hát: gửi tên bài, nghệ sĩ, album, thời lượng tới QQ Music, NetEase (API không chính thức) và LRCLIB. Tắt từng nguồn hoặc tắt lời trong Cài đặt.

## Cấu trúc

```text
src/DynamicIsland/
  App.xaml.cs                   Composition root, single instance, thứ tự giải phóng
  Platform/                     P/Invoke, màn hình/fullscreen, theme, process, package identity, dọn RAM
  Services/MediaService.cs      SMTC: phiên hiện tại, timeline, ảnh bìa, điều khiển, tua
  Services/NotificationService  Toast trong Notification Center (event + polling 2 s), icon, xoá
  Services/SystemStatusService  Mạng, pin, âm lượng
  Services/AudioSpectrumService Loopback capture (process của app phát nhạc) + FFT 5 dải
  Services/Lyrics/              Nguồn QQ/NetEase/LRCLIB, chấm điểm, parser LRC/QRC/YRC, cache
  Services/SettingsService.cs   Cài đặt JSON, khởi động cùng Windows
  ViewModels/                   Trạng thái binding, bảng màu, xử lý artwork, thông báo, cài đặt
  Views/IslandModeController.cs Chính sách 3 cỡ (thuần, không giữ HWND)
  Views/IslandSurface.cs        Bề mặt flyout: bo góc, viền, bóng, nền artwork
  Views/IslandWindow.*          Cửa sổ: vị trí, nội suy, trượt nội dung, banner
packaging/                      Sparse package manifest, logo, script đăng ký
```

## Giới hạn đã biết

- Island ở màn hình chính; cỡ nhỏ che một đoạn giữa thanh tiêu đề của cửa sổ phóng to bên dưới.
- Nền là màu acrylic mô phỏng, không phải acrylic thật của DWM (DWM chỉ áp được cho cả cửa sổ, không cho một vùng bo góc đang co giãn).
- Media chỉ lấy từ app hỗ trợ SMTC (Spotify, trình duyệt, Media Player…).
- Chỉ thấy toast dạng chuẩn của Windows; không đọc được trạng thái Do Not Disturb qua API công khai.
- Island nằm dưới Start menu, Notification Center và Search (z-order của shell).
- Nhận diện fullscreen là heuristic; game borderless không phủ kín màn hình sẽ không được nhận ra.
- Nguồn lời QQ/NetEase là API không chính thức, có thể ngừng hoạt động bất cứ lúc nào.
