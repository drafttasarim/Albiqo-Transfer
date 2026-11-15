# Fiziksel Windows + iPhone doğrulaması

Bu liste tamamlanmadan uygulama donanımda doğrulanmış kararlı sürüm sayılmaz. Test edilen Windows sürümü, PC Bluetooth sürücüsü, iPhone modeli, iOS build'i ve sonuçlar kaydedilmelidir. Kişisel fotoğraflar yerine test albümleri kullanılmalı.

- [ ] Windows x64 ve ARM64 paketleri açılır; kurulu .NET gerektirmeden standart kullanıcıyla çalışır.
- [ ] Türkçe/İngilizce, normal/minimum pencere, %100/%150/%200 ekran ölçeği: yazılar ve düğmeler kesilmez.
- [ ] Apple USB sürücüsü yokken anlaşılır açıklama görülür. Sürücü kurulu ve Apple Devices kapalıyken kendi uygulamamız telefonu bulur.
- [ ] iPhone kilit/güven onayı ve geçici kod ile USB bağlantısı kurulur. Yanlış kodla katalog veya dosya okunmaz.
- [ ] Albiqo iOS1.0.3 (13) ile doğrudan USB aktarımında küçük albüm, HEIC/Live Photo kaynakları ve4GB üzeri video aktarılır. Dosyalar ZIP olmadan albüm klasörüne gelir; kaynak boyutu/SHA-256 ve bileşenler eşleşir.
- [ ] Türkçe karakterler, boşluklar ve Windows ayrılmış/yasak adlarının normalleştirilmesi doğru çalışır.
- [ ] Kablo çekildiğinde yarım dosya tamamlanmış fotoğraf/video olarak görünmez. Aynı albüm/klasörle yeniden bağlanınca kaldığı byte'tan devam eder.
- [ ] Bluetooth opt-in, sistem eşleştirmesi ve geçici kodla yalnız yakındaki Albiqo oturumu bulunur.
- [ ] Bluetooth küçük albüm aktarımı doğrulanır; radyo kapanması, telefonun uzaklaşması ve düşük MTU durumları denenir.
- [ ] Durdur/yeniden bağlan akışları, farklı hedef klasör ve mevcut aynı adlı orijinal dosyaların korunması denenir.
- [ ] Bozuk tam `.partial` hash hatası verir; Retry yeni kopya indirir, eski şüpheli dosyayı yayımlamaz.
- [ ] Tamamlanan albüme yeniden bağlanınca doğrulanmış dosyalar atlanır; PC'de değiştirilmiş foto korunur ve yeni kopya benzersiz ad alır.
- [ ] Kaynak sürümü değişince eski kısmi indirme kullanılmaz; iCloud beklemesi sırasında Durdur çalışır.
- [ ] Eski protokol1 ZIP oturumuyla uyumluluk ayrıca doğrulanır.
- [ ] On yanlış koddan sonra oturum kapanır. Yeni telefon oturumu yeni kod verir.
- [ ] iOS uygulaması arka plana alınca oturum kapanır; eski kod artık çalışmaz.
- [ ] Aktarım sonunda telefon arşivinden hiçbir fotoğraf/video silinmediği doğrulanır.


## Aynı bağlantıda eşleştirme — 0.2.1 / iOS build15

- [ ] USB ve Bluetooth: bir kez kod gir, yenile/durdur/devam et ve albüm değiştir; kod yeniden istenmesin.
- [ ] USB: hızlı çıkarıp tekrar tak; eski eşleştirme canlanmasın, kod yeniden istensin.
- [ ] Bluetooth: kapat/aç veya kapsama dışına çık; aynı cihaz yeniden bağlansa da kod istensin.
- [ ] Başka USB cihazını çıkarmak mevcut telefonun eşleştirmesini bozmasın.
- [ ] Telefon uygulamasını arka plana al; kod geçersizleşsin ve PC bunu temizlesin.
- [ ] Transfer ekranı kapalıyken hiçbir albüm veya medya aktarılamasın.
- [ ] Kod diskte, ayarlarda veya günlüklerde bulunmasın.
