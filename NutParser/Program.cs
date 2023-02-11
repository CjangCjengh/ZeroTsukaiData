using System;

namespace NutParser
{
    internal class Program
    {
        static void Main(string[] args)
        {
            foreach (string path in args)
            {
                try
                {
                    if (!path.EndsWith(".nut", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("Not a NUT file: " + path);
                        continue;
                    }
                    NutParser.Parse(path, path + ".txt");
                    Console.WriteLine("Parsed: " + path);
                }
                catch
                {
                    Console.WriteLine("Failed: " + path);
                    continue;
                }
            }
        }
    }
}
