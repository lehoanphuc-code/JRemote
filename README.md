# JRemote - Screen Mirroring & Control Tool (iOS & Android for Windows 10/11)

**JRemote** là công cụ phản chiếu (Screen Mirroring) và điều khiển màn hình điện thoại iPhone (iOS) và Android trực tiếp trên máy tính Windows 10/11 với hiệu năng cao, độ trễ thấp và giao diện Fluent Design hiện đại.

---

## 🚀 Tính năng nổi bật

### 1. Dành cho Android 🤖
- **Phản chiếu độ trễ cực thấp (< 50ms):** Sử dụng giao thức H.264/H.265 tối ưu qua ADB.
- **Điều khiển 2 chiều toàn diện:**
  - Click chuột = Chạm (Tap)
  - Giữ và kéo = Vuốt (Swipe / Drag)
  - Chuột phải = Phím Quay lại (Back)
  - Cuộn chuột (Scroll Wheel) = Cuộn trang mượt mà
  - Thanh phím ảo tiện lợi: Back, Home, Recent Apps, Bật/Tắt màn hình.
- **Hỗ trợ cả USB và Wi-Fi:**
  - Tự động nhận diện thiết bị cắm cáp USB.
  - Chuyển sang Wi-Fi chỉ với 1 click ("Mở Wi-Fi") để rút cáp và điều khiển không dây.
  - Kết nối trực tiếp qua địa chỉ IP nội bộ (`IP:5555`).

### 2. Dành cho iPhone / iPad (iOS) 🍏
- **Không cần cài App trên iPhone, Không cần Jailbreak:**
  - Tích hợp **AirPlay Receiver Server** (chuẩn giao thức Apple qua mDNS/Bonjour + RTSP).
  - Người dùng chỉ cần mở **Trung tâm điều khiển (Control Center)** trên iPhone ➔ chọn **Phản chiếu màn hình (Screen Mirroring)** ➔ chọn **JRemote-PC**.
- **Điều khiển chuột/phím ngược từ Windows:**
  - Mô phỏng thiết bị **Bluetooth HID (Mouse & Keyboard)** qua Windows 10/11 WinRT GATT Bluetooth API.
  - Kết hợp với tính năng **AssistiveTouch** của iOS để click và di chuyển con trỏ trên iPhone từ máy tính.

### 3. Tiện ích hệ thống
- **Chụp ảnh màn hình (Screenshot):** Lưu nhanh vào thư mục `Pictures/JRemote` và tự động copy vào Clipboard PC.
- **Ghim luôn ở trên cùng (Always on Top):** Giữ cửa sổ phản chiếu luôn nổi trên các ứng dụng khác khi làm việc.
- **Giao diện Windows 11 Fluent Design:** Sử dụng bộ thư viện chuẩn `WPF-UI` với Dark Theme, bo góc và hiệu ứng mượt mà.

---

## 🏗️ Cấu trúc dự án (Architecture)

```
JRemote/
├── JRemote.slnx                    # Solution file
├── scripts/
│   └── download-tools.ps1         # Script tải tự động adb.exe và scrcpy-server.jar
├── bin/
│   ├── adb/                       # Nơi chứa adb.exe & platform-tools
│   └── scrcpy/                    # Nơi chứa scrcpy-server.jar
└── src/
    ├── JRemote.Core/              # Interface, Models (DeviceInfo, TouchEvent, VideoFrame, Enums)
    ├── JRemote.Android/           # ADB Service, Device tracking, Input Injection, Wi-Fi switcher
    ├── JRemote.iOS/               # AirPlay RTSP Server, mDNS Bonjour Advertiser, Bluetooth HID Controller
    ├── JRemote.Rendering/         # WriteableBitmap low-latency renderer, H.264 NAL Parser
    └── JRemote.UI/                # Giao diện chính WPF Fluent Design (MVVM pattern)
```

---

## 🛠️ Hướng dẫn cài đặt & Khởi chạy

### Yêu cầu môi trường
- Hệ điều hành: **Windows 10 (bản 19041 trở lên) hoặc Windows 11**
- **.NET 8.0 SDK** (hoặc .NET 9.0 / .NET 10.0 SDK)
- Bluetooth (nếu muốn dùng tính năng điều khiển chuột cho iPhone)

### Bước 1: Tải công cụ phụ trợ (ADB & Scrcpy)
Mở PowerShell tại thư mục gốc dự án và chạy:
```powershell
.\scripts\download-tools.ps1
```
*(Script sẽ tự động tải `adb.exe` chính thức từ Google và `scrcpy-server.jar` vào thư mục `bin/`)*

### Bước 2: Build & Chạy ứng dụng
```powershell
dotnet build JRemote.slnx
dotnet run --project src/JRemote.UI/JRemote.UI.csproj
```

---

## 📱 Hướng dẫn sử dụng

### Kết nối điện thoại Android
1. **Qua cáp USB:**
   - Bật **Tùy chọn cho nhà phát triển (Developer Options)** trên điện thoại.
   - Bật **Gỡ lỗi qua USB (USB Debugging)**.
   - Cắm cáp vào máy tính. Thiết bị sẽ lập tức hiển thị trong danh sách của JRemote.
   - Nhấn **"Phản Chiếu"** để bắt đầu.
2. **Qua Wi-Fi:**
   - Sau khi cắm cáp lần đầu, nhấn nút **"Mở Wi-Fi"** trên thẻ thiết bị.
   - Rút cáp, xem địa chỉ IP của điện thoại trong phần Cài đặt Wi-Fi.
   - Nhập IP vào ô *"Nhập IP (VD: 192.168.1.50:5555)"* và nhấn **"Kết nối Wi-Fi"**.

### Kết nối iPhone / iPad (iOS)
1. Đảm bảo iPhone và máy tính kết nối **chung một mạng Wi-Fi**.
2. Trên JRemote, trạng thái AirPlay mặc định được bật (`Đang phát sóng AirPlay - Port 7000`).
3. Trên iPhone:
   - Vuốt mở **Trung tâm điều khiển (Control Center)**.
   - Nhấn vào biểu tượng **Phản chiếu màn hình (Screen Mirroring)**.
   - Chọn **`JRemote-PC`**.
   - Màn hình iPhone sẽ tự động xuất hiện trên JRemote!
4. *(Để điều khiển chuột trên iPhone):* Mở **Cài đặt ➔ Trợ năng ➔ Cảm ứng ➔ Bật AssistiveTouch** và ghép đôi Bluetooth với máy tính.
