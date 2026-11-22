using Microsoft.Build.Locator;

// Locator must run before the method that loads the workspace is JIT-compiled.
MSBuildLocator.RegisterDefaults();
return await Analysis.RunAsync(args);
