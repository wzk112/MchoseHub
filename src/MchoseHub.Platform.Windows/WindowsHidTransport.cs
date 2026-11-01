using System.Runtime.InteropServices;
using MchoseHub.Core.Transport;
using Microsoft.Win32.SafeHandles;

namespace MchoseHub.Platform.Windows;

/// <summary>
/// Windows HID 传输层：SetupAPI 枚举 + hid.dll 读写。
/// 不需要管理员权限，也不需要内核驱动——HID 设备由系统自带类驱动接管。
/// </summary>
public sealed class WindowsHidTransport : IHidTransport
{
    public IReadOnlyList<HidDeviceInfo> Enumerate(IReadOnlyCollection<ushort>? vendorIds = null)
    {
        var results = new List<HidDeviceInfo>();

        NativeMethods.HidD_GetHidGuid(out var hidGuid);
        var deviceInfoSet = NativeMethods.SetupDiGetClassDevs(
            ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);

        if (deviceInfoSet == NativeMethods.InvalidHandleValue)
        {
            return results;
        }

        try
        {
            var interfaceData = new NativeMethods.SP_DEVICE_INTERFACE_DATA
            {
                cbSize = Marshal.SizeOf<NativeMethods.SP_DEVICE_INTERFACE_DATA>(),
            };

            for (uint index = 0; ; index++)
            {
                if (!NativeMethods.SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                {
                    break;
                }

                var path = GetDevicePath(deviceInfoSet, ref interfaceData, out var containerId);
                if (path is null)
                {
                    continue;
                }

                var info = DescribeDevice(path);
                if (info is null)
                {
                    continue;
                }
                if (vendorIds is { Count: > 0 } && !vendorIds.Contains(info.VendorId))
                {
                    continue;
                }
                results.Add(info with { ContainerId = containerId });
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return results;
    }

    public IHidSession Open(HidDeviceInfo device) => new WindowsHidSession(device);

    public void Dispose()
    {
    }

    private static string? GetDevicePath(IntPtr deviceInfoSet, ref NativeMethods.SP_DEVICE_INTERFACE_DATA interfaceData, out Guid? containerId)
    {
        containerId = null;
        // 先问需要多大缓冲
        NativeMethods.SetupDiGetDeviceInterfaceDetail(
            deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out int requiredSize, IntPtr.Zero);
        if (requiredSize <= 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(requiredSize);
        try
        {
            // cbSize 只包含结构头 + 路径首字符（x64 下按对齐取 8）
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            var deviceData = new NativeMethods.SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<NativeMethods.SP_DEVINFO_DATA>() };
            if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(
                    deviceInfoSet, ref interfaceData, buffer, requiredSize, out _, ref deviceData))
            {
                return null;
            }
            var key = new NativeMethods.DEVPROPKEY { fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), pid = 2 };
            var bytes = new byte[16];
            if (NativeMethods.SetupDiGetDevicePropertyW(deviceInfoSet, ref deviceData, ref key,
                    out uint type, bytes, 16, out uint size, 0) && type == 0xD && size == 16)
            {
                containerId = new Guid(bytes);
            }
            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static HidDeviceInfo? DescribeDevice(string path)
    {
        // 先从设备路径解析 VID/PID：键盘的顶层集合常被系统独占，打不开时也要能认出来
        if (!TryParseIdsFromPath(path, out ushort pathVid, out ushort pathPid))
        {
            return null;
        }

        using var handle = OpenHandle(path);
        if (handle.IsInvalid)
        {
            // 打不开（被独占或权限不足）仍然返回设备，信息以路径解析为准
            return new HidDeviceInfo
            {
                Path = path,
                VendorId = pathVid,
                ProductId = pathPid,
                ProductName = null,
                InputReportLength = 0,
                OutputReportLength = 0,
                FeatureReportLength = 0,
            };
        }

        var attributes = new NativeMethods.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<NativeMethods.HIDD_ATTRIBUTES>() };
        if (!NativeMethods.HidD_GetAttributes(handle.DangerousGetHandle(), ref attributes))
        {
            attributes.VendorID = pathVid;
            attributes.ProductID = pathPid;
        }

        var caps = GetCaps(handle.DangerousGetHandle());
        return new HidDeviceInfo
        {
            Path = path,
            VendorId = attributes.VendorID,
            ProductId = attributes.ProductID,
            ProductName = ReadString(handle, NativeMethods.HidD_GetProductString),
            Manufacturer = ReadString(handle, NativeMethods.HidD_GetManufacturerString),
            SerialNumber = ReadString(handle, NativeMethods.HidD_GetSerialNumberString),
            UsagePage = caps.UsagePage,
            Usage = caps.Usage,
            InputReportLength = caps.InputReportByteLength,
            OutputReportLength = caps.OutputReportByteLength,
            FeatureReportLength = caps.FeatureReportByteLength,
        };
    }

    /// <summary>从 \\?\HID#VID_05AC&PID_024F&MI_01#… 里解析厂商与产品 ID。</summary>
    internal static bool TryParseIdsFromPath(string path, out ushort vendorId, out ushort productId)
    {
        vendorId = 0;
        productId = 0;

        var match = System.Text.RegularExpressions.Regex.Match(
            path,
            @"VID_([0-9A-Fa-f]{4}).*?PID_([0-9A-Fa-f]{4})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return false;
        }

        vendorId = Convert.ToUInt16(match.Groups[1].Value, 16);
        productId = Convert.ToUInt16(match.Groups[2].Value, 16);
        return true;
    }

    private static NativeMethods.HIDP_CAPS GetCaps(IntPtr handle)
    {
        if (!NativeMethods.HidD_GetPreparsedData(handle, out var preparsed))
        {
            return new NativeMethods.HIDP_CAPS { Reserved = new ushort[17] };
        }
        try
        {
            if (NativeMethods.HidP_GetCaps(preparsed, out var caps) < 0)
            {
                return new NativeMethods.HIDP_CAPS { Reserved = new ushort[17] };
            }
            caps.Reserved ??= new ushort[17];
            return caps;
        }
        finally
        {
            NativeMethods.HidD_FreePreparsedData(preparsed);
        }
    }

    private static string? ReadString(SafeFileHandle handle, Func<IntPtr, byte[], int, bool> reader)
    {
        var buffer = new byte[256];
        return reader(handle.DangerousGetHandle(), buffer, buffer.Length)
            ? System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0')
            : null;
    }

    internal static SafeFileHandle OpenHandle(string path) =>
        NativeMethods.CreateFileW(
            path,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);
}

internal sealed class WindowsHidSession : IHidSession
{
    private const int WriteTimeoutMs = 2000;

    /// <summary>读线程每片等待的时长；碎片化等待才能及时响应关闭。</summary>
    private const int ReadPollMs = 300;

    private readonly SafeFileHandle _handle;
    private readonly int _inputLength;
    private readonly int _outputLength;
    private readonly int _featureLength;
    private readonly Thread? _readThread;

    /// <summary>
    /// 读操作用的等待事件。它是手动重置的，每次 ReadFile 之前必须 Reset()，
    /// 否则第一笔 I/O 完成后事件一直处于有信号状态，后面的等待会立刻返回。
    /// </summary>
    private readonly ManualResetEvent _readEvent = new(false);

    private readonly object _writeGate = new();
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private volatile bool _closing;
    private int _disposed;

    public WindowsHidSession(HidDeviceInfo device)
    {
        Device = device;
        _handle = WindowsHidTransport.OpenHandle(device.Path);
        if (_handle.IsInvalid)
        {
            throw new IOException($"打开 HID 设备失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
        }

        _inputLength = device.InputReportLength;
        _outputLength = Math.Max(device.OutputReportLength, 1);
        _featureLength = Math.Max(device.FeatureReportLength, 1);

        if (_inputLength > 0)
        {
            _readThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = $"HID-Read-{device.VendorId:X4}:{device.ProductId:X4}",
            };
            _readThread.Start();
        }
    }

    public HidDeviceInfo Device { get; }
    public bool IsOpen => !_handle.IsInvalid && !_handle.IsClosed && !_closing;

    public event EventHandler<HidInputReport>? InputReportReceived;

    private void ReadLoop()
    {
        var buffer = new byte[_inputLength];

        // 缓冲区必须在**整个读写期间**保持钉住。ReadFile 返回 ERROR_IO_PENDING 时
        // 数据还没到，托管数组如果这时被 GC 搬走，内核就会往旧地址写，
        // 直接破坏托管堆（过一阵子以 coreclr 内部错误崩掉就是这个原因）。
        var pinnedBuffer = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var pointer = pinnedBuffer.AddrOfPinnedObject();
            while (!_closing)
            {
                _readEvent.Reset();
                var overlapped = new NativeMethods.OVERLAPPED { hEvent = _readEvent.SafeWaitHandle.DangerousGetHandle() };

                if (!NativeMethods.ReadFile(_handle, pointer, buffer.Length, out _, ref overlapped))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != NativeMethods.ERROR_IO_PENDING)
                    {
                        break;
                    }

                    if (!WaitForIo(ref overlapped))
                    {
                        // 关闭中或者等超时：发起取消，然后**必须**等到这笔 I/O 真的结束再出循环。
                        // overlapped 是栈上的局部变量，内核在取消完成时还会往里写；
                        // 提前出栈会让内核写到已经被复用的栈内存上，同样是直接崩进程。
                        NativeMethods.CancelIoEx(_handle, ref overlapped);
                        // Cancellation request alone does not complete the I/O. Keep both native
                        // OVERLAPPED and the pinned buffer alive until the kernel releases them.
                        NativeMethods.GetOverlappedResult(_handle, ref overlapped, out _, true);
                        break;
                    }
                }

                if (!NativeMethods.GetOverlappedResult(_handle, ref overlapped, out int read, false))
                {
                    break;
                }
                if (read <= 0)
                {
                    continue;
                }

                byte reportId = buffer[0];
                var payload = new byte[read - 1];
                Array.Copy(buffer, 1, payload, 0, read - 1);
                try { InputReportReceived?.Invoke(this, new HidInputReport(reportId, payload)); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"HID input subscriber failed: {ex}"); }
            }
        }
        finally
        {
            _closing = true;
            pinnedBuffer.Free();
        }
    }

    /// <summary>
    /// 分片等待一笔重叠 I/O 完成。
    /// timeoutMs 为 0 表示「一直等到有报文，或者被要求关闭」；大于 0 是硬性上限。
    /// </summary>
    private bool WaitForIo(ref NativeMethods.OVERLAPPED overlapped, int timeoutMs = 0)
    {
        int waited = 0;
        while (true)
        {
            if (_closing && timeoutMs == 0)
            {
                return false;
            }

            uint wait = NativeMethods.WaitForSingleObject(overlapped.hEvent, ReadPollMs);
            if (wait == NativeMethods.WAIT_OBJECT_0)
            {
                return true;
            }
            if (wait != NativeMethods.WAIT_TIMEOUT)
            {
                return false;
            }

            waited += ReadPollMs;
            if (timeoutMs > 0 && waited >= timeoutMs)
            {
                return false;
            }
        }
    }

    public void SendOutput(byte reportId, byte[] payload)
    {
        if (payload.Length > _outputLength - 1) throw new ArgumentException("output report 超过设备声明的长度。", nameof(payload));
        var buffer = new byte[_outputLength];
        buffer[0] = reportId;
        Array.Copy(payload, 0, buffer, 1, Math.Min(payload.Length, buffer.Length - 1));

        _ioGate.Wait();
        try
        {
            ThrowIfClosed();

            // 同一句柄上的写操作串行化，避免共用 OVERLAPPED
            lock (_writeGate)
            {
                // 写操作用独立事件：读线程正长期挂在 _readEvent 上，不能共用
                using var writeEvent = new ManualResetEvent(false);
                var overlapped = new NativeMethods.OVERLAPPED { hEvent = writeEvent.SafeWaitHandle.DangerousGetHandle() };

                // 同样要钉住：写完之前内核一直在读这块内存
                var pinnedBuffer = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    if (!NativeMethods.WriteFile(_handle, pinnedBuffer.AddrOfPinnedObject(), buffer.Length, out _, ref overlapped))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != NativeMethods.ERROR_IO_PENDING)
                        {
                            throw new IOException($"发送 output report 失败，Win32 错误码 {error}");
                        }

                        if (NativeMethods.WaitForSingleObject(overlapped.hEvent, WriteTimeoutMs) != NativeMethods.WAIT_OBJECT_0)
                        {
                            // 取消之后仍要等 I/O 收尾：writeEvent 是用 using 释放的，
                            // 内核在取消完成时还要给这个事件发信号
                            NativeMethods.CancelIoEx(_handle, ref overlapped);
                            NativeMethods.GetOverlappedResult(_handle, ref overlapped, out _, true);
                            throw new TimeoutException(
                                $"发送 output report 超时（{WriteTimeoutMs}ms）。" +
                                "该 HID 集合可能不支持 output report，或设备未准备好接收。");
                        }
                    }

                    if (!NativeMethods.GetOverlappedResult(_handle, ref overlapped, out _, false))
                    {
                        throw new IOException($"output report 未完成，Win32 错误码 {Marshal.GetLastWin32Error()}");
                    }
                }
                finally
                {
                    pinnedBuffer.Free();
                }
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public void SendFeature(byte reportId, byte[] payload)
    {
        if (payload.Length > _featureLength - 1) throw new ArgumentException("feature report 超过设备声明的长度。", nameof(payload));
        // 关闭设备时如果还有线程卡在 I/O 里，句柄会被提前释放，轻则报错重则直接崩进程，
        // 所以所有 feature report 操作都要走这道闸，并且在闸内重新检查一次状态。
        _ioGate.Wait();
        try
        {
            ThrowIfClosed();

            // 长度按设备声明的 feature 长度来：长度或 report id 跟描述符不符时
            // HidD_SetFeature 会立刻返回 87（ERROR_INVALID_PARAMETER）。
            var buffer = new byte[_featureLength];
            buffer[0] = reportId;
            Array.Copy(payload, 0, buffer, 1, Math.Min(payload.Length, buffer.Length - 1));
            if (!NativeMethods.HidD_SetFeature(_handle.DangerousGetHandle(), buffer, buffer.Length))
            {
                throw new IOException($"发送 feature report 失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public byte[] GetFeature(byte reportId, int length)
    {
        _ioGate.Wait();
        try
        {
            ThrowIfClosed();

            // 读 feature 的缓冲同样按设备声明的长度来，给大了会被判无效参数（87）。
            var size = _featureLength > 0 ? _featureLength : Math.Max(length, 64);
            var buffer = new byte[size];
            buffer[0] = reportId;
            if (!NativeMethods.HidD_GetFeature(_handle.DangerousGetHandle(), buffer, buffer.Length))
            {
                throw new IOException($"读取 feature report 失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
            }

            return buffer;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>句柄或会话已经收掉时统一抛这个，调用方按「重新打开设备」处理。</summary>
    private void ThrowIfClosed()
    {
        if (_closing || _handle.IsClosed || _handle.IsInvalid)
        {
            throw new ObjectDisposedException(nameof(WindowsHidSession), "设备已经断开，请重新打开。");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 先挂上关闭标记（新的 I/O 会立刻失败），再等已经在跑的 I/O 退出来，最后才放句柄
        _closing = true;
        bool stopped = false;
        _ioGate.Wait();
        try
        {
            // 不要去 Set 读事件：读线程自己会在 300ms 的等待切片里发现 _closing，
            // 取消那笔重叠 I/O 并等它真正收尾后才退出（见 ReadLoop）。
            stopped = _readThread is null || _readThread.Join(TimeSpan.FromSeconds(6));
            if (stopped)
            {
                _handle.Dispose();
            }
        }
        finally
        {
            _ioGate.Release();
        }

        if (stopped)
        {
            _readEvent.Dispose();
        }
        else
        {
            // Defer release until the cancellation actually completes.
            _ = Task.Run(() => { _readThread!.Join(); _handle.Dispose(); _readEvent.Dispose(); });
        }

        // 未结束的 I/O 持有资源，后台等待结束后再释放。
    }
}
