using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SkiaSharp;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace AmazfitHeartRateMonitor
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        // 蓝牙服务UUID
        private const string HEART_RATE_SERVICE_UUID = "0000180d-0000-1000-8000-00805f9b34fb";
        private const string HEART_RATE_CHARACTERISTIC_UUID = "00002a37-0000-1000-8000-00805f9b34fb";

        // 蓝牙相关成员
        private BluetoothLEAdvertisementWatcher? _watcher;
        private BluetoothLEDevice? _connectedDevice;
        private GattCharacteristic? _heartRateCharacteristic;

        // 数据成员
        private DateTime _lastUpdate = DateTime.Now;
        private int _currentHeartRate = 0;
        private List<int> _heartRateHistory = [];
        private readonly DispatcherTimer _updateTimer = new();

        // Web服务器相关
        private IHost? _webHost;
        private Task? _webServerTask;
        private CancellationTokenSource? _cancellationTokenSource;
        private string _webServerUrl = "http://localhost:5001/";
        private int _webServerPort = 5001;

        // UI数据绑定属性
        public ObservableCollection<ObservableValue> HeartRateValues { get; set; } = new ObservableCollection<ObservableValue>();

        public ISeries[] Series { get; set; } =
        [
            new LineSeries<ObservableValue>
            {
                Values = new ObservableCollection<ObservableValue>(),
                Stroke = new SolidColorPaint(new SKColor(236, 72, 153), 3),
                Fill = null,
                GeometryStroke = null,
                GeometrySize = 0,
                LineSmoothness = 0.8
            }
        ];

        public Axis[] XAxes { get; set; } =
        [
            new Axis
            {
                Labeler = value => DateTime.Now.AddSeconds(value - 30).ToString("HH:mm:ss"),
                TextSize = 12,
                LabelsPaint = new SolidColorPaint(new SKColor(148, 163, 184)),
                SeparatorsPaint = new SolidColorPaint(SKColors.Transparent),
                MinStep = 10,
                ForceStepToMin = true
            }
        ];

        public Axis[] YAxes { get; set; } =
        [
            new Axis
            {
                MinLimit = 40,
                MaxLimit = 200,
                TextSize = 12,
                LabelsPaint = new SolidColorPaint(new SKColor(148, 163, 184)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(51, 67, 90), 1)
            }
        ];

        // 统计信息
        public int AverageHeartRate => _heartRateHistory.Count > 0 ? (int)_heartRateHistory.Average() : 0;
        public int MaxHeartRate => _heartRateHistory.Count > 0 ? _heartRateHistory.Max() : 0;
        public int MinHeartRate => _heartRateHistory.Count > 0 ? _heartRateHistory.Min() : 0;

        public event PropertyChangedEventHandler? PropertyChanged;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            // 初始化图表数据
            for (int i = 0; i < 30; i++)
            {
                HeartRateValues.Add(new ObservableValue(0));
            }
            ((LineSeries<ObservableValue>)Series[0]).Values = HeartRateValues;

            // 设置状态
            UpdateStatus("准备就绪");
            UpdateDeviceInfo("未连接", "--", "--");
            UpdateHeartRate(0);

            // 初始化UI更新计时器
            _updateTimer.Interval = TimeSpan.FromMilliseconds(500);
            _updateTimer.Tick += (s, e) => UpdateStatistics();
            _updateTimer.Start();

            // 初始化蓝牙监控器
            InitializeBluetoothWatcher();
        }

        private void InitializeBluetoothWatcher()
        {
            _watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            // 添加心率服务过滤
            _watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(
                BluetoothUuidHelper.FromShortId(0x180D));

            _watcher.Received += async (sender, args) =>
            {
                try
                {
                    // 获取蓝牙设备
                    var device = await BluetoothLEDevice.FromBluetoothAddressAsync(args.BluetoothAddress);
                    if (device == null) return;

                    // 更新UI线程
                    Dispatcher.Invoke(() =>
                    {
                        UpdateStatus($"检测到设备: {device.Name}");
                        UpdateDeviceInfo(device.Name, device.BluetoothAddress.ToString("X"), $"{args.RawSignalStrengthInDBm} dBm");
                    });

                    // 连接到设备
                    await ConnectToDevice(device);
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => UpdateStatus($"错误: {ex.Message}"));
                }
            };
        }

        private async Task ConnectToDevice(BluetoothLEDevice device)
        {
            try
            {
                // 获取心率服务
                var hrServiceResult = await device.GetGattServicesForUuidAsync(
                    BluetoothUuidHelper.FromShortId(0x180D), BluetoothCacheMode.Uncached);

                if (hrServiceResult.Status != GattCommunicationStatus.Success ||
                    hrServiceResult.Services.Count == 0)
                {
                    Dispatcher.Invoke(() => UpdateStatus("未找到心率服务"));
                    return;
                }

                var hrService = hrServiceResult.Services[0];

                // 获取心率特征值
                var hrCharResult = await hrService.GetCharacteristicsForUuidAsync(
                    BluetoothUuidHelper.FromShortId(0x2A37), BluetoothCacheMode.Uncached);

                if (hrCharResult.Status != GattCommunicationStatus.Success ||
                    hrCharResult.Characteristics.Count == 0)
                {
                    return;
                }

                _heartRateCharacteristic = hrCharResult.Characteristics[0];
                _connectedDevice = device;

                // 订阅心率变化通知
                var status = await _heartRateCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);

                if (status == GattCommunicationStatus.Success)
                {
                    _heartRateCharacteristic.ValueChanged += HeartRateValueChanged;
                    Dispatcher.Invoke(() =>
                    {
                        UpdateStatus("已连接 - 接收数据中");
                        FooterText.Text = "已连接Amazfit Balance - 实时接收心率数据";
                    });
                }
                else
                {
                    Dispatcher.Invoke(() => UpdateStatus($"订阅失败: {status}"));
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => UpdateStatus($"连接错误: {ex.Message}"));
            }
        }

        private void HeartRateValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            try
            {
                using var reader = DataReader.FromBuffer(args.CharacteristicValue);
                int heartRate = ParseHeartRateValue(reader);

                // 在UI线程上更新数据
                Dispatcher.Invoke(() => UpdateHeartRate(heartRate));
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => UpdateStatus($"解析错误: {ex.Message}"));
            }
        }

        private int ParseHeartRateValue(DataReader reader)
        {
            reader.ByteOrder = ByteOrder.LittleEndian;

            // 读取标志位
            byte flags = reader.ReadByte();
            bool is16bit = (flags & 0x01) != 0;

            return is16bit ? reader.ReadUInt16() : reader.ReadByte();
        }

        private void UpdateHeartRate(int heartRate)
        {
            _currentHeartRate = heartRate;
            HeartRateText.Text = heartRate > 0 ? heartRate.ToString() : "--";
            _lastUpdate = DateTime.Now;
            UpdateTimeText.Text = $"{_lastUpdate:T}";

            // 更新图表
            HeartRateValues.Add(new ObservableValue(heartRate));

            // 保持最近30个数据点
            if (HeartRateValues.Count > 30)
            {
                HeartRateValues.RemoveAt(0);
            }

            // 记录历史数据用于统计
            _heartRateHistory.Add(heartRate);
            if (_heartRateHistory.Count > 100)
            {
                _heartRateHistory.RemoveAt(0);
            }

            // 更新心率区间
            UpdateHeartRateZone(heartRate);

            // 更新当前状态
            UpdateCurrentStatus(heartRate);
        }

        private void UpdateHeartRateZone(int heartRate)
        {
            if (heartRate <= 0)
            {
                ZoneText.Text = "请连接设备获取心率数据";
                ZoneText.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
                return;
            }

            string zone;
            Brush zoneColor;

            if (heartRate < 60)
            {
                zone = "休息";
                zoneColor = Brushes.Gray;
            }
            else if (heartRate < 90)
            {
                zone = "热身";
                zoneColor = Brushes.LightBlue;
            }
            else if (heartRate < 120)
            {
                zone = "燃脂";
                zoneColor = new SolidColorBrush(Color.FromRgb(139, 92, 246)); // 紫色
            }
            else if (heartRate < 150)
            {
                zone = "有氧";
                zoneColor = new SolidColorBrush(Color.FromRgb(236, 72, 153)); // 粉色
            }
            else
            {
                zone = "极限";
                zoneColor = new SolidColorBrush(Color.FromRgb(244, 63, 94)); // 红色
            }

            ZoneText.Text = $"当前区间: {zone}";
            ZoneText.Foreground = zoneColor;
        }

        private void UpdateCurrentStatus(int heartRate)
        {
            if (heartRate < 60)
            {
                CurrentStatusText.Text = "过低";
                CurrentStatusText.Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246)); // 蓝色
            }
            else if (heartRate > 150)
            {
                CurrentStatusText.Text = "过高";
                CurrentStatusText.Foreground = new SolidColorBrush(Color.FromRgb(244, 63, 94)); // 红色
            }
            else if (heartRate > 120)
            {
                CurrentStatusText.Text = "运动";
                CurrentStatusText.Foreground = new SolidColorBrush(Color.FromRgb(236, 72, 153)); // 粉色
            }
            else
            {
                CurrentStatusText.Text = "正常";
                CurrentStatusText.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // 绿色
            }
        }

        private void UpdateStatistics()
        {
            AvgHeartRateText.Text = AverageHeartRate > 0 ? AverageHeartRate.ToString() : "--";
            MaxHeartRateText.Text = MaxHeartRate > 0 ? MaxHeartRate.ToString() : "--";
            MinHeartRateText.Text = MinHeartRate > 0 ? MinHeartRate.ToString() : "--";
        }

        private void UpdateStatus(string status)
        {
            StatusText.Text = status;
        }

        private void UpdateDeviceInfo(string name, string address, string signal)
        {
            DeviceNameText.Text = name;
            DeviceAddressText.Text = address;
            SignalText.Text = signal;
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started)
            {
                _watcher.Stop();
                StartButton.Content = "开始扫描";
                UpdateStatus("已停止扫描");
                FooterText.Text = "扫描已停止";
            }
            else
            {
                _watcher?.Start();
                StartButton.Content = "停止扫描";
                UpdateStatus("正在扫描设备...");
                FooterText.Text = "正在搜索Amazfit Balance设备...";
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            HeartRateValues.Clear();
            for (int i = 0; i < 30; i++)
            {
                HeartRateValues.Add(new ObservableValue(0));
            }
            _heartRateHistory.Clear();
            UpdateHeartRate(0);
            UpdateStatistics();
        }

        private void WebServerButton_Click(object sender, RoutedEventArgs e)
        {
            if (_webHost != null)
            {
                StopWebServer();
                WebServerButton.Content = "启动Web服务";
                WebServerStatus.Text = "Web服务已停止";
            }
            else
            {
                StartWebServer();
                WebServerButton.Content = "停止Web服务";
                WebServerStatus.Text = $"Web服务运行中: {_webServerUrl}";
            }
        }

        private void StartWebServer()
        {
            try
            {
                _cancellationTokenSource = new CancellationTokenSource();

                _webHost = Host.CreateDefaultBuilder()
                    .ConfigureWebHostDefaults(webBuilder =>
                    {
                        webBuilder.UseUrls(_webServerUrl);
                        webBuilder.Configure(app =>
                        {
                            app.UseRouting();

                            // 提供静态文件
                            app.UseStaticFiles();

                            // API端点获取实时心率
                            app.UseEndpoints(endpoints =>
                            {
                                endpoints.MapGet("/api/heartrate", async context =>
                                {
                                    await context.Response.WriteAsJsonAsync(new
                                    {
                                        heartRate = _currentHeartRate,
                                        timestamp = DateTime.Now.ToString("o")
                                    });
                                });

                                endpoints.MapGet("/", async context =>
                                {
                                    // 提供自定义的心率卡片HTML
                                    var html = GenerateHeartRateCardHtml();
                                    context.Response.ContentType = "text/html";
                                    await context.Response.WriteAsync(html);
                                });
                            });
                        });
                    })
                    .Build();

                _webServerTask = _webHost.StartAsync(_cancellationTokenSource.Token);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _webServerUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                UpdateStatus($"启动Web服务失败: {ex.Message}");
            }
        }

        private void StopWebServer()
        {
            try
            {
                _cancellationTokenSource?.Cancel();
                _webHost?.StopAsync().Wait();
                _webHost?.Dispose();
                _webHost = null;
            }
            catch (Exception ex)
            {
                UpdateStatus($"停止Web服务失败: {ex.Message}");
            }
        }

        private string GenerateHeartRateCardHtml()
        {
            return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>实时心率卡片</title>
    <style>
        body {{
            margin: 0;
            padding: 0;
            display: flex;
            justify-content: center;
            align-items: center;
            height: 100vh;
            background-color: #f0f0f0;
            font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
        }}
        
        .heart-rate-card {{
            width: 300px;
            height: 300px;
            background: linear-gradient(135deg, #6a11cb 0%, #2575fc 100%);
            border-radius: 20px;
            box-shadow: 0 10px 30px rgba(0, 0, 0, 0.2);
            display: flex;
            flex-direction: column;
            justify-content: center;
            align-items: center;
            color: white;
            position: relative;
            overflow: hidden;
        }}
        
        .heart-rate-card::before {{
            content: '';
            position: absolute;
            top: -50%;
            left: -50%;
            width: 200%;
            height: 200%;
            background: radial-gradient(circle, rgba(255,255,255,0.1) 0%, rgba(255,255,255,0) 70%);
            animation: pulse 2s infinite;
        }}
        
        @keyframes pulse {{
            0% {{ transform: scale(0.5); opacity: 0.5; }}
            70% {{ transform: scale(1.2); opacity: 0; }}
            100% {{ transform: scale(1.2); opacity: 0; }}
        }}
        
        .heart-icon {{
            font-size: 64px;
            margin-bottom: 15px;
            animation: heartbeat 1.5s infinite;
        }}
        
        @keyframes heartbeat {{
            0% {{ transform: scale(1); }}
            14% {{ transform: scale(1.3); }}
            28% {{ transform: scale(1); }}
            42% {{ transform: scale(1.3); }}
            70% {{ transform: scale(1); }}
        }}
        
        .heart-rate-value {{
            font-size: 72px;
            font-weight: bold;
            margin-bottom: 5px;
            text-shadow: 0 2px 10px rgba(0, 0, 0, 0.2);
        }}
        
        .heart-rate-label {{
            font-size: 24px;
            opacity: 0.9;
        }}
        
        .device-info {{
            position: absolute;
            bottom: 15px;
            font-size: 14px;
            opacity: 0.7;
        }}
        
        .timestamp {{
            position: absolute;
            top: 15px;
            font-size: 14px;
            opacity: 0.7;
        }}
    </style>
</head>
<body>
    <div class='heart-rate-card'>
        <div class='timestamp' id='timestamp'>--</div>
        <div class='heart-icon'>❤️</div>
        <div class='heart-rate-value' id='heartRate'>--</div>
        <div class='heart-rate-label'>心率 (BPM)</div>
        <div class='device-info'>Amazfit Balance</div>
    </div>
    
    <script>
        async function updateHeartRate() {{
            try {{
                const response = await fetch('/api/heartrate');
                const data = await response.json();
                
                document.getElementById('heartRate').textContent = data.heartRate || '--';
                document.getElementById('timestamp').textContent = new Date(data.timestamp).toLocaleTimeString();
                
                // 根据心率值调整动画速度
                const heartIcon = document.querySelector('.heart-icon');
                if (data.heartRate > 120) {{
                    heartIcon.style.animationDuration = '0.8s';
                }} else if (data.heartRate > 90) {{
                    heartIcon.style.animationDuration = '1s';
                }} else {{
                    heartIcon.style.animationDuration = '1.5s';
                }}
            }} catch (error) {{
                console.error('获取心率失败:', error);
            }}
        }}
        
        // 初始加载
        updateHeartRate();
        // 每2秒更新一次
        setInterval(updateHeartRate, 2000);
    </script>
</body>
</html>
";
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            // 清理资源
            if (_watcher != null)
            {
                _watcher.Stop();
                _watcher = null;
            }

            if (_heartRateCharacteristic != null)
            {
                _heartRateCharacteristic.ValueChanged -= HeartRateValueChanged;
                _heartRateCharacteristic = null;
            }

            if (_connectedDevice != null)
            {
                _connectedDevice.Dispose();
                _connectedDevice = null;
            }

            _updateTimer.Stop();

            // 停止Web服务器
            StopWebServer();
        }

        protected virtual void OnPropertyChanged(string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}