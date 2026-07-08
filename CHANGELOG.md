# 📝 Güncelleme Notları (Changelog)

Bu dosya CoreRemote projesinde yapılan tüm değişiklikleri ve hata düzeltmelerini kronolojik olarak takip etmek için oluşturulmuştur.

## [v1.0.2] - 2026-07-08
### Eklenenler (Added)
- **Tüm Ekranları Yan Yana Görme Özelliği:** Teknisyen panelindeki Ekran seçim menüsüne "Tüm Ekranlar (Yan Yana)" seçeneği eklendi.
  - *Detay:* Ajan tarafında indeks `-1` ile gönderildiğinde Windows'un tüm sanal masaüstü boyutları (`SystemInformation.VirtualScreen`) baz alınarak yayın yapması ve fare kordinatlarının buna göre haritalanması sağlandı. 

## [v1.0.1] - 2026-07-08
### Düzeltilenler (Fixed)
- **Çoklu Ekran Desteği:** `CoreRemote.Agent` içerisinde farenin çoklu monitörlerde doğru konumlanmaması sorunu çözüldü.
  - *Detay:* `SendInput` ile yapılan sanal çözünürlük hesaplaması iptal edilerek Windows'un tüm sanal masaüstünü (tüm monitörleri) kapsayan native `SetCursorPos` fonksiyonu kullanıldı.
  - *Etkilenen Dosya:* `CoreRemote.Agent/Agent.cs`

---
*Not: Bu dosya her GitHub güncellemesinden (push) önce otomatik olarak güncellenecektir.*
