using ChopItUp.Hub.Hosting;

var options = HubOptions.Parse(args, Environment.GetEnvironmentVariable);
if (options.Command != HubCommand.Serve)
    return HostCommands.Run(options, Console.Out, Console.Error);

var app = HubHost.Build(options);
app.Run();
return 0;
