using ChopItUp.Hub.Hosting;

var options = HubOptions.Parse(args, Environment.GetEnvironmentVariable);
var app = HubHost.Build(options);
app.Run();
