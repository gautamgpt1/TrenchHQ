if (args is ["--live", .. var options])
    await SocialLiveValidation.RunAsync(options);
else
    Console.WriteLine("Use --live with the documented protected test configuration. See docs/BUILD.md.");
