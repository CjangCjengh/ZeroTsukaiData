using System;
using System.IO;

namespace BinExtractor
{
    internal class Program
    {
        static void Main(string[] args)
        {
            foreach (string binPath in args)
            {
                try
                {
                    if (!binPath.EndsWith(".BIN", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("Not a BIN file: " + binPath);
                        continue;
                    }
                    string hdPath = Path.ChangeExtension(binPath, ".HD");
                    if (!File.Exists(hdPath))
                    {
                        Console.WriteLine("No HD file: " + hdPath);
                        continue;
                    }
                    BinExtractor.Extract(binPath, hdPath, Path.ChangeExtension(binPath, null));
                    Console.WriteLine("Extracted: " + binPath);
                }
                catch
                {
                    Console.WriteLine("Failed: " + binPath);
                    continue;
                }
            }
        }
    }
}
