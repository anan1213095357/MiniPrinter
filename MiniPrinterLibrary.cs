using QRCoder;
using System.Drawing;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace MiniPrinter
{
    public interface IMiniPrinterService
    {
        Task PrintAsync(string text, string qrContent);

        Task PrintAsync(Bitmap bitmap);
    }

    public class MiniPrinterService : IMiniPrinterService, IDisposable
    {
        private readonly Guid SERVICE_UUID = Guid.Parse("4fafc201-1fb5-459e-8fcc-c5c9c331914b");
        private readonly Guid WRITE_UUID = Guid.Parse("beb5483e-36e1-4688-b7f5-ea07361b26a8");
        private readonly Guid NOTIFY_UUID = Guid.Parse("beb5483e-36e1-4688-b7f5-ea07361b26a8");
        private const string TARGET_DEVICE_NAME = "Mini-Printer";
        private const int MAX_LINE_BYTE = 48;
        private const int CONNECT_MAX_RETRIES = 5; 

        private BluetoothLEDevice _bleDevice = default!;
        private GattCharacteristic _writeCharacteristic = default!;
        private GattCharacteristic _notifyCharacteristic = default!;

        private readonly ILogger<MiniPrinterService> _logger;
        private bool _isConnected = false;

        public MiniPrinterService(ILogger<MiniPrinterService> logger) => _logger = logger;


        public async Task PrintAsync(string text, string qrContent)
        {
            // 1. 确保连接 (带重试逻辑)
            if (!_isConnected || _bleDevice?.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            {
                bool success = await EnsureConnectionWithRetry();
                if (!success)
                {
                    _logger.LogError("无法连接到打印机，已达到最大重试次数，打印取消。");
                    return;
                }
            }

            _logger.LogInformation("正在生成打印数据...");
            using (Bitmap imgToPrint = CreatePrintImage(text, qrContent, MAX_LINE_BYTE * 8))
            {

                imgToPrint.Save("C:\\Users\\anan1\\Pictures\\新建文件夹 (7)\\test.bmp");

                _logger.LogInformation("正在发送打印数据...");
                await PrintBitmap(imgToPrint);
            }
            _logger.LogInformation("打印指令发送完毕。");
        }

        public async Task PrintAsync(Bitmap bitmap)
        {
            // ================== 新增代码开始 ==================
            // 1. 确保连接 (带重试逻辑) - 复制自另一个重载方法
            if (!_isConnected || _bleDevice?.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            {
                bool success = await EnsureConnectionWithRetry();
                if (!success)
                {
                    _logger.LogError("无法连接到打印机，已达到最大重试次数，打印取消。");
                    return;
                }
            }
            // ================== 新增代码结束 ==================

            _logger.LogInformation("正在处理 Bitmap 并发送打印数据...");

            // 建议：在此处处理一下图片尺寸，防止图片过宽导致乱码
            // 你的打印机宽度似乎是 MAX_LINE_BYTE * 8 = 384 像素
            if (bitmap.Width != MAX_LINE_BYTE * 8)
            {
                _logger.LogWarning($"图片宽度 ({bitmap.Width}) 与打印机宽度 ({MAX_LINE_BYTE * 8}) 不匹配，可能会导致打印错位。");
                // 可以在这里加一个自动缩放图片的逻辑，或者由调用方保证宽度正确
            }
            bitmap.Save("C:\\Users\\anan1\\Pictures\\新建文件夹 (7)\\test.bmp");
            await PrintBitmap(bitmap);
            _logger.LogInformation("Bitmap 打印指令发送完毕。");
        }




        private async Task<bool> EnsureConnectionWithRetry()
        {
            for (int i = 1; i <= CONNECT_MAX_RETRIES; i++)
            {
                _logger.LogInformation($"""[第 {i}/{CONNECT_MAX_RETRIES} 次尝试] 正在扫描并连接 '{TARGET_DEVICE_NAME}'...""");

                var deviceDevice = await ScanForPrinter(TARGET_DEVICE_NAME);
                if (deviceDevice == null)
                {
                    _logger.LogWarning("未扫描到设备，等待 2 秒后重试...");
                    await Task.Delay(2000);
                    continue;
                }

                bool connected = await ConnectInternal(deviceDevice.Id, SERVICE_UUID, WRITE_UUID, NOTIFY_UUID);
                if (connected)
                {
                    _isConnected = true;
                    _logger.LogInformation(">>> 连接成功！ <<<");
                    return true;
                }
                else
                {
                    _logger.LogWarning("连接步骤失败，等待 2 秒后重试...");
                    DisposeBluetoothObjects();
                    await Task.Delay(2000);
                }
            }
            return false;
        }

        private async Task<DeviceInformation?> ScanForPrinter(string targetName)
        {
            DeviceInformation? foundDevice = null;
            var tcs = new TaskCompletionSource<bool>();

            string selector = "System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\"";
            string[] requestedProperties = ["System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected"];

            var watcher = DeviceInformation.CreateWatcher(
                selector,
                requestedProperties,
                DeviceInformationKind.AssociationEndpoint);

            watcher.Added += (sender, args) =>
            {
                if (!string.IsNullOrEmpty(args.Name) && args.Name.Contains(targetName))
                {
                    _logger.LogInformation(">>> ！！！锁定目标！！！ <<< ID: " + args.Id);
                    foundDevice = args;
                    watcher.Stop();
                    tcs.TrySetResult(true);
                }
            };

            watcher.Updated += (sender, args) => { };
            watcher.Stopped += (s, e) => tcs.TrySetResult(true);

            watcher.Start();
            // 扫描 5 秒
            await Task.WhenAny(tcs.Task, Task.Delay(5000));

            if (watcher.Status == DeviceWatcherStatus.Started) watcher.Stop();
            return foundDevice;
        }

        private async Task<bool> ConnectInternal(string deviceId, Guid serviceUuid, Guid writeUuid, Guid notifyUuid)
        {
            try
            {
                _bleDevice = await BluetoothLEDevice.FromIdAsync(deviceId);
                if (_bleDevice == null) return false;

                var servicesResult = await _bleDevice.GetGattServicesForUuidAsync(serviceUuid);
                if (servicesResult.Status != GattCommunicationStatus.Success) return false;

                var service = servicesResult.Services.FirstOrDefault();
                if (service == null) return false;

                var writeResult = await service.GetCharacteristicsForUuidAsync(writeUuid);
                _writeCharacteristic = writeResult.Characteristics.FirstOrDefault();

                var notifyResult = await service.GetCharacteristicsForUuidAsync(notifyUuid);
                _notifyCharacteristic = notifyResult.Characteristics.FirstOrDefault();

                if (_writeCharacteristic == null) return false;

                if (_notifyCharacteristic != null)
                {
                    var status = await _notifyCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify);

                    if (status == GattCommunicationStatus.Success)
                    {
                        _notifyCharacteristic.ValueChanged += NotifyCharacteristic_ValueChanged;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"连接异常: {ex.Message}");
                return false;
            }
        }

        private void NotifyCharacteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            byte[] value = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(value);
        }

        private async Task PrintBitmap(Bitmap bitmap)
        {
            if (bitmap.Width != MAX_LINE_BYTE * 8)
            {
                _logger.LogWarning("警告：图片宽度不匹配，可能会乱码。");
            }

            int width = bitmap.Width;
            int height = bitmap.Height;

            for (int y = 0; y < height; y++)
            {
                byte[] lineData = new byte[MAX_LINE_BYTE];

                for (int x = 0; x < width; x++)
                {
                    if (x / 8 >= MAX_LINE_BYTE) break;
                    Color pixel = bitmap.GetPixel(x, y);
                    bool isBlack = (pixel.R + pixel.G + pixel.B) < 384;

                    if (isBlack)
                    {
                        int byteIndex = x / 8;
                        int bitIndex = 7 - (x % 8);
                        lineData[byteIndex] |= (byte)(1 << bitIndex);
                    }
                }
                await Write(lineData);
            }

            // 发送结束指令
            byte[] finishCmd = [0xA6, 0xA6, 0xA6, 0xA6, 0x01];
            await Write(finishCmd);
        }

        private async Task Write(byte[] data)
        {
            if (_writeCharacteristic == null) return;
            try
            {
                var options = GattWriteOption.WriteWithoutResponse;
                var buffer = data.AsBuffer();
                await _writeCharacteristic.WriteValueAsync(buffer, options);
                await Task.Delay(5);
            }
            catch (Exception ex)
            {
                _logger.LogError($"写入失败: {ex.Message}");
            }
        }

        private static Bitmap CreatePrintImage(string text, string qrContent, int width)
        {
            QRCodeGenerator qrGenerator = new QRCodeGenerator();
            QRCodeData qrCodeData = qrGenerator.CreateQrCode(qrContent, QRCodeGenerator.ECCLevel.Q);
            QRCode qrCode = new QRCode(qrCodeData);
            Bitmap qrBitmap = qrCode.GetGraphic(20);

            int estimatedHeight = width;

            Bitmap finalImg = new Bitmap(width, estimatedHeight);
            using (Graphics g = Graphics.FromImage(finalImg))
            {
                g.Clear(Color.White);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

                int qrSize = 250;
                int qrX = (width - qrSize) / 2;
                g.DrawImage(qrBitmap, new Rectangle(qrX, 10, qrSize, qrSize));

                Font font = new("Arial", 24, FontStyle.Bold);
                SizeF textSize = g.MeasureString(text, font);
                float textX = (width - textSize.Width) / 2;
                float textY = qrSize + 20;

                g.DrawString(text, font, Brushes.Black, textX, textY);
            }
            return finalImg;
        }

        private void DisposeBluetoothObjects()
        {
            _bleDevice?.Dispose();
            _bleDevice = null;
            _writeCharacteristic = null;
            _notifyCharacteristic = null;
            _isConnected = false;
        }

        public void Dispose()
        {
            DisposeBluetoothObjects();
        }


    }
}