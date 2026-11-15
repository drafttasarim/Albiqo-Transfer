# Albiqo Transfer 0.2.1 Preview

USB ve Bluetooth bağlantısında eşleştirme kodunu ilk seferde bir kez gir. Aynı bağlantıda albümleri yenilemek, aktarımı durdurmak veya başka albüme geçmek kodu yeniden istemez.

- USB kablosu çıkarılırsa veya Bluetooth bağlantısı gerçekten kesilirse eşleştirme unutulur. Yeniden bağlanırken kod gerekir.
- Kod yalnız bellekte tutulur; ayarlara, dosyaya veya günlüğe kaydedilmez.
- Durdurulan dosyaya aynı bağlantıdan devam edilebilir.
- Albüm değişimlerinde kodun korunması için iPhone/iPad tarafında **Albiqo 1.0.3 (15) veya sonrası** gerekir. Bu iOS güncellemesi henüz App Store'da yayınlanmamış olabilir.
- USB için Windows'ta **Apple Devices** sürücüsü gerekir; ayrıca iTunes gerekmez.

iPhone uygulaması arka plana geçerse veya telefonun oturum güvenlik süresi dolarsa yeni kodla eşleştirme gerekir. Albüm aktarım ekranı kapalıyken fotoğraf/video erişimi kapanır.

Bu bir ön sürümdür. Otomatik protokol ve Windows arayüz testleri yapılır; fiziksel iPhone–Windows USB/Bluetooth denemesi donanımında ayrıca doğrulanmalıdır. [Fiziksel test listesi](https://github.com/drafttasarim/Albiqo-Transfer/blob/main/MANUAL-TEST-CHECKLIST.md).

## English

Enter the pairing code once for a live USB or Bluetooth connection. Refreshing albums, stopping a transfer, or changing the selected album keeps that pairing. Unplugging USB, losing Bluetooth, explicitly disconnecting, or closing the PC app clears the memory-only code.

Cross-album pairing requires **Albiqo iOS 1.0.3 (15)+**, which may not yet be available on the App Store. Backgrounding Albiqo or reaching the phone's session security limits requires pairing again. Album file access stops when its transfer screen is closed.

USB requires Apple's driver installed through **Apple Devices** on Windows; a separate iTunes installation is unnecessary. This preview includes automated protocol and Windows UI checks; physical device transfer remains a separate validation step.
