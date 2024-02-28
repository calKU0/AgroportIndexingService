using Google.Apis.Auth.OAuth2;
using Google.Apis.Indexing.v3;
using Google.Apis.Indexing.v3.Data;
using Google.Apis.Services;
using Serilog;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.IO;
using System.ServiceProcess;
using System.Threading.Tasks;
using System.Timers;
using System.Configuration;

namespace AgroportIndexingService
{
    public partial class AgroportIndexingService : ServiceBase
    {
        private Timer timer;
        private readonly string jsonKey = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "agroportKey.json");
        private readonly string urlsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "urls.txt");
        private readonly string indexedFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "indexed.txt");
        private readonly int startHour = Convert.ToInt16(ConfigurationManager.AppSettings["startHour"]);
        private readonly int startFromUrl = Convert.ToInt32(ConfigurationManager.AppSettings["startFromUrl"]);
        private IndexingService service;

        public AgroportIndexingService()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            if (!Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            Log.Logger = new LoggerConfiguration()
                .WriteTo.File(Path.Combine(logDir, "log.txt"), rollingInterval: RollingInterval.Day)
                .CreateLogger();

            Log.Information("Service started.");

            var scopes = "https://www.googleapis.com/auth/indexing";

            var credentials = GoogleCredential.FromFile(jsonKey)
                .CreateScoped(scopes);

            var baseClient = new BaseClientService.Initializer
            {
                HttpClientInitializer = credentials
            };

            service = new IndexingService(baseClient);

            timer = new Timer();
            timer.Interval = 300000; // 5min
            timer.Elapsed += Timer_Elapsed;
            timer.Start();
        }

        protected override void OnStop()
        {
            Log.Information("Service stopped.");

            timer.Stop();
            timer.Dispose();

            Log.CloseAndFlush();
        }

        private async void Timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            short currentHour = Convert.ToInt16(DateTime.Now.Hour.ToString());
            short currentMinute = Convert.ToInt16(DateTime.Now.Minute.ToString());

            if (currentHour == startHour && currentMinute <= 15)
            {
                var urls = await ReadUrlsFromFileAsync(urlsFile);
                int i = startFromUrl;
                while (true)
                {
                    var cleanUrl = urls[i].Trim();

                    var content = new UrlNotification
                    {
                        Url = cleanUrl,
                        Type = "URL_UPDATED"
                    };

                    var request = service.UrlNotifications.Publish(content);
                    try
                    {
                        var response = await request.ExecuteAsync();

                        if (response.UrlNotificationMetadata != null)
                        {
                            await WriteIndexedUrlToFileAsync(indexedFile, urls[i]);
                            urls.RemoveAt(i);

                            SaveLog($"Processed URL: {cleanUrl}", LogEventLevel.Information);
                        }
                    }
                    catch (Google.GoogleApiException)
                    {
                        SaveLog("Exceeded limit quota! (200 urls per day)", LogEventLevel.Error);
                        i++;
                        break;
                    }
                    catch (Exception ex)
                    {
                        SaveLog($"An error occurred: {ex}", LogEventLevel.Error);
                        i++;
                        break;
                    }
                }
                await WriteUrlsToFileAsync(urlsFile, urls);

                if (urls.Count == 0)
                {
                    urls = await ReadUrlsFromFileAsync(indexedFile);
                    await WriteUrlsToFileAsync(urlsFile, urls);
                    File.WriteAllText(indexedFile, string.Empty);

                    SaveLog("urls.txt is empty. Copied content from indexed.txt to urls.txt and cleared indexed.txt.", LogEventLevel.Information);
                }
            }
        }

        private async Task<List<string>> ReadUrlsFromFileAsync(string filePath)
        {
            List<string> urls = new List<string>();
            using (StreamReader reader = new StreamReader(filePath))
            {
                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    urls.Add(line);
                }
            }
            return urls;
        }

        private async Task WriteIndexedUrlToFileAsync(string filePath, string url)
        {
            using (StreamWriter writer = new StreamWriter(filePath, append: true))
            {
                await writer.WriteLineAsync(url);
            }
        }

        private async Task WriteUrlsToFileAsync(string filePath, List<string> urls)
        {
            using (StreamWriter writer = new StreamWriter(filePath))
            {
                foreach (var url in urls)
                {
                    await writer.WriteLineAsync(url);
                }
            }
        }

        private void SaveLog(string logText, LogEventLevel logLevel)
        {
            Log.Write(logLevel, "{Message}", logText);
        }
    }
}
