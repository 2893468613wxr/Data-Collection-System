using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using WpfApp22.Infrastructure;
using WpfApp22.App.ViewModels;

namespace WpfApp22.App;

/// <summary>
/// 应用程序入口：装配依赖注入容器。
///
/// 上位机程序必须做全局异常兜底：现场无人值守时，一个未捕获异常导致程序直接消失，
/// 采集数据就全丢了。这里把异常写入日志文件，再决定是否继续运行。
/// </summary>
public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 必须在创建任何 Plot 之前执行：Plot 内部的 FontStyle 是在构造时从 Fonts.Default
        // 取字体名的，晚一步设置就只对之后新建的 Plot 生效。
        // ScottPlot 默认用 Segoe UI，没有中文字形，图例里的中文会变成豆腐块。
        ScottPlot.Fonts.Default = "Microsoft YaHei UI";

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogFatal(args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
        {
            LogFatal(args.Exception);
            args.Handled = true;
        };

        var services = new ServiceCollection();
        services.AddAcquisitionPlatform();
        services.AddSingleton<MainViewModel>();
        Services = services.BuildServiceProvider();

        var viewModel = Services.GetRequiredService<MainViewModel>();
        var window = new MainWindow();
        viewModel.AttachChart(window);
        window.DataContext = viewModel;
        window.Show();

        // 默认不自动启动采集：让操作员自己按“全部启动”，
        // 避免程序一打开就往串口/网口发请求，现场设备还没上电时反而刷一屏错误。
        //
        // 但工控机开机自启、无人值守的场景必须要能自动跑，所以留一个显式开关：
        //   WpfApp22.App.exe --autostart
        if (e.Args.Any(a => string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase)))
        {
            viewModel.StartAllCommand.Execute(null);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Services.GetService<MainViewModel>() is { } vm)
        {
            _ = vm.StopAllCommand.ExecuteAsync(null);
        }

        base.OnExit(e);
    }

    private static void LogFatal(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd}.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}{Environment.NewLine}");
        }
        catch
        {
            // 日志都写不进去时只能放弃，绝不能因为记录异常而二次崩溃
        }
    }
}
