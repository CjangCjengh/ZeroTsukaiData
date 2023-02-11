using System;
using System.IO;

namespace BinPacker
{
    internal class Program
    {
        static void Main(string[] args)
        {
            foreach (string path in args)
            {
                try
                {
                    if (!Directory.Exists(path))
                    {
                        Console.WriteLine("Not a directory: " + path);
                        continue;
                    }
                    BinPacker.Pack(path, path + ".BIN", path + ".HD");
                    Console.WriteLine("Packed: " + path);
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
