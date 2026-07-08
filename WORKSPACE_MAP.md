# ⚡ CoreRemote Proje Haritası ve Çalışma Kılavuzu

Bu doküman, **CoreRemote** projesinin mimarisini, dizin yapısını, hangi bileşenin ne işe yaradığını, sistemdeki tetikleyicileri ve veri akışlarını detaylı bir şekilde açıklamaktadır.

---

## 📌 Genel Bakış (Architecture Overview)

CoreRemote; RustDesk, AnyDesk veya TeamViewer benzeri çalışan, Windows işletim sistemlerine yönelik geliştirilmiş bir **uzaktan yönetim ve kontrol (Remote Desktop & Support)** yazılımıdır. Sistem 4 ana katmandan oluşur:

1. **Backend Sinyalleşme Sunucusu (Node.js)**: Ajanlar ile operatör/teknisyenler arasındaki bağlantıları yönetir, SQLite veritabanını tutar, C# dosyalarını dinamik olarak derler.
2. **Operatör Paneli (Next.js Dashboard)**: Web tabanlı yönetim arayüzüdür. Cihaz listesini izleme, durum kontrolü yapma ve ajan yükleyicisi oluşturma işlevlerine sahiptir.
3. **CoreRemote Ajanı (C# Agent)**: Uzaktan kontrol edilmek istenen hedef makinede arka planda çalışan, ekran yayını yapan ve Win32 API'leri kullanarak klavye/fare hareketlerini simüle eden Windows uygulamasıdır.
4. **Teknisyen Portalı (C# Viewer)**: Operatör panelinden tetiklenen (`coreremote://` protokolü ile) veya doğrudan çalışan; canlı ekran izleme, dosya transferi, uzak CMD konsolu, görev yöneticisi ve sohbet modüllerini barındıran masaüstü istemcisidir.

---

## 🗺️ Bileşen İlişki Haritası (Mermaid Diyagramı)

Aşağıdaki diyagram, bileşenlerin birbiriyle nasıl haberleştiğini ve isteklerin/tetikleyicilerin nasıl aktığını görselleştirmektedir:

```mermaid
graph TD
    subgraph TargetClient ["Hedef İstemci Makinesi"]
        Agent["C# Agent (CoreRemoteAgent.exe)"]
    end

    subgraph TechPC ["Teknisyen Bilgisayarı"]
        Viewer["C# Viewer (CoreRemoteViewer.exe)"]
    end

    subgraph ServerInfra ["Sunucu Altyapısı (IIS & Node.js)"]
        IIS["IIS Web Server (Reverse Proxy)"]
        ServerNode["Node.js Backend (server.js)"]
        DB[("SQLite (coreremote.db)")]
    end

    subgraph WebBrowser ["Web Tarayıcı"]
        Dashboard["Next.js Console (Operator Dashboard)"]
    end

    %% IIS Yönlendirmeleri (web.config)
    IIS -->|/api, /socket.io, WS Yönlendirmeleri| ServerNode
    IIS -->|Diğer tüm HTTP istekleri| Dashboard

    %% Ajan Bağlantısı
    Agent -->|1. WS Bağlantısı /agent-socket| ServerNode
    ServerNode -->|Cihaz Kaydı & Telemetri Kaydı| DB

    %% Dashboard İzleme ve Tetikleme
    Dashboard -->|2. HTTP GET /api/devices| ServerNode
    Dashboard -->|3. Socket.IO (Watch & Update)| ServerNode
    ServerNode -.->|Uzaktan Güncelleme Komutu| Agent

    %% Teknisyen Bağlantısı ve Akış
    Dashboard -->|4. Özel Protokol Tetikleme: coreremote://connect/ID| Viewer
    Viewer -->|5. WS Bağlantısı /operator-socket| ServerNode
    ServerNode -->|6. Canlı Yayın Başlat (start_stream)| Agent
    Agent -->|7. Canlı Ekran Frame (0x01) ve Ses (0x02)| ServerNode
    ServerNode -->|8. Ekran ve Ses Akışını Yönlendir| Viewer
    Viewer -->|9. Fare/Klavye girdisi, Dosya, CMD| ServerNode
    ServerNode -->|10. Girdi & Komutları İlet| Agent
    Agent -->|Win32 API simülasyonu| TargetOS["Windows OS"]
```

---

## 📂 Dizin Yapısı ve Dosyaların Görevleri

### 1. Kök Dizin (Root)
* 📄 [web.config](file:///c:/Users/ufuk.kaya/Desktop/Projeler/CoreRemote/web.config): IIS (Internet Information Services) üzerinde çalışan Reverse Proxy (Tersine Vekil Sunucu) kurallarıdır.
  * `/api`, `/socket.io`, `/agent-socket` ve `/operator-socket` isteklerini port `5000`'deki Node.js backendine yönlendirir.
  * Geri kalan tüm istekleri port `3000`'deki Next.js operatör paneline yönlendirir.
* 📄 [deploy-iis.ps1](file:///c:/Users/ufuk.kaya/Desktop/Projeler/CoreRemote/deploy-iis.ps1): Projeyi `C:\inetpub\wwwroot\CoreRemote` dizinine dağıtan, bağımlılıkları (`npm install`) kuran, Next.js uygulamasını derleyen ve **PM2** (`pm2 start`) servislerini (backend ve console için) başlatan dağıtım betiğidir.
* 📄 [check-update.ps1](file:///c:/Users/ufuk.kaya/Desktop/Projeler/CoreRemote/check-update.ps1): Sunucunun GitHub'daki `origin/main` dalı ile lokal sürümünü karşılaştırarak güncelleme olup olmadığını denetleyen yardımcı betiktir.

---

### 2. CoreRemote.Server (Backend Sinyalleşme Sunucusu)
Bu dizin, backend uygulamasını ve SQLite veritabanını barındırır.
* 📄 [server.js](file:///c:/Users/ufuk.kaya/Desktop/Projeler/CoreRemote/CoreRemote.Server/server.js): Uygulamanın beynidir.
  * **Veritabanı Kurulumu**: SQLite üzerinde `devices` (cihaz bilgileri) ve `audit_logs` (eylem günlükleri) tablolarını yönetir.
  * **İkon Çevirici**: Sunucu başladığında `agent_logo.png` dosyasını C# ajanının simgesi için uyumlu `.ico` formatına dönüştürür.
  * **WebSockets**: Ajanlar için `/agent-socket` ve teknisyenler için `/operator-socket` WebSocket sunucularını yönetir. Ekran ve ses verilerini anlık olarak arada köprü kurarak aktarır.
  * **Dashboard Socket.IO**: Web dashboard arayüzü ile gerçek zamanlı durum takibi ve komut gönderimi için Socket.IO odalarını yönetir.
  * **Dinamik C# Derleyici API'leri**:
    * `/api/builder/install`: Ajana ait C# kodunun içine sunucu adresi ve özel başlıkları yazıp Base64 formatına çevirerek istemcide derlenip çalışan PowerShell kurulum betiğini döner.
    * `/api/builder/download-exe`: İstemcinin girdiği parametrelere göre ajanı sunucu üzerinde `csc.exe` (.NET Framework derleyicisi) kullanarak dinamik olarak derler ve doğrudan indirtir.
    * `/api/builder/download-technician`: Teknisyen uygulamasını sunucu adresine göre özelleştirip derleyerek teknisyen EXE'sini indirten uç noktadır.

---

### 3. coreremote-console (Operatör Paneli - Frontend)
Next.js ile yazılmış modern bir web kontrol paneli arayüzüdür.
* 📄 [src/app/page.tsx](file:///c:/Users/ufuk.kaya/Desktop/Projeler/CoreRemote/coreremote-console/src/app/page.tsx): Paneldeki tüm etkileşimleri kontrol eden ana sayfadır.
  * **Cihaz Listesi**: Çevrimiçi/Çevrimdışı makineleri listeler.
  * **Detay ve İzleme**: Seçilen cihazın ekranını Socket.IO'dan gelen frame'ler yardımıyla HTML5 `<canvas>` üzerinde çizer ve canlı gecikme (FPS/KB) istatistiklerini gösterir.
  * **Builder Tab**: Özel başlık ve sunucu adresi girilerek özelleştirilmiş Ajan indirme komutunu veya EXE'sini üretir.
  * **Updater Tab**: Sunucunun kendi sürümünü güncellemesini sağlar ve çevrimiçi ajanlara uzaktan güncelleme tetikleyicisi gönderir.

---

### 4. CoreRemote.Agent (C# Ajan Uygulaması)
Hedef makinede Windows servisi gibi arka planda (penceresiz) çalışan ana bileşendir.
* 📄 [Agent.cs](file:///c:/Users/ufuk.kaya/Desktop/Projeler/CoreRemote/CoreRemote.Agent/Agent.cs): Ajanın tüm yeteneklerini barındırır.
  * **Başlangıç ve Koruma**: Yönetici yetkisi yoksa kendini UAC ile yükselterek yeniden başlatır. Windows Registry (`Run`) kaydı oluşturarak sistem açılışında otomatik başlar. Konsol ekranını gizler (`SW_HIDE`).
  * **Sistem Tepsisi (Tray Icon)**: Sağ altta bir "CR" simgesi oluşturur, durumunu günceller.
  * **Ekran Yayını**: Seçilen monitörün ekran görüntüsünü JPEG formatında sıkıştırarak WebSocket üzerinden gönderir. İmlecin (Mouse) konumuna kırmızı bir yuvarlak çizerek teknisyene görsel yardım sağlar.
  * **Win32 Girdi Simülasyonu**: Sunucudan gelen fare hareketi (`SetCursorPos`), tıklama ve klavye tuş basımı (`SendInput`) komutlarını yerel işletim sistemine aktarır.
  * **Özel Eylemler**:
    * `block_input`: Hedef bilgisayarın fiziksel klavye ve faresini kilitler.
    * `blank_screen`: Hedef monitörü kapatır (`SC_MONITORPOWER`), böylece yerel kullanıcı teknisyenin ne yaptığını göremez.
    * `term_cmd`: Arka planda gizli bir `cmd.exe` açar, teknisyenin komut satırı ile çalışmasını sağlar.
    * `file_chunk`: Karşıdan gönderilen dosyaları parçalar halinde alır ve `C:\ProgramData\CoreRemote\Downloads` altına yazar.
    * `download_file`: İstenen bir dosyayı karşıya (teknisyene) yükler.
    * `chat_msg`: İki yönlü sohbet penceresi (`ChatForm`) açar.
    * `audio_stream`: Mikrofon veya hoparlör sesini yakalayarak teknisyene gönderir.
    * `clipboard_sync`: Pano (Clipboard) verisini teknisyen ile senkronize eder.
* 📄 [compile.ps1](file:///c:/Users/ufuk.kaya/Desktop/Projeler/CoreRemote/CoreRemote.Agent/compile.ps1): Lokal geliştirme sırasında `Agent.cs` dosyasını `csc.exe` yardımıyla penceresiz Windows uygulaması (`/target:winexe`) olarak derleyen PowerShell betiğidir.

---

### 5. CoreRemote.Technician (C# Teknisyen Uygulaması)
Destek verecek olan teknisyenin bilgisayarında çalışan Windows Forms tabanlı masaüstü uygulamasıdır.
* 📄 [Technician.cs](file:///c:/Users/ufuk.kaya/Desktop/Projeler/CoreRemote/CoreRemote.Technician/Technician.cs):
  * **Protokol Kaydı**: Bilgisayara `coreremote://` protokolünü kaydeder. Böylece web panelindeki "Bağlan" butonuna tıklandığında tarayıcı otomatik olarak bu uygulamayı hedef `deviceId` ile başlatır.
  * **Ana Ekran (MainForm)**: Sunucudaki cihazları listeler, çift tıklamayla canlı bağlantı açar.
  * **Canlı Bağlantı Formu (ViewerForm)**: AnyDesk benzeri, sol tarafında gizlenebilir/açılabilir sekmeli bir kenar çubuğu (Sidebar) barındırır.
    * **Sekme 1 (Canlı Ekran)**: Fare/klavye girdilerini yakalayıp sunucuya gönderir. Monitör seçimi, ekranı karartma, girdiyi kilitleme ve ses dinleme kontrollerini barındırır.
    * **Sekme 2 (Dosya Gezgini)**: Hedef makinedeki dosya dizinini listeler, dosya indirme/yükleme işlemlerini yürütür.
    * **Sekme 3 (Görev Yöneticisi)**: Hedef makinedeki aktif işlemleri (Process) listeler ve sonlandırma komutu gönderir.
    * **Sekme 4 (Uzak Konsol)**: Hedef makinenin komut satırına doğrudan komut gönderip yanıtını gösteren bir terminal emülatörüdür.
    * **Sekme 5 (Sohbet)**: Hedef kullanıcı ile doğrudan mesajlaşma imkanı tanır.
  * **Kendi Kendini Güncelleme**: Açılışta sunucudaki `/api/technician/version` uç noktasını sorgular. Yeni sürüm varsa arka planda indirir ve geçici bir `.bat` dosyası ile kendini güncelleyip yeniden başlatır.

---

## ⚡ Sistemdeki Tetikleyiciler (Triggers)

CoreRemote'daki ana işlevlerin çalışmasını başlatan tetikleyici mekanizmalar şunlardır:

### 1. Otomatik Başlatma ve Bağlantı Kurma
* **Tetikleyen**: Windows Açılışı (Agent tarafında Registry tetikleyicisi) veya Manuel Başlatma.
* **Sonuç**: `Agent.cs` içerisindeki `ConnectLoopAsync` fonksiyonu çalışır, sunucunun `/agent-socket` adresine WebSocket bağlantısı açar ve durumunu anlık olarak SQLite veritabanına `online` yazar.

### 2. Canlı İzleme Oturumu Başlatma
* **Tetikleyen**: Operatörün Web Dashboard üzerinde "Bağlan" butonuna tıklaması.
* **İşlem Zinciri**:
  1. Web paneli, tarayıcı üzerinden `coreremote://connect/{deviceId}` linkini tetikler.
  2. Windows, bu protokolü kaydeden `CoreRemoteViewer.exe`'yi ilgili `deviceId` parametresiyle açar.
  3. Teknisyen uygulaması sunucuya `/operator-socket` üzerinden bağlanır.
  4. Sunucu (`server.js`), ilgili `deviceId`'li ajana WebSocket üzerinden `{ action: "start_stream" }` komutunu gönderir.
  5. Ajan ekran yakalama loopunu başlatır ve görüntüyü sunucuya WebSocket binary paketi (`0x01` byte prefixi) olarak yollar.
  6. Sunucu bu binary paketini teknisyen uygulamasına aktarır ve ekranda çizdirir.

### 3. Uzaktan Kontrol Girdileri
* **Tetikleyen**: Teknisyen bilgisayarında farenin hareket etmesi, tıklanması veya klavyede tuşa basılması.
* **İşlem Zinciri**:
  1. Teknisyen uygulaması olayları yakalar, ekran çözünürlüğüne göre oranlayıp (0-1 arası) sunucuya WebSocket üzerinden yollar.
  2. Sunucu bu eylemi anında hedef ajana iletir.
  3. Ajan Win32 `SendInput` ve `SetCursorPos` fonksiyonlarıyla hedef bilgisayarda eylemi fiziksel olarak gerçekleştirir.

### 4. Cihaz Güncellemeleri
* **Tetikleyen**: Operatör panelinden veya sunucu REST servisinden uzaktan güncelleme tetiklenmesi.
* **İşlem Zinciri**:
  1. Sunucu, ajana `{ type: "trigger_update", url: "güncelleme_linki" }` paketini yollar.
  2. Ajan güncel EXE dosyasını indirir.
  3. Ajan mevcut sürecini sonlandırıp yeni sürümü başlatacak bir alt süreci tetikler.

---

## 🛠️ Derleme ve Dağıtım Adımları

Geliştirme veya sunucu kurulumu aşamasında aşağıdaki yollar izlenir:

1. **Geliştirme Ortamı Derlemesi (Ajan)**:
   * `CoreRemote.Agent` klasöründeki `compile.ps1` betiğini çalıştırarak `CoreRemoteAgent.exe` oluşturulur.
2. **IIS Dağıtımı**:
   * Yönetici yetkileriyle açılan PowerShell penceresinde `.\deploy-iis.ps1` çalıştırılır. Bu betik backend ve frontend servislerini PM2 ile ayağa kaldırır, IIS için web config yönlendirmelerini etkinleştirir.
3. **Dinamik Kurulum**:
   * Operatör panelinde oluşturulan PowerShell komutu (`IEX (New-Object Net.WebClient).DownloadString(...)`) hedef makinede çalıştırıldığında; sunucudan özelleştirilmiş C# kodunu çeker, yerel olarak `csc.exe` ile derler, autostart ayarlarını yapar ve ajanı arka planda başlatır.
