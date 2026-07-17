var settings = AppSettings.Load(args);

using var application = new BeastApplication(settings);
await application.RunAsync();
