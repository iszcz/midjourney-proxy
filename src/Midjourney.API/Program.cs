// Midjourney Proxy - Proxy for Midjourney's Discord, enabling AI drawings via API with one-click face swap. A free, non-profit drawing API project.
// Copyright (C) 2024 trueai.org

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

// Additional Terms:
// This software shall not be used for any illegal activities.
// Users must comply with all applicable laws and regulations,
// particularly those related to image and video processing.
// The use of this software for any form of illegal face swapping,
// invasion of privacy, or any other unlawful purposes is strictly prohibited.
// Violation of these terms may result in termination of the license and may subject the violator to legal action.

using Serilog;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;

namespace Midjourney.API
{
    public class Program
    {
        // ����һ��ȫ�ֵ���־���𿪹�
        public static LoggingLevelSwitch LogLevelSwitch { get; private set; } = new LoggingLevelSwitch();

        public static void Main(string[] args)
        {
            try
            {
                // ��������������
                var host = CreateHostBuilder(args).Build();

                // ȷ����Ӧ�ó������ʱ�رղ�ˢ����־
                AppDomain.CurrentDomain.ProcessExit += (s, e) => Log.CloseAndFlush();

                // ��¼��ǰĿ¼
                Log.Information($"Current directory: {Directory.GetCurrentDirectory()}");

                host.Run();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ӧ�ó�������ʧ��");
            }
            finally
            {
                // ȷ����־��ˢ�º͹ر�
                Log.Information("Ӧ�ó��򼴽��ر�");
                Log.CloseAndFlush();
            }
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
          Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((hostingContext, config) =>
            {
                // ���ö�ȡ��ɺ󣬿����������������ǰ��������
                var configuration = config.Build();

                // ���������������־����ʹ��������Ϣ
                ConfigureInitialLogger(configuration, hostingContext.HostingEnvironment.IsDevelopment());
            })
            .ConfigureLogging((hostContext, loggingBuilder) =>
            {
                // ����Ĭ����־�ṩ������ȫ���� Serilog
                loggingBuilder.ClearProviders();
            })
            .UseSerilog()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseStartup<Startup>();
            });

        /// <summary>
        /// ��ȡ���ò����³�ʼ��־��
        /// </summary>
        /// <param name="configuration"></param>
        /// <param name="isDevelopment"></param>
        private static void ConfigureInitialLogger(IConfiguration configuration, bool isDevelopment)
        {
            // ���ó�ʼ��־����
            LogLevelSwitch.MinimumLevel = isDevelopment ? LogEventLevel.Debug : LogEventLevel.Information;

            // ������־����
            //var loggerConfiguration = new LoggerConfiguration()
            //      .ReadFrom.Configuration(configuration)
            //      .Enrich.FromLogContext();

            // д�����ã������Ƕ�ȡ�����ļ�
            // ���ļ���� 10MB
            var fileSizeLimitBytes = 10 * 1024 * 1024;
            var loggerConfiguration = new LoggerConfiguration()
                //.MinimumLevel.Information()
                .MinimumLevel.ControlledBy(LogLevelSwitch) // ʹ�� LoggingLevelSwitch ������־����
                .MinimumLevel.Override("Default", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.Console()
                .WriteTo.File("logs/log.txt",
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: fileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: 31);

            // ���������ض�����
            if (isDevelopment)
            {
                //loggerConfiguration.MinimumLevel.Debug();

                //// ���������û�����ÿ���̨��־��������
                //// ���򣬲�Ҫ�ڴ��������ӣ������ظ�
                //bool hasConsoleInConfig = configuration
                //    .GetSection("Serilog:WriteTo")
                //    .GetChildren()
                //    .Any(section => section["Name"]?.Equals("Console", StringComparison.OrdinalIgnoreCase) == true);

                //if (!hasConsoleInConfig)
                //{
                //    loggerConfiguration.WriteTo.Console();
                //}

                //loggerConfiguration.WriteTo.Console();

                // ���� Serilog �������
                SelfLog.Enable(Console.Error);
            }

            // ���л�������¼���󵽵����ļ�
            loggerConfiguration.WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(evt => evt.Level >= LogEventLevel.Error)
                .WriteTo.File("logs/error.txt",
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: fileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: 31));

            Log.Logger = loggerConfiguration.CreateLogger();
        }

        /// <summary>
        /// ������־����
        /// </summary>
        /// <param name="level"></param>
        public static void SetLogLevel(LogEventLevel level)
        {
            LogLevelSwitch.MinimumLevel = level;

            Log.Write(level, "��־����������Ϊ: {Level}", level);
        }

    }
}