# Fiziksel Windows + iPhone doğrulaması

Bu liste tamamlanmadan uygulama donanımda doğrulanmış kararlı sürüm sayılmaz. Test edilen Windows sürümü, PC Bluetooth sürücüsü, iPhone modeli, iOS build'i ve sonuçlar kaydedilmelidir. Kişisel fotoğraflar yerine test albümleri kullanılmalı.

- [ ] Windows x64 ve ARM64 paketleri açılır; kurulu .NET gerektirmeden standart kullanıcıyla çalışır.
- [ ] Türkçe/İngilizce, normal/minimum pencere, %100/%150/%200 ekran ölçeği: yazılar ve düğmeler kesilmez.
- [ ] Apple USB sürücüsü yokken anlaşılır açıklama görülür. Sürücü kurulu ve Apple Devices kapalıyken kendi uygulamamız telefonu bulur.
- [ ] iPhone kilit/güven onayı ve geçici kod ile USB bağlantısı kurulur. Yanlış kodla katalog veya dosya okunmaz.
- [ ] USB ile küçük albüm, büyük video albümü ve ZIP64 (4GB üzeri) aktarılır. SHA-256 ve açılan klasör/dosya içerikleri kaynakla eşleşir.
- [ ] Türkçe karakterler, boşluklar ve Windows ayrılmış/yasak adlarının normalleştirilmesi doğru çalışır.
- [ ] Kablo çekildiğinde yarım dosya tamamlanmış ZIP olarak görünmez. Aynı albüm/klasörle yeniden bağlanınca kaldığı byte'tan devam eder.
- [ ] Bluetooth opt-in, sistem eşleştirmesi ve geçici kodla yalnız yakındaki Albiqo oturumu bulunur.
- [ ] Bluetooth küçük albüm aktarımı doğrulanır; radyo kapanması, telefonun uzaklaşması ve düşük MTU durumları denenir.
- [ ] Durdur/yeniden bağlan akışları, farklı hedef klasör ve mevcut aynı adlı ZIP korunması denenir.
- [ ] Bozuk tam `.partial` hash hatası verir; Retry yeni kopya indirir, eski şüpheli dosyayı yayımlamaz.
- [ ] On yanlış koddan sonra oturum kapanır. Yeni telefon oturumu yeni kod verir.
- [ ] iOS uygulaması arka plana alınca oturum kapanır; eski kod artık çalışmaz.
- [ ] Aktarım sonunda telefon arşivinden hiçbir fotoğraf/video silinmediği doğrulanır.

