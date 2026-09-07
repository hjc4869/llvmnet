using LlvmNet;

try
{
    return Driver.Run(args);
}
catch (Exception error)
{
    Console.Error.WriteLine($"llvmnet: {error.Message}");
    if (Environment.GetEnvironmentVariable("LLVMNET_TRACE") == "1")
        Console.Error.WriteLine(error);
    return 1;
}