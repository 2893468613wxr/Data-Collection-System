using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WpfApp22.Core.Abstractions;
using WpfApp22.Core.Models;
using WpfApp22.Core.Services;
using WpfApp22.Infrastructure.Drivers;
using WpfApp22.Infrastructure.Export;
using WpfApp22.Infrastructure.Protocols;

// 自检程序：不依赖 UI，直接验证协议编解码、采集引擎、报警引擎与导出链路。
// 在没有真实设备的开发机上，这是保证重构不回归的最快手段。
var passed = 0;
var failed = 0;

void Check(string name, bool ok, string? detail = null)
{
    if (ok)
    {
        passed++;
        Console.WriteLine($"  PASS  {name}");
    }
    else
    {
        failed++;
        Console.WriteLine($"  FAIL  {name}{(detail is null ? "" : $"  -> {detail}")}");
    }
}

Console.WriteLine("[1] Modbus RTU 编解码");
{
    var codec = new ModbusRtuCodec();
    var request = codec.BuildReadRequest(1, 0, 2, RegisterKind.HoldingRegister);
    Check("请求帧长度 8", request.Length == 8);
    Check("功能码 0x03", request[1] == 0x03);
    var crc = ModbusRtuCodec.Crc16(request, 0, 6);
    Check("CRC 低字节在前", request[6] == (byte)(crc & 0xFF) && request[7] == (byte)(crc >> 8));

    var response = new byte[] { 1, 3, 4, 0, 1, 0, 2, 0, 0 };
    var responseCrc = ModbusRtuCodec.Crc16(response, 0, 7);
    response[7] = (byte)(responseCrc & 0xFF);
    response[8] = (byte)(responseCrc >> 8);

    var ok = codec.TryExtractFrame(response, response.Length, out var frame, out var consumed);
    Check("响应解析成功", ok && frame is not null);
    Check("解析出 2 个寄存器", frame?.Registers.Length == 2);
    Check("寄存器值正确", frame!.Registers[0] == 1 && frame.Registers[1] == 2);
    Check("消费字节数 = 9", consumed == 9);

    // 现场最常见的脏数据：帧前有残留字节，必须能自动重新同步
    var dirty = new byte[response.Length + 2];
    dirty[0] = 0xFF;
    dirty[1] = 0x00;
    Array.Copy(response, 0, dirty, 2, response.Length);
    ok = codec.TryExtractFrame(dirty, dirty.Length, out var dirtyFrame, out _);
    Check("前导脏数据可重新同步", ok && dirtyFrame?.Registers.Length == 2);

    // CRC 错误的帧必须被拒绝
    var broken = (byte[])response.Clone();
    broken[3] ^= 0xFF;
    ok = codec.TryExtractFrame(broken, broken.Length, out _, out _);
    Check("CRC 错误帧被拒绝", !ok);

    // 0x01 读线圈：请求功能码 + 位响应解析
    var coilRequest = codec.BuildReadRequest(1, 3, 1, RegisterKind.Coil);
    Check("线圈请求功能码 0x01", coilRequest[1] == 0x01);
    Check("线圈请求地址 = 3", coilRequest[2] == 0 && coilRequest[3] == 3);
    Check("离散输入请求功能码 0x02", codec.BuildReadRequest(1, 0, 1, RegisterKind.DiscreteInput)[1] == 0x02);

    // 位响应：地址 3 起始，返回 1 字节 0b0000_1010 → 第 1、3 位为 1（bit0 = 起始地址那一位）
    var coilResp = new byte[] { 1, 1, 1, 0b0000_1010, 0, 0 };
    var coilCrc = ModbusRtuCodec.Crc16(coilResp, 0, 4);
    coilResp[4] = (byte)(coilCrc & 0xFF);
    coilResp[5] = (byte)(coilCrc >> 8);
    ok = codec.TryExtractFrame(coilResp, coilResp.Length, out var coilFrame, out _);
    Check("线圈响应解析成功", ok && coilFrame?.FunctionCode == 0x01);
    Check("位展开正确（0b1010）",
        coilFrame!.Bits.Length == 8 && !coilFrame.Bits[0] && coilFrame.Bits[1] && !coilFrame.Bits[2] && coilFrame.Bits[3]);
}

Console.WriteLine("[2] Modbus TCP 编解码");
{
    var codec = new ModbusTcpCodec();
    var request = codec.BuildReadRequest(1, 0, 2, RegisterKind.InputRegister);
    Check("请求帧长度 12", request.Length == 12);
    Check("协议标识为 0", request[2] == 0 && request[3] == 0);
    Check("长度字段为 6", request[4] == 0 && request[5] == 6);
    Check("输入寄存器功能码 0x04", request[7] == 0x04);

    // MBAP 长度字段 = 单元标识 1 字节 + PDU 6 字节（功能码 1 + 字节数 1 + 数据 4）
    var response = new byte[] { 0, 1, 0, 0, 0, 7, 1, 3, 4, 0, 1, 0, 2 };
    var ok = codec.TryExtractFrame(response, response.Length, out var frame, out var consumed);
    Check("响应解析成功", ok && frame?.Registers.Length == 2);
    Check("消费字节数 = 13", consumed == 13);

    // 只收到半个 MBAP 头时不能误判为完整帧
    var partial = new byte[] { 0, 1, 0, 0, 0, 9, 1, 3 };
    ok = codec.TryExtractFrame(partial, partial.Length, out _, out _);
    Check("半包不误判", !ok);

    // 0x02 读离散输入
    Check("离散输入请求功能码 0x02", codec.BuildReadRequest(1, 0, 1, RegisterKind.DiscreteInput)[7] == 0x02);
    Check("线圈请求功能码 0x01", codec.BuildReadRequest(1, 0, 1, RegisterKind.Coil)[7] == 0x01);

    // 位响应：MBAP + 单元标识 + 0x02 + 字节数 1 + 数据 0b0000_0011
    var bitResp = new byte[] { 0, 1, 0, 0, 0, 4, 1, 2, 1, 0b0000_0011 };
    ok = codec.TryExtractFrame(bitResp, bitResp.Length, out var bitFrame, out var bitConsumed);
    Check("TCP 位响应解析成功", ok && bitFrame?.FunctionCode == 0x02);
    Check("TCP 位展开正确", bitFrame!.Bits.Length == 8 && bitFrame.Bits[0] && bitFrame.Bits[1] && !bitFrame.Bits[2]);
    Check("TCP 位帧消费字节数 = 10", bitConsumed == 10, bitConsumed.ToString());
}

Console.WriteLine("[3] ASCII 行协议");
{
    var codec = new AsciiLineCodec();
    var line = Encoding.ASCII.GetBytes("R100=25.37\r\n");
    var ok = codec.TryExtractFrame(line, line.Length, out var frame, out _);
    Check("解析出工程量", ok && frame?.ScalarValue is 25.37);
}

Console.WriteLine("[4] 寄存器解码与功能码映射");
{
    // 40.0f 的 IEEE754 表示 = 0x42200000
    var regs = new ushort[] { 0x4220, 0x0000 };
    Check("Float32 高字在前", Math.Abs(RegisterDecoder.Decode(regs, ValueDataType.Float32, WordOrder.HighWordFirst) - 40.0) < 1e-6);
    Check("Float32 低字在前可识别", Math.Abs(RegisterDecoder.Decode(regs, ValueDataType.Float32, WordOrder.LowWordFirst) - 40.0) > 1);
    Check("Int16 有符号", RegisterDecoder.Decode(new ushort[] { 0xFFFF }, ValueDataType.Int16, WordOrder.HighWordFirst) == -1);
    Check("UInt16 无符号", RegisterDecoder.Decode(new ushort[] { 0xFFFF }, ValueDataType.UInt16, WordOrder.HighWordFirst) == 65535);

    Check("保持寄存器 → 0x03", RegisterKind.HoldingRegister.FunctionCode() == 0x03);
    Check("输入寄存器 → 0x04", RegisterKind.InputRegister.FunctionCode() == 0x04);
    Check("线圈 → 0x01", RegisterKind.Coil.FunctionCode() == 0x01);
    Check("离散输入 → 0x02", RegisterKind.DiscreteInput.FunctionCode() == 0x02);
    Check("位类型判断", RegisterKind.Coil.IsBitAccess() && RegisterKind.DiscreteInput.IsBitAccess()
                        && !RegisterKind.HoldingRegister.IsBitAccess());
    Check("位类型只请求 1 个位",
        new PointDefinition { RegisterKind = RegisterKind.Coil, DataType = ValueDataType.Float32 }.RegisterCount == 1);
    Check("旧配置里的 HoldingRegister 仍可用", RegisterKind.HoldingRegister.DisplayName().Contains("0x03"));
}

Console.WriteLine("[5] 报警引擎：去抖与滞回");
{
    var engine = new AlarmEngine();
    var raised = 0;
    var cleared = 0;
    engine.AlarmRaised += _ => raised++;
    engine.AlarmCleared += _ => cleared++;

    var point = new PointDefinition
    {
        Name = "压力",
        Unit = "MPa",
        Alarm = new AlarmRule
        {
            Enabled = true,
            Operator = AlarmOperator.GreaterThan,
            Threshold = 100,
            Hysteresis = 10,
            DebounceCount = 2,
            Severity = AlarmSeverity.Warning
        }
    };

    var channelId = Guid.NewGuid();
    engine.Evaluate(channelId, "CH", point, new Sample(1000, 50));
    Check("正常值不报警", raised == 0);

    engine.Evaluate(channelId, "CH", point, new Sample(1001, 110));
    Check("首次超限不触发（去抖中）", raised == 0);

    engine.Evaluate(channelId, "CH", point, new Sample(1002, 110));
    Check("连续超限后触发", raised == 1);

    engine.Evaluate(channelId, "CH", point, new Sample(1003, 105));
    Check("滞回区内保持报警不恢复", cleared == 0);

    engine.Evaluate(channelId, "CH", point, new Sample(1004, 80));
    Check("回落超过滞回后恢复", cleared == 1 && raised == 1);

    // 恢复后再次触发同样需要重新满足去抖次数，这是有意为之：防止恢复瞬间立刻又报警
    engine.Evaluate(channelId, "CH", point, new Sample(1005, 120));
    engine.Evaluate(channelId, "CH", point, new Sample(1006, 120));
    Check("恢复后可再次触发", raised == 2);
}

Console.WriteLine("[6] 环形缓冲");
{
    var buffer = new WpfApp22.Core.Buffers.TimeSeriesBuffer(10);
    for (var i = 0; i < 15; i++) buffer.Append(i, i * 1.5);
    Check("容量封顶", buffer.Count == 10);
    Check("累计计数", buffer.TotalAppended == 15);

    var times = new double[10];
    var values = new double[10];
    var n = buffer.CopyLatest(10, times, values);
    Check("取最新 10 点", n == 10);
    Check("最老点为 5（已被覆盖）", Math.Abs(times[0] - 5) < 1e-9, times[0].ToString());
    Check("最新点为 14", Math.Abs(times[9] - 14) < 1e-9);
}

Console.WriteLine("[7] 端到端采集（内置模拟器）");
{
    var factory = new DeviceDriverFactory();
    var point = new PointDefinition
    {
        Name = "温度",
        Unit = "℃",
        RegisterAddress = 0,
        Alarm = new AlarmRule
        {
            Enabled = true,
            Operator = AlarmOperator.GreaterThan,
            Threshold = -1000,
            DebounceCount = 1,
            Severity = AlarmSeverity.Critical,
            Message = "自检触发"
        }
    };
    var config = new ChannelConfig { Name = "自检通道", PollIntervalMs = 50, BufferCapacity = 5000 };
    config.Points.Add(point);

    var service = new AcquisitionService(factory, pipelineCapacity: 10_000);
    var runtime = service.AddChannel(config);

    var alarmCount = 0;
    service.AlarmRaised += _ => Interlocked.Increment(ref alarmCount);

    await service.StartAsync();
    await service.StartAllAsync();
    await Task.Delay(1500);
    await service.StopAllAsync();

    var store = service.Hub.Get(point.Id);
    Check("通道进入采集状态", runtime.TotalSamples > 10, runtime.TotalSamples.ToString());
    Check("缓冲区写入数据", store is { Buffer.Count: > 10 }, store?.Buffer.Count.ToString());
    Check("报警链路打通", alarmCount > 0, alarmCount.ToString());
    Check("无采样丢弃", service.DroppedSamples == 0, service.DroppedSamples.ToString());

    Console.WriteLine("[8] 导出 CSV / Excel");
    var times = new double[store!.Buffer.Count];
    var values = new double[times.Length];
    var count = store.FillForExport(long.MinValue, long.MaxValue, times, values);
    Check("导出取数", count > 10, count.ToString());

    var series = new ExportSeries
    {
        ChannelName = "自检通道",
        PointName = "温度",
        Unit = "℃",
        Timestamps = times[..count],
        Values = values[..count]
    };

    var csvPath = Path.Combine(Path.GetTempPath(), "wpfapp22-selftest.csv");
    await new CsvExporter().ExportAsync(new ExportRequest { FilePath = csvPath, Series = [series] });
    Check("CSV 文件生成", File.Exists(csvPath) && new FileInfo(csvPath).Length > 0);

    var xlsxPath = Path.Combine(Path.GetTempPath(), "wpfapp22-selftest.xlsx");
    await new ExcelExporter().ExportAsync(new ExportRequest { FilePath = xlsxPath, Series = [series] });
    Check("Excel 文件生成", File.Exists(xlsxPath) && new FileInfo(xlsxPath).Length > 0);

    Console.WriteLine("[9] 配置序列化往返");
    var json = JsonSerializer.Serialize(new List<ChannelConfig> { config });
    var back = JsonSerializer.Deserialize<List<ChannelConfig>>(json);
    Check("配置往返一致", back is { Count: 1 } && back[0].Points[0].Name == "温度");

    await service.DisposeAsync();
}

Console.WriteLine("[10] 端到端采集（真实 Modbus TCP socket 链路）");
{
    // 前面 [2] 只验证了编解码函数，[7] 走的是内置模拟器。
    // 这一段起一个迷你 Modbus TCP 从站，把 TcpTransport + ModbusTcpCodec + ModbusDriver
    // 这半条链路放到真实 socket 上跑一遍 —— 也就是「用 Modbus Slave 联调」在代码层面的等价验证。
    var listener = new TcpListener(IPAddress.Loopback, 0);   // 端口 0 = 让系统随便给一个空闲端口
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

    // 迷你从站：收到 0x03/0x04 读寄存器请求就回一组可预期的值
    var slaveTask = Task.Run(async () =>
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    using var conn = client;
                    var stream = conn.GetStream();
                    var rx = new byte[512];

                    while (!cts.IsCancellationRequested)
                    {
                        int read;
                        try { read = await stream.ReadAsync(rx, cts.Token).ConfigureAwait(false); }
                        catch { break; }
                        if (read < 12) break;

                        var func = rx[7];
                        var start = (ushort)((rx[8] << 8) | rx[9]);
                        var qty = (ushort)((rx[10] << 8) | rx[11]);
                        if (qty == 0) qty = 1;

                        // 期望值公式：地址 N 的值 = 100 * (N + 2)
                        // 于是地址 0 → 200，地址 1 → 300，方便断言精确校验链路正确性
                        var resp = new byte[9 + qty * 2];
                        resp[0] = rx[0]; resp[1] = rx[1];                     // 回显事务标识
                        resp[2] = 0; resp[3] = 0;                             // 协议标识固定 0
                        var length = 3 + qty * 2;                             // 单元标识 1 + PDU(功能码1 + 字节数1 + 数据)
                        resp[4] = (byte)(length >> 8); resp[5] = (byte)(length & 0xFF);
                        resp[6] = rx[6];                                      // 回显单元标识
                        resp[7] = func;
                        resp[8] = (byte)(qty * 2);
                        for (var i = 0; i < qty; i++)
                        {
                            var v = (ushort)(100 * (start + i + 2));
                            resp[9 + i * 2] = (byte)(v >> 8);
                            resp[10 + i * 2] = (byte)(v & 0xFF);
                        }

                        try { await stream.WriteAsync(resp, cts.Token).ConfigureAwait(false); }
                        catch { break; }
                    }
                }, cts.Token);
            }
        }
        catch (OperationCanceledException) { /* 正常收尾 */ }
        catch (SocketException) { /* 监听已关闭 */ }
    }, cts.Token);

    var tcpConfig = new ChannelConfig
    {
        Name = "自检TCP从站",
        Transport = TransportKind.Tcp,
        Protocol = ProtocolKind.ModbusTcp,
        Host = "127.0.0.1",
        Port = port,
        SlaveId = 1,
        PollIntervalMs = 50,
        TimeoutMs = 1000,
        RetryCount = 2,
        BufferCapacity = 5000
    };
    var tcpPoint0 = new PointDefinition { Name = "温度", RegisterAddress = 0 };
    var tcpPoint1 = new PointDefinition { Name = "压力", RegisterAddress = 1 };
    tcpConfig.Points.Add(tcpPoint0);
    tcpConfig.Points.Add(tcpPoint1);

    var tcpService = new AcquisitionService(new DeviceDriverFactory(), pipelineCapacity: 10_000);
    var tcpRuntime = tcpService.AddChannel(tcpConfig);

    await tcpService.StartAsync();
    await tcpService.StartAllAsync();
    await Task.Delay(1500);
    await tcpService.StopAllAsync();

    var tcpStore0 = tcpService.Hub.Get(tcpPoint0.Id);
    Check("TCP 通道进入采集状态", tcpRuntime.TotalSamples > 10, tcpRuntime.TotalSamples.ToString());
    Check("TCP 链路无采样丢弃", tcpService.DroppedSamples == 0, tcpService.DroppedSamples.ToString());

    // 取最新一个样本严格比对：值不对就说明链路某处解析错了，而不是"大概通了"
    if (tcpStore0 is { Buffer.Count: > 0 })
    {
        var oneTime = new double[1];
        var oneValue = new double[1];
        var n = tcpStore0.Buffer.CopyLatest(1, oneTime, oneValue);
        var okVal = n == 1 && Math.Abs(oneValue[0] - 200) < 1e-9;

        // 诊断：失败时把最近若干点和通道错误一并打出来，别只报一句 NaN
        if (!okVal)
        {
            var dbgTimes = new double[12];
            var dbgValues = new double[12];
            var dn = tcpStore0.Buffer.CopyLatest(12, dbgTimes, dbgValues);
            Console.WriteLine($"        [诊断] LastError={tcpRuntime.LastError}  ConsecutiveFailures={tcpRuntime.ConsecutiveFailures}  State={tcpRuntime.State}  Total={tcpRuntime.TotalSamples}");
            Console.WriteLine($"        [诊断] 最近 {dn} 点: {string.Join(", ", dbgValues[..dn].Select(v => double.IsNaN(v) ? "NaN" : v.ToString("F0")))}");
        }

        Check("地址0 解析值 = 200（真实 socket 往返）", okVal, oneValue[0].ToString());
    }
    else
    {
        Check("地址0 解析值 = 200（真实 socket 往返）", false, "缓冲区无数据");
    }

    var tcpStore1 = tcpService.Hub.Get(tcpPoint1.Id);
    if (tcpStore1 is { Buffer.Count: > 0 })
    {
        var oneTime = new double[1];
        var oneValue = new double[1];
        var n = tcpStore1.Buffer.CopyLatest(1, oneTime, oneValue);
        Check("地址1 解析值 = 300（逐点轮询正确）", n == 1 && Math.Abs(oneValue[0] - 300) < 1e-9, oneValue[0].ToString());
    }
    else
    {
        Check("地址1 解析值 = 300（逐点轮询正确）", false, "缓冲区无数据");
    }

    await tcpService.DisposeAsync();
    await cts.CancelAsync();
    listener.Stop();
    try { await slaveTask; } catch { /* 收尾 */ }
}

Console.WriteLine("[11] 从站只支持 0x01 而上位机发 0x03：通道不能被反复断开");
{
    // 这是现场最常见的"一换功能码就掉线"：从站回了异常帧（链路其实是通的），
    // 老逻辑把它当成通讯故障 → 反复重连 → 用户看到通道一直在断开/重连。
    // 这里用真实 socket 复现，断言通道保持运行，且改对功能码后立刻出数。
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

    var slaveTask = Task.Run(async () =>
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    using var conn = client;
                    var stream = conn.GetStream();
                    var rx = new byte[512];

                    while (!cts.IsCancellationRequested)
                    {
                        int read;
                        try { read = await stream.ReadAsync(rx, cts.Token).ConfigureAwait(false); }
                        catch { break; }
                        if (read < 12) break;

                        var func = rx[7];
                        var start = (ushort)((rx[8] << 8) | rx[9]);
                        var qty = (ushort)((rx[10] << 8) | rx[11]);
                        if (qty == 0) qty = 1;

                        byte[] resp;
                        if (func is 0x01 or 0x02)
                        {
                            // 位值规则：地址 N 的位 = (N % 2 == 0) → 0 号位为 1，1 号位为 0
                            var bits = new bool[qty];
                            for (var i = 0; i < qty; i++) bits[i] = (start + i) % 2 == 0;
                            var data = ModbusBits.Pack(bits);

                            resp = new byte[9 + data.Length];
                            var len = 3 + data.Length;
                            resp[4] = (byte)(len >> 8);
                            resp[5] = (byte)(len & 0xFF);
                            resp[7] = func;
                            resp[8] = (byte)data.Length;
                            Array.Copy(data, 0, resp, 9, data.Length);
                        }
                        else
                        {
                            // 不支持的读功能码 → 回异常帧：功能码 | 0x80 + 异常码 0x01（非法功能码）
                            resp = new byte[9];
                            resp[4] = 0;
                            resp[5] = 3;
                            resp[7] = (byte)(func | 0x80);
                            resp[8] = 0x01;
                        }

                        resp[0] = rx[0]; resp[1] = rx[1];   // 回显事务标识
                        resp[6] = rx[6];                    // 回显单元标识

                        try { await stream.WriteAsync(resp, cts.Token).ConfigureAwait(false); }
                        catch { break; }
                    }
                }, cts.Token);
            }
        }
        catch (OperationCanceledException) { /* 正常收尾 */ }
        catch (SocketException) { /* 监听已关闭 */ }
    }, cts.Token);

    var mismatched = new ChannelConfig
    {
        Name = "只支持0x01的从站",
        Transport = TransportKind.Tcp,
        Protocol = ProtocolKind.ModbusTcp,
        Host = "127.0.0.1",
        Port = port,
        SlaveId = 1,
        PollIntervalMs = 100,
        TimeoutMs = 1000,
        RetryCount = 2,
        AutoReconnect = true,
        BufferCapacity = 5000
    };

    // 故意配错：用 0x03 去读一个只支持 0x01 的从站
    var valvePoint = new PointDefinition { Name = "阀门反馈", RegisterAddress = 0, RegisterKind = RegisterKind.HoldingRegister };
    mismatched.Points.Add(valvePoint);

    var svc = new AcquisitionService(new DeviceDriverFactory(), pipelineCapacity: 10_000);
    var rt = svc.AddChannel(mismatched);

    var messages = new List<string>();
    svc.AlarmRaised += e => { lock (messages) messages.Add(e.Message); };

    await svc.StartAsync();
    await svc.StartAllAsync();
    await Task.Delay(1500);

    List<string> snapshot;
    lock (messages) snapshot = new List<string>(messages);

    Check("异常帧不再让通道掉线（保持采集中）", rt.State == ChannelState.Running, rt.State.ToString());
    Check("提示了一次功能码不匹配", snapshot.Count(m => m.Contains("非法功能码")) == 1,
        string.Join(" | ", snapshot));
    Check("没有误报通讯中断", snapshot.All(m => !m.Contains("通讯中断")), string.Join(" | ", snapshot));

    // 现场的正确处置：把点位改成从站真正支持的功能码
    valvePoint.RegisterKind = RegisterKind.Coil;
    await Task.Delay(1000);

    var valveStore = svc.Hub.Get(valvePoint.Id);
    if (valveStore is { Buffer.Count: > 0 })
    {
        var t = new double[1];
        var v = new double[1];
        var n = valveStore.Buffer.CopyLatest(1, t, v);
        Check("改成 0x01 后立刻采到线圈值 1", n == 1 && Math.Abs(v[0] - 1.0) < 1e-9,
            n == 1 ? v[0].ToString() : "无数据");
    }
    else
    {
        Check("改成 0x01 后立刻采到线圈值 1", false, $"缓冲区无数据 LastError={rt.LastError}");
    }

    await svc.StopAllAsync();
    await svc.DisposeAsync();
    await cts.CancelAsync();
    listener.Stop();
    try { await slaveTask; } catch { /* 收尾 */ }
}

Console.WriteLine();
Console.WriteLine($"结果：通过 {passed}，失败 {failed}");
return failed == 0 ? 0 : 1;
