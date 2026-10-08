# 多通道数据采集系统 · 长期项目笔记

## 项目定位
- 大三学生简历项目，需要能讲出技术点。
- 无硬件环境：内置模拟器开箱即用，同时支持真实 Modbus Slave / com0com 联调。
- 技术栈：WPF + .NET 10 + ScottPlot 5 + CommunityToolkit.Mvvm。
- 分层：Core（零 UI 依赖）、Infrastructure（协议/驱动/导出/通知）、App（WPF MVVM）。

## 关键约束
- TFM：`net10.0-windows10.0.19041.0`（不是 `net10.0-windows`，否则 SkiaSharp.Views.WPF 会退回 net462 并报 NU1701）。
- 输出目录认准 `bin/Debug/net10.0-windows10.0.19041.0`，旧的 `net10.0-windows` 目录是历史产物，应清理。
- 交付前必须 `dotnet build WpfApp22.slnx` 0 警告 + `tools/SelfCheck` 全通过。

## 已实现的核心能力
1. 多通道并发采集（模拟器 / Modbus RTU / Modbus TCP）。
2. Modbus 四种读功能码按点位配置：0x01/0x02/0x03/0x04。
3. 从站异常响应与链路故障分流，避免配置不匹配导致反复重连。
4. 点位数据类型、字序、线性变换、报警（去抖/滞回/区间/级别）。
5. 实时趋势曲线（按设备筛选）、历史数据 CSV/Excel 导出、多渠道报警推送。
6. 按设备查看点位实时值 + 关键字过滤。

## 可复用的实现技巧
- `BindingProxy`（Freezable）解决 `DataGridColumn` 无法直接绑定到窗口 DataContext 的问题。
- 列表项自定义模板里的根容器要设 `Background="Transparent"`，否则点击空白处不触发选中。
- ScottPlot 5 中文渲染：先 `DateTimeTicksBottom()` 换轴，再上色，再改字体；`LabelStyle` 用 `ForeColor`，`Legend` 用 `FontColor`。
- 停止采集时正在飞的请求被取消 → 不能当成通讯失败，要判断 `ct.IsCancellationRequested`。

## 待办（用户未要求，但可后续扩展）
- 时序数据库持久化。
- 图例放曲线区域外 / 独立图例面板。
- 变化率报警、Webhook 重试队列。
