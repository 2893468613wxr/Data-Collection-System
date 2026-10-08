using Microsoft.Extensions.DependencyInjection;
using WpfApp22.Core.Abstractions;
using WpfApp22.Core.Services;
using WpfApp22.Infrastructure.Drivers;
using WpfApp22.Infrastructure.Export;
using WpfApp22.Infrastructure.Notify;

namespace WpfApp22.Infrastructure;

/// <summary>
/// 依赖注入装配。
/// 界面层只依赖 Core 的抽象接口，具体用哪个驱动、哪个导出器、哪个通知渠道，
/// 全部在这里一次性配好 —— 这就是"依赖倒置"在本项目里的落地方式。
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddAcquisitionPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IDeviceDriverFactory, DeviceDriverFactory>();

        services.AddSingleton(provider =>
            new AcquisitionService(provider.GetRequiredService<IDeviceDriverFactory>()));

        // 导出渠道
        services.AddSingleton<IDataExporter, CsvExporter>();
        services.AddSingleton<IDataExporter, ExcelExporter>();
        services.AddSingleton<ExportService>();

        // 通知渠道。日志 + 声音默认启用；Webhook 只有在 config/notify.json（或环境变量
        // WPFAPP22_WEBHOOK_URL）里填了地址之后才算启用，否则它自己会把 Enabled 报成 false，
        // 分发器就会跳过它，不会产生一堆注定失败的请求。
        var notify = NotifyOptions.Load();
        services.AddSingleton<IAlarmNotifier, LogNotifier>();

        // Console.Beep 是 Windows 专有 API，而 Infrastructure 是跨平台项目（net10.0）。
        // 用 OperatingSystem.IsWindows() 做运行时判断，既是正确的行为，
        // 也让分析器能确认调用点已被平台守卫，不再报 CA1416。
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IAlarmNotifier>(new SoundNotifier
            {
                Enabled = notify.SoundEnabled,
                OnlyForCritical = notify.SoundOnlyForCritical
            });
        }

        services.AddSingleton<IAlarmNotifier>(new WebhookNotifier { Url = notify.WebhookUrl });
        services.AddSingleton<AlarmDispatcher>();

        return services;
    }
}
