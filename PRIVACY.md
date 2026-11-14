# Albiqo Transfer — gizlilik

Albiqo Transfer, kullanıcının seçtiği hazır albüm ZIP'lerini iPhone'dan kendi Windows PC'sine kaydeder. Fotoğraf ve videolar bir Albiqo bulut sunucusuna yüklenmez. Uygulamada hesap, reklam SDK'sı, analitik SDK'sı veya uygulamaya ait telemetri gönderimi yoktur.

USB iletişimi Windows'taki Apple aygıt servisi üzerinden telefonun yerel aktarım sunucusuna tünellenir. Bluetooth aktarımı şifreli BLE bağlantısı ve geçici altı haneli oturum kodu kullanır. Kod yalnız bellekte tutulur; diske veya günlüğe yazılmaz. Telefon ekranı kapanıp oturum durunca veya bağlantı kapatılınca erişim biter.

Albümlerin adı, dosya boyutu, hazırlanma tarihi ve SHA-256 değeri indirme ve doğrulama için işlenir. Yalnız telefonda aktarım için açıkça hazırlanmış arşivler listelenir. Uygulama telefonun tüm fotoğraf arşivine doğrudan erişmez; orijinalleri silmez.

Eksik aktarımlar PC'de `.partial` dosyası ve devam etme metadata'sı bırakabilir. Bu dosyalar kullanıcının seçtiği klasördedir. Başarılı aktarımda tamamlanan ZIP kalır ve yan metadata kaldırılır. Kullanıcı kendi bilgisayarındaki bu dosyaları normal dosya yönetimiyle silebilir.

Windows/Apple sürücülerinin, Bluetooth eşleştirmesinin ve GitHub indirmelerinin kendi hizmet koşulları ve gizlilik uygulamaları vardır. Albiqo Transfer bunları değiştirmez.

Contact: albiqo@drafttasarim.com

