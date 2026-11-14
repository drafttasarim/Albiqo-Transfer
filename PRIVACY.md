# Albiqo Transfer — gizlilik

Albiqo Transfer, kullanıcının seçtiği albümün orijinal fotoğraf ve video kaynaklarını iPhone'dan kendi Windows PC'sindeki albüm klasörüne kaydeder. Fotoğraf ve videolar bir Albiqo bulut sunucusuna yüklenmez. Uygulamada hesap, reklam SDK'sı, analitik SDK'sı veya uygulamaya ait telemetri gönderimi yoktur.

USB iletişimi Windows'taki Apple aygıt servisi üzerinden telefonun yerel aktarım sunucusuna tünellenir. Bluetooth aktarımı şifreli BLE bağlantısı ve geçici altı haneli oturum kodu kullanır. Kod yalnız bellekte tutulur; diske veya günlüğe yazılmaz. Telefon aktarım oturumu veya bağlantı kapanınca erişim biter.

Albüm adı, kaynak dosya adı, oluşturulma tarihi, kaynak kimliği/sürümü, alınan byte miktarı ve doğrulama için SHA-256 işlenir. Yalnız telefonda aktarım için açıkça seçilen albüm sunulur. Windows uygulaması telefonun tüm fotoğraf arşivine doğrudan erişmez; orijinalleri silmez. iCloud'daki kaynakların indirilmesini Apple Photos yönetir ve kendi sistem önbelleğini kullanabilir. Doğrudan aktarımda Albiqo telefonda albüm ZIP'i veya ikinci bir medya staging kopyası oluşturmaz.

Eksik aktarımlar PC'de `.partial` dosyası ve devam etme metadata'sı bırakabilir. Bu dosyalar kullanıcının seçtiği albüm klasöründeki gizli `.albiqo-transfer` alt klasöründedir. Tamamlanan orijinal dosyalar albüm klasörüne kaydedilir. Tekrar bağlanınca gereksiz kopya indirmemek için kaynak kimliği/sürümü, kaydedilen dosya adı, gerçek boyut ve SHA-256 içeren tamamlanma kayıtları korunur. Yeniden indirme öncesi gerçek PC dosyası doğrulanır. Kullanıcı kendi bilgisayarındaki dosyaları ve metadata'yı normal dosya yönetimiyle silebilir; metadata'yı silmek devam etme ve tamamlananları hatırlama bilgisini kaldırır.

Eski protokol1 oturumlarında önceden hazırlanmış albüm ZIP'leri ve bunların devam etme metadata'sı alınabilir. Yeni doğrudan aktarım oturumunda bu uyumluluk yolu kullanılmaz.

Windows/Apple sürücülerinin, Bluetooth eşleştirmesinin ve GitHub indirmelerinin kendi hizmet koşulları ve gizlilik uygulamaları vardır. Albiqo Transfer bunları değiştirmez.

İletişim: albiqo@drafttasarim.com
