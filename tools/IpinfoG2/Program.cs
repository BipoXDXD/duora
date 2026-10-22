namespace Duora.Tools.IpinfoG2;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args is ["--self-test"])
        { return SelfTest.Run(); }
        Console.Error.WriteLine("G2 rehearsal is not prepared.");
        return 1;
    }
}
