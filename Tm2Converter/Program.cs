using System;
using System.IO;

namespace Tm2Converter
{
    internal class Program
    {
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Tm2Converter - TIM2 (TM2) to PNG converter for PS2 textures");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  Drag TM2 file(s) or a folder onto Tm2Converter.exe");
                Console.WriteLine();
                Console.WriteLine("  Tm2Converter.exe <file.tm2>       Convert single file");
                Console.WriteLine("  Tm2Converter.exe <folder>         Convert all TM2 in folder");
                Console.WriteLine("  Tm2Converter.exe <a> <b> <c>...   Convert multiple files");
                Console.WriteLine();
                Console.WriteLine("Output: PNG files in a _png subfolder (batch) or alongside input (single)");
                Console.WriteLine();
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
                return;
            }

            foreach (string arg in args)
            {
                try
                {
                    string fullPath = Path.GetFullPath(arg);

                    if (Directory.Exists(fullPath))
                    {
                        // Batch convert entire directory
                        string outDir = fullPath + "_png";
                        Console.WriteLine("=== Batch mode: " + fullPath + " ===");
                        Tm2Converter.ConvertDirectory(fullPath, outDir);
                    }
                    else if (File.Exists(fullPath))
                    {
                        // Single file convert
                        string dir = Path.GetDirectoryName(fullPath);
                        string baseName = Path.GetFileNameWithoutExtension(fullPath);
                        string pngPath = Path.Combine(dir, baseName + ".png");

                        Console.Write(Path.GetFileName(fullPath) + " -> ");
                        if (Tm2Converter.ConvertFile(fullPath, pngPath))
                        {
                            Console.WriteLine(baseName + ".png");
                        }
                    }
                    else
                    {
                        Console.WriteLine("Not found: " + fullPath);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error: " + arg);
                    Console.WriteLine("  " + ex.Message);
                }
            }

            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
    }
}
