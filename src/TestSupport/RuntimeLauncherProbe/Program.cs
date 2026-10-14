using GameDirector.Client;
try
{
    Console.Write(RuntimeLauncher.ResolveSibling(args[0]));
    return 0;
}
catch (Exception ex)
{
    Console.Error.Write(ex.Message);
    return 1;
}
