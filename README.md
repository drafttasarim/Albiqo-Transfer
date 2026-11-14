# Albiqo Transfer

Albiqo'da hazırladığın albümleri kendi Windows uygulamamızla bilgisayarına kaydet. USB kablo veya Bluetooth seç; albüm adıyla hazırlanan ZIP'i istediğin klasöre al. ZIP açıldığında albümle aynı adlı klasör bulunur.

## USB için önce bunu kur

**Windows'ta ilk USB kullanımından önce [Apple Devices'ı Microsoft Store'dan kur](https://apps.microsoft.com/detail/9NP83LWLPZ9K).** Gereken Apple USB sürücüsü/servisi bu kurulumla gelir. **Ayrıca iTunes kurmak gerekmez.** Telefonda Güven'e basmak, bilgisayarda bu sürücünün bulunduğu anlamına gelmez.

Kurulumdan sonra veri kablosunu yeniden tak, iPhone'un kilidini aç ve Apple Devices içinde telefonun göründüğünü kontrol et. Sonra Albiqo Transfer'da **Telefonları bul** düğmesine bas. Albümleri kaydetme ve aktarım işlemini Albiqo Transfer yapar; Apple Devices'tan elle dosya aktarmana gerek yok.

[Windows indirme sayfası](https://github.com/drafttasarim/Albiqo-Transfer/releases) · [Kaynak kod](https://github.com/drafttasarim/Albiqo-Transfer)

## Ön sürüm durumu

Bu kaynak ve Windows EXE derlemesi **geliştirme ön sürümüdür**. Protokol, USB servis tüneli benzetimi, kesilen indirmeye devam etme ve SHA-256 kontrolleri otomatik test edilir. Fiziksel iPhone + Windows üzerinde USB/Bluetooth uçtan uca testleri ve Windows arayüzünün gerçek cihaz/dpi kontrolleri henüz tamamlanmadı. Bu yüzden donanımda doğrulanmış kararlı sürüm olarak sunulmaz. Mevcut App Store build11 bu yeni aktarım sunucusunu içermez; eşleşen Albiqo iOS güncellemesi gerekir.

GitHub Windows runner'ı `--smoke-test` ile Türkçe/İngilizce arayüzün normal ve minimum pencere boyutlarını çalıştırıp PNG ve JSON kanıtı üretir. Bu testin workflow'da bulunması geçtiği anlamına gelmez; çalıştırma sonucu ayrıca kontrol edilmelidir. [Fiziksel test listesi](MANUAL-TEST-CHECKLIST.md).

## Kullanım

1. USB kullanacaksan önce yukarıdaki Apple Devices kurulumunu tamamla. iPhone'da Albiqo ile albümü aktarım için hazırla ve **PC Aktarımı** ekranını aç.
2. Windows'ta Albiqo Transfer'i açıp **USB** veya **Bluetooth** seç.
3. **Telefonları bul** düğmesine bas. Telefonunu seçip iPhone'daki altı haneli geçici kodu gir.
4. **Bağlan** ile hazır albümleri gör. Kaydedilecek klasörü ve albümü/albümleri seç.
5. **Seçileni PC'ye kaydet** düğmesine bas. Dosya doğrulanınca ZIP hazır olur; mevcut dosyalar ezilmez.

Aktarım durdurulursa eksik dosya saklanır. Aynı albüm ve aynı klasörle yeniden bağlandığında doğrulanan metadata ile kaldığı byte'tan devam eder. Orijinaller telefondan otomatik silinmez. ZIP arşivini açmak kullanıcıya bırakılır.

### USB

- Veri aktarabilen USB-C/Lightning kablosu kullan. iPhone'un kilidini açıp gerekiyorsa **Güven** onayını ver.
- Windows'ta **Apple Mobile Device USB sürücüsü ve servisi gerekir**. [Apple Devices'ı kur](https://apps.microsoft.com/detail/9NP83LWLPZ9K); ayrıca iTunes gerekmez. Bu donanım sürücüsü olmadan kendi uygulamamız da iPhone'a USB tüneli açamaz.
- Apple'ın Windows aygıt desteğini kurduktan sonra Apple Devices uygulamasını açman veya Dosyalar ekranında elle işlem yapman gerekmez. Listeleme, indirme, devam etme ve doğrulamayı **Albiqo Transfer** yapar.
- USB isteği localhost Apple usbmux servisine gider; Albiqo iPhone uygulamasındaki yalnız loopback üzerinde dinleyen aktarım sunucusuna tünellenir. LAN veya internete sunucu açılmaz.

**Telefon bulunamıyorsa:** Önce Apple Devices'ın telefonu görüp görmediğini kontrol et. Orada da görünmüyorsa veri destekli kabloyu/USB girişini ve sürücü kurulumunu kontrol et. Apple Devices görüyorsa ama Albiqo göremiyorsa Albiqo Transfer'ın güncel ön sürümünü kullan. [Apple'ın USB tanıma rehberi](https://support.apple.com/en-us/108643).

### Bluetooth

- Windows Bluetooth Low Energy destekli radyo/sürücü ve telefonun Bluetooth izni gerekir.
- Windows'un normal eşleştirme onayını tamamla. Geçici Albiqo kodu ayrıca istenir.
- Bu, klasik sistem “Bluetooth ile dosya gönder” değil; iki Albiqo uygulaması arasında **şifreli özel BLE GATT protokolüdür**.
- Büyük albümler ve videolar Bluetooth'ta uzun sürebilir. USB büyük arşivler için daha uygundur.
- iPhone aktarım ekranı açık kalmalı. Uygulama arka plana geçerse oturum güvenlik için durur.

## Gereksinimler

Windows11 veya güncel Windows10 22H2, x64/ARM64. Uygulama .NET10 LTS ile bağımsız paketlenir; kullanıcıdan ayrıca .NET kurması beklenmez. Yönetici olarak çalıştırmak gerekmez. Windows10'un işletim sistemi destek koşulları Microsoft'a bağlıdır.

Arayüz sistem diline göre Türkçe veya İngilizcedir. Bağlantı/protokol hata ayrıntılarının bir kısmı İngilizce olabilir.

## Derleme

```powershell
dotnet run --project tests/Albiqo.Transfer.SelfTests/Albiqo.Transfer.SelfTests.csproj -c Release
dotnet publish src/Albiqo.Transfer.Windows/Albiqo.Transfer.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableWindowsTargeting=true -o artifacts/win-x64
```

ARM64 için `-r win-arm64` kullan. `.github/workflows/windows-build.yml`, Windows runner'da testleri ve iki EXE paketini oluşturup indirilebilir GitHub Actions artifact'i üretir. **Kendi başına GitHub Release yayınlamaz**. Kaynak sahipleri doğrulamadan sonra paketleri bir ön sürüm Release'ine ekleyebilir. Bu depodaki workflow minimum salt-okuma repository izni kullanır.

Paket, dağıtılan .NET bileşenlerinin lisans ve üçüncü taraf bildirimlerini `third-party/` içinde korur. Bu bildirimler Albiqo'nun kendi kaynak koduna ayrıca bir açık kaynak lisansı verildiği anlamına gelmez.

## Teknik sözleşme ve kaynaklar

- [Protokol1](PROTOCOL.md): UUID'ler, sınırlı byte aralıkları, metadata, kimlik doğrulama, resume ve hash doğrulaması.
- [Microsoft BLE GATT istemci API'leri](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client).
- [Microsoft Windows BluetoothLEDevice](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothledevice).
- [Apple CoreBluetooth peripheral](https://developer.apple.com/documentation/corebluetooth/cbperipheralmanager).
- [libusbmuxd birincil protokol uygulaması](https://github.com/libimobiledevice/libusbmuxd/blob/master/src/libusbmuxd.c): Windows TCP localhost, plist ListDevices/Connect ve network-order port. Albiqo kendi küçük C# istemcisini uygular; libusbmuxd binary'si paketlenmez.
- [usbmux protokol başlıkları](https://github.com/libimobiledevice/libusbmuxd/blob/master/src/usbmuxd-proto.h): varsayılan Windows27015 portu ve paket başlığı.
- [Apple Windows USB aygıt desteği](https://support.apple.com/en-gb/120402).
- [.NET destek politikası](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core): yeni uygulama .NET10 LTS hedefler.

İletişim: albiqo@drafttasarim.com

