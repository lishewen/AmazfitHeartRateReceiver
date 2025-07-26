using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace AmazfitHeartRateReceiver;

internal class Program
{
    // 蓝牙服务UUID常量
    private const string HEART_RATE_SERVICE_UUID = "0000180d-0000-1000-8000-00805f9b34fb";
    private const string HEART_RATE_CHARACTERISTIC_UUID = "00002a37-0000-1000-8000-00805f9b34fb";

    // 用于存储检测到的手表设备
    private static readonly Dictionary<ulong, BluetoothLEDevice> devices = [];
    static void Main(string[] args)
    {
        Console.WriteLine("正在启动Amazfit Balance心率接收程序...");
        StartHeartRateScanner();
        Console.ReadLine(); // 保持程序运行
    }
    static void StartHeartRateScanner()
    {
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        // 添加心率服务过滤
        watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(BluetoothUuidHelper.FromShortId(0x180D));

        watcher.Received += async (sender, args) =>
        {
            try
            {
                // 获取蓝牙设备
                var device = await BluetoothLEDevice.FromBluetoothAddressAsync(args.BluetoothAddress);
                if (device == null) return;

                // 检查是否已处理过该设备
                if (devices.ContainsKey(device.BluetoothAddress)) return;
                devices.Add(device.BluetoothAddress, device);

                Console.WriteLine($"检测到设备: {device.Name} ({device.BluetoothAddress:X})");

                // 获取心率服务
                var hrService = (await device.GetGattServicesForUuidAsync(BluetoothUuidHelper.FromShortId(0x180D))).Services[0];
                if (hrService == null) return;

                // 获取心率特征值
                var hrCharacteristic = (await hrService.GetCharacteristicsForUuidAsync(BluetoothUuidHelper.FromShortId(0x2A37))).Characteristics[0];
                if (hrCharacteristic == null) return;

                // 启用特征值通知
                hrCharacteristic.ValueChanged += HeartRateValueChanged;
                await hrCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);

                Console.WriteLine("已订阅心率数据通知");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"错误: {ex.Message}");
            }
        };

        watcher.Start();
        Console.WriteLine("扫描已启动，等待Amazfit Balance手表广播...");
    }

    private static void HeartRateValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            using var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var heartRate = ParseHeartRateValue(reader);
            Console.WriteLine($"[{DateTime.Now:T}] 心率: {heartRate} BPM");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"解析错误: {ex.Message}");
        }
    }

    private static int ParseHeartRateValue(DataReader reader)
    {
        reader.ByteOrder = ByteOrder.LittleEndian;

        // 读取标志位
        byte flags = reader.ReadByte();
        bool is16bit = (flags & 0x01) != 0;

        return is16bit ? reader.ReadUInt16() : reader.ReadByte();
    }
}