using WpfApp22.Core.Models;

namespace WpfApp22.Core.Abstractions;

/// <summary>
/// 驱动工厂：根据通道配置里的"传输方式 + 协议"组合出对应的驱动实例。
/// Core 只定义契约，具体映射规则由 Infrastructure 用依赖注入注册。
/// </summary>
public interface IDeviceDriverFactory
{
    IDeviceDriver Create(ChannelConfig config);
}
